using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Cluster.Control;
using Orleans.Durable.Storage.Engine.Journals;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.MetaStore;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Durable.Storage.Engine.Wal;
using Orleans.Durable.Storage.FileSystem.Archive;
using Orleans.Durable.Storage.FileSystem.MetaStore;
using Orleans.Durable.Storage.Hosting;
using Orleans.Durable.Storage.RocksDb;
using Orleans.Durable.Storage.Testing;
using Orleans.Hosting;
using Orleans.Runtime;

namespace Orleans.Durable.Storage.Tests.LazyFs;

/// <summary>
/// Linux-only multi-process crash and Porcupine linearizability soak tests over LazyFS FUSE mounts.
/// </summary>
/// <remarks>
/// <para>
/// Each silo's storage root (<c>wal/</c> and WAL-less <see cref="RocksDbJournalIndex"/> <c>index/</c>) is mounted on
/// its own LazyFS FUSE filesystem simulating an unsynced OS page cache. When a silo process is killed with
/// <c>SIGKILL</c> and its LazyFS mount receives <c>lazyfs::clear-cache</c>, every dirty write, directory creation, or
/// rename that was not flushed with <c>fsync</c>/<c>fdatasync</c> is dropped before the silo restarts.
/// </para>
/// <para>
/// Run on Linux via <c>just lazyfs</c> (sets <c>CLOG_LAZYFS=1</c>). When <c>CLOG_LAZYFS</c> is unset and LazyFS or
/// <c>/dev/fuse</c> is unavailable, tests skip cleanly; when <c>CLOG_LAZYFS=1</c>, a missing LazyFS environment fails
/// explicitly.
/// </para>
/// </remarks>
public sealed class LazyFsCrashSoakTests : IDisposable
{
    private const string SiloConfigEnvVar = "CLOG_LAZYFS_SILO_CONFIG";
    private const string ForceRunEnvVar = "CLOG_LAZYFS";
    private const string RoundsEnvVar = "CLOG_LAZYFS_ROUNDS";
    private const string ReadyMarker = "CLOG_LAZYFS_SILO_READY:";
    private const string ReplyMarker = "CLOG_LAZYFS_REPLY:";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    /// <summary>
    /// Proves LazyFS actively intercepts writes in its simulated page cache: an <c>fsync</c>'d file survives
    /// <c>lazyfs::clear-cache</c> while an unsynced file loses its dirty bytes.
    /// </summary>
    [Fact(Explicit = true, Timeout = 60_000)]
    [Trait("Category", "LazyFs")]
    public async Task LazyFsClearCacheDropsUnsyncedWritesWhilePreservingFsyncedData()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (binary, libDir, unmountTool) = RequireLazyFsOrSkip();
        var root = CreateLinuxTempDirectory("clog-lazyfs-probe");
        try
        {
            await using var mount = await LazyFsMount.StartAsync(root, binary, libDir, unmountTool, cancellationToken: cancellationToken);
            var syncedPath = Path.Combine(mount.MountPath, "synced.bin");
            var unsyncedPath = Path.Combine(mount.MountPath, "unsynced.bin");
            var syncedBytes = "durable-fsynced-payload"u8.ToArray();
            var unsyncedBytes = "unsynced-page-cache-only"u8.ToArray();

            using (var synced = new FileStream(syncedPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                synced.Write(syncedBytes);
                synced.Flush(flushToDisk: true);
            }

            using (var unsynced = new FileStream(unsyncedPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                unsynced.Write(unsyncedBytes);
                unsynced.Flush(flushToDisk: false);
            }

            // Before clear-cache, LazyFS serves both files from its page cache.
            Assert.Equal(syncedBytes, await File.ReadAllBytesAsync(syncedPath, cancellationToken));
            Assert.Equal(unsyncedBytes, await File.ReadAllBytesAsync(unsyncedPath, cancellationToken));

            await mount.ClearCacheAsync(cancellationToken);

            // After clear-cache, synced data persists while unsynced data is dropped.
            Assert.Equal(syncedBytes, await File.ReadAllBytesAsync(syncedPath, cancellationToken));
            Assert.NotEqual(unsyncedBytes, await File.ReadAllBytesAsync(unsyncedPath, cancellationToken));
        }
        finally
        {
            DeleteDirectoryBestEffort(root);
        }
    }

    /// <summary>
    /// Runs a 3-process Orleans + Clog cluster where every silo's WAL and WAL-less <see cref="RocksDbJournalIndex"/>
    /// reside on separate LazyFS mounts, injects rotating single-silo <c>SIGKILL</c> + <c>lazyfs::clear-cache</c>,
    /// <c>SIGSTOP</c>/<c>SIGCONT</c> pauses, and a simultaneous 3-silo power loss + <c>lazyfs::clear-cache</c>, then
    /// verifies clean recovery without index corruption, slow-path archive agreement, and Porcupine linearizability.
    /// </summary>
    [Fact(Explicit = true, Timeout = 300_000)]
    [Trait("Category", "LazyFs")]
    public async Task MultiProcessClusterSurvivesLazyFsCrashAndSigKillWithLinearizableHistory()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (binary, libDir, unmountTool) = RequireLazyFsOrSkip();
        var rounds = ReadConfiguredRounds();
        var workRoot = CreateLinuxTempDirectory("clog-lazyfs-soak");
        var mounts = new LazyFsMount?[3];
        var silos = new SiloProcess?[3];
        var siloStderr = new List<string>();

        try
        {
            var metaDir = Path.Combine(workRoot, "meta");
            var objDir = Path.Combine(workRoot, "objects");
            _ = Directory.CreateDirectory(metaDir);
            _ = Directory.CreateDirectory(objDir);

            var storageClusterId = Guid.NewGuid();
            var orleansClusterId = "clog-lazyfs-" + storageClusterId.ToString("N")[..8];

            for (var i = 0; i < 3; i++)
            {
                var mountWork = Path.Combine(workRoot, string.Create(CultureInfo.InvariantCulture, $"lazyfs-{i}"));
                _ = Directory.CreateDirectory(mountWork);
                mounts[i] = await LazyFsMount.StartAsync(mountWork, binary, libDir, unmountTool, cancellationToken: cancellationToken);
            }

            var configs = new SiloWorkerConfig[3];
            var ports = ReserveLoopbackPorts(6);
            for (var i = 0; i < 3; i++)
            {
                configs[i] = new SiloWorkerConfig(
                    SiloIndex: i,
                    SiloName: string.Create(CultureInfo.InvariantCulture, $"lazyfs-silo-{i}"),
                    OrleansClusterId: orleansClusterId,
                    StorageClusterId: storageClusterId,
                    SiloPort: ports[i * 2],
                    GatewayPort: ports[i * 2 + 1],
                    SiloRoot: mounts[i]!.MountPath,
                    MetaStoreDir: metaDir,
                    ObjectStoreDir: objDir);
            }

            // Start all 3 silos concurrently and wait for cluster formation + initial ledger readiness.
            var startTasks = configs.Select(cfg => SiloProcess.StartAsync(workRoot, cfg, cancellationToken)).ToArray();
            var started = await Task.WhenAll(startTasks);
            var originalNodeIds = new string[3];
            for (var i = 0; i < 3; i++)
            {
                silos[i] = started[i];
                originalNodeIds[i] = started[i].NodeId;
            }

            await WaitForActiveClusterAsync(silos, expectedActive: 3, TimeSpan.FromSeconds(45), cancellationToken);

            string[] keys = ["journal-a", "journal-b", "journal-c", "journal-d", "journal-e", "journal-f"];
            var contended = new HashSet<string>(["journal-a", "journal-b"], StringComparer.Ordinal);
            var history = new HistoryRecorder<JournalInput, JournalOutput>(TimeProvider.System);
            var acceptedPerKey = new ConcurrentDictionary<string, long>(StringComparer.Ordinal);
            var disrupted = new ConcurrentDictionary<int, bool>();
            var unexpectedFailures = new ConcurrentQueue<string>();
            var stopping = false;
            var nextClientId = 0;

            using var workloadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var clientTasks = new List<Task>();
            foreach (var key in keys)
            {
                var count = contended.Contains(key) ? 2 : 1;
                for (var c = 0; c < count; c++)
                {
                    var clientId = nextClientId++;
                    clientTasks.Add(Task.Run(
                        () => RunClientLoopAsync(
                            clientId,
                            key,
                            () => silos,
                            disrupted,
                            () => Volatile.Read(ref stopping),
                            history,
                            acceptedPerKey,
                            unexpectedFailures,
                            workloadCts.Token),
                        workloadCts.Token));
                }
            }

            // Wait for initial progress on every journal before injecting faults.
            await WaitForProgressAsync(keys, acceptedPerKey, minIncrease: 3, TimeSpan.FromSeconds(45), unexpectedFailures, cancellationToken);

            // Fault rounds: rotating SIGKILL + LazyFS clear-cache + SIGSTOP/SIGCONT pause + restart.
            for (var round = 0; round < rounds; round++)
            {
                var victim = round % 3;
                var paused = (round + 1) % 3;

                // Flush RocksDB on one silo during the first round so RocksDB has both flushed SSTs+MANIFEST and an unflushed suffix.
                if (round == 0)
                {
                    var flushTarget = silos[victim];
                    if (flushTarget is not null)
                    {
                        _ = await flushTarget.CallAsync<AckReply>(new WorkerCommand("flush-index"), cancellationToken);
                        await WaitForProgressAsync(keys, acceptedPerKey, minIncrease: 2, TimeSpan.FromSeconds(30), unexpectedFailures, cancellationToken);
                    }
                }

                // Pause a peer briefly with SIGSTOP/SIGCONT while writes continue.
                if (silos[paused] is { } pauseTarget)
                {
                    SendUnixSignal(pauseTarget.ProcessId, "-STOP");
                    try
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(1200), TimeProvider.System, cancellationToken);
                    }
                    finally
                    {
                        SendUnixSignal(pauseTarget.ProcessId, "-CONT");
                    }
                }

                // SIGKILL the victim silo and immediately wipe its LazyFS unsynced page cache.
                disrupted[victim] = true;
                var killed = silos[victim];
                silos[victim] = null;
                if (killed is not null)
                {
                    var diag = await killed.KillAndCollectStderrAsync();
                    siloStderr.Add(diag);
                }

                await mounts[victim]!.ClearCacheAsync(cancellationToken);

                // Keep serving on the 2 surviving silos while membership declares the killed silo dead and hands off its ledgers.
                await WaitForProgressAsync(keys, acceptedPerKey, minIncrease: 2, TimeSpan.FromSeconds(45), unexpectedFailures, cancellationToken);

                // Restart the killed silo on its SAME LazyFS mount so it recovers WAL + RocksDB index + replays unflushed suffix.
                var restarted = await SiloProcess.StartAsync(workRoot, configs[victim], cancellationToken);
                Assert.Equal(originalNodeIds[victim], restarted.NodeId);
                silos[victim] = restarted;
                _ = disrupted.TryRemove(victim, out _);

                await WaitForActiveClusterAsync(silos, expectedActive: 3, TimeSpan.FromSeconds(45), cancellationToken);
                await WaitForProgressAsync(keys, acceptedPerKey, minIncrease: 2, TimeSpan.FromSeconds(45), unexpectedFailures, cancellationToken);
            }

            // Full-cluster simultaneous power loss: flush one silo's index, verify SSTs landed on LazyFS, let fresh writes
            // accumulate in WAL-less memtables, then SIGKILL ALL 3 silos simultaneously and clear ALL 3 LazyFS caches.
            if (silos[0] is { } silo0)
            {
                _ = await silo0.CallAsync<AckReply>(new WorkerCommand("flush-index"), cancellationToken);
                var indexDir = Path.Combine(mounts[0]!.MountPath, "index");
                Assert.True(
                    Directory.Exists(indexDir) && Directory.EnumerateFiles(indexDir, "*.sst", SearchOption.AllDirectories).Any(),
                    $"Expected atomic-flushed RocksDB SSTs under '{indexDir}'.");
            }

            await WaitForProgressAsync(keys, acceptedPerKey, minIncrease: 2, TimeSpan.FromSeconds(30), unexpectedFailures, cancellationToken);

            for (var i = 0; i < 3; i++)
            {
                disrupted[i] = true;
            }

            var killAll = new Task<string>[3];
            for (var i = 0; i < 3; i++)
            {
                var victim = silos[i];
                silos[i] = null;
                killAll[i] = victim is not null ? victim.KillAndCollectStderrAsync() : Task.FromResult(string.Empty);
            }

            siloStderr.AddRange(await Task.WhenAll(killAll));
            await Task.WhenAll(mounts.Select(mount => mount!.ClearCacheAsync(cancellationToken)));

            // Restart all 3 silos simultaneously on their LazyFS mounts after power loss.
            var restartAll = configs.Select(cfg => SiloProcess.StartAsync(workRoot, cfg, cancellationToken)).ToArray();
            var recovered = await Task.WhenAll(restartAll);
            for (var i = 0; i < 3; i++)
            {
                Assert.Equal(originalNodeIds[i], recovered[i].NodeId);
                silos[i] = recovered[i];
                _ = disrupted.TryRemove(i, out _);
            }

            await WaitForActiveClusterAsync(silos, expectedActive: 3, TimeSpan.FromSeconds(60), cancellationToken);
            await WaitForProgressAsync(keys, acceptedPerKey, minIncrease: 2, TimeSpan.FromSeconds(60), unexpectedFailures, cancellationToken);

            // Stop all client loops cleanly and verify zero unexpected errors on non-disrupted silos.
            Volatile.Write(ref stopping, true);
            await Task.WhenAll(clientTasks).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            Assert.True(unexpectedFailures.IsEmpty, string.Join(Environment.NewLine, unexpectedFailures));

            // Perform final reads + slow-path ArchiveReader folds for every key and verify exact agreement.
            var readerSilo = silos[0] ?? throw new InvalidOperationException("Silo 0 is not running for final verification.");
            var finalClientStart = nextClientId;
            var finalClient = finalClientStart;
            foreach (var key in keys)
            {
                var invocation = history.Invoke(finalClient++, JournalInput.Read(key));
                var liveRead = await readerSilo.CallAsync<ReadReply>(new WorkerCommand("read", Key: key), cancellationToken);
                var liveContent = Convert.FromBase64String(liveRead.ContentBase64);
                var liveVersion = JournalVersion.TryParse(liveRead.Version, out var parsedLive)
                    ? parsedLive
                    : throw new InvalidDataException($"Unparseable live version '{liveRead.Version}' for '{key}'.");
                history.Complete(invocation, JournalOutput.Read(liveRead.Exists, liveRead.Version, liveContent));

                var slowRead = await readerSilo.CallAsync<SlowPathReply>(new WorkerCommand("read-slow-path", Key: key), cancellationToken);
                var slowContent = Convert.FromBase64String(slowRead.ContentBase64);
                var foldedVersion = JournalVersion.TryParse(slowRead.Version, out var parsedFold)
                    ? parsedFold
                    : throw new InvalidDataException($"Unparseable folded version '{slowRead.Version}' for '{key}'.");

                if (slowRead.Rebased)
                {
                    Assert.True(
                        liveVersion == foldedVersion || liveVersion.Count != 0,
                        $"The final read of '{key}' named rebased state {liveVersion}, neither folded {foldedVersion} nor an earlier name.");
                }
                else
                {
                    Assert.Equal(foldedVersion, liveVersion);
                }

                Assert.Equal(slowRead.Exists, liveRead.Exists);
                Assert.Equal(slowContent, liveContent);
            }

            // Confirm no RocksDB index corruption or fatal storage errors occurred across any silo run.
            foreach (var silo in silos)
            {
                if (silo is not null)
                {
                    siloStderr.Add(await silo.StopGracefullyAsync());
                }
            }

            Array.Clear(silos);

            foreach (var stderr in siloStderr)
            {
                Assert.DoesNotContain("JournalIndexCorruptException", stderr, StringComparison.Ordinal);
                Assert.DoesNotContain("FatalErrorHandler", stderr, StringComparison.Ordinal);
            }

            // Validate complete multi-process history with Clog's Porcupine linearizability checker.
            var operations = history.History(static input => input.Kind == JournalOperationKind.Read ? JournalOutput.Moved : JournalOutput.Ambiguous);
            foreach (var key in keys)
            {
                Assert.True(
                    operations.Any(op => op.Input.Key == key && op.Input.Kind == JournalOperationKind.Append && op.Output.Outcome == JournalOutcome.Accepted),
                    $"History of '{key}' has no accepted writes.");
                Assert.True(
                    operations.Any(op => op.Input.Key == key && op.ClientId >= finalClientStart && op.Output.Outcome == JournalOutcome.Accepted),
                    $"History of '{key}' lacks its final read.");
            }

            ClusterWorkload.AssertReadsReportAcceptedVersions(operations);
            LinearizabilityAssert.Linearizable(JournalModel.Instance, operations, cancellationToken: cancellationToken);
        }
        finally
        {
            for (var i = 0; i < silos.Length; i++)
            {
                if (silos[i] is { } running)
                {
                    _ = await running.KillAndCollectStderrAsync();
                    silos[i] = null;
                }
            }

            for (var i = 0; i < mounts.Length; i++)
            {
                if (mounts[i] is { } mount)
                {
                    await mount.DisposeAsync();
                    mounts[i] = null;
                }
            }

            DeleteDirectoryBestEffort(workRoot);
        }
    }

    /// <summary>
    /// Child silo worker process spawned by <see cref="MultiProcessClusterSurvivesLazyFsCrashAndSigKillWithLinearizableHistory"/>.
    /// Marked <c>Explicit = true</c> without the <c>LazyFs</c> trait so neither <c>just test</c> nor <c>just lazyfs</c>
    /// invokes it directly.
    /// </summary>
    [Fact(Explicit = true)]
    public async Task LazyFsSiloWorker()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var configPath = Environment.GetEnvironmentVariable(SiloConfigEnvVar);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(configPath), "Runs only as a child silo process of LazyFsCrashSoakTests.");

        var configJson = await File.ReadAllTextAsync(configPath, cancellationToken);
        var config = JsonSerializer.Deserialize<SiloWorkerConfig>(configJson, JsonOptions)
            ?? throw new InvalidDataException("Invalid LazyFS silo worker config.");

        var builder = Host.CreateApplicationBuilder();
        _ = builder.Logging.ClearProviders();
        _ = builder.Logging.AddConsole(static console => console.LogToStandardErrorThreshold = LogLevel.Trace);
        _ = builder.Logging.SetMinimumLevel(LogLevel.Warning);

        var metaStore = new FileSystemMetaStore(config.MetaStoreDir, TimeProvider.System);
        var objectStore = new FileSystemObjectStore(config.ObjectStoreDir, TimeProvider.System);
        _ = builder.Services.AddSingleton<IMetaStore>(metaStore);
        _ = builder.Services.AddSingleton<IObjectStore>(objectStore);
        _ = builder.Services.AddSingleton<WalStorage, BufferedPortableWalStorage>();
        _ = builder.Services.AddSingleton<IMembershipTable>(new FileSystemMembershipTable(metaStore, config.StorageClusterId));

        _ = builder.UseOrleans(silo =>
        {
            _ = silo.Configure<ClusterOptions>(o =>
            {
                o.ClusterId = config.OrleansClusterId;
                o.ServiceId = "clog-lazyfs-soak";
            });
            _ = silo.Configure<SiloOptions>(o => o.SiloName = config.SiloName);
            _ = silo.ConfigureEndpoints(IPAddress.Loopback, config.SiloPort, config.GatewayPort);
            _ = silo.Configure<ClusterMembershipOptions>(o =>
            {
                o.ProbeTimeout = TimeSpan.FromMilliseconds(500);
                o.NumMissedProbesLimit = 2;
                o.TableRefreshTimeout = TimeSpan.FromSeconds(1);
                o.IAmAliveTablePublishTimeout = TimeSpan.FromSeconds(1);
                o.NumVotesForDeathDeclaration = 1;
                o.DeathVoteExpirationTimeout = TimeSpan.FromSeconds(10);
            });

            _ = silo.AddDurableJournalStorage(opts => opts.Configure(siloOpts =>
            {
                siloOpts.Node = new DurableStorageOptions
                {
                    Cluster = config.StorageClusterId,
                    Root = config.SiloRoot,
                    WalFileSize = 4L << 20,
                    PreallocateWalFiles = false,
                    ArchiveStore = ArchiveStoreKind.Lsm,
                    ArchiveStorePrefix = "lazyfssoak",
                };
                siloOpts.Control.ControlInterval = TimeSpan.FromMilliseconds(250);
                siloOpts.Control.MapRefreshInterval = TimeSpan.FromMilliseconds(250);
                siloOpts.Control.BalanceInterval = TimeSpan.FromMilliseconds(250);
                siloOpts.Control.ReplicaGrace = TimeSpan.FromSeconds(2);
                siloOpts.Control.LedgersPerNode = 2;
                siloOpts.Control.TransitionTimeout = TimeSpan.FromSeconds(10);
            }));
            _ = silo.AddDurableJournalIndex(RocksDbJournalIndex.ForNode);
        });

        using var host = builder.Build();
        await host.StartAsync(cancellationToken);
        try
        {
            var services = host.Services;
            var localNode = services.GetRequiredService<LocalNode>();
            var router = services.GetRequiredService<JournalRouter>();
            var nodeLedgers = services.GetRequiredService<NodeLedgers>();
            var journalIndex = services.GetRequiredService<IJournalIndex>();
            var membership = services.GetRequiredService<IClusterMembershipService>();

            var readyPayload = JsonSerializer.Serialize(
                new ReadyReply(localNode.Node.ToString(), Environment.ProcessId),
                JsonOptions);
            await Console.Out.WriteLineAsync((ReadyMarker + readyPayload).AsMemory(), cancellationToken);
            await Console.Out.FlushAsync(cancellationToken);

            string? line;
            while ((line = await Console.In.ReadLineAsync(cancellationToken)) is not null)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var command = JsonSerializer.Deserialize<WorkerCommand>(line, JsonOptions)
                    ?? throw new InvalidDataException("Invalid worker command.");

                if (string.Equals(command.Command, "stop", StringComparison.Ordinal))
                {
                    await WriteReplyAsync(new AckReply(true), cancellationToken);
                    break;
                }

                try
                {
                    object reply = command.Command switch
                    {
                        "status" => HandleStatus(membership, nodeLedgers),
                        "write" => await HandleWriteAsync(router, command, cancellationToken),
                        "read" => await HandleReadAsync(router, command, cancellationToken),
                        "read-slow-path" => await HandleReadSlowPathAsync(nodeLedgers, command, cancellationToken),
                        "flush-index" => await HandleFlushIndexAsync(journalIndex, cancellationToken),
                        _ => throw new InvalidOperationException($"Unknown command '{command.Command}'."),
                    };
                    await WriteReplyAsync(reply, cancellationToken);
                }
                catch (Exception exception)
                {
                    await WriteReplyAsync(new ErrorReply(exception.GetType().Name, exception.Message), cancellationToken);
                }
            }
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }

    private static StatusReply HandleStatus(IClusterMembershipService membership, NodeLedgers nodeLedgers)
    {
        var activeCount = membership.CurrentSnapshot.Members.Values.Count(static member => member.Status == SiloStatus.Active);
        var hasRuntime = nodeLedgers.Runtime is not null;
        return new StatusReply(activeCount, hasRuntime);
    }

    private static async Task<WriteReply> HandleWriteAsync(JournalRouter router, WorkerCommand command, CancellationToken cancellationToken)
    {
        var key = command.Key ?? throw new InvalidOperationException("Missing write key.");
        var expectedText = command.ExpectedVersion ?? throw new InvalidOperationException("Missing expectedVersion.");
        var expected = JournalVersion.TryParse(expectedText, out var parsed)
            ? parsed
            : throw new InvalidDataException($"Invalid expectedVersion '{expectedText}'.");
        var body = Convert.FromBase64String(command.PayloadBase64 ?? string.Empty);
        var rowKey = JournalRowKey.Encode(key);

        using var writer = new ArcBufferWriter();
        writer.Write(body);
        using var payload = writer.ConsumeSlice(body.Length);
        using var record = JournalEntryRecord.Append(rowKey, expected, payload);

        var outcome = await router.WriteAsync(key, rowKey, record, cancellationToken);
        return outcome.Value switch
        {
            JournalWriteOutcome.Accepted accepted => new WriteReply("Accepted", accepted.Version.ToString()),
            JournalWriteOutcome.Rejected rejected => new WriteReply("Rejected", rejected.CurrentVersion.ToString()),
            JournalWriteOutcome.ReplaceRequired => new WriteReply("ReplaceRequired", null),
            JournalMoved => new WriteReply("Moved", null),
            JournalWriteOutcome.Fenced => new WriteReply("Fenced", null),
            JournalWriteOutcome.Ambiguous => new WriteReply("Ambiguous", null),
            _ => throw new UnreachableException(),
        };
    }

    private static async Task<ReadReply> HandleReadAsync(JournalRouter router, WorkerCommand command, CancellationToken cancellationToken)
    {
        var key = command.Key ?? throw new InvalidOperationException("Missing read key.");
        var rowKey = JournalRowKey.Encode(key);
        var read = await router.ReadAsync(key, rowKey, cancellationToken);
        using (read.Content)
        {
            var slice = read.Content.Reader.PeekSlice(read.Content.Length);
            byte[] bytes;
            try
            {
                bytes = slice.ToArray();
            }
            finally
            {
                slice.Dispose();
            }

            return new ReadReply(read.Exists, read.Version.ToString(), Convert.ToBase64String(bytes));
        }
    }

    private static async Task<SlowPathReply> HandleReadSlowPathAsync(NodeLedgers nodeLedgers, WorkerCommand command, CancellationToken cancellationToken)
    {
        var key = command.Key ?? throw new InvalidOperationException("Missing read-slow-path key.");
        var runtime = nodeLedgers.Runtime ?? throw new InvalidOperationException("NodeLedgers runtime is not available.");
        var rowKey = JournalRowKey.Encode(key);
        _ = await runtime.View.ReadAsync(cancellationToken);
        var archiveReader = new ArchiveReader(runtime.Reader, runtime.Archive, TimeProvider.System);
        using var history = await archiveReader.ReadAsync(rowKey.Memory, cancellationToken);
        var folded = JournalState.Fold(history.Prefix, history.Rows);
        var foldedVersion = folded?.CurrentVersion ?? JournalVersion.Origin;
        var rebased = foldedVersion.Count == 0 && foldedVersion != JournalVersion.Origin;
        var content = ClusterWorkload.SlowPathContent(history.Prefix, history.Rows);
        return new SlowPathReply(folded?.Exists ?? false, foldedVersion.ToString(), Convert.ToBase64String(content), rebased);
    }

    private static async Task<AckReply> HandleFlushIndexAsync(IJournalIndex index, CancellationToken cancellationToken)
    {
        await index.FlushAsync(cancellationToken);
        return new AckReply(true);
    }

    private static async Task WriteReplyAsync(object payload, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload, payload.GetType(), JsonOptions);
        await Console.Out.WriteLineAsync((ReplyMarker + json).AsMemory(), cancellationToken);
        await Console.Out.FlushAsync(cancellationToken);
    }

    private static async Task RunClientLoopAsync(
        int clientId,
        string key,
        Func<SiloProcess?[]> getSilos,
        ConcurrentDictionary<int, bool> disrupted,
        Func<bool> isStopping,
        HistoryRecorder<JournalInput, JournalOutput> history,
        ConcurrentDictionary<string, long> acceptedPerKey,
        ConcurrentQueue<string> unexpectedFailures,
        CancellationToken cancellationToken)
    {
        JournalVersion? expected = null;
        var readNext = false;
        var accepts = 0;
        var opIndex = 0;

        while (!isStopping() && !cancellationToken.IsCancellationRequested)
        {
            var (siloIndex, silo) = PickAvailableSilo(getSilos(), disrupted, clientId + opIndex);
            if (silo is null)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), TimeProvider.System, cancellationToken);
                continue;
            }

            opIndex++;
            if (expected is { } version && !readNext)
            {
                var body = Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{key}#{clientId}.{opIndex};"));
                var invocation = history.Invoke(clientId, JournalInput.Append(key, version.ToString(), body), string.Create(CultureInfo.InvariantCulture, $"silo-{siloIndex}"));
                try
                {
                    var reply = await silo.CallAsync<WriteReply>(
                        new WorkerCommand("write", Key: key, ExpectedVersion: version.ToString(), PayloadBase64: Convert.ToBase64String(body)),
                        cancellationToken);

                    switch (reply.Outcome)
                    {
                        case "Accepted":
                            var nextVersion = JournalVersion.TryParse(reply.Version, out var parsed)
                                ? parsed
                                : throw new InvalidDataException($"Unparseable accepted version '{reply.Version}'.");
                            history.Complete(invocation, JournalOutput.Accepted(nextVersion.ToString()));
                            _ = acceptedPerKey.AddOrUpdate(key, 1, static (_, c) => c + 1);
                            expected = nextVersion;
                            readNext = ++accepts % 2 == 0;
                            break;
                        case "Rejected":
                            history.Complete(invocation, JournalOutput.Rejected(reply.Version ?? JournalModel.Origin, deposed: true));
                            expected = null;
                            break;
                        case "ReplaceRequired":
                            history.Complete(invocation, JournalOutput.ReplaceRequiredBy(deposed: true));
                            unexpectedFailures.Enqueue($"Unexpected ReplaceRequired on '{key}' via silo-{siloIndex}.");
                            expected = null;
                            break;
                        case "Moved":
                            history.Complete(invocation, JournalOutput.Moved);
                            expected = null;
                            break;
                        case "Fenced":
                            history.Complete(invocation, JournalOutput.Fenced);
                            expected = null;
                            break;
                        case "Ambiguous":
                            history.CompleteUnbounded(invocation, JournalOutput.Ambiguous);
                            expected = null;
                            break;
                        default:
                            history.CompleteUnbounded(invocation, JournalOutput.Ambiguous);
                            unexpectedFailures.Enqueue($"Unknown write outcome '{reply.Outcome}' on '{key}' via silo-{siloIndex}.");
                            expected = null;
                            break;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    history.CompleteUnbounded(invocation, JournalOutput.Ambiguous);
                    break;
                }
                catch (Exception failure)
                {
                    history.CompleteUnbounded(invocation, JournalOutput.Ambiguous);
                    if (!disrupted.ContainsKey(siloIndex))
                    {
                        // During failover a surviving router can briefly observe a dead peer's broken socket; treat
                        // transient transport/timeout errors as expected disruption rather than silent corruption.
                        if (!IsExpectedFailoverError(failure))
                        {
                            unexpectedFailures.Enqueue($"Write '{key}' via silo-{siloIndex}: {failure.Message}");
                        }
                    }

                    expected = null;
                }
            }
            else
            {
                var invocation = history.Invoke(clientId, JournalInput.Read(key), string.Create(CultureInfo.InvariantCulture, $"silo-{siloIndex}"));
                try
                {
                    var reply = await silo.CallAsync<ReadReply>(new WorkerCommand("read", Key: key), cancellationToken);
                    var content = Convert.FromBase64String(reply.ContentBase64);
                    var versionParsed = JournalVersion.TryParse(reply.Version, out var parsed)
                        ? parsed
                        : throw new InvalidDataException($"Unparseable read version '{reply.Version}'.");
                    history.Complete(invocation, JournalOutput.Read(reply.Exists, reply.Version, content));
                    expected = versionParsed;
                    readNext = false;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    history.Complete(invocation, JournalOutput.Moved);
                    break;
                }
                catch (Exception failure)
                {
                    history.Complete(invocation, JournalOutput.Moved);
                    if (!disrupted.ContainsKey(siloIndex) && !IsExpectedFailoverError(failure))
                    {
                        unexpectedFailures.Enqueue($"Read '{key}' via silo-{siloIndex}: {failure.Message}");
                    }

                    expected = null;
                }
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(15), TimeProvider.System, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private static bool IsExpectedFailoverError(Exception failure)
    {
        var message = failure.Message;
        return message.Contains("Timeout", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Unavailable", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Disposed", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Moved", StringComparison.OrdinalIgnoreCase)
            || message.Contains("exited", StringComparison.OrdinalIgnoreCase)
            || message.Contains("closed", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Socket", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Silo", StringComparison.OrdinalIgnoreCase)
            || message.Contains("cancel", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Node", StringComparison.OrdinalIgnoreCase)
            || message.Contains("No response", StringComparison.OrdinalIgnoreCase);
    }

    private static (int Index, SiloProcess? Silo) PickAvailableSilo(SiloProcess?[] silos, ConcurrentDictionary<int, bool> disrupted, int seed)
    {
        var available = new List<(int Index, SiloProcess Silo)>(3);
        for (var i = 0; i < silos.Length; i++)
        {
            if (silos[i] is { } running && !disrupted.ContainsKey(i) && !running.HasExited)
            {
                available.Add((i, running));
            }
        }

        if (available.Count == 0)
        {
            return (-1, null);
        }

        return available[Math.Abs(seed) % available.Count];
    }

    private static async Task WaitForActiveClusterAsync(SiloProcess?[] silos, int expectedActive, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = TimeProvider.System.GetUtcNow() + timeout;
        while (TimeProvider.System.GetUtcNow() < deadline)
        {
            var allReady = true;
            foreach (var silo in silos)
            {
                if (silo is null)
                {
                    allReady = false;
                    break;
                }

                try
                {
                    var status = await silo.CallAsync<StatusReply>(new WorkerCommand("status"), cancellationToken);
                    if (status.ActiveSilos < expectedActive || !status.RuntimeReady)
                    {
                        allReady = false;
                        break;
                    }
                }
                catch
                {
                    allReady = false;
                    break;
                }
            }

            if (allReady)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), TimeProvider.System, cancellationToken);
        }

        throw new TimeoutException($"Cluster did not reach {expectedActive} active silos with NodeLedgers runtime ready within {timeout}.");
    }

    private static async Task WaitForProgressAsync(
        IReadOnlyList<string> keys,
        ConcurrentDictionary<string, long> acceptedPerKey,
        int minIncrease,
        TimeSpan timeout,
        ConcurrentQueue<string> unexpectedFailures,
        CancellationToken cancellationToken)
    {
        var targets = keys.ToDictionary(static k => k, k => acceptedPerKey.GetValueOrDefault(k) + minIncrease, StringComparer.Ordinal);
        var deadline = TimeProvider.System.GetUtcNow() + timeout;
        while (TimeProvider.System.GetUtcNow() < deadline)
        {
            Assert.True(unexpectedFailures.IsEmpty, string.Join(Environment.NewLine, unexpectedFailures));
            if (targets.All(pair => acceptedPerKey.GetValueOrDefault(pair.Key) >= pair.Value))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), TimeProvider.System, cancellationToken);
        }

        var behind = string.Join(", ", targets.Where(pair => acceptedPerKey.GetValueOrDefault(pair.Key) < pair.Value)
            .Select(pair => $"{pair.Key}: {acceptedPerKey.GetValueOrDefault(pair.Key)}/{pair.Value}"));
        throw new TimeoutException($"LazyFS soak workload did not reach progress targets within {timeout}: {behind}.");
    }

    private static (string BinaryPath, string? LibraryDirectory, string UnmountTool) RequireLazyFsOrSkip()
    {
        var force = string.Equals(Environment.GetEnvironmentVariable(ForceRunEnvVar), "1", StringComparison.Ordinal);
        if (LazyFsMount.TryLocateEnvironment(out var binary, out var libDir, out var unmountTool, out var reason))
        {
            return (binary, libDir, unmountTool);
        }

        if (force)
        {
            Assert.Fail($"{ForceRunEnvVar}=1 was set, but LazyFS is unavailable: {reason}");
        }

        Assert.Skip(reason);
        throw new UnreachableException();
    }

    private static int ReadConfiguredRounds()
    {
        var text = Environment.GetEnvironmentVariable(RoundsEnvVar);
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : 2;
    }

    private static string CreateLinuxTempDirectory(string prefix)
    {
        var baseDir = OperatingSystem.IsLinux() && Directory.Exists("/tmp") ? "/tmp" : Path.GetTempPath();
        var path = Path.Combine(baseDir, prefix + "-" + Guid.NewGuid().ToString("N")[..12]);
        _ = Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectoryBestEffort(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup of temp directories.
        }
    }

    private static int[] ReserveLoopbackPorts(int count)
    {
        var listeners = new TcpListener[count];
        var ports = new int[count];
        try
        {
            for (var i = 0; i < count; i++)
            {
                listeners[i] = new TcpListener(IPAddress.Loopback, 0);
                listeners[i].Start();
                ports[i] = ((IPEndPoint)listeners[i].LocalEndpoint).Port;
            }

            return ports;
        }
        finally
        {
            foreach (var listener in listeners)
            {
                listener?.Stop();
            }
        }
    }

    private static void SendUnixSignal(int pid, string signalFlag)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var killer = Process.Start(new ProcessStartInfo("kill")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { signalFlag, pid.ToString(CultureInfo.InvariantCulture) },
        });
        _ = killer?.WaitForExit(3000);
    }

    private sealed class SiloProcess
    {
        private readonly Process _process;
        private readonly SemaphoreSlim _rpcGate = new(1, 1);
        private readonly Task<string> _stderrTask;

        private SiloProcess(Process process, int processId, string nodeId, Task<string> stderrTask)
        {
            _process = process;
            ProcessId = processId;
            NodeId = nodeId;
            _stderrTask = stderrTask;
        }

        public int ProcessId { get; }

        public string NodeId { get; }

        public bool HasExited => _process.HasExited;

        public static async Task<SiloProcess> StartAsync(string workRoot, SiloWorkerConfig config, CancellationToken cancellationToken)
        {
            var configPath = Path.Combine(workRoot, string.Create(CultureInfo.InvariantCulture, $"silo-{config.SiloIndex}-{Guid.NewGuid():N}.json"));
            await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(config, JsonOptions), cancellationToken);

            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Test host executable path is unavailable.");
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            {
                start.ArgumentList.Add(typeof(LazyFsCrashSoakTests).Assembly.Location);
            }

            start.ArgumentList.Add("--explicit");
            start.ArgumentList.Add("on");
            start.ArgumentList.Add("--filter-method");
            start.ArgumentList.Add($"*{nameof(LazyFsSiloWorker)}");
            start.Environment[SiloConfigEnvVar] = configPath;
            start.Environment["TESTINGPLATFORM_PIPE_DIRECTORY"] = "/tmp";

            var process = Process.Start(start) ?? throw new InvalidOperationException($"Failed to launch child silo process for {config.SiloName}.");
            var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

            var deadline = TimeProvider.System.GetUtcNow() + TimeSpan.FromSeconds(45);
            var stdoutLog = new StringBuilder();
            while (TimeProvider.System.GetUtcNow() < deadline)
            {
                var line = await process.StandardOutput.ReadLineAsync(cancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(45), cancellationToken);
                if (line is null)
                {
                    await process.WaitForExitAsync(cancellationToken);
                    var stderr = await stderrTask;
                    throw new InvalidOperationException($"Child silo {config.SiloName} exited with code {process.ExitCode} before reporting ready.\nSTDOUT:\n{stdoutLog}\nSTDERR:\n{stderr}");
                }

                if (line.StartsWith(ReadyMarker, StringComparison.Ordinal))
                {
                    var ready = JsonSerializer.Deserialize<ReadyReply>(line[ReadyMarker.Length..], JsonOptions)
                        ?? throw new InvalidDataException("Invalid ready reply from child silo.");
                    return new SiloProcess(process, ready.Pid, ready.NodeId, stderrTask);
                }

                _ = stdoutLog.AppendLine(line);
            }

            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"Child silo {config.SiloName} did not report ready within 45 seconds.");
        }

        public async Task<TReply> CallAsync<TReply>(WorkerCommand command, CancellationToken cancellationToken)
        {
            await _rpcGate.WaitAsync(cancellationToken);
            try
            {
                if (_process.HasExited)
                {
                    throw new IOException($"Silo process exited with code {_process.ExitCode}.");
                }

                var line = JsonSerializer.Serialize(command, JsonOptions);
                await _process.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken);
                await _process.StandardInput.FlushAsync(cancellationToken);

                while (true)
                {
                    var responseLine = await _process.StandardOutput.ReadLineAsync(cancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(25), cancellationToken);
                    if (responseLine is null)
                    {
                        throw new IOException("Child silo closed stdout before replying.");
                    }

                    if (!responseLine.StartsWith(ReplyMarker, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var payload = responseLine[ReplyMarker.Length..];
                    using var doc = JsonDocument.Parse(payload);
                    if (doc.RootElement.TryGetProperty("errorType", out var errorType))
                    {
                        var errorMessage = doc.RootElement.TryGetProperty("errorMessage", out var msg) ? msg.GetString() : string.Empty;
                        throw new IOException($"Child silo returned {errorType.GetString()}: {errorMessage}");
                    }

                    return JsonSerializer.Deserialize<TReply>(payload, JsonOptions)
                        ?? throw new InvalidDataException($"Failed to deserialize reply as {typeof(TReply).Name}: {payload}");
                }
            }
            finally
            {
                _ = _rpcGate.Release();
            }
        }

        public async Task<string> StopGracefullyAsync()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _ = await CallAsync<AckReply>(new WorkerCommand("stop"), CancellationToken.None);
                    await _process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));
                }
            }
            catch
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    await _process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
                }
            }

            var stderr = await _stderrTask;
            _process.Dispose();
            _rpcGate.Dispose();
            return stderr;
        }

        public async Task<string> KillAndCollectStderrAsync()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    await _process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
                }
            }
            catch
            {
                // Best-effort kill.
            }

            var stderr = string.Empty;
            try
            {
                stderr = await _stderrTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch
            {
                // Ignore if pipe timed out.
            }

            _process.Dispose();
            _rpcGate.Dispose();
            return stderr;
        }
    }

    private sealed record SiloWorkerConfig(
        int SiloIndex,
        string SiloName,
        string OrleansClusterId,
        Guid StorageClusterId,
        int SiloPort,
        int GatewayPort,
        string SiloRoot,
        string MetaStoreDir,
        string ObjectStoreDir);

    private sealed record WorkerCommand(
        string Command,
        string? Key = null,
        string? ExpectedVersion = null,
        string? PayloadBase64 = null);

    private sealed record ReadyReply(string NodeId, int Pid);

    private sealed record StatusReply(int ActiveSilos, bool RuntimeReady);

    private sealed record WriteReply(string Outcome, string? Version);

    private sealed record ReadReply(bool Exists, string Version, string ContentBase64);

    private sealed record SlowPathReply(bool Exists, string Version, string ContentBase64, bool Rebased);

    private sealed record AckReply(bool Ok);

    private sealed record ErrorReply(string ErrorType, string ErrorMessage);
}
