using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Durable.Journaling;
using Orleans.Runtime;
using Orleans.Runtime.Internal;

namespace Orleans.Durable.Jobs;

/// <summary>
/// Services and settings shared by every <see cref="DurableJobQueue"/> activation in a silo.
/// </summary>
internal sealed class DurableJobQueueShared
{
    public DurableJobQueueShared(IServiceProvider services)
    {
        Options = services.GetRequiredService<IOptions<DurableJobQueueOptions>>().Value;
        TimeProvider = services.GetKeyedService<TimeProvider>(DurableJobTimeProviderNames.DurableJobs) ?? TimeProvider.System;
        Logger = services.GetRequiredService<ILoggerFactory>().CreateLogger<DurableJobQueue>();
        var format = services.GetRequiredService<IOptions<JournaledStateManagerOptions>>().Value.JournalFormatKey;
        CommandCodec = services.GetRequiredKeyedService<IDurableValueCommandCodec<DurableJobQueueCommand>>(format);
    }

    public DurableJobQueueOptions Options { get; }

    public TimeProvider TimeProvider { get; }

    public ILogger Logger { get; }

    public IDurableValueCommandCodec<DurableJobQueueCommand> CommandCodec { get; }
}

/// <summary>
/// Grain-scoped outbox-style durable job queue implemented as an <see cref="IStateMachine"/> and
/// <see cref="IGrainReminderSource"/> on <see cref="IJournaledStateManager"/> and <see cref="GrainReminderCoordinator"/>.
/// </summary>
internal sealed partial class DurableJobQueue :
    IDurableJobQueue,
    IStateMachine,
    IGrainReminderSource,
    IDurableJobFeatureHandler,
    IDurableValueCommandHandler<DurableJobQueueCommand>,
    ILifecycleObserver,
    IDisposable
{
    /// <summary>The reserved journal state name for the grain-local durable job queue.</summary>
    internal const string StateName = "$orleans.durable-job-queue";

    /// <summary>The reserved reminder job name registered with <see cref="GrainReminderCoordinator"/> and <see cref="IDurableJobHandlerRegistry"/>.</summary>
    internal const string ReminderJobName = "orleans.durable.jobqueue";

    /// <summary>The sentinel shard ID reported on <see cref="DurableJob.ShardId"/> for grain-local queued jobs.</summary>
    internal const string LocalShardId = "$local";

    internal static readonly TimeSpan RecheckDelay = TimeSpan.FromSeconds(1);

    private readonly IGrainContext _grainContext;
    private readonly IJournaledStateManager _stateManager;
    private readonly GrainReminderCoordinator _reminderCoordinator;
    private readonly DurableJobQueueShared _shared;
    private readonly Dictionary<string, JobEntry> _jobs = new(StringComparer.Ordinal);
    private readonly SortedSet<DueOrderKey> _dueOrder = new(DueOrderKeyComparer.Instance);
    private readonly SingleWaiterAutoResetEvent _signal = new() { RunContinuationsAsynchronously = true };
    private readonly CancellationTokenSource _shutdown = new();
    private JournalStreamWriter _writer;
    private IDurableJobQueueHandler? _explicitQueueHandler;
    private IDurableJobHandler? _explicitHandler;
    private long _nextGeneration;
    private long _lastSequence;
    private long _capturedSequence;
    private long _durableSequence;
    private Task? _loop;
    private TaskCompletionSource? _nextPass;
    private bool _failed;

    public DurableJobQueue(
        IGrainContext grainContext,
        IJournaledStateManager stateManager,
        GrainReminderCoordinator reminderCoordinator,
        IDurableJobHandlerRegistry jobHandlers,
        DurableJobQueueShared shared)
    {
        ArgumentNullException.ThrowIfNull(grainContext);
        ArgumentNullException.ThrowIfNull(stateManager);
        ArgumentNullException.ThrowIfNull(reminderCoordinator);
        ArgumentNullException.ThrowIfNull(jobHandlers);
        ArgumentNullException.ThrowIfNull(shared);

        _grainContext = grainContext;
        _stateManager = stateManager;
        _reminderCoordinator = reminderCoordinator;
        _shared = shared;

        _stateManager.RegisterStateMachine(StateName, this);
        _reminderCoordinator.RegisterSource(this, RecheckDelay);
        jobHandlers.Register(this);

        _grainContext.ObservableLifecycle.Subscribe(nameof(DurableJobQueue), GrainLifecycleStage.Activate, this);
        _grainContext.ObservableLifecycle.Subscribe(nameof(DurableJobQueue), GrainLifecycleStage.Last, _ =>
        {
            EnsureStarted();
            return Task.CompletedTask;
        }, static _ => Task.CompletedTask);
    }

    string IGrainReminderSource.JobName => ReminderJobName;

    public IReadOnlyCollection<PendingGrainJob> PendingJobs => _jobs.Count == 0
        ? Array.Empty<PendingGrainJob>()
        : _jobs.Values.Select(static e => e.Job).ToArray();

    public int Count => _jobs.Count;

    internal string? TrackedBackingJobId => _reminderCoordinator.GetTrackedBackingJobId(this);

    internal bool IsFailed => _failed;

    public bool TryGetJob(string jobId, [NotNullWhen(true)] out PendingGrainJob? job)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        if (_jobs.TryGetValue(jobId, out var entry))
        {
            job = entry.Job;
            return true;
        }

        job = null;
        return false;
    }

    public IDurableJobQueue RegisterHandler(IDurableJobHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _explicitHandler = handler;
        _explicitQueueHandler = null;
        if (_jobs.Count > 0)
        {
            Signal();
        }

        return this;
    }

    public IDurableJobQueue RegisterHandler(IDurableJobQueueHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _explicitQueueHandler = handler;
        _explicitHandler = null;
        if (_jobs.Count > 0)
        {
            Signal();
        }

        return this;
    }

    public string Schedule(GrainJobRequest request)
    {
        ThrowIfUnavailable();
        request.Validate(_shared.Options.MaxMetadataBytesPerJob, nameof(request));

        if (request.JobId is { } explicitId && _jobs.TryGetValue(explicitId, out var existing))
        {
            if (IsEquivalent(existing.Job, request))
            {
                return existing.Job.Id;
            }

            throw new InvalidOperationException(
                $"A pending job with id '{explicitId}' already exists with different parameters (name, due time, or metadata).");
        }

        if (_jobs.Count >= _shared.Options.MaxPendingJobs)
        {
            throw new InvalidOperationException(
                $"The durable job queue has reached MaxPendingJobs ({_shared.Options.MaxPendingJobs}).");
        }

        var jobId = request.JobId ?? Guid.NewGuid().ToString("N");
        var metadataCopy = CopyMetadata(request.Metadata);
        var generation = _nextGeneration + 1;
        var pending = new PendingGrainJob
        {
            Id = jobId,
            Name = request.JobName,
            DueTime = request.DueTime,
            Metadata = metadataCopy,
            TraceParent = request.TraceParent ?? Activity.Current?.Id,
            TraceState = request.TraceState ?? Activity.Current?.TraceStateString,
            DequeueCount = 0,
            ExecutionGeneration = generation,
        };

        Emit(new DurableJobQueueCommand.Schedule(pending));
        return jobId;
    }

    public bool Cancel(string jobId)
    {
        ThrowIfUnavailable();
        ArgumentException.ThrowIfNullOrEmpty(jobId);

        if (!_jobs.ContainsKey(jobId))
        {
            return false;
        }

        Emit(new DurableJobQueueCommand.Cancel(jobId));
        return true;
    }

    DateTimeOffset? IGrainReminderSource.GetNextDueTime(DateTimeOffset now)
    {
        if (_dueOrder.Count == 0)
        {
            return null;
        }

        // Fast path: when all staged entries have been captured (or during recovery), _dueOrder.Min is O(1) min-due.
        if (_lastSequence == _capturedSequence)
        {
            return _jobs[_dueOrder.Min.Id].Job.DueTime;
        }

        // Only entries captured into the current write (Sequence <= _capturedSequence) are eligible for wakeup emission.
        foreach (var key in _dueOrder)
        {
            var entry = _jobs[key.Id];
            if (entry.Sequence <= _capturedSequence)
            {
                return entry.Job.DueTime;
            }
        }

        return null;
    }

    ValueTask IStateMachine.PrepareWriteAsync(CancellationToken cancellationToken)
    {
        if (_lastSequence != _capturedSequence && _dueOrder.Count > 0)
        {
            var minDue = _jobs[_dueOrder.Min.Id].Job.DueTime;
            return _reminderCoordinator.EnsureFallbackReminderAsync(this, minDue, _shutdown.Token, cancellationToken);
        }

        return ValueTask.CompletedTask;
    }

    void IStateMachine.Reset(JournalStreamWriter writer)
    {
        _writer = writer;
        _jobs.Clear();
        _dueOrder.Clear();
        _nextGeneration = 0;
        _lastSequence = 0;
        _capturedSequence = 0;
        _durableSequence = 0;
    }

    void IStateMachine.ReplayEntry(JournalEntry entry, JournalReplayContext context) =>
        context.GetRequiredCommandCodec(entry.FormatKey, _shared.CommandCodec).Apply(entry.Reader, this);

    void IStateMachine.OnRecoveryCompleted()
    {
        _capturedSequence = _lastSequence;
        _durableSequence = _lastSequence;
    }

    void IStateMachine.WritePendingEntries(JournalStreamWriter writer) => _capturedSequence = _lastSequence;

    void IStateMachine.WriteSnapshot(JournalStreamWriter writer)
    {
        var jobs = _jobs.Count == 0
            ? Array.Empty<PendingGrainJob>()
            : _jobs.Values.Select(static e => e.Job).ToArray();
        _shared.CommandCodec.WriteSet(new DurableJobQueueCommand.Snapshot(jobs, _nextGeneration), writer);
        _capturedSequence = _lastSequence;
    }

    void IStateMachine.OnWriteCompleted()
    {
        _durableSequence = _capturedSequence;
        if (_dueOrder.Count > 0)
        {
            var minEntry = _jobs[_dueOrder.Min.Id];
            if (minEntry.Sequence <= _durableSequence && minEntry.Job.DueTime <= _shared.TimeProvider.GetUtcNow())
            {
                Signal();
            }
        }
    }

    void IDurableValueCommandHandler<DurableJobQueueCommand>.ApplySet(DurableJobQueueCommand command) => Apply(command);

    public bool CanHandle(string jobName) => string.Equals(jobName, ReminderJobName, StringComparison.Ordinal);

    public ValueTask<DurableJobRunResult> ExecuteJobAsync(IJobRunContext context, CancellationToken attemptCancellationToken) =>
        _reminderCoordinator.ExecuteJobAsync(this, context, RunPassForJobAsync, attemptCancellationToken);

    async Task ILifecycleObserver.OnStart(CancellationToken cancellationToken)
    {
        if (!_reminderCoordinator.SupportsMessageWrites && _dueOrder.Count > 0)
        {
            var minDue = _jobs[_dueOrder.Min.Id].Job.DueTime;
            await _reminderCoordinator.EnsureFallbackReminderAsync(this, minDue, _shutdown.Token, cancellationToken).ConfigureAwait(true);
        }
    }

    Task ILifecycleObserver.OnStop(CancellationToken cancellationToken)
    {
        Stop();
        return Task.CompletedTask;
    }

    public void Dispose() => Stop();

    internal void Signal()
    {
        if (!_shutdown.IsCancellationRequested && !_failed)
        {
            _signal.Signal();
        }
    }

    internal void EnsureStarted()
    {
        if (_loop is null && !_shutdown.IsCancellationRequested && !_failed)
        {
            _loop = RunAsync();
        }
    }

    internal async ValueTask RunPassForJobAsync(CancellationToken cancellationToken)
    {
        EnsureStarted();
        var pass = _nextPass ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
        _signal.Signal();
        await pass.Task.WaitAsync(cancellationToken).ConfigureAwait(true);
        _shutdown.Token.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// Processes all currently due durable jobs once in due-time order.
    /// Exposed internally so deterministic model tests can drive passes directly on the calling context.
    /// </summary>
    internal async ValueTask<bool> ProcessDueJobsAsync(CancellationToken cancellationToken)
    {
        var madeProgress = false;
        while (!cancellationToken.IsCancellationRequested && !_failed)
        {
            var now = _shared.TimeProvider.GetUtcNow();
            if (!TryPickNextDueDurableJob(now, out var entry))
            {
                break;
            }

            madeProgress = true;
            var job = entry.Job;
            var jobId = job.Id;
            var expectedGeneration = job.ExecutionGeneration;
            var runContext = new JobRunContext(
                job.ToDurableJob(_grainContext.GrainId),
                Guid.NewGuid().ToString("N"),
                job.DequeueCount + 1,
                isEphemeralReminder: false);

            DurableJobRunResult result;
            try
            {
                result = await InvokeHandlerAsync(job, runContext, cancellationToken).ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                if (HasDirtyState())
                {
                    FailAndDeactivate(exception, $"Durable job '{jobId}' threw after mutating journaled state.");
                    return madeProgress;
                }

                if (!IsSameExecution(jobId, expectedGeneration))
                {
                    continue;
                }

                ApplyFailureOutcome(job, runContext, exception);
                await _stateManager.WriteStateAsync(cancellationToken).ConfigureAwait(true);
                continue;
            }

            if (result.IsFailed)
            {
                if (HasDirtyState())
                {
                    FailAndDeactivate(result.Exception, $"Durable job '{jobId}' failed after mutating journaled state.");
                    return madeProgress;
                }

                if (!IsSameExecution(jobId, expectedGeneration))
                {
                    continue;
                }

                ApplyFailureOutcome(job, runContext, result.Exception);
                await _stateManager.WriteStateAsync(cancellationToken).ConfigureAwait(true);
                continue;
            }

            // Post-await (jobId, ExecutionGeneration) guard: if the job was cancelled or replaced while awaiting,
            // do not overwrite or delete the newer generation. Still flush any dirty state staged by the handler.
            if (!IsSameExecution(jobId, expectedGeneration))
            {
                if (HasDirtyState())
                {
                    await _stateManager.WriteStateAsync(cancellationToken).ConfigureAwait(true);
                }

                continue;
            }

            if (result.IsRescheduleRequested)
            {
                Emit(new DurableJobQueueCommand.Update(jobId, result.RescheduleTime.Value, dequeueCount: 0, executionGeneration: _nextGeneration + 1));
                await _stateManager.WriteStateAsync(cancellationToken).ConfigureAwait(true);
            }
            else if (result.IsInProgress)
            {
                var nextPoll = _shared.TimeProvider.GetUtcNow() + result.PollAfterDelay.Value;
                Emit(new DurableJobQueueCommand.Update(jobId, nextPoll, job.DequeueCount, executionGeneration: _nextGeneration + 1));
                await _stateManager.WriteStateAsync(cancellationToken).ConfigureAwait(true);
            }
            else
            {
                Emit(new DurableJobQueueCommand.Cancel(jobId));
                await _stateManager.WriteStateAsync(cancellationToken).ConfigureAwait(true);
            }
        }

        return madeProgress;
    }

    private async Task RunAsync()
    {
        await Task.Yield();
        var cancellationToken = _shutdown.Token;
        while (!cancellationToken.IsCancellationRequested && !_failed)
        {
            var pass = _nextPass;
            _nextPass = null;
            var progress = false;
            try
            {
                progress = await ProcessDueJobsAsync(cancellationToken).ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                progress = false;
                LogPassFailed(_shared.Logger, exception, _grainContext.GrainId);
                FailAndDeactivate(exception, "Durable job queue processing pass failed.");
            }

            pass?.TrySetResult();
            if (!progress && !cancellationToken.IsCancellationRequested && !_failed)
            {
                await _signal.WaitAsync().ConfigureAwait(true);
            }
        }

        _nextPass?.TrySetCanceled(cancellationToken);
    }

    private bool TryPickNextDueDurableJob(DateTimeOffset now, [NotNullWhen(true)] out JobEntry? entry)
    {
        foreach (var key in _dueOrder)
        {
            if (key.DueTicks > now.UtcTicks)
            {
                break;
            }

            var candidate = _jobs[key.Id];
            if (candidate.Sequence <= _durableSequence)
            {
                entry = candidate;
                return true;
            }
        }

        entry = null;
        return false;
    }

    private bool IsSameExecution(string jobId, long expectedGeneration) =>
        _jobs.TryGetValue(jobId, out var current) && current.Job.ExecutionGeneration == expectedGeneration;

    private bool HasDirtyState() =>
        _stateManager is JournaledStateManager concrete && concrete.HasUncommittedChanges;

    private async ValueTask<DurableJobRunResult> InvokeHandlerAsync(
        PendingGrainJob job,
        IJobRunContext runContext,
        CancellationToken cancellationToken)
    {
        if (_explicitQueueHandler is { } queueHandler)
        {
            return await queueHandler.ExecuteJobAsync(runContext, cancellationToken).ConfigureAwait(true);
        }

        if (_explicitHandler is { } handler)
        {
            await handler.ExecuteJobAsync(runContext, cancellationToken).ConfigureAwait(true);
            return DurableJobRunResult.Completed;
        }

        if (_grainContext.GrainInstance is IDurableJobQueueHandler grainQueueHandler)
        {
            return await grainQueueHandler.ExecuteJobAsync(runContext, cancellationToken).ConfigureAwait(true);
        }

        if (_grainContext.GrainInstance is IDurableJobHandler grainHandler)
        {
            await grainHandler.ExecuteJobAsync(runContext, cancellationToken).ConfigureAwait(true);
            return DurableJobRunResult.Completed;
        }

        throw new InvalidOperationException(
            $"No IDurableJobQueueHandler or IDurableJobHandler is registered for grain '{_grainContext.GrainId}' to execute job '{job.Name}' (id '{job.Id}').");
    }

    private void ApplyFailureOutcome(PendingGrainJob job, IJobRunContext runContext, Exception exception)
    {
        var nextDue = ComputeRetryDueTime(runContext, exception);
        if (nextDue is { } retryAt)
        {
            Emit(new DurableJobQueueCommand.Update(job.Id, retryAt, job.DequeueCount + 1, executionGeneration: _nextGeneration + 1));
        }
        else
        {
            Emit(new DurableJobQueueCommand.Cancel(job.Id));
        }
    }

    private DateTimeOffset? ComputeRetryDueTime(IJobRunContext context, Exception exception)
    {
        if (_shared.Options.ShouldRetry is { } policy)
        {
            return policy(context, exception);
        }

        if (context.DequeueCount > _shared.Options.MaxRetryCount)
        {
            return null;
        }

        var shift = Math.Min(context.DequeueCount - 1, 30);
        var multiplier = 1L << shift;
        var baseTicks = _shared.Options.RetryInitialInterval.Ticks;
        var maxTicks = _shared.Options.RetryMaxInterval.Ticks;
        var delayTicks = baseTicks > maxTicks / multiplier ? maxTicks : baseTicks * multiplier;
        return _shared.TimeProvider.GetUtcNow() + TimeSpan.FromTicks(delayTicks);
    }

    private void FailAndDeactivate(Exception exception, string description)
    {
        _failed = true;
        Stop();

        if (_stateManager is JournaledStateManager concrete)
        {
            concrete.FenceExternal(exception);
        }

        try
        {
            _grainContext.Deactivate(new DeactivationReason(DeactivationReasonCode.ApplicationError, exception, description));
        }
        catch (Exception deactivationException)
        {
            LogDeactivationFailed(_shared.Logger, deactivationException, _grainContext.GrainId);
        }
    }

    private void Stop()
    {
        if (!_shutdown.IsCancellationRequested)
        {
            _shutdown.Cancel();
        }

        _signal.Signal();
    }

    private void ThrowIfUnavailable()
    {
        _shutdown.Token.ThrowIfCancellationRequested();
        if (_failed)
        {
            throw new InvalidOperationException("The durable job queue has failed and can no longer accept mutations.");
        }
    }

    private void Emit(DurableJobQueueCommand command)
    {
        _shared.CommandCodec.WriteSet(command, _writer);
        Apply(command);
    }

    private void Apply(DurableJobQueueCommand command)
    {
        switch (command)
        {
            case DurableJobQueueCommand.Schedule { Job: var job }:
                if (_jobs.TryGetValue(job.Id, out var oldEntry))
                {
                    _ = _dueOrder.Remove(new DueOrderKey(oldEntry.Job.DueTime.UtcTicks, oldEntry.Job.Id));
                }

                _nextGeneration = Math.Max(_nextGeneration, job.ExecutionGeneration);
                _jobs[job.Id] = new JobEntry(job, ++_lastSequence);
                _ = _dueOrder.Add(new DueOrderKey(job.DueTime.UtcTicks, job.Id));
                break;

            case DurableJobQueueCommand.Cancel { JobId: var jobId }:
                if (_jobs.Remove(jobId, out var removed))
                {
                    _ = _dueOrder.Remove(new DueOrderKey(removed.Job.DueTime.UtcTicks, removed.Job.Id));
                    _lastSequence++;
                }

                break;

            case DurableJobQueueCommand.Update { JobId: var jobId, DueTime: var dueTime, DequeueCount: var dequeueCount, ExecutionGeneration: var generation }:
                if (_jobs.TryGetValue(jobId, out var existing))
                {
                    _ = _dueOrder.Remove(new DueOrderKey(existing.Job.DueTime.UtcTicks, existing.Job.Id));
                    var updated = new PendingGrainJob
                    {
                        Id = existing.Job.Id,
                        Name = existing.Job.Name,
                        DueTime = dueTime,
                        Metadata = existing.Job.Metadata,
                        TraceParent = existing.Job.TraceParent,
                        TraceState = existing.Job.TraceState,
                        DequeueCount = dequeueCount,
                        ExecutionGeneration = generation,
                    };
                    _nextGeneration = Math.Max(_nextGeneration, generation);
                    _jobs[jobId] = new JobEntry(updated, ++_lastSequence);
                    _ = _dueOrder.Add(new DueOrderKey(updated.DueTime.UtcTicks, updated.Id));
                }

                break;

            case DurableJobQueueCommand.Snapshot snapshot:
                _jobs.Clear();
                _dueOrder.Clear();
                _nextGeneration = snapshot.NextGeneration;
                foreach (var restored in snapshot.Jobs)
                {
                    _nextGeneration = Math.Max(_nextGeneration, restored.ExecutionGeneration);
                    _jobs[restored.Id] = new JobEntry(restored, 0);
                    _ = _dueOrder.Add(new DueOrderKey(restored.DueTime.UtcTicks, restored.Id));
                }

                break;
        }
    }

    internal static DateTimeOffset GetReminderRetryTime(IJobRunContext context, DateTimeOffset now, TimeSpan initialInterval)
    {
        var shift = Math.Min(Math.Max(context.DequeueCount - 1, 0), 8);
        var delay = TimeSpan.FromTicks(Math.Min(initialInterval.Ticks * (1L << shift), TimeSpan.FromMinutes(1).Ticks));
        return now + delay;
    }

    private static Dictionary<string, string>? CopyMetadata(IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata is null || metadata.Count == 0)
        {
            return null;
        }

        var copy = new Dictionary<string, string>(metadata.Count, StringComparer.Ordinal);
        foreach (var (key, value) in metadata)
        {
            copy[key] = value;
        }

        return copy;
    }

    private static bool IsEquivalent(PendingGrainJob existing, GrainJobRequest request)
    {
        if (!string.Equals(existing.Name, request.JobName, StringComparison.Ordinal) ||
            existing.DueTime.UtcTicks != request.DueTime.UtcTicks)
        {
            return false;
        }

        var existingCount = existing.Metadata?.Count ?? 0;
        var requestCount = request.Metadata?.Count ?? 0;
        if (existingCount != requestCount)
        {
            return false;
        }

        if (existingCount == 0)
        {
            return true;
        }

        foreach (var (key, value) in existing.Metadata!)
        {
            if (!request.Metadata!.TryGetValue(key, out var other) || !string.Equals(value, other, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private sealed class JobEntry(PendingGrainJob job, long sequence)
    {
        public PendingGrainJob Job { get; } = job;

        public long Sequence { get; } = sequence;
    }

    private readonly record struct DueOrderKey(long DueTicks, string Id);

    private sealed class DueOrderKeyComparer : IComparer<DueOrderKey>
    {
        public static readonly DueOrderKeyComparer Instance = new();

        public int Compare(DueOrderKey x, DueOrderKey y)
        {
            var cmp = x.DueTicks.CompareTo(y.DueTicks);
            return cmp != 0 ? cmp : StringComparer.Ordinal.Compare(x.Id, y.Id);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Durable job queue processing pass failed for grain {GrainId}.")]
    private static partial void LogPassFailed(ILogger logger, Exception exception, GrainId grainId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to deactivate grain {GrainId} after durable job queue failure.")]
    private static partial void LogDeactivationFailed(ILogger logger, Exception exception, GrainId grainId);
}
