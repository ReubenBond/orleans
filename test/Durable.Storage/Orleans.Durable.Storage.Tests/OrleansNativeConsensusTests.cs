using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
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
/// Durable storage over the native consensus (caspaxos.md, orleans.md §2): each of three silos hosts a consensus voter, a
/// proposer, and a durable storage node whose metadata registers the voters serve, and a journaled grain runs over them
/// through real Orleans RPC, system targets, serialization, and SQLite. Orleans membership lies in a register of a
/// separate in-memory MetaStore, never one consensus serves, so the silos start together, as a deployment's do: each
/// voter's admission, and each node's registration, needs the others. Each silo keeps its port across restarts.
/// </summary>
[Trait("Category", "Integration")]
[NoPageLeakCheck("A killed in-process silo never returns the pages it rented; the consensus adapter's tests check its transport for leaks.")]
public sealed class OrleansNativeConsensusTests
{
    private const string TestKey = "DurableStorage:NativeConsensus:Test";
    private const int Voters = 3;

    private static readonly ConcurrentDictionary<string, ClusterState> States = new(StringComparer.Ordinal);

    private static readonly TimeSpan Patience = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The voters admit one another and serve; a journaled grain writes through durable storage over them; killing a
    /// voter's silo leaves a quorum, and the grain recovers its state on another silo; the killed silo restarts at its
    /// endpoint with a new generation as the same voter, already admitted; and a voter's silo restarted on a lost
    /// database is refused while the others keep serving.
    /// </summary>
    [Fact(Timeout = 600_000)]
    public async Task JournaledGrainsRunOverNativeConsensusThroughVoterLossRestartAndLostState()
    {
        var token = TestContext.Current.CancellationToken;
        using var logs = new TestOutputLoggerProvider(LogLevel.Information);
        using var state = new ClusterState(logs);
        States[state.Id] = state;
        try
        {
            var builder = new TestClusterBuilder(Voters);
            builder.Options.UseTestClusterMembership = false;
            builder.Options.InitializeClientOnDeploy = false;
            builder.Properties[TestKey] = state.Id;
            _ = builder.AddSiloBuilderConfigurator<SiloConfigurator>();
            await using var cluster = builder.Build();
            await cluster.DeployAsync(token).WaitAsync(Patience, token);

            var silos = cluster.Silos.ToList();
            var incarnations = silos.ToDictionary(static silo => silo.Name, silo => Host(silo).Voter!.Incarnation, StringComparer.Ordinal);
            Assert.Equal(Voters, incarnations.Count);
            Assert.All(silos, silo => Assert.True(Host(silo).Voter!.IsServing));

            var grain = Grains(silos[0]).GetGrain<IPlacedJournaledChunksGrain>("chunks");
            _ = await EventuallyAsync(() => grain.AppendAsync([1]), "the grain's first write", token);
            _ = await grain.AppendAsync([2]);
            var hostAddress = await grain.SiloAsync();
            var host = silos.Single(silo => silo.SiloAddress.Equals(hostAddress));

            // Losing one voter leaves two of three: the grain recovers on another silo, whose durable storage reads and
            // writes its metadata registers through the remaining voters.
            await cluster.KillSiloAsync(host, token).WaitAsync(Patience, token);
            grain = Grains(silos.First(silo => silo != host)).GetGrain<IPlacedJournaledChunksGrain>("chunks");
            Assert.Equal([1, 2], await EventuallyAsync(grain.ReadAsync, "the grain to recover on another silo", token));
            Assert.NotEqual(hostAddress, await grain.SiloAsync());
            _ = await grain.AppendAsync([3]);

            // The killed silo restarts at its endpoint with a new generation, serving the same voter without admission.
            var restarted = await cluster.RestartSiloAsync(host).WaitAsync(Patience, token);
            Assert.NotNull(restarted);
            Assert.Equal(host.SiloAddress.Endpoint, restarted.SiloAddress.Endpoint);
            Assert.NotEqual(host.SiloAddress.Generation, restarted.SiloAddress.Generation);
            Assert.Equal(incarnations[host.Name], Host(restarted).Voter!.Incarnation);
            Assert.True(Host(restarted).Voter!.IsServing);
            Assert.Equal([1, 2, 3], await EventuallyAsync(grain.ReadAsync, "the grain to answer once the killed silo restarted", token));

            // A voter's silo restarted on a lost database is refused: the other voters admitted its first incarnation.
            var serving = await grain.SiloAsync();
            var lost = cluster.Silos.First(silo => silo.Name != restarted.Name && !silo.SiloAddress.Equals(serving));
            await cluster.StopSiloAsync(lost, token).WaitAsync(Patience, token);
            foreach (var file in Directory.EnumerateFiles(state.Root, string.Create(CultureInfo.InvariantCulture, $"voter-{lost.SiloAddress.Endpoint.Port - cluster.Options.BaseSiloPort}.db*")))
            {
                File.Delete(file);
            }

            var refusal = await Assert.ThrowsAnyAsync<Exception>(() => cluster.RestartSiloAsync(lost).WaitAsync(Patience, token));
            Assert.Contains("state was lost", Flatten(refusal), StringComparison.Ordinal);
            var remaining = Grains(restarted).GetGrain<IPlacedJournaledChunksGrain>("chunks");
            Assert.Equal([1, 2, 3], await EventuallyAsync(remaining.ReadAsync, "the grain to answer from the remaining voters", token));
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

    /// <summary>One test's shared stores: Orleans membership's MetaStore, the LSM's objects, and the directory of its silos' disks.</summary>
    private sealed class ClusterState(TestOutputLoggerProvider logs) : IDisposable
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");

        public Guid Cluster { get; } = Guid.NewGuid();

        public TestOutputLoggerProvider Logs { get; } = logs;

        public InMemoryMetaStore Membership { get; } = new();

        public InMemoryObjectStore Objects { get; } = new();

        public string Root { get; } = Directory.CreateTempSubdirectory("native-consensus-").FullName;

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                // A killed in-process silo keeps its files open until the process exits; the temporary directory remains.
            }
        }
    }

    /// <summary>
    /// Configures each silo as a voter, a proposer, and a durable storage node over the LSM. The silo of instance
    /// <c>i</c> listens on the cluster's base port plus <c>i</c>, so every silo derives every voter's endpoint.
    /// </summary>
    private sealed class SiloConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder)
        {
            var state = States[siloBuilder.Configuration[TestKey]!];
            var basePort = int.Parse(siloBuilder.Configuration["BaseSiloPort"]!, CultureInfo.InvariantCulture);
            var ordinal = int.Parse(siloBuilder.Configuration["Orleans:Endpoints:SiloPort"]!, CultureInfo.InvariantCulture) - basePort;
            var name = siloBuilder.Configuration["Orleans:Name"]!;
            _ = siloBuilder.Services.AddSingleton<IMembershipTable>(new FileSystemMembershipTable(state.Membership, state.Cluster));
            _ = siloBuilder.Services.AddLogging(logging => logging.AddProvider(state.Logs).AddFilter("Orleans", LogLevel.Warning).AddFilter("Orleans.Durable.Storage", LogLevel.Information));
            _ = siloBuilder.AddCasPaxosMetaStore(options => options.Configure(consensus =>
            {
                for (var voter = 0; voter < Voters; voter++)
                {
                    consensus.Voters.Add(new CasPaxosVoterOptions
                    {
                        Id = string.Create(CultureInfo.InvariantCulture, $"voter-{voter}"),
                        Endpoint = string.Create(CultureInfo.InvariantCulture, $"127.0.0.1:{basePort + voter}"),
                        Database = Path.Combine(state.Root, string.Create(CultureInfo.InvariantCulture, $"voter-{voter}.db")),
                    });
                }

                consensus.ProposerId = ordinal + 1;
                consensus.ProposerDatabase = Path.Combine(state.Root, $"proposer-{name}.db");
            }));

            _ = siloBuilder.Services.AddSingleton<IObjectStore>(state.Objects);
            _ = siloBuilder.AddDurableJournalStorageWith(new DurableStorageSiloOptions
            {
                Node = new DurableStorageOptions
                {
                    Cluster = state.Cluster,
                    Root = Directory.CreateDirectory(Path.Combine(state.Root, name)).FullName,
                    Ordinal = ordinal,
                    WalFileSize = 1 << 20,
                    PreallocateWalFiles = false,
                    ArchiveStore = ArchiveStoreKind.Lsm,
                },
            });
            _ = siloBuilder.Services.Configure<Durable.Journaling.Json.JsonJournalOptions>(static options => options.AddTypeInfoResolver(JournaledChunksJsonContext.Default));

            // Membership declares a killed silo dead within seconds, which starts the takeovers of its ledgers.
            _ = siloBuilder.Configure<ClusterMembershipOptions>(static membership =>
            {
                membership.ProbeTimeout = TimeSpan.FromSeconds(1);
                membership.NumMissedProbesLimit = 2;
                membership.NumVotesForDeathDeclaration = 1;
            });
        }
    }
}
