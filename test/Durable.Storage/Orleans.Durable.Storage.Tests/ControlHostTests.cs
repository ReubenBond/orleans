using System.Collections.Immutable;
using System.Diagnostics;
using Azure.Storage.Blobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Orleans.Durable.Storage.AzureStorage.Archive;
using Orleans.Durable.Storage.Control;
using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Archive.Logs;
using Orleans.Durable.Storage.Engine.Archive.Lsm;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Cluster.Control;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Control;
using Orleans.Durable.Storage.Engine.Ledgers.Objects;
using Orleans.Durable.Storage.Engine.MetaStore;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Durable.Storage.Engine.ObjectStore.Memory;
using Orleans.Durable.Storage.Hosting;
using Orleans.Durable.Storage.Testing;
using Orleans.Journaling;
using Orleans.Runtime;
using Orleans.Runtime.Placement;

namespace Orleans.Durable.Storage.Tests;

/// <summary>
/// Tests of the control plane's silo-side hosts (orleans.md §5.6, §5.7): the membership projection's table, the controller
/// grain's coalesced pass, the sweep host's pacing, coalescing, maintenance and stop, archive logs' sweeps behind their
/// gate, and the archive store a silo's options select over what its services supply.
/// </summary>
public sealed class ControlHostTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _leaks.Dispose();

    public static TheoryData<SiloStatus?, int, string> Standings => new()
    {
        { SiloStatus.Dead, 2, nameof(MembershipStanding.Dead) },
        { SiloStatus.Dead, 1, nameof(MembershipStanding.Dead) },
        { SiloStatus.ShuttingDown, 2, nameof(MembershipStanding.Departing) },
        { SiloStatus.Stopping, 2, nameof(MembershipStanding.Departing) },
        { SiloStatus.None, 2, nameof(MembershipStanding.Live) },
        { SiloStatus.Created, 2, nameof(MembershipStanding.Live) },
        { SiloStatus.Joining, 2, nameof(MembershipStanding.Live) },
        { SiloStatus.Active, 2, nameof(MembershipStanding.Live) },
        { SiloStatus.Active, 1, nameof(MembershipStanding.Live) },

        // Absent: declared dead before the observer joined when older than the observer, otherwise not yet seen.
        { null, 1, nameof(MembershipStanding.Dead) },
        { null, 2, nameof(MembershipStanding.Live) },
        { null, 3, nameof(MembershipStanding.Live) },
    };

    [Theory]
    [MemberData(nameof(Standings))]
    public void MembershipMapsEachStatusAndGenerationToOneStanding(SiloStatus? status, int registeredGeneration, string expected)
        => Assert.Equal(expected, MembershipProjection.Standing(status, registeredGeneration, observerGeneration: 2).ToString());

    /// <summary>
    /// orleans.md §5.13, §6: a silo's archive store is the one its options select, over the stores its services supply,
    /// under the cluster's store prefix, derived from Orleans' service ID unless configured: the LSM over their object
    /// store, under the prefix; archive logs over their archive-log container, which opens the logs and durable storage's objects under
    /// the prefix in one container, the logs writing the silo's journal format, which <c>Orleans.Durable.Storage.AzureStorage</c>'s
    /// <c>AddAzureArchiveLogs</c> takes from the composition rather than as a setting of its own, registering one client
    /// of a container with the store's client options. A silo whose services lack what its kind needs, which selects the
    /// memory store, or whose prefix can derive from no service ID, or only from a service ID Orleans gives by default, does
    /// not start; a configured prefix needs no service ID of its own.
    /// </summary>
    [Fact]
    public async Task ASilosArchiveStoreIsTheOneItsOptionsSelectOverWhatItsServicesSupply()
    {
        var objects = new InMemoryObjectStore();
        var derived = ArchiveStorePrefix.Of("service");
        var lsm = Assert.IsType<SiloArchive.Lsm>(SiloArchives.Create(Services(ArchiveStoreKind.Lsm, services => services.AddSingleton<IObjectStore>(objects))));
        Assert.Equal((ArchiveStoreKind.Lsm, derived), (lsm.Kind, lsm.Prefix));
        using (var content = Orleans.Durable.Storage.Engine.Buffers.ArcBuffer.Wrap("x"u8.ToArray()))
        {
            _ = await lsm.Objects.CreateAsync(ObjectName.Store, content, crc32c: null, Token);
        }

        Assert.Equal([derived.Value + "/store"], await objects.ListAsync(string.Empty, cancellationToken: Token).Select(static listed => listed.Name.Value).ToListAsync(Token));
        var configured = Assert.IsType<SiloArchive.Lsm>(SiloArchives.Create(Services(ArchiveStoreKind.Lsm, services => services.AddSingleton<IObjectStore>(objects), prefix: "tenant-7")));
        Assert.Equal(new ArchiveStorePrefix("tenant-7"), configured.Prefix);

        var container = new RecordingContainer(new MemoryArchiveLogContainer(objects));
        var logs = Assert.IsType<SiloArchive.Logs>(SiloArchives.Create(Services(ArchiveStoreKind.Logs, services => services.AddSingleton<IArchiveLogContainer>(container), format: "test-format")));
        Assert.Equal((ArchiveStoreKind.Logs, ArchiveStoreKind.Logs, derived), (logs.Kind, logs.Store.Kind, logs.Prefix));
        Assert.Equal([(derived.Value, "test-format")], container.OpenedLogs);
        Assert.Equal([derived.Value], container.OpenedObjects);

        BlobClientOptions? clientOptions = null;
        var azure = Assert.IsType<SiloArchive.Logs>(SiloArchives.Create(Services(ArchiveStoreKind.Logs, services => services.AddAzureArchiveLogs(
            options =>
            {
                clientOptions = options;
                return new BlobContainerClient(new Uri("https://account.blob.core.windows.net/journals?sv=2025-01-05&sig=signature"), options);
            }))));
        Assert.True(azure.Store.AdmitsKey(Orleans.Durable.Storage.Engine.Journals.JournalRowKey.Encode("grain/1").Span));
        Assert.False(azure.Store.AdmitsKey(Orleans.Durable.Storage.Engine.Journals.JournalRowKey.Encode("grain\u00e9").Span));
        Assert.NotNull(clientOptions);
        Assert.Equal(global::Azure.Storage.Request100ContinueMode.Always, clientOptions.Request100ContinueOptions.Mode);

        _ = Assert.Throws<InvalidOperationException>(() => SiloArchives.Create(Services(ArchiveStoreKind.Lsm)));
        Assert.Contains("AddAzureArchiveLogs", Assert.Throws<InvalidOperationException>(() => SiloArchives.Create(Services(ArchiveStoreKind.Logs, services => services.AddSingleton<IObjectStore>(objects)))).Message, StringComparison.Ordinal);
        _ = Assert.Throws<InvalidOperationException>(() => SiloArchives.Create(Services(ArchiveStoreKind.Memory, services => services.AddSingleton<IObjectStore>(objects))));
        Assert.Contains("ServiceId", Assert.Throws<InvalidOperationException>(() => SiloArchives.Create(Services(ArchiveStoreKind.Lsm, services => services.AddSingleton<IObjectStore>(objects), serviceId: ""))).Message, StringComparison.Ordinal);
        foreach (var orleansDefault in new[] { global::Orleans.Configuration.ClusterOptions.DefaultServiceId, "default", "dev" })
        {
            var message = Assert.Throws<InvalidOperationException>(() => SiloArchives.Create(Services(ArchiveStoreKind.Lsm, services => services.AddSingleton<IObjectStore>(objects), serviceId: orleansDefault))).Message;
            Assert.Contains("ClusterOptions.ServiceId", message, StringComparison.Ordinal);
            Assert.Contains("DurableStorageOptions.ArchiveStorePrefix", message, StringComparison.Ordinal);
            Assert.Equal(new ArchiveStorePrefix("tenant-7"), Assert.IsType<SiloArchive.Lsm>(SiloArchives.Create(Services(ArchiveStoreKind.Lsm, services => services.AddSingleton<IObjectStore>(objects), prefix: "tenant-7", serviceId: orleansDefault))).Prefix);
        }

        static ServiceProvider Services(ArchiveStoreKind kind, Action<IServiceCollection>? configure = null, string? prefix = null, string serviceId = "service", string? format = null)
        {
            var services = new ServiceCollection();
            _ = services.AddSingleton(new DurableStorageOptions { Cluster = Guid.NewGuid(), Root = Path.GetTempPath(), ArchiveStore = kind, ArchiveStorePrefix = prefix });
            _ = services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new global::Orleans.Configuration.ClusterOptions { ServiceId = serviceId }));
            _ = services.AddSingleton(Microsoft.Extensions.Options.Options.Create(format is null ? new JournaledStateManagerOptions() : new JournaledStateManagerOptions { JournalFormatKey = format }));
            configure?.Invoke(services);
            return services.BuildServiceProvider();
        }
    }

    /// <summary>
    /// journals.md §8.4, orleans.md §6: a silo on archive logs whose Journals' <c>MaxBodyBytes</c> passes its log store's
    /// largest append is refused as its archive is built, before its first registration could record the parameter; one at
    /// the store's limit is built.
    /// </summary>
    [Fact]
    public void ASiloOnArchiveLogsWhoseBodyLimitPassesItsStoresLargestAppendIsRefused()
    {
        var limit = new MemoryArchiveLogStore().Layout.MaxAppendBytes!.Value;
        var refusal = Assert.Throws<InvalidOperationException>(() => SiloArchives.Create(Services(limit + 1)));
        Assert.Contains("Journals:MaxBodyBytes", refusal.Message, StringComparison.Ordinal);
        _ = Assert.IsType<SiloArchive.Logs>(SiloArchives.Create(Services(limit)));

        static ServiceProvider Services(int maxBodyBytes)
        {
            var options = new DurableStorageOptions { Cluster = Guid.NewGuid(), Root = Path.GetTempPath(), ArchiveStore = ArchiveStoreKind.Logs };
            options.Journals.MaxBodyBytes = maxBodyBytes;
            return new ServiceCollection()
                .AddSingleton(options)
                .AddSingleton(Microsoft.Extensions.Options.Options.Create(new global::Orleans.Configuration.ClusterOptions { ServiceId = "service" }))
                .AddSingleton(Microsoft.Extensions.Options.Options.Create(new JournaledStateManagerOptions()))
                .AddSingleton<IArchiveLogContainer>(new MemoryArchiveLogContainer(new InMemoryObjectStore()))
                .BuildServiceProvider();
        }
    }

    /// <summary>
    /// orleans.md §5.13: the cluster's store prefix derives from Orleans' service ID as 10 lowercase base32 characters of
    /// its SHA-256, which name one object-name component, the same for the same ID and distinct for these distinct ones; a
    /// configured prefix is 1 to 16 characters of <c>[a-z0-9_-]</c>, and the options refuse any other.
    /// </summary>
    [Fact]
    public void TheStorePrefixDerivesFromTheServiceIdOrIsConfigured()
    {
        Assert.Equal(ArchiveStorePrefix.Of("service"), ArchiveStorePrefix.Of("service"));
        Assert.Equal(ArchiveStorePrefix.DerivedLength, ArchiveStorePrefix.Of("service").Value.Length);
        Assert.Equal(1000, Enumerable.Range(0, 1000).Select(static n => ArchiveStorePrefix.Of($"service-{n}")).Distinct().Count());
        Assert.All(Enumerable.Range(0, 100), static n => Assert.Matches("^[a-z2-7]{10}$", ArchiveStorePrefix.Of($"service-{n}").Value));
        // RFC 4648 base32 of SHA-256("service"), lowercased and cut to 10 characters, as Python's base64.b32encode gives it.
        Assert.Equal("tx3lajviy3", ArchiveStorePrefix.Of("service").Value);

        Assert.True(ArchiveStorePrefix.IsValid("z09_-azaz09_-azz"));
        Assert.False(ArchiveStorePrefix.IsValid("z09_-azaz09_-azzz"));
        Assert.False(ArchiveStorePrefix.IsValid(string.Empty));
        Assert.False(ArchiveStorePrefix.IsValid("A"));
        Assert.False(ArchiveStorePrefix.IsValid("a/b"));
        Assert.False(ArchiveStorePrefix.IsValid("a.b"));
        _ = Assert.Throws<ArgumentException>(() => new DurableStorageOptions { Cluster = Guid.NewGuid(), Root = Path.GetTempPath(), ArchiveStore = ArchiveStoreKind.Lsm, ArchiveStorePrefix = "Tenant" }.Validate());
        new DurableStorageOptions { Cluster = Guid.NewGuid(), Root = Path.GetTempPath(), ArchiveStore = ArchiveStoreKind.Lsm, ArchiveStorePrefix = "tenant" }.Validate();
    }

    /// <summary>An archive-log container which records each opening.</summary>
    private sealed class RecordingContainer(IArchiveLogContainer inner) : IArchiveLogContainer
    {
        public List<(string Prefix, string Format)> OpenedLogs { get; } = [];

        public List<string> OpenedObjects { get; } = [];

        public IArchiveLogStore OpenLogs(string prefix, string format)
        {
            OpenedLogs.Add((prefix, format));
            return inner.OpenLogs(prefix, format);
        }

        public IObjectStore OpenObjects(string prefix)
        {
            OpenedObjects.Add(prefix);
            return inner.OpenObjects(prefix);
        }
    }

    /// <summary>
    /// OR-I20: the LSM's sweeps wait for the silo's archive gate, sweeping nothing before it passes and never a store it
    /// refused, and end quietly with a gate cancelled as the silo stops.
    /// </summary>
    [Fact]
    public async Task TheLsmsSweepsWaitForTheGateAndNeverSweepAStoreItRefused()
    {
        var gate = new ArchiveGate();
        var sweeps = new LsmSweeps(gate.Passed, new InMemoryObjectStore(), maintenance: null, TimeProvider.System, NullLoggerFactory.Instance);
        Assert.IsType<ArchiveSweepOutcome.Waiting>((await sweeps.SweepAsync(Token)).Value);

        gate.Pass(new ArchiveGateEvidence.Lsm(new InMemoryObjectStore()));
        Assert.IsType<ArchiveSweepOutcome.Swept>((await sweeps.SweepAsync(Token)).Value);

        var refused = new ArchiveGate();
        var mismatch = new ArchiveStoreMismatchException(new ArchiveStoreRecordField(ArchiveStoreRecord.IdField, IsParameter: false), "a", "b");
        refused.Refuse(mismatch);
        Assert.Same(mismatch, Assert.IsType<ArchiveSweepOutcome.Refused>((await new LsmSweeps(refused.Passed, new InMemoryObjectStore(), null, TimeProvider.System, NullLoggerFactory.Instance).SweepAsync(Token)).Value).Reason);

        var stopped = new ArchiveGate();
        stopped.Cancel();
        Assert.IsType<ArchiveSweepOutcome.Stopping>((await new LsmSweeps(stopped.Passed, new InMemoryObjectStore(), null, TimeProvider.System, NullLoggerFactory.Instance).SweepAsync(Token)).Value);
    }

    [Fact]
    public async Task RequestsInOneTurnRunThePassOnceInALaterTurnOnTheOwnersScheduler()
    {
        var owner = new ConcurrentExclusiveSchedulerPair().ExclusiveScheduler;
        var ranOn = new TaskCompletionSource<TaskScheduler>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pass = new CoalescedPass(() => ranOn.TrySetResult(TaskScheduler.Current));

        var runsWithinTheTurn = await Task.Factory.StartNew(() =>
        {
            pass.Request();
            pass.Request();
            pass.Request();
            return pass.Runs;
        }, Token, TaskCreationOptions.None, owner);

        Assert.Equal(0, runsWithinTheTurn);
        Assert.Same(owner, await ranOn.Task.WaitAsync(Patience, Token));
        Assert.Equal(1, pass.Runs);

        // A request after the run asks for another.
        await Task.Factory.StartNew(pass.Request, Token, TaskCreationOptions.None, owner);
        await WaitUntilAsync(() => pass.Runs == 2);
    }

    [Fact]
    public async Task SweepsRunOnRequestAtLeastAnIntervalApartEachFollowedByMaintenance()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var options = new ControlOptions { SweepInterval = TimeSpan.FromMinutes(10) };
        var maintained = new List<DateTimeOffset>();
        var maintenance = new ObjectStoreMaintenance(() =>
        {
            lock (maintained)
            {
                maintained.Add(time.GetUtcNow());
            }
        });
        using var host = new SweepHost(options, new LsmSweeps(Task.CompletedTask, new InMemoryObjectStore(), maintenance, time, NullLoggerFactory.Instance), time, NullLoggerFactory.Instance);
        await host.Start();

        host.Request();
        await WaitUntilAsync(() => host.Sweeps == 1 && Maintained() == 1);

        // Three requests within the interval are one sweep, which waits out the interval.
        time.Advance(options.SweepInterval / 2);
        host.Request();
        host.Request();
        host.Request();
        while (host.Sweeps < 2)
        {
            Assert.True(time.GetUtcNow() - maintained[0] <= 2 * options.SweepInterval, "The second sweep never ran.");
            time.Advance(options.SweepInterval / 20);
            await Task.Delay(TimeSpan.FromMilliseconds(10), TimeProvider.System, Token);
        }

        await WaitUntilAsync(() => Maintained() == 2);
        Assert.True(maintained[1] - maintained[0] >= options.SweepInterval, $"Sweeps ran {maintained[1] - maintained[0]} apart.");

        // No request, no sweep.
        time.Advance(3 * options.SweepInterval);
        await Task.Delay(TimeSpan.FromMilliseconds(100), TimeProvider.System, Token);
        Assert.Equal(2, host.Sweeps);
        Assert.Equal(2, Maintained());

        // A stop ends a sweep's wait at once.
        host.Request();
        await WaitUntilAsync(() => host.Sweeps == 3);
        host.Request();
        await host.StopAsync().WaitAsync(Patience, Token);
        Assert.Equal(3, host.Sweeps);

        int Maintained()
        {
            lock (maintained)
            {
                return maintained.Count;
            }
        }
    }

    /// <summary>
    /// orleans.md §5.6, §5.13: archive logs' sweeps sweep nothing until the silo's archive gate passed, each request before
    /// it sweeping nothing and leaving the cadence free, and then sweep at each request over the gate's stores.
    /// </summary>
    [Fact]
    public async Task ArchiveLogSweepsWaitForTheGate()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var gate = new TaskCompletionSource<ArchiveGateEvidence>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var host = new SweepHost(Options, new ArchiveLogSweeps(gate.Task, new InMemoryMetaStore(), Guid.NewGuid(), NullLoggerFactory.Instance), time, NullLoggerFactory.Instance);
        await host.Start();

        host.Request();
        await Task.Delay(TimeSpan.FromMilliseconds(100), TimeProvider.System, Token);
        Assert.Equal(0, host.Sweeps);

        gate.SetResult(new ArchiveGateEvidence.Logs(await PassGateAsync()));
        host.Request();
        await WaitUntilAsync(() => host.Sweeps == 1);
        await host.StopAsync().WaitAsync(Patience, Token);
    }

    /// <summary>
    /// orleans.md §5.13: a store the silo's archive gate refused, here one whose record names another ID, is never swept: the host
    /// logs its critical event, 6245, and sweeps no more.
    /// </summary>
    [Fact]
    public async Task AStoreTheGateRefusedIsNeverSwept()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var refused = await Assert.ThrowsAsync<ArchiveStoreMismatchException>(async () => await PassGateAsync(id: ArchiveStoreId.NewRandom()));
        using var loggers = new RecordingLoggerFactory();
        using var host = new SweepHost(Options, new ArchiveLogSweeps(Task.FromException<ArchiveGateEvidence>(refused), new InMemoryMetaStore(), Guid.NewGuid(), loggers), time, loggers);
        await host.Start();

        host.Request();
        await WaitUntilAsync(() => host.Ended);
        Assert.Equal(0, host.Sweeps);
        Assert.Contains(6245, loggers.Logger.Events);
    }

    /// <summary>
    /// orleans.md §5.13 (#389): a gate refused while no sweep was asked wakes the host's loop, which meets the refusal, logs
    /// 6245 once, and ends, so the host's end follows its report; the wake is no request. The loop alone reports the
    /// refusal: a report written beside it could land after the host ended and stopped, as the refusal's watcher's did.
    /// </summary>
    [Fact]
    public async Task ARefusalNoRequestMetEndsTheHostOnlyOnceItIsLogged()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var gate = new TaskCompletionSource<ArchiveGateEvidence>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var loggers = new RecordingLoggerFactory();
        using var host = new SweepHost(Options, new ArchiveLogSweeps(gate.Task, new InMemoryMetaStore(), Guid.NewGuid(), loggers), time, loggers);
        await host.Start();

        gate.SetException(await Assert.ThrowsAsync<ArchiveStoreMismatchException>(async () => await PassGateAsync(id: ArchiveStoreId.NewRandom())));
        await WaitUntilAsync(() => host.Ended);

        Assert.Single(loggers.Logger.Events, static id => id == 6245);
        Assert.Equal((0, 0L), (host.Sweeps, host.Requests));
        host.Request();
        await host.StopAsync().WaitAsync(Patience, Token);
        Assert.Single(loggers.Logger.Events, static id => id == 6245);
    }

    /// <summary>
    /// orleans.md §5.13: a gate cancelled as the silo stops is no refusal: the host stops sweeping, and logs no 6245.
    /// </summary>
    [Fact]
    public async Task ACancelledGateStopsTheSweepsWithoutAnEvent()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using var loggers = new RecordingLoggerFactory();
        using var host = new SweepHost(Options, new ArchiveLogSweeps(Task.FromCanceled<ArchiveGateEvidence>(new CancellationToken(canceled: true)), new InMemoryMetaStore(), Guid.NewGuid(), loggers), time, loggers);
        await host.Start();

        host.Request();
        await WaitUntilAsync(() => host.Ended);
        Assert.Equal(0, host.Sweeps);
        Assert.DoesNotContain(6245, loggers.Logger.Events);
    }

    /// <summary>
    /// orleans.md §5.6: archive logs' sweeps whose setup failed, a read of the ledger map's register, try it again at the
    /// next request, and then sweep.
    /// </summary>
    [Fact]
    public async Task ArchiveLogSweepsRetryTheirSetupAtTheNextRequest()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var metaStore = new FailingOnceMetaStore(new InMemoryMetaStore());
        using var host = new SweepHost(Options, new ArchiveLogSweeps(Task.FromResult<ArchiveGateEvidence>(new ArchiveGateEvidence.Logs(await PassGateAsync())), metaStore, Guid.NewGuid(), NullLoggerFactory.Instance), time, NullLoggerFactory.Instance);
        await host.Start();

        host.Request();
        await WaitUntilAsync(() => metaStore.Calls == 1);
        await Task.Delay(TimeSpan.FromMilliseconds(50), TimeProvider.System, Token);
        Assert.Equal(0, host.Sweeps);

        host.Request();
        await WaitUntilAsync(() => host.Sweeps == 1);
        await host.StopAsync().WaitAsync(Patience, Token);
    }

    /// <summary>
    /// archive-logs.md §8.5, orleans.md §5.6: the archive-log sweeps keep their sweeper from sweep to sweep, so each sweep's
    /// slice of the bases' listing resumes from the continuation at which the last one ended, and the sweep after the
    /// listing's end lists from its start.
    /// </summary>
    [Fact]
    public async Task ArchiveLogSweepsResumeEachSliceWhereTheLastEnded()
    {
        var memory = new MemoryArchiveLogStore() { ListPageSize = 1 };
        var name = new ArchiveLogName("a", 1);
        _ = await memory.OpenAsync(name, ArchiveLogPosition.Origin, new ArchiveLogOpener(ArchiveLogOpenerKind.Replace, null, 1), new([1]), Token);
        _ = await memory.OpenAsync(name, new ArchiveLogPosition(1, 1, 1, 0), new ArchiveLogOpener(ArchiveLogOpenerKind.Replace, null, 1), new([2]), Token);
        var logs = new CountingArchiveLogStore(memory);
        var sweeps = new ArchiveLogSweeps(Task.FromResult<ArchiveGateEvidence>(new ArchiveGateEvidence.Logs(await PassGateAsync(logs: logs))), new InMemoryMetaStore(), Guid.NewGuid(), NullLoggerFactory.Instance) { PagesPerSweep = 1 };
        var first = await memory.ListGenerationsAsync(continuation: null, Token).FirstAsync(Token);
        Assert.NotNull(first.Continuation);

        for (var sweep = 0; sweep < 3; sweep++)
        {
            Assert.Same(ArchiveSweepOutcome.Swept.Instance, (await sweeps.SweepAsync(Token)).Value);
        }

        Assert.Equal([null, first.Continuation, null], logs.Continuations);
    }

    /// <summary>
    /// orleans.md §5.2, §5.6: a sweep host stopped mid-sweep, its archive-log sweep's listing held by a request which ends
    /// only when cancelled, stops within <c>TransitionTimeout</c>, as the silo's stop bounds its work: the stop cancels the
    /// sweep, whose held listing ends with the cancellation, and nothing is swept.
    /// </summary>
    [Fact]
    public async Task ASweepHostStoppedMidSweepCancelsItsHeldListingWithinTheBound()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var logs = new HeldListingStore(new MemoryArchiveLogStore());
        using var host = new SweepHost(Options, new ArchiveLogSweeps(Task.FromResult<ArchiveGateEvidence>(new ArchiveGateEvidence.Logs(await PassGateAsync(logs: logs))), new InMemoryMetaStore(), Guid.NewGuid(), NullLoggerFactory.Instance), time, NullLoggerFactory.Instance);
        await host.Start();

        host.Request();
        await logs.Listing.Task.WaitAsync(Patience, Token);
        await host.StopAsync().WaitAsync(Options.TransitionTimeout, Token);

        Assert.True(logs.Cancelled);
        Assert.Equal(0, host.Sweeps);
    }

    public static TheoryData<string, string> GatesWhichSweepNothing => new()
    {
        { "lsm", "waiting" },
        { "lsm", "refused" },
        { "lsm", "stopping" },
        { "logs", "waiting" },
        { "logs", "refused" },
        { "logs", "stopping" },
    };

    /// <summary>
    /// orleans.md §5.6, §5.13, OR-I20: the sweep host's wiring of each store's sweeps to the silo's archive gate. While the
    /// gate waits, once it is refused, and once it is cancelled as the silo stops, a requested sweep lists and deletes
    /// nothing in the object store or the archive-log store, and a refused gate is logged once, as event 6245.
    /// </summary>
    [Theory]
    [MemberData(nameof(GatesWhichSweepNothing))]
    public async Task ASweepOverAGateWhichHasNotPassedListsAndDeletesNothing(string kind, string state)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var objects = new CountingObjectStore(new InMemoryObjectStore());
        var logs = new CountingArchiveLogStore(new MemoryArchiveLogStore());
        SiloArchive archive = kind == "lsm" ? new SiloArchive.Lsm(objects, new ArchiveStorePrefix("t")) : new SiloArchive.Logs(logs, objects, new ArchiveStorePrefix("t"));
        var gate = archive.Gate;
        var refusal = new ArchiveStoreMismatchException(new ArchiveStoreRecordField(ArchiveStoreRecord.IdField, IsParameter: false), "stored", "expected");
        using var loggers = new RecordingLoggerFactory();
        ArchiveSweeps sweeps = archive switch
        {
            SiloArchive.Lsm lsm => lsm.Sweeps(maintenance: null, time, loggers),
            SiloArchive.Logs archiveLogs => archiveLogs.Sweeps(new InMemoryMetaStore(), Guid.NewGuid(), loggers),
            _ => throw new UnreachableException(),
        };
        using var host = new SweepHost(Options, sweeps, time, loggers);
        await host.Start();
        switch (state)
        {
            case "refused":
                gate.Refuse(refusal);
                break;
            case "stopping":
                gate.Cancel();
                break;
            default:
                break;
        }

        if (state == "waiting")
        {
            // The sweep itself answers Waiting while the gate is undecided, with no request; no sleep stands in for it.
            Assert.Same(ArchiveSweepOutcome.Waiting.Instance, (await sweeps.SweepAsync(Token).WaitAsync(Patience, Token)).Value);
        }
        else
        {
            host.Request();
            await WaitUntilAsync(() => host.Ended);
        }

        await host.StopAsync().WaitAsync(Patience, Token);
        Assert.Equal((0, 0, 0), (host.Sweeps, objects.Calls, logs.Calls));
        Assert.Equal(state == "refused" ? 1 : 0, loggers.Logger.Events.Count(static id => id == 6245));
    }

    /// <summary>
    /// orleans.md §5.6: the controller grain is placed on the calling silo when its archive gate passed, and otherwise on
    /// another compatible silo, the calling one only when no other is.
    /// </summary>
    [Fact]
    public void TheControllerIsPlacedOnTheCallersSiloOnlyOnceItsGatePassed()
    {
        var local = SiloAddress.New(System.Net.IPAddress.Loopback, 11111, 1);
        var other = SiloAddress.New(System.Net.IPAddress.Loopback, 11112, 1);
        var random = new Random(1);

        Assert.Equal(local, ControllerPlacementDirector.Choose(passed: true, local, [local, other], random));
        Assert.Equal(other, ControllerPlacementDirector.Choose(passed: false, local, [local, other], random));
        Assert.Equal(local, ControllerPlacementDirector.Choose(passed: false, local, [local], random));
        Assert.Equal(other, ControllerPlacementDirector.Choose(passed: true, local, [other], random));
    }

    /// <summary>
    /// orleans.md §5.6, V4: the controller's placement director, given the caller's own silo archive, places the grain on the
    /// calling silo only once that silo's gate passed: while it is pending or refused, every placement chooses another
    /// compatible silo, and a placement hint, a migration's target, wins whatever the gate.
    /// </summary>
    [Fact]
    public async Task TheDirectorPlacesTheControllerByTheCallersGateAndHonoursAHint()
    {
        var local = SiloAddress.New(System.Net.IPAddress.Loopback, 11111, 1);
        var other = SiloAddress.New(System.Net.IPAddress.Loopback, 11112, 1);
        var context = new FixedPlacementContext(local, [local, other]);
        var pending = new SiloArchive.Lsm(new InMemoryObjectStore(), new ArchiveStorePrefix("t"));
        var refused = new SiloArchive.Lsm(new InMemoryObjectStore(), new ArchiveStorePrefix("t"));
        refused.Gate.Refuse(new ArchiveStoreMismatchException(new ArchiveStoreRecordField(ArchiveStoreRecord.IdField, IsParameter: false), "stored", "expected"));
        var passed = new SiloArchive.Lsm(new InMemoryObjectStore(), new ArchiveStorePrefix("t"));
        passed.Gate.Pass(new ArchiveGateEvidence.Lsm(passed.Objects));

        foreach (var archive in new[] { pending, refused })
        {
            for (var placement = 0; placement < 20; placement++)
            {
                Assert.Equal(other, await new ControllerPlacementDirector(archive).OnAddActivation(ControllerPlacement.Instance, Target(hint: null), context));
            }

            Assert.Equal(local, await new ControllerPlacementDirector(archive).OnAddActivation(ControllerPlacement.Instance, Target(hint: local), context));
        }

        Assert.Equal(local, await new ControllerPlacementDirector(passed).OnAddActivation(ControllerPlacement.Instance, Target(hint: null), context));
        Assert.Equal(other, await new ControllerPlacementDirector(passed).OnAddActivation(ControllerPlacement.Instance, Target(hint: other), context));
        Assert.Equal(local, await new ControllerPlacementDirector(pending).OnAddActivation(ControllerPlacement.Instance, Target(hint: null), new FixedPlacementContext(local, [local])));

        // When CLNR records an ObjectLedgerStore, a silo lacking local ObjectLedgers avoids placing the controller locally.
        var register = new ScriptedConditionalRegister();
        var objectStoreId = new ObjectLedgerStoreId(new UInt128(0, 0xABCD));
        var entry = new NodeRegistryEntry(new Orleans.Durable.Storage.Engine.Ledgers.NodeId(Guid.NewGuid()), local);
        var registryValue = new NodeRegistryValue(1, [entry], new ArchiveStoreIdentity(ArchiveStoreKind.Lsm, new ArchiveStorePrefix("t"), new ArchiveStoreId(new UInt128(0, 1))), objectStoreId);
        await using var resolver = new NodeRegistryResolver(_ => new ValueTask<IConditionalRegister>(register), new FakeMembership(), TimeProvider.System, NullLogger.Instance);
        resolver.Advance(registryValue);
        for (var placement = 0; placement < 20; placement++)
        {
            Assert.Equal(other, await new ControllerPlacementDirector(passed, objectLedgers: null, registry: resolver).OnAddActivation(ControllerPlacement.Instance, Target(hint: null), context));
        }

        // Once local ObjectLedgers pass their gate, the controller can place locally again.
        var inner = new InMemoryObjectStore();
        var objectStore = ObjectLedgerStore.OverAppendable(new PrefixedAppendableObjectStore(inner, inner, "t/"), new ObjectLedgerStoreOptions(), TimeProvider.System);
        var siloObjectLedgers = new SiloObjectLedgers(objectStore, new ArchiveStorePrefix("t"));
        siloObjectLedgers.Gate.Pass(objectStore);
        Assert.Equal(local, await new ControllerPlacementDirector(passed, siloObjectLedgers, registry: resolver).OnAddActivation(ControllerPlacement.Instance, Target(hint: null), context));

        static PlacementTarget Target(SiloAddress? hint)
        {
            var data = new Dictionary<string, object>(StringComparer.Ordinal);
            if (hint is not null)
            {
                data[IPlacementDirector.PlacementHintKey] = hint;
            }

            return new PlacementTarget(GrainId.Create("controller", "0"), data, default, 0);
        }
    }

    /// <summary>
    /// orleans.md §5.14: <see cref="SweepHost"/> runs object-ledger sweeps only once the object-ledger gate passes, wakes
    /// immediately on a newer map epoch without waiting for <c>SweepInterval</c>, backs off after transient sweep failures,
    /// cancels an in-flight sweep on <see cref="SweepHost.CancelObjectLedgers"/> without stopping the loop, logs refusal
    /// event 6249 once, and marks <see cref="SweepHost.Ended"/> only once both loops end.
    /// </summary>
    [Fact]
    public async Task ObjectLedgerSweepsRespectGateWakeOnNewEpochBackOffAndCancelOnDeactivation()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var inner = new InMemoryObjectStore();
        var objects = new FlakyCountingObjectStore(inner);
        var store = ObjectLedgerStore.OverAppendable(
            new PrefixedAppendableObjectStore(objects, objects, "t/"),
            new ObjectLedgerStoreOptions(),
            time);
        var gate = new TaskCompletionSource<ObjectLedgerStore>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var loggers = new RecordingLoggerFactory();
        var archiveGate = new TaskCompletionSource<ArchiveGateEvidence>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var host = new SweepHost(
            Options,
            new LsmSweeps(archiveGate.Task, new InMemoryObjectStore(), null, time, loggers),
            time,
            loggers,
            new ObjectLedgerSweeps(gate.Task, loggers));
        await host.Start();

        // While the gate is pending, RequestObjectLedgers lists nothing.
        var map1 = new LedgerMapValue(1, new LedgerId(1), 0, []);
        host.RequestObjectLedgers(map1);
        await Task.Delay(TimeSpan.FromMilliseconds(25), TimeProvider.System, Token);
        Assert.Equal((0, 0), (host.ObjectSweeps, objects.ListCalls));

        // Passing the gate lets map1 sweep immediately, and a newer epoch (map2) wakes immediately without waiting SweepInterval.
        gate.SetResult(store);
        host.RequestObjectLedgers(map1);
        await WaitUntilAsync(() => host.ObjectSweeps == 1);
        var map2 = new LedgerMapValue(2, new LedgerId(1), 0, []);
        host.RequestObjectLedgers(map2);
        await WaitUntilAsync(() => host.ObjectSweeps == 2);

        // Transient failure backs off for 1 second instead of spinning in a listing storm.
        objects.FailList = true;
        var map3 = new LedgerMapValue(3, new LedgerId(1), 0, []);
        host.RequestObjectLedgers(map3);
        await WaitUntilAsync(() => objects.ListCalls >= 3);
        var callsAfterFailure = objects.ListCalls;
        await Task.Delay(TimeSpan.FromMilliseconds(40), TimeProvider.System, Token);
        Assert.Equal(callsAfterFailure, objects.ListCalls);

        // Recovering after backoff completes the sweep.
        objects.FailList = false;
        time.Advance(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => host.ObjectSweeps == 3);

        // CancelObjectLedgers cancels an in-flight listing while keeping the host loop alive for a later activation.
        objects.HoldNextList();
        var map4 = new LedgerMapValue(4, new LedgerId(1), 0, []);
        host.RequestObjectLedgers(map4);
        await objects.ListHeld.Task.WaitAsync(Patience, Token);
        host.CancelObjectLedgers();
        await WaitUntilAsync(() => objects.LastListCancelled);
        Assert.False(host.Ended);

        var map5 = new LedgerMapValue(5, new LedgerId(1), 0, []);
        host.RequestObjectLedgers(map5);
        await WaitUntilAsync(() => host.ObjectSweeps == 4);

        await host.StopAsync().WaitAsync(Patience, Token);
        Assert.True(host.Ended);

        // A refused object-ledger gate logs 6249 once and ends its loop; Ended becomes true only when both loops end.
        var refusedArchive = new TaskCompletionSource<ArchiveGateEvidence>(TaskCreationOptions.RunContinuationsAsynchronously);
        var refusedObjects = new TaskCompletionSource<ObjectLedgerStore>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var refusalLogs = new RecordingLoggerFactory();
        using var refusedHost = new SweepHost(
            Options,
            new LsmSweeps(refusedArchive.Task, new InMemoryObjectStore(), null, time, refusalLogs),
            time,
            refusalLogs,
            new ObjectLedgerSweeps(refusedObjects.Task, refusalLogs));
        await refusedHost.Start();
        refusedObjects.SetException(new ObjectLedgerStoreMismatchException("wrong store"));
        await WaitUntilAsync(() => refusalLogs.Logger.Events.Contains(6249));
        Assert.False(refusedHost.Ended);
        refusedArchive.SetCanceled(new CancellationToken(canceled: true));
        refusedHost.Request();
        await WaitUntilAsync(() => refusedHost.Ended);
        Assert.Single(refusalLogs.Logger.Events, static id => id == 6249);
    }

    /// <summary>
    /// orleans.md §5.14: <see cref="ObjectLedgerSweeps.SweepAsync"/> reclaims revoked ledgers under the
    /// <see cref="ObjectLedgerLayout.Append"/> layout by writing the <c>dropped</c> marker, replacing the newest extent
    /// with a <c>SEAL</c> tombstone, and deleting older extents.
    /// </summary>
    [Fact]
    public async Task ObjectLedgerSweepsReclaimAppendLayout()
    {
        var store = new InMemoryObjectStore();
        var prefixed = new PrefixedAppendableObjectStore(store, store, "t/");
        var options = new ObjectLedgerStoreOptions
        {
            Layout = ObjectLedgerLayout.Append,
            ExtentTargetBytes = 64,
            ExtentMaxAppends = 1,
        };
        var objectStore = ObjectLedgerStore.OverAppendable(prefixed, options, TimeProvider.System);
        var ledger = new LedgerId(1);
        await using (var storage = objectStore.Open(ledger, windowSize: 2))
        {
            using var p0 = ArcBuffer.Wrap(new byte[40]);
            using var p1 = ArcBuffer.Wrap(new byte[40]);
            Assert.Equal<LedgerWriteOutcome>(ReplicaWriteOutcome.Written.Instance, await storage.WriteAsync(Slot.Zero, p0, Slot.Zero, Token));
            Assert.Equal<LedgerWriteOutcome>(ReplicaWriteOutcome.Written.Instance, await storage.WriteAsync(new Slot(1), p1, new Slot(1), Token));
        }

        var sweeps = new ObjectLedgerSweeps(Task.FromResult(objectStore), NullLoggerFactory.Instance);
        var map = new LedgerMapValue(1, new LedgerId(2), 0, []);
        Assert.IsType<ArchiveSweepOutcome.Swept>((await sweeps.SweepAsync(map, Token)).Value);

        Assert.NotNull(await prefixed.GetPropertiesAsync(ObjectLedgerNames.Dropped(ledger), Token));
        var remainingExtents = new List<ListedObject>();
        await foreach (var entry in prefixed.ListAsync(ObjectLedgerNames.Extents(ledger), cancellationToken: Token))
        {
            remainingExtents.Add(entry);
        }

        var tombstone = Assert.Single(remainingExtents);
        Assert.Equal(ObjectLedgerExtentFormat.TombstoneLength, tombstone.Length);
    }

    /// <summary>
    /// Audit #1 and #6: <see cref="SweepHost"/> recovers when a periodic timer expiry races with
    /// <see cref="SweepHost.RequestObjectLedgers"/>, stops sweeping a cancelled map once
    /// <see cref="SweepHost.CancelObjectLedgers"/> clears it even after the gate has passed, and disposes cleanly while
    /// an object-ledger sweep is in flight.
    /// </summary>
    [Fact]
    public async Task ObjectLedgerSweepHostHandlesTimerWakeRaceCancelAfterPassAndDisposeDuringSweep()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var inner = new InMemoryObjectStore();
        var objects = new FlakyCountingObjectStore(inner);
        var store = ObjectLedgerStore.OverAppendable(
            new PrefixedAppendableObjectStore(objects, objects, "t/"),
            new ObjectLedgerStoreOptions(),
            time);
        var gate = new TaskCompletionSource<ObjectLedgerStore>(TaskCreationOptions.RunContinuationsAsynchronously);
        gate.SetResult(store);
        using var loggers = new RecordingLoggerFactory();
        var archiveGate = new TaskCompletionSource<ArchiveGateEvidence>(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new ControlOptions { SweepInterval = TimeSpan.FromSeconds(5) };

        using var host = new SweepHost(
            options,
            new LsmSweeps(archiveGate.Task, new InMemoryObjectStore(), null, time, loggers),
            time,
            loggers,
            new ObjectLedgerSweeps(gate.Task, loggers));
        await host.Start();

        // Sweep epoch 1, then trigger both the periodic timer and an immediate wake for epoch 2 concurrently (Audit #1).
        host.RequestObjectLedgers(new LedgerMapValue(1, new LedgerId(1), 0, []));
        await WaitUntilAsync(() => host.ObjectSweeps == 1);
        time.Advance(options.SweepInterval);
        host.RequestObjectLedgers(new LedgerMapValue(2, new LedgerId(1), 0, []));
        await WaitUntilAsync(() => host.ObjectSweeps >= 2);
        var sweepsAfterRace = host.ObjectSweeps;

        // A subsequent RequestObjectLedgers must still wake the loop immediately (no lost wakeup / stuck _objectPending).
        host.RequestObjectLedgers(new LedgerMapValue(3, new LedgerId(1), 0, []));
        await WaitUntilAsync(() => host.ObjectSweeps > sweepsAfterRace);

        // CancelObjectLedgers after the gate has passed must clear the map and NOT re-sweep it (Audit #6).
        objects.HoldNextList();
        host.RequestObjectLedgers(new LedgerMapValue(4, new LedgerId(1), 0, []));
        await objects.ListHeld.Task.WaitAsync(Patience, Token);
        var listsBeforeCancel = objects.ListCalls;
        host.CancelObjectLedgers();
        await WaitUntilAsync(() => objects.LastListCancelled);
        time.Advance(options.SweepInterval * 3);
        await Task.Delay(TimeSpan.FromMilliseconds(30), TimeProvider.System, Token);
        Assert.Equal(listsBeforeCancel, objects.ListCalls);

        // Disposing while a sweep is in flight must not throw ObjectDisposedException (Audit #6).
        objects.HoldNextList();
        host.RequestObjectLedgers(new LedgerMapValue(5, new LedgerId(1), 0, []));
        await objects.ListHeld.Task.WaitAsync(Patience, Token);
        host.Dispose();
        await WaitUntilAsync(() => objects.LastListCancelled);
    }

    /// <summary>
    /// orleans.md §5.6, §5.14: <see cref="ControllerPlacementDirector"/> requires the object-ledger gate to have passed
    /// whenever object-store ledgers are configured on the silo or selected by <see cref="ControlOptions.LedgerPlacement"/>.
    /// </summary>
    [Fact]
    public async Task TheDirectorChecksTheObjectLedgerGateWhenConfiguredOrSelected()
    {
        var local = SiloAddress.New(System.Net.IPAddress.Loopback, 11111, 1);
        var other = SiloAddress.New(System.Net.IPAddress.Loopback, 11112, 1);
        var context = new FixedPlacementContext(local, [local, other]);
        var archive = new SiloArchive.Lsm(new InMemoryObjectStore(), new ArchiveStorePrefix("t"));
        archive.Gate.Pass(new ArchiveGateEvidence.Lsm(archive.Objects));
        var target = new PlacementTarget(GrainId.Create("controller", "0"), [], default, 0);

        var inner = new InMemoryObjectStore();
        var store = ObjectLedgerStore.OverAppendable(new PrefixedAppendableObjectStore(inner, inner, "t/"), new ObjectLedgerStoreOptions(), TimeProvider.System);
        var pending = new SiloObjectLedgers(store, new ArchiveStorePrefix("t"));
        var refused = new SiloObjectLedgers(store, new ArchiveStorePrefix("t"));
        refused.Gate.Refuse(new ObjectLedgerStoreMismatchException("wrong store"));
        var passed = new SiloObjectLedgers(store, new ArchiveStorePrefix("t"));
        passed.Gate.Pass(store);

        // Pending or refused object-ledger gate deflects placement away from local even when archive gate passed.
        Assert.Equal(other, await new ControllerPlacementDirector(archive, pending).OnAddActivation(ControllerPlacement.Instance, target, context));
        Assert.Equal(other, await new ControllerPlacementDirector(archive, refused).OnAddActivation(ControllerPlacement.Instance, target, context));
        Assert.Equal(local, await new ControllerPlacementDirector(archive, passed).OnAddActivation(ControllerPlacement.Instance, target, context));

        // When LedgerPlacement.ObjectStore is selected, a silo with no object-ledger store configured deflects placement too.
        var objectControl = new ControlOptions { LedgerPlacement = LedgerPlacement.ObjectStore };
        Assert.Equal(other, await new ControllerPlacementDirector(archive, SiloObjectLedgers.CreateUnconfigured(), objectControl).OnAddActivation(ControllerPlacement.Instance, target, context));
        Assert.Equal(local, await new ControllerPlacementDirector(archive, passed, objectControl).OnAddActivation(ControllerPlacement.Instance, target, context));
    }

    /// <summary>A placement context of a fixed local silo and compatible silos.</summary>
    private sealed class FixedPlacementContext(SiloAddress local, SiloAddress[] compatible) : IPlacementContext
    {
        public SiloAddress LocalSilo => local;

        public SiloStatus LocalSiloStatus => SiloStatus.Active;

        public SiloAddress[] GetCompatibleSilos(PlacementTarget target) => compatible;

        public IReadOnlyDictionary<ushort, SiloAddress[]> GetCompatibleSilosWithVersions(PlacementTarget target) => new Dictionary<ushort, SiloAddress[]> { [0] = compatible };
    }

    private static ControlOptions Options => new() { SweepInterval = TimeSpan.FromMinutes(10) };

    /// <summary>
    /// Passes the archive gate of a memory archive-log store, as a silo does, with the identity its record names, or
    /// <paramref name="id"/>, which the gate refuses.
    /// </summary>
    private static async Task<ArchiveLogGate> PassGateAsync(IArchiveLogStore? logs = null, ArchiveStoreId? id = null)
    {
        var store = new LogArchiveStore(logs ?? new MemoryArchiveLogStore(), new InMemoryObjectStore(), ArchiveLogNames.Hexadecimal);
        var record = await store.StoreRecordAsync(ImmutableDictionary<string, string>.Empty, Token);
        return await ArchiveLogGate.PassAsync(store, new ArchiveStoreIdentity(ArchiveStoreKind.Logs, new ArchiveStorePrefix("t"), id ?? record.Id), ImmutableDictionary<string, string>.Empty, Token);
    }
    private static async Task WaitUntilAsync(Func<bool> done)
    {
        var deadline = TimeProvider.System.GetTimestamp();
        while (!done())
        {
            Assert.True(TimeProvider.System.GetElapsedTime(deadline) < Patience, "The condition never held.");
            await Task.Delay(TimeSpan.FromMilliseconds(5), TimeProvider.System, Token);
        }
    }

    /// <summary>An archive-log store whose generation listings each wait until cancelled, recording the cancellation.</summary>
    private sealed class HeldListingStore(IArchiveLogStore inner) : IArchiveLogStore
    {
        private int _cancelled;

        public TaskCompletionSource Listing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Cancelled => Volatile.Read(ref _cancelled) != 0;

        public ArchiveLogLayout Layout => inner.Layout;

        public bool AdmitsName(string name) => inner.AdmitsName(name);

        public ValueTask<ArchiveLogRead> ReadAsync(ArchiveLogName name, CancellationToken cancellationToken) => inner.ReadAsync(name, cancellationToken);

        public ValueTask<ArchiveLogProperties?> GetPropertiesAsync(ArchiveLogName name, CancellationToken cancellationToken) => inner.GetPropertiesAsync(name, cancellationToken);

        public ValueTask<ArchiveLogWriteOutcome> AppendAsync(ArchiveLogName name, ArchiveLogPosition from, IReadOnlyList<System.Buffers.ReadOnlySequence<byte>> run, CancellationToken cancellationToken) =>
            inner.AppendAsync(name, from, run, cancellationToken);

        public ValueTask<ArchiveLogWriteOutcome> OpenAsync(ArchiveLogName name, ArchiveLogPosition from, ArchiveLogOpener opener, System.Buffers.ReadOnlySequence<byte> content, CancellationToken cancellationToken) =>
            inner.OpenAsync(name, from, opener, content, cancellationToken);

        public async IAsyncEnumerable<ArchiveLogGenerationPage> ListGenerationsAsync(string? continuation, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _ = Listing.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, TimeProvider.System, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _ = Interlocked.Exchange(ref _cancelled, 1);
                throw;
            }

            yield break;
        }

        public ValueTask DeleteGenerationAsync(string log, ulong generation, CancellationToken cancellationToken) => inner.DeleteGenerationAsync(log, generation, cancellationToken);
    }

    /// <summary>An object store which counts its listings and deletions.</summary>
    private sealed class CountingObjectStore(IObjectStore inner) : IObjectStore
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public ValueTask<ObjectCreateOutcome> CreateAsync(ObjectName name, Orleans.Durable.Storage.Engine.Buffers.ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default) =>
            inner.CreateAsync(name, content, crc32c, cancellationToken);

        public ValueTask<Orleans.Durable.Storage.Engine.Buffers.ArcBuffer?> ReadAsync(ObjectName name, long offset, int length, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(name, offset, length, cancellationToken);

        public ValueTask<ObjectProperties?> GetPropertiesAsync(ObjectName name, CancellationToken cancellationToken = default) =>
            inner.GetPropertiesAsync(name, cancellationToken);

        public IAsyncEnumerable<ListedObject> ListAsync(string prefix, string? startAfter = null, CancellationToken cancellationToken = default)
        {
            _ = Interlocked.Increment(ref _calls);
            return inner.ListAsync(prefix, startAfter, cancellationToken);
        }

        public ValueTask DeleteAsync(ObjectName name, CancellationToken cancellationToken = default)
        {
            _ = Interlocked.Increment(ref _calls);
            return inner.DeleteAsync(name, cancellationToken);
        }

        public ValueTask<VersionedObject?> ReadVersionedAsync(ObjectName name, CancellationToken cancellationToken = default) =>
            inner.ReadVersionedAsync(name, cancellationToken);

        public ValueTask<ObjectReplaceOutcome> ReplaceAsync(ObjectName name, ObjectVersion? expectedVersion, Orleans.Durable.Storage.Engine.Buffers.ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default) =>
            inner.ReplaceAsync(name, expectedVersion, content, crc32c, cancellationToken);
    }

    /// <summary>An object store whose listings can count, fail, or block until cancelled for sweep-host tests.</summary>
    private sealed class FlakyCountingObjectStore(InMemoryObjectStore inner) : IObjectStore, IAppendableObjectStore
    {
        private int _listCalls;
        private int _holdNext;
        private int _lastCancelled;

        public int ListCalls => Volatile.Read(ref _listCalls);

        public bool FailList { get; set; }

        public bool LastListCancelled => Volatile.Read(ref _lastCancelled) != 0;

        public TaskCompletionSource ListHeld { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void HoldNextList()
        {
            ListHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _lastCancelled, 0);
            Volatile.Write(ref _holdNext, 1);
        }

        public ValueTask<ObjectCreateOutcome> CreateAsync(ObjectName name, Orleans.Durable.Storage.Engine.Buffers.ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default) =>
            inner.CreateAsync(name, content, crc32c, cancellationToken);

        public ValueTask<Orleans.Durable.Storage.Engine.Buffers.ArcBuffer?> ReadAsync(ObjectName name, long offset, int length, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(name, offset, length, cancellationToken);

        public ValueTask<ObjectProperties?> GetPropertiesAsync(ObjectName name, CancellationToken cancellationToken = default) =>
            inner.GetPropertiesAsync(name, cancellationToken);

        public async IAsyncEnumerable<ListedObject> ListAsync(string prefix, string? startAfter = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _ = Interlocked.Increment(ref _listCalls);
            if (FailList)
            {
                throw new IOException("Transient object-store listing failure.");
            }

            if (Interlocked.Exchange(ref _holdNext, 0) == 1)
            {
                _ = ListHeld.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, TimeProvider.System, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    Volatile.Write(ref _lastCancelled, 1);
                    throw;
                }

                yield break;
            }

            await foreach (var item in inner.ListAsync(prefix, startAfter, cancellationToken).ConfigureAwait(false))
            {
                yield return item;
            }
        }

        public ValueTask DeleteAsync(ObjectName name, CancellationToken cancellationToken = default) =>
            inner.DeleteAsync(name, cancellationToken);

        public ValueTask<VersionedObject?> ReadVersionedAsync(ObjectName name, CancellationToken cancellationToken = default) =>
            inner.ReadVersionedAsync(name, cancellationToken);

        public ValueTask<ObjectReplaceOutcome> ReplaceAsync(ObjectName name, ObjectVersion? expectedVersion, Orleans.Durable.Storage.Engine.Buffers.ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default) =>
            inner.ReplaceAsync(name, expectedVersion, content, crc32c, cancellationToken);

        public ValueTask<ObjectCreateOutcome> CreateAppendableAsync(ObjectName name, CancellationToken cancellationToken = default) =>
            inner.CreateAppendableAsync(name, cancellationToken);

        public ValueTask<ObjectAppendOutcome> AppendAsync(ObjectName name, long expectedOffset, Orleans.Durable.Storage.Engine.Buffers.ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default) =>
            inner.AppendAsync(name, expectedOffset, content, crc32c, cancellationToken);

        public ValueTask<ObjectSealOutcome> SealAsync(ObjectName name, CancellationToken cancellationToken = default) =>
            inner.SealAsync(name, cancellationToken);

        public ValueTask ReplaceWithTombstoneAsync(ObjectName name, Orleans.Durable.Storage.Engine.Buffers.ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default) =>
            inner.ReplaceWithTombstoneAsync(name, content, crc32c, cancellationToken);
    }

    /// <summary>An archive-log store which counts its generation listings and deletions, and records each listing's continuation.</summary>
    private sealed class CountingArchiveLogStore(IArchiveLogStore inner) : IArchiveLogStore
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public System.Collections.Concurrent.ConcurrentQueue<string?> Continuations { get; } = new();

        public ArchiveLogLayout Layout => inner.Layout;

        public bool AdmitsName(string name) => inner.AdmitsName(name);

        public ValueTask<ArchiveLogRead> ReadAsync(ArchiveLogName name, CancellationToken cancellationToken) => inner.ReadAsync(name, cancellationToken);

        public ValueTask<ArchiveLogProperties?> GetPropertiesAsync(ArchiveLogName name, CancellationToken cancellationToken) => inner.GetPropertiesAsync(name, cancellationToken);

        public ValueTask<ArchiveLogWriteOutcome> AppendAsync(ArchiveLogName name, ArchiveLogPosition from, IReadOnlyList<System.Buffers.ReadOnlySequence<byte>> run, CancellationToken cancellationToken) =>
            inner.AppendAsync(name, from, run, cancellationToken);

        public ValueTask<ArchiveLogWriteOutcome> OpenAsync(ArchiveLogName name, ArchiveLogPosition from, ArchiveLogOpener opener, System.Buffers.ReadOnlySequence<byte> content, CancellationToken cancellationToken) =>
            inner.OpenAsync(name, from, opener, content, cancellationToken);

        public IAsyncEnumerable<ArchiveLogGenerationPage> ListGenerationsAsync(string? continuation, CancellationToken cancellationToken)
        {
            _ = Interlocked.Increment(ref _calls);
            Continuations.Enqueue(continuation);
            return inner.ListGenerationsAsync(continuation, cancellationToken);
        }

        public ValueTask DeleteGenerationAsync(string log, ulong generation, CancellationToken cancellationToken)
        {
            _ = Interlocked.Increment(ref _calls);
            return inner.DeleteGenerationAsync(log, generation, cancellationToken);
        }
    }

    /// <summary>A MetaStore whose first register request fails.</summary>
    private sealed class FailingOnceMetaStore(IMetaStore inner) : IMetaStore
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public ValueTask<IConditionalRegister> GetRegisterAsync(Guid cluster, string name, CancellationToken cancellationToken = default) =>
            Interlocked.Increment(ref _calls) == 1
                ? ValueTask.FromException<IConditionalRegister>(new IOException("The MetaStore did not answer."))
                : inner.GetRegisterAsync(cluster, name, cancellationToken);
    }

    /// <summary>A logger factory whose every logger is one recording logger.</summary>
    private sealed class RecordingLoggerFactory : ILoggerFactory
    {
        public RecordingLogger Logger { get; } = new();

        public ILogger CreateLogger(string categoryName) => Logger;

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }
    }
}
