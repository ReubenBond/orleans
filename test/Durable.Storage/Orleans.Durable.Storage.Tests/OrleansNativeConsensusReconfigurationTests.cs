using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Consensus.CasPaxos.Messages;
using Orleans.Consensus.CasPaxos.Model;
using Orleans.Consensus.CasPaxos.Server;
using Orleans.Durable.Storage.CasPaxos.Hosting;
using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.MetaStore;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Durable.Storage.Engine.ObjectStore.Memory;
using Orleans.Durable.Storage.Hosting;
using Orleans.Durable.Storage.Testing;
using Orleans.Durable.Storage.Tests.LazyFs;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.TestingHost;

namespace Orleans.Durable.Storage.Tests;

/// <summary>
/// End-to-end multi-silo operator-driven Fast CASPaxos voter reconfiguration (caspaxos.md, metastore.md §8.1):
/// replacing a lost voter, adding a voter, removing a voter, stale-configuration silo fail-safe and recovery, and
/// crash/resume mid-reconfiguration while Durable Storage journals are actively written and read over real Orleans RPC
/// and SQLite.
/// </summary>
[Trait("Category", "Integration")]
[NoPageLeakCheck("Stopping and restarting in-process silos does not return rented pages; Orleans.Durable.Storage.CasPaxos.Tests checks transport leaks.")]
public sealed class OrleansNativeConsensusReconfigurationTests
{
    private const string TestKey = "DurableStorage:NativeConsensusReconfig:Test";
    private const int BootstrapVoters = 3;

    private static readonly ConcurrentDictionary<string, ClusterState> States = new(StringComparer.Ordinal);
    private static readonly TimeSpan Patience = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Replaces a permanently lost voter with a fresh voter identity on a new silo while Durable Storage journals run,
    /// verifying that: the retired voter restarted on a fresh disk is still refused, a stale-configuration silo detects
    /// fencing and recovers to configuration 2, a restarting surviving silo with bootstrap options refreshes cleanly,
    /// and all pre- and post-reconfiguration journal writes remain durable and readable.
    /// </summary>
    [Fact(Timeout = 600_000)]
    public async Task ReplaceLostVoterWhileJournalsRunAndStaleSiloRecovers()
    {
        var token = TestContext.Current.CancellationToken;
        using var logs = new TestOutputLoggerProvider(LogLevel.Information);
        using var state = new ClusterState(logs);
        States[state.Id] = state;
        try
        {
            var builder = new TestClusterBuilder(BootstrapVoters);
            builder.Options.UseTestClusterMembership = false;
            builder.Options.InitializeClientOnDeploy = false;
            builder.Properties[TestKey] = state.Id;
            _ = builder.AddSiloBuilderConfigurator<SiloConfigurator>();
            await using var cluster = builder.Build();
            await cluster.DeployAsync(token).WaitAsync(Patience, token);

            var silo0 = cluster.Silos[0];
            var silo1 = cluster.Silos[1];
            var silo2 = cluster.Silos[2];
            var basePort = cluster.Options.BaseSiloPort;

            var grainA = Grains(silo0).GetGrain<IPlacedJournaledChunksGrain>("reconfig-a");
            var grainB = Grains(silo1).GetGrain<IPlacedJournaledChunksGrain>("reconfig-b");
            _ = await EventuallyAsync(() => grainA.AppendAsync([10]), "initial write to grainA", token);
            _ = await grainA.AppendAsync([20]);
            _ = await EventuallyAsync(() => grainB.AppendAsync([100]), "initial write to grainB", token);

            // Permanently lose voter-2: stop its silo and wipe its acceptor database.
            await cluster.StopSiloAsync(silo2, token).WaitAsync(Patience, token);
            foreach (var file in Directory.EnumerateFiles(state.Root, "voter-2.db*"))
            {
                File.Delete(file);
            }

            // Start a replacement silo on a new port hosting fresh voter-3 at joinConfiguration = 2.
            state.NextJoiningVoter = new JoiningVoterSpec(
                "voter-3",
                JoinConfiguration: 2,
                [
                    new CasPaxosVoterPeerOptions { Id = "voter-0", Endpoint = string.Create(CultureInfo.InvariantCulture, $"127.0.0.1:{basePort}") },
                    new CasPaxosVoterPeerOptions { Id = "voter-1", Endpoint = string.Create(CultureInfo.InvariantCulture, $"127.0.0.1:{basePort + 1}") },
                ]);

            var silo3 = await cluster.StartAdditionalSiloAsync(startAdditionalSiloOnNewPort: true).WaitAsync(Patience, token);
            var replacementEndpoint = silo3.SiloAddress.Endpoint.ToString();
            Assert.True(Host(silo3).Voter!.IsServing);
            Assert.Equal(2, Host(silo3).Voter!.JoinConfiguration);

            // Verify VR-I11: the joining voter's floor (2,0,0) already fences c = 1 before reconfiguration runs.
            var preJoinReject = await Host(silo3).Voter!.PrepareAsync("probe", new PrepareRequest(Ballot.InitialClassic(1, 99)), token);
            Assert.False(preJoinReject.IsPromised);
            Assert.Equal(Ballot.Floor(2), preJoinReject.ConflictingBallot);

            // Keep journal writes active while driving ReplaceVoterAsync from silo0.
            var backgroundAppends = 0;
            using var writerCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            var writerTask = Task.Run(async () =>
            {
                byte nextChunk = 30;
                while (!writerCts.IsCancellationRequested)
                {
                    try
                    {
                        _ = await grainA.AppendAsync([nextChunk]);
                        nextChunk = (byte)((nextChunk + 1) % 250);
                        _ = Interlocked.Increment(ref backgroundAppends);
                        await Task.Delay(TimeSpan.FromMilliseconds(50), TimeProvider.System, writerCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(100), TimeProvider.System, writerCts.Token);
                    }
                }
            }, CancellationToken.None);

            var reconfig = Services(silo0).GetRequiredService<CasPaxosReconfigurationService>();
            Assert.Equal(1, Host(silo1).Registers.ActiveConfiguration);
            var active = await reconfig.ReplaceVoterAsync("voter-2", new VoterDescriptor("voter-3", replacementEndpoint), token).WaitAsync(Patience, token);
            Assert.Equal(2, active.Number);
            Assert.Equal(VoterConfigurationState.Active, active.State);
            Assert.Equal(["voter-0", "voter-1", "voter-3"], active.CanonicalVoters.Select(static v => v.Id));

            await writerCts.CancelAsync();
            await writerTask.WaitAsync(Patience, token);

            // Stale silo1 (which started on c = 1 and never drove reconfiguration) detects fencing and refreshes transparently to c = 2.
            var probeRegister = await Services(silo1).GetRequiredService<IMetaStore>().GetRegisterAsync(state.Cluster, "reconfig-probe", token);
            var writeOutcome = await probeRegister.WriteAsync(null, new ReadOnlySequence<byte>("after-replace"u8.ToArray()), token);
            Assert.True(writeOutcome is RegisterWriteOutcome.Applied);
            Assert.Equal(2, Host(silo1).Registers.ActiveConfiguration);

            // Verify journals written before and during reconfiguration remain durable and readable, and new writes succeed.
            var readA = await EventuallyAsync(grainA.ReadAsync, "grainA read after voter replacement", token);
            Assert.Equal(10, readA[0]);
            Assert.Equal(20, readA[1]);
            Assert.Equal(2 + Volatile.Read(ref backgroundAppends), readA.Length);

            var grainBFromSilo3 = Grains(silo3).GetGrain<IPlacedJournaledChunksGrain>("reconfig-b");
            Assert.Equal([100], await EventuallyAsync(grainBFromSilo3.ReadAsync, "grainB read from replacement silo", token));
            _ = await grainBFromSilo3.AppendAsync([200]);
            Assert.Equal([100, 200], await grainBFromSilo3.ReadAsync());

            // A surviving voter silo restarted with its original bootstrap V(1) config (and proposer.db already at c = 2)
            // rejoins cleanly and refreshes to c = 2.
            var restarted0 = await cluster.RestartSiloAsync(silo0).WaitAsync(Patience, token);
            Assert.NotNull(restarted0);
            Assert.True(Host(restarted0).Voter!.IsServing);
            var restartedProbe = await Services(restarted0).GetRequiredService<IMetaStore>().GetRegisterAsync(state.Cluster, "reconfig-probe", token);
            using var entry = Assert.IsType<MetaStoreEntry>(await restartedProbe.ReadAsync(token));
            Assert.Equal("after-replace"u8.ToArray(), entry.Value.ToArray());
            Assert.Equal(2, Host(restarted0).Registers.ActiveConfiguration);

            // Even after replacement, reviving retired voter-2 on a fresh disk under its old identity is refused
            // by surviving voters' admission records.
            var lostRefusal = await Assert.ThrowsAnyAsync<Exception>(() => cluster.RestartSiloAsync(silo2).WaitAsync(Patience, token));
            Assert.Contains("state was lost", Flatten(lostRefusal), StringComparison.Ordinal);
        }
        finally
        {
            _ = States.TryRemove(state.Id, out _);
        }
    }

    /// <summary>
    /// Adds a 4th voter with an injected mid-reconfiguration failure (after Choose and Fence, before Install/Activate),
    /// verifying that a stale silo fails safe during the Pending pause, ResumeAsync completes the 4-voter transition,
    /// and RemoveVoterAsync subsequently shrinks the cluster back to 3 voters while Durable Storage journals run.
    /// </summary>
    [Fact(Timeout = 600_000)]
    public async Task AddAndRemoveVoterWithMidReconfigurationFailureAndResumeWhileJournalsRun()
    {
        var token = TestContext.Current.CancellationToken;
        using var logs = new TestOutputLoggerProvider(LogLevel.Information);
        using var state = new ClusterState(logs);
        States[state.Id] = state;
        try
        {
            var builder = new TestClusterBuilder(BootstrapVoters);
            builder.Options.UseTestClusterMembership = false;
            builder.Options.InitializeClientOnDeploy = false;
            builder.Properties[TestKey] = state.Id;
            _ = builder.AddSiloBuilderConfigurator<SiloConfigurator>();
            await using var cluster = builder.Build();
            await cluster.DeployAsync(token).WaitAsync(Patience, token);

            var silo0 = cluster.Silos[0];
            var silo1 = cluster.Silos[1];
            var silo2 = cluster.Silos[2];
            var basePort = cluster.Options.BaseSiloPort;

            var grain1 = Grains(silo0).GetGrain<IPlacedJournaledChunksGrain>("add-remove-1");
            var grain2 = Grains(silo1).GetGrain<IPlacedJournaledChunksGrain>("add-remove-2");
            _ = await EventuallyAsync(() => grain1.AppendAsync([1, 2]), "initial write to grain1", token);
            _ = await EventuallyAsync(() => grain2.AppendAsync([7, 8]), "initial write to grain2", token);

            // Start 4th silo hosting joining voter-3 at joinConfiguration = 2.
            state.NextJoiningVoter = new JoiningVoterSpec(
                "voter-3",
                JoinConfiguration: 2,
                [
                    new CasPaxosVoterPeerOptions { Id = "voter-0", Endpoint = string.Create(CultureInfo.InvariantCulture, $"127.0.0.1:{basePort}") },
                    new CasPaxosVoterPeerOptions { Id = "voter-1", Endpoint = string.Create(CultureInfo.InvariantCulture, $"127.0.0.1:{basePort + 1}") },
                    new CasPaxosVoterPeerOptions { Id = "voter-2", Endpoint = string.Create(CultureInfo.InvariantCulture, $"127.0.0.1:{basePort + 2}") },
                ]);
            var silo3 = await cluster.StartAdditionalSiloAsync(startAdditionalSiloOnNewPort: true).WaitAsync(Patience, token);
            var voter3Endpoint = silo3.SiloAddress.Endpoint.ToString();
            Assert.True(Host(silo3).Voter!.IsServing);

            // Inject a mid-reconfiguration crash after Step 1 (Choose) and Step 2 (Fence c = 1), before Step 3/4 (Install/Activate).
            var target4 = new VoterDescriptor[]
            {
                new("voter-0", string.Create(CultureInfo.InvariantCulture, $"127.0.0.1:{basePort}")),
                new("voter-1", string.Create(CultureInfo.InvariantCulture, $"127.0.0.1:{basePort + 1}")),
                new("voter-2", string.Create(CultureInfo.InvariantCulture, $"127.0.0.1:{basePort + 2}")),
                new("voter-3", voter3Endpoint),
            };
            var pending2 = new VoterConfiguration(2, [.. target4], VoterConfigurationState.Pending);
            var chooseResult = await Host(silo0).CompareExchangeAsync(
                VoterConfiguration.RegisterKey, null, VoterConfigurationEncoding.Encode(pending2), token);
            Assert.Equal(CompareExchangeStatus.Applied, chooseResult.Status);

            // Fence a classic quorum of V(1) (voter-0 and voter-1) to floor (2,0,0), simulating crash before Install.
            Assert.Equal(Ballot.Floor(2), await Host(silo0).Voter!.SetFloorAsync(Ballot.Floor(2), token));
            Assert.Equal(Ballot.Floor(2), await Host(silo1).Voter!.SetFloorAsync(Ballot.Floor(2), token));

            var reconfig0 = Services(silo0).GetRequiredService<CasPaxosReconfigurationService>();
            var observedPending = await reconfig0.GetCurrentConfigurationAsync(token);
            Assert.Equal(2, observedPending.Number);
            Assert.Equal(VoterConfigurationState.Pending, observedPending.State);

            // While c = 2 is Pending, stale silo1 fails safe on MetaStore writes (neither c = 1 nor Pending c = 2 commits).
            using (var shortWait = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                shortWait.CancelAfter(TimeSpan.FromMilliseconds(400));
                var staleRegister = await Services(silo1).GetRequiredService<IMetaStore>().GetRegisterAsync(state.Cluster, "blocked-during-pending", token);
                _ = await Assert.ThrowsAnyAsync<Exception>(() => staleRegister.WriteAsync(null, new ReadOnlySequence<byte>("must-not-commit"u8.ToArray()), shortWait.Token).AsTask());
            }

            Assert.Equal(1, Host(silo1).Registers.ActiveConfiguration);

            // Data-plane reads of already-loaded journals continue unaffected during the metadata pause.
            Assert.Equal([1, 2], await grain1.ReadAsync());

            // Resume cleanly via CasPaxosReconfigurationService.ResumeAsync -> completes Install & Activate for 4 voters.
            var active2 = await reconfig0.ResumeAsync(token).WaitAsync(Patience, token);
            Assert.Equal(2, active2.Number);
            Assert.Equal(VoterConfigurationState.Active, active2.State);
            Assert.Equal(["voter-0", "voter-1", "voter-2", "voter-3"], active2.CanonicalVoters.Select(static v => v.Id));

            // Stale silo1 recovers to c = 2 and confirms the blocked write did not apply.
            var checkRegister = await Services(silo1).GetRequiredService<IMetaStore>().GetRegisterAsync(state.Cluster, "blocked-during-pending", token);
            Assert.Null(await checkRegister.ReadAsync(token));
            Assert.Equal(2, Host(silo1).Registers.ActiveConfiguration);

            _ = await grain1.AppendAsync([3]);
            _ = await grain2.AppendAsync([9]);
            Assert.Equal([1, 2, 3], await grain1.ReadAsync());
            Assert.Equal([7, 8, 9], await grain2.ReadAsync());

            // Now remove voter-2 (4 -> 3 voters, c = 2 -> 3) via CasPaxosReconfigurationService.RemoveVoterAsync.
            var active3 = await reconfig0.RemoveVoterAsync("voter-2", token).WaitAsync(Patience, token);
            Assert.Equal(3, active3.Number);
            Assert.Equal(VoterConfigurationState.Active, active3.State);
            Assert.Equal(["voter-0", "voter-1", "voter-3"], active3.CanonicalVoters.Select(static v => v.Id));

            // Decommission retired voter-2 completely and verify journals remain readable and writable on {voter-0, voter-1, voter-3}.
            await cluster.StopSiloAsync(silo2, token).WaitAsync(Patience, token);
            var grain3 = Grains(silo3).GetGrain<IPlacedJournaledChunksGrain>("add-remove-3");
            _ = await EventuallyAsync(() => grain3.AppendAsync([42]), "write to new grain after removing voter-2", token);
            Assert.Equal([42], await grain3.ReadAsync());
            Assert.Equal([1, 2, 3], await EventuallyAsync(() => Grains(silo0).GetGrain<IPlacedJournaledChunksGrain>("add-remove-1").ReadAsync(), "read grain1 after removing voter-2", token));
            Assert.Equal([7, 8, 9], await EventuallyAsync(() => Grains(silo1).GetGrain<IPlacedJournaledChunksGrain>("add-remove-2").ReadAsync(), "read grain2 after removing voter-2", token));

            var postRemoveWrite = await Services(silo3).GetRequiredService<IMetaStore>().GetRegisterAsync(state.Cluster, "after-remove", token);
            var postRemoveOutcome = await postRemoveWrite.WriteAsync(null, new ReadOnlySequence<byte>("c3"u8.ToArray()), token);
            Assert.True(postRemoveOutcome is RegisterWriteOutcome.Applied);
            Assert.Equal(3, Host(silo3).Registers.ActiveConfiguration);
        }
        finally
        {
            _ = States.TryRemove(state.Id, out _);
        }
    }

    private static IServiceProvider Services(SiloHandle silo) => ((InProcessSiloHandle)silo).SiloHost.Services;

    private static CasPaxosConsensusHost Host(SiloHandle silo) => Services(silo).GetRequiredService<CasPaxosConsensusHost>();

    private static IGrainFactory Grains(SiloHandle silo) => Services(silo).GetRequiredService<IGrainFactory>();

    private static string Flatten(Exception failure)
    {
        var messages = new List<string>();
        for (var current = failure; current is not null; current = current.InnerException)
        {
            messages.Add(current.Message);
            if (current is AggregateException aggregate)
            {
                messages.AddRange(aggregate.InnerExceptions.Select(Flatten));
            }
        }

        return string.Join(" | ", messages);
    }

    private static async Task<T> EventuallyAsync<T>(Func<Task<T>> call, string what, CancellationToken cancellationToken)
    {
        var deadline = TimeProvider.System.GetTimestamp() + (long)(Patience.TotalSeconds * TimeProvider.System.TimestampFrequency);
        while (true)
        {
            try
            {
                return await call().WaitAsync(Patience, cancellationToken);
            }
            catch (Exception failure) when (failure is not OperationCanceledException && TimeProvider.System.GetTimestamp() < deadline)
            {
                TestContext.Current.SendDiagnosticMessage($"Waiting for {what}: {failure.GetType().Name}: {failure.Message}");
                await Task.Delay(TimeSpan.FromSeconds(1), TimeProvider.System, cancellationToken);
            }
        }
    }

    private sealed record JoiningVoterSpec(string Id, long JoinConfiguration, IReadOnlyList<CasPaxosVoterPeerOptions> AdmissionPeers);

    private sealed class ClusterState(TestOutputLoggerProvider logs) : IDisposable
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");

        public Guid Cluster { get; } = Guid.NewGuid();

        public TestOutputLoggerProvider Logs { get; } = logs;

        public InMemoryMetaStore Membership { get; } = new();

        public InMemoryObjectStore Objects { get; } = new();

        public JoiningVoterSpec? NextJoiningVoter { get; set; }

        private int _nextOrdinal = BootstrapVoters - 1;
        private readonly ConcurrentDictionary<string, int> _ordinals = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<int, JoiningVoterSpec> _joiningByPort = new();

        public int AllocateFreshOrdinal() => Interlocked.Increment(ref _nextOrdinal);

        public int OrdinalFor(string siloName, int portDelta) =>
            _ordinals.GetOrAdd(siloName, _ => portDelta is >= 0 and < BootstrapVoters ? portDelta : Interlocked.Increment(ref _nextOrdinal));

        public JoiningVoterSpec? TryTakeJoiningVoter(int siloPort)
        {
            if (_joiningByPort.TryGetValue(siloPort, out var existing))
            {
                return existing;
            }

            if (NextJoiningVoter is { } pending)
            {
                NextJoiningVoter = null;
                _joiningByPort[siloPort] = pending;
                return pending;
            }

            return null;
        }

        public string Root { get; } = Directory.CreateTempSubdirectory("native-consensus-reconfig-").FullName;

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                // A stopped or killed in-process silo may keep handles briefly; ignore temp cleanup errors.
            }
        }
    }

    private sealed class SiloConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder)
        {
            var state = States[siloBuilder.Configuration[TestKey]!];
            var basePort = int.Parse(siloBuilder.Configuration["BaseSiloPort"]!, CultureInfo.InvariantCulture);
            var siloPort = int.Parse(siloBuilder.Configuration["Orleans:Endpoints:SiloPort"]!, CultureInfo.InvariantCulture);
            var name = siloBuilder.Configuration["Orleans:Name"]!;
            var joining = state.TryTakeJoiningVoter(siloPort);
            var ordinal = joining is not null ? state.AllocateFreshOrdinal() : state.OrdinalFor(name, siloPort - basePort);
            _ = siloBuilder.Services.AddSingleton<IMembershipTable>(new FileSystemMembershipTable(state.Membership, state.Cluster));
            _ = siloBuilder.Services.AddLogging(logging => logging.AddProvider(state.Logs).AddFilter("Orleans", LogLevel.Warning).AddFilter("Orleans.Durable.Storage", LogLevel.Information));
            _ = siloBuilder.AddCasPaxosMetaStore(options => options.Configure(consensus =>
            {
                for (var voter = 0; voter < BootstrapVoters; voter++)
                {
                    consensus.Voters.Add(new CasPaxosVoterOptions
                    {
                        Id = string.Create(CultureInfo.InvariantCulture, $"voter-{voter}"),
                        Endpoint = string.Create(CultureInfo.InvariantCulture, $"127.0.0.1:{basePort + voter}"),
                        Database = Path.Combine(state.Root, string.Create(CultureInfo.InvariantCulture, $"voter-{voter}.db")),
                    });
                }

                if (joining is not null)
                {
                    var joinOptions = new CasPaxosJoiningVoterOptions
                    {
                        Id = joining.Id,
                        Database = Path.Combine(state.Root, $"{joining.Id}.db"),
                        JoinConfiguration = joining.JoinConfiguration,
                    };
                    foreach (var peer in joining.AdmissionPeers)
                    {
                        joinOptions.AdmissionPeers.Add(peer);
                    }

                    consensus.JoiningVoter = joinOptions;
                }

                consensus.ProposerId = ordinal + 1;
                consensus.ProposerDatabase = Path.Combine(state.Root, $"proposer-{ordinal}.db");
            }));

            _ = siloBuilder.Services.AddSingleton<IObjectStore>(state.Objects);
            _ = siloBuilder.AddDurableJournalStorageWith(new DurableStorageSiloOptions
            {
                Node = new DurableStorageOptions
                {
                    Cluster = state.Cluster,
                    Root = Directory.CreateDirectory(Path.Combine(state.Root, string.Create(CultureInfo.InvariantCulture, $"node-{ordinal}"))).FullName,
                    Ordinal = ordinal,
                    WalFileSize = 1 << 20,
                    PreallocateWalFiles = false,
                    ArchiveStore = ArchiveStoreKind.Lsm,
                },
            });
            _ = siloBuilder.Services.Configure<Durable.Journaling.Json.JsonJournalOptions>(static options => options.AddTypeInfoResolver(JournaledChunksJsonContext.Default));

            _ = siloBuilder.Configure<ClusterMembershipOptions>(static membership =>
            {
                membership.ProbeTimeout = TimeSpan.FromSeconds(1);
                membership.NumMissedProbesLimit = 2;
                membership.NumVotesForDeathDeclaration = 1;
            });
        }
    }
}
