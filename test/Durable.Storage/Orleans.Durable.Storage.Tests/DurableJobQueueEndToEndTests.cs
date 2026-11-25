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
/// End-to-end <see cref="TestCluster"/> tests for <see cref="IDurableJobQueue"/> over real Durable Storage (both LSM
/// and Archive Logs): 1-RTT atomic grain-state + job scheduling, zero Azure/backing writes when completed or cancelled
/// before ledger drain, idle deactivation reminder via <see cref="ReminderMessageHandler.ReminderLedgerSession"/>, primary-crash recovery via
/// single-pass <c>DrainScan</c> + deterministic sealed <c>JobShard</c> publishing, and sharing
/// <see cref="GrainReminderCoordinator"/> alongside <see cref="IOutbox"/>/<see cref="IInbox"/> on the same grain.
/// </summary>
[TestCategory("EndToEnd")]
public sealed class DurableJobQueueEndToEndTests : IAsyncLifetime
{
    private const string StateKey = "DurableStorage:JobQueueE2E:State";
    private static readonly ConcurrentDictionary<string, E2EState> States = new(StringComparer.Ordinal);
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(45);

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();
    private readonly E2EState _state = new();
    private TestCluster? _cluster;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        Assert.True(States.TryAdd(_state.Id, _state));
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
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

    [Theory(Timeout = 180_000)]
    [InlineData(ArchiveStoreKind.Lsm)]
    [InlineData(ArchiveStoreKind.Logs)]
    public async Task DurableJobQueueAtomicWakeupsCrashRecoveryAndMessagingCoexistenceWorkEndToEnd(ArchiveStoreKind archiveKind)
    {
        var ct = TestContext.Current.CancellationToken;
        _state.ArchiveKind = archiveKind;
        var cluster = await DeployAsync(3, ct);

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

        // 1. 1-RTT atomic grain-state + Schedule + immediate execution + cancel -> 0 ScheduleJobAsync, 0 object writes!
        var objectWritesBefore = _state.TotalObjectWrites;
        var worker = cluster.GrainFactory.GetGrain<IJobQueueWorkerGrain>("worker-1");
        await worker.ScheduleWithStateAsync("job-fast", "run-fast", delay: TimeSpan.Zero, stateTag: "fast-state");
        await WaitUntilAsync(async () => (await worker.ExecutedJobsAsync()).Contains("job-fast"), "fast job execution");
        Assert.Equal("fast-state", await worker.StateTagAsync());
        Assert.Equal(0, await worker.PendingCountAsync());

        // Schedule a future job and cancel it before it fires -> still 0 ScheduleJobAsync, 0 object writes!
        await worker.ScheduleWithStateAsync("job-cancel", "never-run", delay: TimeSpan.FromHours(1), stateTag: "cancel-state");
        Assert.Equal(1, await worker.PendingCountAsync());
        Assert.True(await worker.CancelAsync("job-cancel"));
        Assert.Equal(0, await worker.PendingCountAsync());
        Assert.Equal(0, _state.TotalScheduleJobCalls);
        Assert.Equal(objectWritesBefore, _state.TotalObjectWrites);

        // 2. Shared GrainWakeupCoordinator with Orleans.Durable.Messaging on the SAME grain + idle deactivation wakeup!
        var sink = cluster.GrainFactory.GetGrain<IMessageSinkGrain>("sink-1");
        var combo = cluster.GrainFactory.GetGrain<IComboMessagingAndJobGrain>("combo-1");
        await combo.SendMessageAndScheduleJobAsync(sink.GetGrainId(), "combo-msg", "combo-job", TimeSpan.FromSeconds(2));
        await combo.DeactivateNowAsync();

        await WaitUntilAsync(async () => (await sink.ReceivedAsync()).Contains("combo-msg"), "combo outbox message delivery");
        await WaitUntilAsync(async () => (await combo.ExecutedJobsAsync()).Contains("combo-job"), "combo delayed job wakeup after deactivation");
        Assert.Equal(0, _state.TotalScheduleJobCalls);
        Assert.Equal(objectWritesBefore, _state.TotalObjectWrites);

        // Roll & drain the worker's ledger -> cleared SetWakeup(null) produces ZERO backing sealed shards or ScheduleJobAsync calls!
        var workerRowKey = JournalRowKey.Encode(JournalId.FromGrainId(worker.GetGrainId()).Value);
        var workerLedger = (await ReadMapAsync()).FindOpen(workerRowKey.Span)!.Value;
        var controller = cluster.GrainFactory.GetGrain<IClusterControllerGrain>(IClusterControllerGrain.Key);
        await controller.RequestRollAsync(workerLedger.Id);
        await WaitUntilAsync(async () => (await ReadMapAsync()).Get(workerLedger.Id) is null, "worker ledger to drain and drop");
        Assert.Equal(0, _state.TotalScheduleJobCalls);
        Assert.Equal(0, _state.TotalSealedShardPublishes);

        // 3. Primary crash after committing delayed job -> closed-ledger drain publishes deterministic sealed JobShard and executes on surviving silo!
        // Poll out-of-band E2EState.ExecutedJobs (without calling crashWorker!) to prove the backing durable job itself woke the grain!
        var crashWorker = cluster.GrainFactory.GetGrain<IJobQueueWorkerGrain>("worker-crash");
        await crashWorker.ScheduleWithStateAsync("job-crash", "survive-crash", delay: TimeSpan.FromSeconds(2), stateTag: "crash-state");
        Assert.Equal(0, _state.TotalScheduleJobCalls);
        Assert.Equal(0, _state.TotalSealedShardPublishes);

        var crashRowKey = JournalRowKey.Encode(JournalId.FromGrainId(crashWorker.GetGrainId()).Value);
        var crashLedger = (await ReadMapAsync()).FindOpen(crashRowKey.Span)!.Value;
        var doomedSiloName = _state.SiloByNode(crashLedger.Primary.Node);
        var doomedSilo = cluster.Silos.Single(s => s.Name == doomedSiloName);

        await cluster.KillSiloAsync(doomedSilo, TestContext.Current.CancellationToken).WaitAsync(Patience, TestContext.Current.CancellationToken);

        await WaitUntilAsync(
            () => Task.FromResult(_state.ExecutedJobs.Contains("job-crash")),
            "surviving silo to drain closed ledger wakeup into sealed JobShard and wake crashWorker without external calls");
        Assert.Equal(0, _state.TotalScheduleJobCalls);
        Assert.True(_state.TotalSealedShardPublishes >= 1, "Expected closed-ledger drain to publish at least 1 deterministic sealed JobShard after primary crash.");
        Assert.Equal("crash-state", await crashWorker.StateTagAsync());
        Assert.Contains("job-crash", await crashWorker.ExecutedJobsAsync());
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
        public string Roots { get; } = Path.Combine(Path.GetTempPath(), $"orleans-e2e-jobqueue-{Guid.NewGuid():N}");
        public ArchiveStoreKind ArchiveKind { get; set; } = ArchiveStoreKind.Lsm;
        public InMemoryMetaStore MetaStore { get; } = new();
        public AtomicReminderEndToEndTests.CountingObjectStore LsmObjects { get; } = new(new InMemoryObjectStore());
        public AtomicReminderEndToEndTests.CountingObjectStore LogObjects { get; } = new(new InMemoryObjectStore());
        public MemoryArchiveLogContainer LogsContainer { get; }
        public VolatileJournalStorageProvider SharedJobsStorage { get; } = new();
        internal ConcurrentDictionary<string, LocalNode> Nodes { get; } = new(StringComparer.Ordinal);
        internal ConcurrentDictionary<string, LocalJournals> Journals { get; } = new(StringComparer.Ordinal);
        public ConcurrentBag<string> ExecutedJobs { get; } = [];
        private int _scheduleJobCalls;
        private int _sealedShardPublishes;

        public E2EState() => LogsContainer = new MemoryArchiveLogContainer(LogObjects);

        public int TotalScheduleJobCalls => Volatile.Read(ref _scheduleJobCalls);

        public int TotalSealedShardPublishes => Volatile.Read(ref _sealedShardPublishes);

        public int TotalObjectWrites => LsmObjects.WriteCount + LogObjects.WriteCount;

        public void RecordScheduleJob() => Interlocked.Increment(ref _scheduleJobCalls);

        public void RecordSealedShardPublish() => Interlocked.Increment(ref _sealedShardPublishes);

        public string SiloByNode(NodeId node) => Nodes.Single(kvp => kvp.Value.Node == node).Key;
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
                silo.Node.ArchiveStorePrefix = "jobqueuee2e";
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

            _ = siloBuilder.AddJournalStorage("jobs", _ => state.SharedJobsStorage);
            _ = siloBuilder.UseJournaledDurableJobs(options => options.ActiveProviderName = "jobs");
            _ = siloBuilder.AddMessaging(options => options.RetryInitialInterval = TimeSpan.FromMilliseconds(100));

            _ = siloBuilder.Services.Replace(ServiceDescriptor.Singleton<ILocalDurableJobManager>(sp =>
                new CountingDurableJobManager(sp.GetRequiredService<LocalDurableJobManager>(), state)));
            _ = siloBuilder.Services.Replace(ServiceDescriptor.Singleton<ISealedJobShardPublisher>(sp =>
                new CountingSealedShardPublisher(sp.GetRequiredService<LocalDurableJobManager>(), state)));

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

        public Task<bool> PublishSealedShardAsync(
            string deterministicId,
            DateTimeOffset minDueTime,
            DateTimeOffset maxDueTime,
            IReadOnlyList<ScheduleJobRequest> jobs,
            CancellationToken cancellationToken)
        {
            state.RecordSealedShardPublish();
            return inner.PublishSealedShardAsync(deterministicId, minDueTime, maxDueTime, jobs, cancellationToken);
        }
    }

    public interface IJobQueueWorkerGrain : IGrainWithStringKey
    {
        Task ScheduleWithStateAsync(string jobId, string jobName, TimeSpan delay, string stateTag);
        Task<bool> CancelAsync(string jobId);
        Task<int> PendingCountAsync();
        Task<string[]> ExecutedJobsAsync();
        Task<string?> StateTagAsync();
    }

    public interface IMessageSinkGrain : IGrainWithStringKey
    {
        Task<string[]> ReceivedAsync();
    }

    public interface IComboMessagingAndJobGrain : IGrainWithStringKey
    {
        Task SendMessageAndScheduleJobAsync(GrainId sink, string messageText, string jobId, TimeSpan jobDelay);
        Task<string[]> ExecutedJobsAsync();
        Task DeactivateNowAsync();
    }

    [JournalPlacement]
    public sealed class JobQueueWorkerGrain(
        IDurableJobQueue jobQueue,
        IDurableStateManager state,
        [FromKeyedServices("tag")] IDurableValue<string?> tag,
        [FromKeyedServices("executed")] IDurableList<string> executed,
        E2EState e2e) : Grain, IJobQueueWorkerGrain, IDurableJobQueueHandler
    {
        public async Task ScheduleWithStateAsync(string jobId, string jobName, TimeSpan delay, string stateTag)
        {
            tag.Value = stateTag;
            _ = jobQueue.Schedule(new GrainJobRequest
            {
                JobId = jobId,
                JobName = jobName,
                DueTime = DateTimeOffset.UtcNow + delay,
            });
            await state.WriteStateAsync();
        }

        public async Task<bool> CancelAsync(string jobId)
        {
            var cancelled = jobQueue.Cancel(jobId);
            if (cancelled)
            {
                await state.WriteStateAsync();
            }

            return cancelled;
        }

        public Task<int> PendingCountAsync() => Task.FromResult(jobQueue.Count);

        public Task<string[]> ExecutedJobsAsync() => Task.FromResult(executed.ToArray());

        public Task<string?> StateTagAsync() => Task.FromResult(tag.Value);

        public ValueTask<DurableJobRunResult> ExecuteJobAsync(IJobRunContext context, CancellationToken attemptCancellationToken)
        {
            executed.Add(context.Job.Id);
            e2e.ExecutedJobs.Add(context.Job.Id);
            return ValueTask.FromResult(DurableJobRunResult.Completed);
        }
    }

    [JournalPlacement]
    public sealed class MessageSinkGrain : Grain, IMessageSinkGrain
    {
        private readonly IDurableList<string> _received;

        public MessageSinkGrain(IInbox inbox, [FromKeyedServices("received")] IDurableList<string> received)
        {
            _received = received;
            inbox.Register(new SinkHandler(received));
        }

        public Task<string[]> ReceivedAsync() => Task.FromResult(_received.ToArray());

        private sealed class SinkHandler(IDurableList<string> received) : IInboxHandler
        {
            public bool CanHandle(InboxMessage message) => string.Equals(message.Envelope.Subject, "combo", StringComparison.Ordinal);

            public ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
            {
                received.Add(Encoding.UTF8.GetString(context.Message.Envelope.Payload));
                context.Complete();
                return ValueTask.CompletedTask;
            }
        }
    }

    [JournalPlacement]
    public sealed class ComboMessagingAndJobGrain(
        IOutbox outbox,
        IDurableJobQueue jobQueue,
        IDurableStateManager state,
        [FromKeyedServices("executed")] IDurableList<string> executed) : Grain, IComboMessagingAndJobGrain, IDurableJobQueueHandler
    {
        public async Task SendMessageAndScheduleJobAsync(GrainId sink, string messageText, string jobId, TimeSpan jobDelay)
        {
            outbox.Send(new OutboxMessage
            {
                ReceiverId = sink,
                Envelope = new Envelope
                {
                    MessageId = MessageId.Parse(Guid.NewGuid().ToString("N"), CultureInfo.InvariantCulture),
                    Subject = "combo",
                    Payload = Encoding.UTF8.GetBytes(messageText),
                },
            });
            _ = jobQueue.Schedule(new GrainJobRequest
            {
                JobId = jobId,
                JobName = "combo-task",
                DueTime = DateTimeOffset.UtcNow + jobDelay,
            });
            // Commits Outbox message + DurableJobQueue job + both SetWakeup companions atomically in ONE WriteStateAsync (1 RTT)!
            await state.WriteStateAsync();
        }

        public Task<string[]> ExecutedJobsAsync() => Task.FromResult(executed.ToArray());

        public Task DeactivateNowAsync()
        {
            DeactivateOnIdle();
            return Task.CompletedTask;
        }

        public ValueTask<DurableJobRunResult> ExecuteJobAsync(IJobRunContext context, CancellationToken attemptCancellationToken)
        {
            executed.Add(context.Job.Id);
            return ValueTask.FromResult(DurableJobRunResult.Completed);
        }
    }
}
