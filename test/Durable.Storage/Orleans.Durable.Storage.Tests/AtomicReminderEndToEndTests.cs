using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Configuration;
using Orleans.Durable.Jobs;
using Orleans.Durable.Journaling;
using Orleans.Durable.Messaging;
using Orleans.Durable.Storage.Control;
using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Archive.Logs;
using Orleans.Durable.Storage.Engine.Cluster.Control;
using Orleans.Durable.Storage.Engine.Journals;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.MetaStore;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Durable.Storage.Engine.ObjectStore.Memory;
using Orleans.Durable.Storage.Hosting;
using Orleans.Durable.Storage.Testing;
using Orleans.Hosting;
using Orleans.Placement;
using Orleans.Runtime;
using Orleans.TestingHost;

namespace Orleans.Durable.Storage.Tests;

/// <summary>
/// Real <see cref="TestCluster"/> end-to-end tests for Phase J3 atomic 1-RTT messaging wakeups over both
/// <see cref="ArchiveStoreKind.Lsm"/> and <see cref="ArchiveStoreKind.Logs"/> (orleans.md §5.15, verification.md §8.4).
/// </summary>
[Collection(TestingHostCollection.Name)]
[TestCategory("EndToEnd")]
public sealed class AtomicReminderEndToEndTests : IAsyncLifetime
{
    private const string StateKey = "AtomicReminderE2EState";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);
    private static readonly ConcurrentDictionary<string, E2EState> States = new(StringComparer.Ordinal);

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();
    private readonly E2EState _state = new();
    private TestCluster? _cluster;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        States[_state.Id] = _state;
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _state.AllowDelivery = true;
        if (_cluster is not null)
        {
            foreach (var silo in _cluster.Silos.Reverse().ToList())
            {
                try
                {
                    await _cluster.StopSiloAsync(silo).WaitAsync(Patience, CancellationToken.None);
                }
                catch
                {
                    // Best-effort teardown when a test already killed a silo.
                }
            }

            await _cluster.DisposeAsync();
        }

        _ = States.TryRemove(_state.Id, out _);
        if (Directory.Exists(_state.Roots))
        {
            Directory.Delete(_state.Roots, recursive: true);
        }

        _leaks.Dispose();
    }

    /// <summary>
    /// End-to-end verification across both LSM and Archive Logs:
    /// <list type="number">
    /// <item>Warm local-primary cold <c>Outbox</c> send + fast deliver + retire = 0 <c>ScheduleJobAsync</c> calls, 0 sealed shard publishes, and 0 after ledger close &amp; drain (<c>SetWakeup(null)</c>).</item>
    /// <item>Remote (non-primary) silo forwarded message write succeeds with 0 <c>ScheduleJobAsync</c> / sealed shard calls, and invalid unregistered channel returns <see cref="ArgumentException"/> (<see cref="JournalRefusal.Argument"/>).</item>
    /// <item>Idle grain deactivation while ledger stays open with delayed message: <see cref="ReminderMessageHandler.ReminderLedgerSession"/> wakes the grain at <c>DueTime</c> with 0 <c>ScheduleJobAsync</c> / sealed shard calls.</item>
    /// <item>Primary crash while delivery is blocked: surviving silo drains closed ledger through single-pass <c>DrainScan</c> + <see cref="ReminderMessageProcessor"/>, publishes 1 deterministic sealed <c>JobShard</c> via <see cref="ISealedJobShardPublisher"/> (0 <c>ScheduleJobAsync</c> calls, retrying past an injected transient publish failure), and wakes the grain on a surviving silo to complete delivery.</item>
    /// </list>
    /// </summary>
    [Theory(Timeout = 180_000)]
    [InlineData(ArchiveStoreKind.Lsm)]
    [InlineData(ArchiveStoreKind.Logs)]
    public async Task AtomicMessagingRemindersWorkEndToEndAcrossLsmAndArchiveLogs(ArchiveStoreKind archiveKind)
    {
        var ct = TestContext.Current.CancellationToken;
        _state.ArchiveKind = archiveKind;
        var cluster = await DeployAsync(3, ct);

        // Wait until all 3 silos' Journals are serving and the initial 3-ledger split has settled (0 closed ledgers)
        // so background split/drain I/O does not add noise to the 0-object-write assertions below.
        await WaitUntilAsync(
            async () =>
            {
                if (_state.Journals.Count != 3 || !_state.Journals.Values.All(static j => j.State == LocalJournals.JournalsState.Serving))
                {
                    return false;
                }

                var current = await ReadMapAsync();
                return current.Ledgers.Length == 3
                    && current.Ledgers.All(static l => l.IsOpen)
                    && current.Ledgers.Select(static l => l.Primary.Node).Distinct().Count() == 3;
            },
            "all 3 silos' Journals to serve and 3 balanced open ledgers to settle");

        // 1. Warm local-primary Outbox send + fast deliver + retire -> 0 ScheduleJobAsync, 0 sealed shards, 0 object-store writes!
        var objectWritesBeforeFast = _state.TotalObjectWrites;
        var receiver = cluster.GrainFactory.GetGrain<IReminderReceiverGrain>("rx-fast");
        var sender = cluster.GrainFactory.GetGrain<IReminderSenderGrain>("tx-fast");
        var senderRowKey = JournalRowKey.Encode(JournalId.FromGrainId(sender.GetGrainId()).Value);
        await sender.SendAsync(receiver.GetGrainId(), "hello-1rtt", deliverDelay: null);
        await WaitUntilAsync(async () => (await receiver.ReceivedAsync()).Contains("hello-1rtt"), "fast 1-RTT message delivery");
        await WaitUntilAsync(async () => await sender.OutboxCountAsync() == 0, "sender outbox retirement");

        Assert.Equal(0, _state.TotalScheduleJobCalls);
        Assert.Equal(0, _state.TotalSealedShardPublishes);
        Assert.Equal(objectWritesBeforeFast, _state.TotalObjectWrites);

        // Roll and drain the ledger owning sender -> cleared SetWakeup(null) produces ZERO backing sealed shards!
        var openLedger = (await ReadMapAsync()).FindOpen(senderRowKey.Span)!.Value;
        var controller = cluster.GrainFactory.GetGrain<IClusterControllerGrain>(IClusterControllerGrain.Key);
        await controller.RequestRollAsync(openLedger.Id);
        await WaitUntilAsync(async () => (await ReadMapAsync()).Get(openLedger.Id) is null, "rolled ledger to drain and drop");
        Assert.Equal(0, _state.TotalScheduleJobCalls);
        Assert.Equal(0, _state.TotalSealedShardPublishes);

        // 2. Remote (non-primary) silo forwarded message write + invalid channel refusal!
        var objectWritesBeforeRemote = _state.TotalObjectWrites;
        var remoteRowKey = JournalRowKey.Encode("remote/companion");
        var currentOpen = (await ReadMapAsync()).FindOpen(remoteRowKey.Span)!.Value;
        var primarySiloName = _state.SiloByNode(currentOpen.Primary.Node);
        var remoteSiloName = _state.SiloNames.First(n => n != primarySiloName);
        var remoteSilo = cluster.Silos.Single(s => s.Name == remoteSiloName);
        var remoteRouter = ((InProcessSiloHandle)remoteSilo).SiloHost.Services.GetRequiredService<JournalRouter>();
        var remoteRouting = ((InProcessSiloHandle)remoteSilo).SiloHost.Services.GetRequiredService<LocalRouting>();
        var remoteOptions = ((InProcessSiloHandle)remoteSilo).SiloHost.Services.GetRequiredService<DurableStorageOptions>();
        var remoteStorage = new DurableJournalStorage(new JournalId("remote/companion"), remoteRouter, remoteRouting, remoteOptions.Journals.MaxBodyBytes, "json", TimeProvider.System);
        await remoteStorage.ReadAsync(new NoOpConsumer(), Token);

        var validReminder = ReminderMessageCodec.Encode(new ReminderRecord(GrainId.Create("remote", "1"), "orleans.messaging.pump", (DateTimeOffset.UtcNow + TimeSpan.FromHours(1)).UtcTicks));
        await remoteStorage.AppendWithMessagesAsync(
            new ReadOnlySequence<byte>("{\"ok\":1}\n"u8.ToArray()),
            new[] { new JournalMessageRecord(ReminderMessageCodec.ChannelName, validReminder) },
            Token);
        // Clear it so we keep sealed shard count at 0 for the next check.
        var clearReminder = ReminderMessageCodec.Encode(new ReminderRecord(GrainId.Create("remote", "1"), "orleans.messaging.pump", null));
        await remoteStorage.AppendWithMessagesAsync(
            new ReadOnlySequence<byte>("{\"ok\":2}\n"u8.ToArray()),
            new[] { new JournalMessageRecord(ReminderMessageCodec.ChannelName, clearReminder) },
            Token);
        Assert.Equal(0, _state.TotalScheduleJobCalls);
        Assert.Equal(0, _state.TotalSealedShardPublishes);
        Assert.Equal(objectWritesBeforeRemote, _state.TotalObjectWrites);

        // Unregistered message channel from remote silo returns ArgumentException (JournalRefusal.Argument), NOT Ambiguous!
        var badEx = await Assert.ThrowsAsync<ArgumentException>(() => remoteStorage.AppendWithMessagesAsync(
            new ReadOnlySequence<byte>("{\"bad\":1}\n"u8.ToArray()),
            new[] { new JournalMessageRecord("unregistered.channel", new byte[] { 1, 2, 3 }) },
            ct).AsTask());
        Assert.Contains("unregistered.channel", badEx.Message, StringComparison.Ordinal);

        // 3. Idle grain deactivation while ledger stays open -> ReminderLedgerSession wakes the truly deactivated
        //    receiver grain at DeliverAt (0 sealed shards, 0 object writes; poll E2EState without calling the grain)!
        var objectWritesBeforeDelayed = _state.TotalObjectWrites;
        var delayedReceiver = cluster.GrainFactory.GetGrain<IReminderReceiverGrain>("rx-delayed");
        var delayedSender = cluster.GrainFactory.GetGrain<IReminderSenderGrain>("tx-delayed");
        await delayedSender.SendAsync(delayedReceiver.GetGrainId(), "wake-while-open", deliverDelay: TimeSpan.FromSeconds(2));
        await WaitUntilAsync(async () => await delayedSender.OutboxCountAsync() == 0, "delayed message acceptance by receiver inbox");
        await delayedReceiver.DeactivateNowAsync();
        await delayedSender.DeactivateNowAsync();

        await WaitUntilAsync(() => _state.HasDelivered("wake-while-open"), "ReminderLedgerSession to wake deactivated receiver grain");
        Assert.Contains("wake-while-open", await delayedReceiver.ReceivedAsync());
        Assert.Equal(0, _state.TotalScheduleJobCalls);
        Assert.Equal(0, _state.TotalSealedShardPublishes);
        Assert.Equal(objectWritesBeforeDelayed, _state.TotalObjectWrites);

        // 4. Primary crash after delayed message is durably accepted in crashReceiver's Inbox -> single-pass DrainScan
        //    publishes 1 deterministic sealed JobShard (retrying past 1 injected transient publish failure!) and wakes
        //    crashReceiver on a surviving silo without any external grain call!
        var crashReceiver = cluster.GrainFactory.GetGrain<IReminderReceiverGrain>("rx-crash");
        var crashSender = cluster.GrainFactory.GetGrain<IReminderSenderGrain>("tx-crash");
        await crashSender.SendAsync(crashReceiver.GetGrainId(), "survive-primary-crash", deliverDelay: TimeSpan.FromSeconds(4));
        await WaitUntilAsync(async () => await crashSender.OutboxCountAsync() == 0, "crash message acceptance by crashReceiver inbox");
        Assert.False(_state.HasDelivered("survive-primary-crash"));
        Assert.Equal(0, _state.TotalScheduleJobCalls);
        Assert.Equal(0, _state.TotalSealedShardPublishes);

        var crashMap = await ReadMapAsync();
        var crashRowKey = JournalRowKey.Encode(JournalId.FromGrainId(crashReceiver.GetGrainId()).Value);
        var crashLedger = crashMap.FindOpen(crashRowKey.Span)!.Value;
        var doomedSiloName = _state.SiloByNode(crashLedger.Primary.Node);
        var doomedSilo = cluster.Silos.Single(s => s.Name == doomedSiloName);

        // Inject 1 transient PublishSealedShardAsync failure so ReminderMessageProcessor's retry path is exercised end-to-end!
        _state.ArmTransientPublishFailures(1);

        // Kill crashReceiver's primary silo (ungraceful crash) so its activation and volatile ReminderLedgerSession heap are lost!
        await cluster.KillSiloAsync(doomedSilo, TestContext.Current.CancellationToken).WaitAsync(Patience, TestContext.Current.CancellationToken);

        // Surviving silo takes over, closes crashLedger, folds reminder in single-pass DrainScan, publishes deterministic
        // sealed JobShard (retrying past injected failure), claims it, and wakes crashReceiver (no external grain calls)!
        await WaitUntilAsync(
            () => _state.HasDelivered("survive-primary-crash"),
            "surviving silo to drain closed ledger reminder into sealed JobShard and wake crashReceiver");
        Assert.Contains("survive-primary-crash", await crashReceiver.ReceivedAsync());
        Assert.Equal(0, _state.TotalScheduleJobCalls);
        Assert.True(
            _state.TotalSealedShardPublishes >= 2,
            $"Expected injected transient failure + retry to invoke PublishSealedShardAsync at least twice, got TotalSealedShardPublishes={_state.TotalSealedShardPublishes}, CreatedSealedShards={_state.CreatedSealedShards}, TotalScheduleJobCalls={_state.TotalScheduleJobCalls}, crashLedger={crashLedger.Id}, doomedSilo={doomedSiloName}.");
        Assert.True(_state.CreatedSealedShards >= 1, "Expected at least 1 deterministic sealed JobShard created.");

        // 5. Verify racing-drainer idempotency across two surviving silos: publishing the same deterministic sealed
        //    shard ID twice returns true on the first write and false on the racing duplicate!
        var survivingSilos = cluster.Silos.Where(s => s.Name != doomedSiloName).ToArray();
        var publisherA = ((InProcessSiloHandle)survivingSilos[0]).SiloHost.Services.GetRequiredService<ISealedJobShardPublisher>();
        var publisherB = ((InProcessSiloHandle)survivingSilos[^1]).SiloHost.Services.GetRequiredService<ISealedJobShardPublisher>();
        var firstPublished = _state.PublishedShards.First();
        var raceShardId = $"{firstPublished.DeterministicId}-race";
        var raceMinDue = DateTimeOffset.UtcNow.AddHours(2);
        var raceMaxDue = raceMinDue.AddHours(1);
        var raceJobs = new[]
        {
            new ScheduleJobRequest
            {
                Target = crashReceiver.GetGrainId(),
                JobName = MessagingPump.JobName,
                DueTime = raceMinDue.AddMinutes(5),
            },
        };
        Assert.True(await publisherA.PublishSealedShardAsync(raceShardId, raceMinDue, raceMaxDue, raceJobs, ct));
        Assert.False(await publisherB.PublishSealedShardAsync(raceShardId, raceMinDue, raceMaxDue, raceJobs, ct));
    }

    private async Task<TestCluster> DeployAsync(int silos, CancellationToken cancellationToken)
    {
        var builder = new TestClusterBuilder((short)silos);
        builder.ConfigureHostConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [StateKey] = _state.Id,
        }));
        builder.AddSiloBuilderConfigurator<SiloConfigurator>();
        _cluster = builder.Build();
        await _cluster.DeployAsync(cancellationToken).WaitAsync(Patience, cancellationToken);
        return _cluster;
    }

    private async Task<LedgerMapValue> ReadMapAsync()
    {
        var register = await _state.MetaStore.GetRegisterAsync(_state.ClusterId, LedgerMapRegister.Name, Token);
        var (value, _) = await LedgerMapValue.ReadAsync(register, Token);
        return value;
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, string description) =>
        await WaitUntilAsync(() => Task.FromResult(predicate()), description);

    private static async Task WaitUntilAsync(Func<Task<bool>> predicate, string description)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < Patience)
        {
            if (await predicate().ConfigureAwait(false))
            {
                return;
            }

            await Task.Delay(50, Token).ConfigureAwait(false);
        }

        Assert.Fail($"Timed out after {Patience} waiting for {description}.");
    }

    public sealed class E2EState
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public Guid ClusterId { get; } = Guid.NewGuid();
        public string Roots { get; } = Path.Combine(Path.GetTempPath(), $"orleans-e2e-reminder-{Guid.NewGuid():N}");
        public ArchiveStoreKind ArchiveKind { get; set; } = ArchiveStoreKind.Lsm;
        public InMemoryMetaStore MetaStore { get; } = new();
        public CountingObjectStore LsmObjects { get; } = new(new InMemoryObjectStore());
        public CountingObjectStore LogObjects { get; } = new(new InMemoryObjectStore());
        public MemoryArchiveLogContainer LogsContainer { get; }
        public VolatileJournalStorageProvider SharedJobsStorage { get; } = new();
        internal ConcurrentDictionary<string, LocalNode> Nodes { get; } = new(StringComparer.Ordinal);
        internal ConcurrentDictionary<string, LocalJournals> Journals { get; } = new(StringComparer.Ordinal);
        public ConcurrentBag<(string DeterministicId, DateTimeOffset MinDueTime, DateTimeOffset MaxDueTime, ScheduleJobRequest[] Jobs)> PublishedShards { get; } = [];
        public ConcurrentDictionary<string, byte> Delivered { get; } = new(StringComparer.Ordinal);
        public volatile bool AllowDelivery = true;
        private int _scheduleJobCalls;
        private int _sealedShardPublishes;
        private int _createdSealedShards;
        private int _remainingPublishFailures;

        public E2EState() => LogsContainer = new MemoryArchiveLogContainer(LogObjects);

        public void RecordDelivered(string text) => Delivered[text] = 1;

        public bool HasDelivered(string text) => Delivered.ContainsKey(text);

        public int TotalScheduleJobCalls => Volatile.Read(ref _scheduleJobCalls);

        public int TotalSealedShardPublishes => Volatile.Read(ref _sealedShardPublishes);

        public int CreatedSealedShards => Volatile.Read(ref _createdSealedShards);

        public int TotalObjectWrites => LsmObjects.WriteCount + LogObjects.WriteCount;

        public void RecordScheduleJob() => Interlocked.Increment(ref _scheduleJobCalls);

        public void ArmTransientPublishFailures(int count) => Volatile.Write(ref _remainingPublishFailures, count);

        public bool RecordPublishAndShouldFail()
        {
            Interlocked.Increment(ref _sealedShardPublishes);
            while (true)
            {
                var current = Volatile.Read(ref _remainingPublishFailures);
                if (current <= 0)
                {
                    return false;
                }

                if (Interlocked.CompareExchange(ref _remainingPublishFailures, current - 1, current) == current)
                {
                    return true;
                }
            }
        }

        public void RecordCreatedShard(string deterministicId, DateTimeOffset minDue, DateTimeOffset maxDue, IReadOnlyList<ScheduleJobRequest> jobs)
        {
            Interlocked.Increment(ref _createdSealedShards);
            PublishedShards.Add((deterministicId, minDue, maxDue, [.. jobs]));
        }

        public IEnumerable<string> SiloNames => Nodes.Keys;

        public string SiloByNode(NodeId node) => Nodes.Single(kvp => kvp.Value.Node == node).Key;
    }

    public sealed class CountingObjectStore(InMemoryObjectStore inner) : IObjectStore, IAppendableObjectStore
    {
        private int _writes;

        public int WriteCount => Volatile.Read(ref _writes);

        public ValueTask<ObjectCreateOutcome> CreateAsync(ObjectName name, Engine.Buffers.ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _writes);
            return inner.CreateAsync(name, content, crc32c, cancellationToken);
        }

        public ValueTask<Engine.Buffers.ArcBuffer?> ReadAsync(ObjectName name, long offset, int length, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(name, offset, length, cancellationToken);

        public ValueTask<ObjectProperties?> GetPropertiesAsync(ObjectName name, CancellationToken cancellationToken = default) =>
            inner.GetPropertiesAsync(name, cancellationToken);

        public IAsyncEnumerable<ListedObject> ListAsync(string prefix, string? startAfter = null, CancellationToken cancellationToken = default) =>
            inner.ListAsync(prefix, startAfter, cancellationToken);

        public ValueTask DeleteAsync(ObjectName name, CancellationToken cancellationToken = default) =>
            inner.DeleteAsync(name, cancellationToken);

        public ValueTask<VersionedObject?> ReadVersionedAsync(ObjectName name, CancellationToken cancellationToken = default) =>
            inner.ReadVersionedAsync(name, cancellationToken);

        public ValueTask<ObjectReplaceOutcome> ReplaceAsync(ObjectName name, ObjectVersion? expectedVersion, Engine.Buffers.ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _writes);
            return inner.ReplaceAsync(name, expectedVersion, content, crc32c, cancellationToken);
        }

        public ValueTask<ObjectCreateOutcome> CreateAppendableAsync(ObjectName name, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _writes);
            return inner.CreateAppendableAsync(name, cancellationToken);
        }

        public ValueTask<ObjectAppendOutcome> AppendAsync(ObjectName name, long expectedLength, Engine.Buffers.ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _writes);
            return inner.AppendAsync(name, expectedLength, content, crc32c, cancellationToken);
        }

        public ValueTask<ObjectSealOutcome> SealAsync(ObjectName name, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _writes);
            return inner.SealAsync(name, cancellationToken);
        }

        public ValueTask ReplaceWithTombstoneAsync(ObjectName name, Engine.Buffers.ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _writes);
            return inner.ReplaceWithTombstoneAsync(name, content, crc32c, cancellationToken);
        }
    }

    private sealed class SiloConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder)
        {
            var state = States[siloBuilder.Configuration[StateKey]!];
            var siloName = siloBuilder.Configuration["Orleans:Name"]!;
            var root = Path.Combine(state.Roots, siloName);
            _ = Directory.CreateDirectory(root);

            _ = siloBuilder.Services.AddSingleton(state);
            _ = siloBuilder.Services.AddSingleton<IMetaStore>(state.MetaStore);
            if (state.ArchiveKind == ArchiveStoreKind.Lsm)
            {
                _ = siloBuilder.Services.AddSingleton<IObjectStore>(state.LsmObjects);
            }
            else
            {
                _ = siloBuilder.Services.AddSingleton<IArchiveLogContainer>(state.LogsContainer);
            }

            _ = siloBuilder.Configure<ClusterMembershipOptions>(static membership =>
            {
                membership.ProbeTimeout = TimeSpan.FromSeconds(1);
                membership.NumMissedProbesLimit = 2;
                membership.NumVotesForDeathDeclaration = 1;
            });

            _ = siloBuilder.AddDurableJournalStorage(options => options.Configure(silo =>
            {
                silo.Node.Cluster = state.ClusterId;
                silo.Node.Root = root;
                silo.Node.ArchiveStore = state.ArchiveKind;
                silo.Node.ArchiveStorePrefix = "remindere2e";
                silo.Node.WalFileSize = 4L << 20;
                silo.Node.PreallocateWalFiles = false;
                silo.Control.Replicas = 3;
                silo.Control.LedgersPerNode = 1;
                silo.Control.SequencerWindow = 4;
                silo.Control.ControlInterval = TimeSpan.FromMilliseconds(200);
                silo.Control.BalanceInterval = TimeSpan.FromMilliseconds(200);
                silo.Control.TransitionTimeout = TimeSpan.FromSeconds(10);
                silo.Control.ManifestCommitInterval = TimeSpan.Zero;
            }));

            // Backing Durable Jobs shards use a cluster-shared in-memory journal storage under a named provider while grain journals use Durable Storage.
            _ = siloBuilder.AddJournalStorage("jobs", _ => state.SharedJobsStorage);
            _ = siloBuilder.UseJournaledDurableJobs(options => options.ActiveProviderName = "jobs");
            _ = siloBuilder.AddMessaging(options =>
            {
                options.RetryInitialInterval = TimeSpan.FromMilliseconds(100);
            });

            // Decorate ILocalDurableJobManager and ISealedJobShardPublisher to count every backing call!
            _ = siloBuilder.Services.Replace(ServiceDescriptor.Singleton<ILocalDurableJobManager>(sp =>
                new CountingDurableJobManager(sp.GetRequiredService<LocalDurableJobManager>(), state)));
            _ = siloBuilder.Services.Replace(ServiceDescriptor.Singleton<ISealedJobShardPublisher>(sp =>
                new CountingSealedShardPublisher(sp.GetRequiredService<LocalDurableJobManager>(), state)));

            // Capture LocalNode & LocalJournals handles once the silo starts.
            _ = siloBuilder.Services.AddSingleton<ILifecycleParticipant<ISiloLifecycle>>(sp =>
                new HandleCapture(siloName, state, sp.GetRequiredService<LocalNode>(), sp.GetRequiredService<LocalJournals>()));
        }
    }

    private sealed class HandleCapture(string siloName, E2EState state, LocalNode node, LocalJournals journals) : ILifecycleParticipant<ISiloLifecycle>
    {
        public void Participate(ISiloLifecycle observer) =>
            _ = observer.Subscribe(nameof(HandleCapture), ServiceLifecycleStage.Active, _ =>
            {
                state.Nodes[siloName] = node;
                state.Journals[siloName] = journals;
                return Task.CompletedTask;
            });
    }

    private sealed class CountingDurableJobManager(ILocalDurableJobManager inner, E2EState state) : ILocalDurableJobManager
    {
        public Task<DurableJob> ScheduleJobAsync(ScheduleJobRequest request, CancellationToken cancellationToken)
        {
            state.RecordScheduleJob();
            return inner.ScheduleJobAsync(request, cancellationToken);
        }

        public Task<bool> CancelAsync(DurableJob job, CancellationToken requestCancellationToken) =>
            inner.CancelAsync(job, requestCancellationToken);
    }

    private sealed class CountingSealedShardPublisher(ISealedJobShardPublisher inner, E2EState state) : ISealedJobShardPublisher
    {
        public TimeSpan ShardDuration => inner.ShardDuration;

        public async Task<bool> PublishSealedShardAsync(
            string deterministicId,
            DateTimeOffset minDueTime,
            DateTimeOffset maxDueTime,
            IReadOnlyList<ScheduleJobRequest> jobs,
            CancellationToken cancellationToken)
        {
            if (state.RecordPublishAndShouldFail())
            {
                throw new InvalidOperationException("Injected transient PublishSealedShardAsync failure.");
            }

            var created = await inner.PublishSealedShardAsync(deterministicId, minDueTime, maxDueTime, jobs, cancellationToken).ConfigureAwait(false);
            if (created)
            {
                state.RecordCreatedShard(deterministicId, minDueTime, maxDueTime, jobs);
            }

            return created;
        }
    }

    private sealed class NoOpConsumer : IJournalStorageConsumer
    {
        public void Read(JournalBufferReader buffer, IJournalMetadata? metadata)
        {
            if (buffer.Length > 0)
            {
                buffer.Skip(buffer.Length);
            }
        }
    }

    public interface IReminderSenderGrain : IGrainWithStringKey
    {
        Task SendAsync(GrainId target, string text, TimeSpan? deliverDelay);
        Task<int> OutboxCountAsync();
        Task DeactivateNowAsync();
    }

    public interface IReminderReceiverGrain : IGrainWithStringKey
    {
        Task<string[]> ReceivedAsync();
        Task DeactivateNowAsync();
    }

    [JournalPlacement]
    public sealed class ReminderSenderGrain(IOutbox outbox, IDurableStateManager state) : Grain, IReminderSenderGrain
    {
        public async Task SendAsync(GrainId target, string text, TimeSpan? deliverDelay)
        {
            var envelope = new Envelope
            {
                MessageId = MessageId.Parse(Guid.NewGuid().ToString("N"), CultureInfo.InvariantCulture),
                Subject = "reminder-e2e",
                Payload = Encoding.UTF8.GetBytes(text),
                DeliverAt = deliverDelay is { } d ? DateTimeOffset.UtcNow + d : null,
            };
            outbox.Send(new OutboxMessage { Envelope = envelope, ReceiverId = target });
            await state.WriteStateAsync();
        }

        public Task<int> OutboxCountAsync() => Task.FromResult(((Outbox)outbox).Count);

        public Task DeactivateNowAsync()
        {
            DeactivateOnIdle();
            return Task.CompletedTask;
        }
    }

    [JournalPlacement]
    public sealed class ReminderReceiverGrain : Grain, IReminderReceiverGrain
    {
        private readonly IDurableList<string> _received;

        public ReminderReceiverGrain(
            IInbox inbox,
            [FromKeyedServices("received")] IDurableList<string> received,
            E2EState e2e)
        {
            _received = received;
            // Register during construction (before SetupState journal recovery) per Orleans Messaging contract.
            inbox.Register(new ReceiverHandler(received, e2e));
        }

        public Task<string[]> ReceivedAsync() => Task.FromResult(_received.ToArray());

        public Task DeactivateNowAsync()
        {
            DeactivateOnIdle();
            return Task.CompletedTask;
        }

        private sealed class ReceiverHandler(IDurableList<string> received, E2EState e2e) : IInboxHandler
        {
            public bool CanHandle(InboxMessage message) => string.Equals(message.Envelope.Subject, "reminder-e2e", StringComparison.Ordinal);

            public ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
            {
                if (!e2e.AllowDelivery)
                {
                    throw new InvalidOperationException("Delivery temporarily blocked by E2E test.");
                }

                var text = Encoding.UTF8.GetString(context.Message.Envelope.Payload);
                received.Add(text);
                context.Complete();
                e2e.RecordDelivered(text);
                return ValueTask.CompletedTask;
            }
        }
    }
}
