using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Durable.Journaling;
using Orleans.Runtime;

namespace Orleans.Durable.Jobs;

/// <summary>
/// Grain-scoped coordinator that binds <see cref="IGrainWakeupSource"/> instances to atomic ledger-local
/// <c>orleans.wakeup</c> companion writes when supported by <see cref="JournaledStateManager"/>, or falls back to
/// <see cref="ILocalDurableJobManager.ScheduleJobAsync"/> otherwise (orleans.md §5.15).
/// </summary>
internal sealed class GrainWakeupCoordinator : ICompanionWriteParticipant
{
    internal static readonly TimeSpan DefaultRecheckDelay = TimeSpan.FromSeconds(1);

    private readonly IGrainContext _grainContext;
    private readonly IJournaledStateManager _stateManager;
    private readonly ILocalDurableJobManager _jobManager;
    private readonly TimeProvider _timeProvider;
    private readonly List<SourceState> _sources = [];
    private readonly Dictionary<string, SourceState> _byJobName = new(StringComparer.Ordinal);

    /// <summary>Initializes a new instance of the <see cref="GrainWakeupCoordinator"/> class.</summary>
    public GrainWakeupCoordinator(
        IGrainContext grainContext,
        IJournaledStateManager stateManager,
        ILocalDurableJobManager jobManager,
        [FromKeyedServices(DurableJobTimeProviderNames.DurableJobs)] TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(grainContext);
        ArgumentNullException.ThrowIfNull(stateManager);
        ArgumentNullException.ThrowIfNull(jobManager);
        _grainContext = grainContext;
        _stateManager = stateManager;
        _jobManager = jobManager;
        _timeProvider = timeProvider ?? TimeProvider.System;

        if (_stateManager is JournaledStateManager journaledStateManager)
        {
            journaledStateManager.RegisterCompanionParticipant(this);
        }
    }

    /// <summary>
    /// Gets a value indicating whether the grain's journal storage supports atomic companion writes.
    /// </summary>
    public bool SupportsCompanionWrites => _stateManager is JournaledStateManager { SupportsCompanionWrites: true };

    /// <summary>Registers a wakeup source on this grain activation.</summary>
    /// <param name="source">The wakeup source.</param>
    /// <param name="recheckDelay">Optional minimum recheck clamp for immediate/past due work (defaults to 1 second).</param>
    /// <exception cref="ArgumentException">A source with the same <see cref="IGrainWakeupSource.JobName"/> is already registered.</exception>
    public void RegisterSource(IGrainWakeupSource source, TimeSpan? recheckDelay = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(source.JobName, nameof(source));
        var delay = recheckDelay ?? DefaultRecheckDelay;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(delay, TimeSpan.Zero, nameof(recheckDelay));

        if (_byJobName.ContainsKey(source.JobName))
        {
            throw new ArgumentException($"A wakeup source for job '{source.JobName}' is already registered on this grain.", nameof(source));
        }

        var state = new SourceState(source, delay);
        _sources.Add(state);
        _byJobName.Add(source.JobName, state);
    }

    /// <summary>
    /// Ensures a wakeup is covered during <c>PrepareWriteAsync</c>: when companion writes are supported, returns
    /// synchronously with zero <c>ScheduleJobAsync</c> calls (emission happens atomically in write capture); otherwise
    /// delegates directly to <see cref="EnsureBackingWakeupAsync"/>.
    /// </summary>
    public ValueTask EnsureFallbackWakeupAsync(
        IGrainWakeupSource source,
        DateTimeOffset dueTime,
        CancellationToken shutdownToken,
        CancellationToken cancellationToken)
    {
        if (SupportsCompanionWrites)
        {
            return ValueTask.CompletedTask;
        }

        return EnsureBackingWakeupAsync(source, dueTime, shutdownToken, cancellationToken);
    }

    /// <summary>
    /// Ensures a backing durable job via <see cref="ILocalDurableJobManager.ScheduleJobAsync"/> will wake this grain
    /// for <paramref name="source"/> no later than <paramref name="dueTime"/> (clamped to at least <c>now + RecheckDelay</c>).
    /// </summary>
    public async ValueTask EnsureBackingWakeupAsync(
        IGrainWakeupSource source,
        DateTimeOffset dueTime,
        CancellationToken shutdownToken,
        CancellationToken cancellationToken)
    {
        var entry = GetRequiredState(source);
        var now = _timeProvider.GetUtcNow();
        dueTime = ClampDueTime(now, dueTime, entry.RecheckDelay);

        if (entry.WakeupJobId is not null && entry.WakeupDueTime <= dueTime)
        {
            return;
        }

        if (entry.Scheduling is null || entry.SchedulingEpoch != entry.WakeupEpoch || entry.SchedulingDueTime > dueTime)
        {
            entry.SchedulingEpoch = entry.WakeupEpoch;
            entry.SchedulingDueTime = dueTime;
            entry.Scheduling = _jobManager.ScheduleJobAsync(
                new ScheduleJobRequest
                {
                    Target = _grainContext.GrainId,
                    JobName = source.JobName,
                    DueTime = entry.SchedulingDueTime,
                },
                shutdownToken);
        }

        var scheduling = entry.Scheduling;
        var epoch = entry.SchedulingEpoch;
        try
        {
            var job = await scheduling.WaitAsync(cancellationToken).ConfigureAwait(true);
            if (epoch == entry.WakeupEpoch && (entry.WakeupJobId is null || job.DueTime < entry.WakeupDueTime))
            {
                entry.WakeupJobId = job.Id;
                entry.WakeupDueTime = job.DueTime;
            }
        }
        finally
        {
            if (scheduling.IsCompleted && ReferenceEquals(entry.Scheduling, scheduling))
            {
                entry.Scheduling = null;
            }
        }
    }

    /// <summary>
    /// Executes due work for <paramref name="source"/> and decides the resulting <see cref="DurableJobRunResult"/>.
    /// Tracks <c>(WakeupJobId, WakeupDueTime)</c> exclusively for backing durable jobs
    /// (<c>!context.IsEphemeralWakeup</c>), adopting an arriving backing job whenever it is the only tracked job,
    /// matches the tracked job, or wakes earlier than the tracked job, while returning <c>Completed</c> for redundant
    /// later backing jobs.
    /// </summary>
    public async ValueTask<DurableJobRunResult> ExecuteJobAsync(
        IGrainWakeupSource source,
        IJobRunContext context,
        Func<CancellationToken, ValueTask> runWorkAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(runWorkAsync);

        var entry = GetRequiredState(source);
        var isEphemeral = context.IsEphemeralWakeup;
        var jobId = context.Job.Id;
        if (!isEphemeral && entry.WakeupJobId is null)
        {
            entry.WakeupJobId = jobId;
            entry.WakeupDueTime = context.Job.DueTime;
        }

        try
        {
            await runWorkAsync(cancellationToken).ConfigureAwait(true);
        }
        catch
        {
            if (!isEphemeral)
            {
                CompleteBackingWakeup(entry, jobId);
            }

            throw;
        }

        var now = _timeProvider.GetUtcNow();
        if (source.GetNextDueTime(now) is not { } rawDue)
        {
            if (!isEphemeral)
            {
                CompleteBackingWakeup(entry, jobId);
            }

            return DurableJobRunResult.Completed;
        }

        var next = rawDue > now ? rawDue : AddClamped(now, entry.RecheckDelay);
        if (isEphemeral)
        {
            // Ephemeral open-ledger wakeups never mutate backing-job tracking.
            return DurableJobRunResult.RescheduleAt(next);
        }

        // Backing job: adopt if untracked, same job, or wakes strictly earlier than the currently tracked backing job.
        if (entry.WakeupJobId is null || entry.WakeupJobId == jobId || next < entry.WakeupDueTime)
        {
            entry.WakeupJobId = jobId;
            entry.WakeupDueTime = next;
            return DurableJobRunResult.RescheduleAt(next);
        }

        // Redundant later backing job: leave the earlier tracked backing job intact and complete this one.
        entry.WakeupEpoch++;
        return DurableJobRunResult.Completed;
    }

    /// <summary>Gets the currently tracked backing durable job ID for <paramref name="source"/>, if any; for tests.</summary>
    internal string? GetTrackedBackingJobId(IGrainWakeupSource source) => GetRequiredState(source).WakeupJobId;

    /// <summary>Awaits any in-flight backing <c>ScheduleJobAsync</c> calls on deactivation.</summary>
    public async Task WaitForPendingSchedulingAsync()
    {
        foreach (var entry in _sources)
        {
            if (entry.Scheduling is { } scheduling)
            {
                await ((Task)scheduling).ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    /// <inheritdoc />
    void ICompanionWriteParticipant.OnRecoveryCompleted()
    {
        var now = _timeProvider.GetUtcNow();
        foreach (var entry in _sources)
        {
            entry.HadPendingWork = entry.Source.GetNextDueTime(now) is not null;
            entry.LastEmittedDueTime = null;
            entry.HasPendingCapture = false;
        }
    }

    /// <inheritdoc />
    ValueTask ICompanionWriteParticipant.PrepareWriteAsync(CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    /// <inheritdoc />
    void ICompanionWriteParticipant.CaptureCompanions(JournaledStateManager stateManager)
    {
        var now = _timeProvider.GetUtcNow();
        foreach (var entry in _sources)
        {
            if (entry.Source.GetNextDueTime(now) is { } rawDue)
            {
                var clamped = ClampDueTime(now, rawDue, entry.RecheckDelay);
                if (entry.LastEmittedDueTime is { } prev && now < prev && clamped >= prev)
                {
                    // Existing earlier wakeup already covers this write on the current ledger.
                    continue;
                }

                var payload = WakeupCompanionCodec.Encode(new WakeupRecord(_grainContext.GrainId, entry.Source.JobName, clamped.UtcTicks));
                stateManager.StageCompanionWrite(new JournalCompanionRecord(WakeupCompanionCodec.StreamName, payload));
                entry.PendingHadPendingWork = true;
                entry.PendingEmittedDueTime = clamped;
                entry.HasPendingCapture = true;
            }
            else if (entry.HadPendingWork)
            {
                var payload = WakeupCompanionCodec.Encode(new WakeupRecord(_grainContext.GrainId, entry.Source.JobName, DueTimeUtcTicks: null));
                stateManager.StageCompanionWrite(new JournalCompanionRecord(WakeupCompanionCodec.StreamName, payload));
                entry.PendingHadPendingWork = false;
                entry.PendingEmittedDueTime = null;
                entry.HasPendingCapture = true;
            }
        }
    }

    /// <inheritdoc />
    void ICompanionWriteParticipant.OnWriteCompleted()
    {
        foreach (var entry in _sources)
        {
            if (entry.HasPendingCapture)
            {
                entry.HadPendingWork = entry.PendingHadPendingWork;
                entry.LastEmittedDueTime = entry.PendingEmittedDueTime;
                entry.HasPendingCapture = false;
            }
        }
    }

    /// <inheritdoc />
    void ICompanionWriteParticipant.OnReset(bool clearPendingWork)
    {
        foreach (var entry in _sources)
        {
            entry.HasPendingCapture = false;
            entry.LastEmittedDueTime = null;
            if (clearPendingWork)
            {
                entry.HadPendingWork = false;
            }
        }
    }

    private SourceState GetRequiredState(IGrainWakeupSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (_byJobName.TryGetValue(source.JobName, out var state))
        {
            return state;
        }

        throw new InvalidOperationException($"Wakeup source '{source.JobName}' is not registered with GrainWakeupCoordinator.");
    }

    private static void CompleteBackingWakeup(SourceState entry, string jobId)
    {
        if (entry.WakeupJobId == jobId)
        {
            entry.WakeupJobId = null;
        }

        entry.WakeupEpoch++;
    }

    private static DateTimeOffset ClampDueTime(DateTimeOffset now, DateTimeOffset dueTime, TimeSpan recheckDelay)
    {
        var earliest = AddClamped(now, recheckDelay);
        return dueTime < earliest ? earliest : dueTime;
    }

    private static DateTimeOffset AddClamped(DateTimeOffset time, TimeSpan delay)
    {
        if (delay <= TimeSpan.Zero)
        {
            return time;
        }

        var remaining = DateTimeOffset.MaxValue - time;
        return delay >= remaining ? DateTimeOffset.MaxValue : time + delay;
    }

    private sealed class SourceState(IGrainWakeupSource source, TimeSpan recheckDelay)
    {
        public IGrainWakeupSource Source { get; } = source;

        public TimeSpan RecheckDelay { get; } = recheckDelay;

        // Companion-write emission tracking.
        public bool HadPendingWork;
        public DateTimeOffset? LastEmittedDueTime;
        public bool HasPendingCapture;
        public bool PendingHadPendingWork;
        public DateTimeOffset? PendingEmittedDueTime;

        // Backing durable job tracking (!IsEphemeralWakeup).
        public string? WakeupJobId;
        public DateTimeOffset WakeupDueTime;
        public int WakeupEpoch;
        public Task<DurableJob>? Scheduling;
        public DateTimeOffset SchedulingDueTime;
        public int SchedulingEpoch;
    }
}
