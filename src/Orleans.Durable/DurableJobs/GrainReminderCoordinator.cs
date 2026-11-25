using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Durable.Journaling;
using Orleans.Runtime;

namespace Orleans.Durable.Jobs;

/// <summary>
/// Grain-scoped coordinator that binds <see cref="IGrainReminderSource"/> instances to atomic ledger-local
/// <c>orleans.reminder</c> message writes when supported by <see cref="JournaledStateManager"/>, or falls back to
/// <see cref="ILocalDurableJobManager.ScheduleJobAsync"/> otherwise (orleans.md §5.15).
/// </summary>
internal sealed class GrainReminderCoordinator : IMessageWriteParticipant
{
    internal static readonly TimeSpan DefaultRecheckDelay = TimeSpan.FromSeconds(1);

    private readonly IGrainContext _grainContext;
    private readonly IJournaledStateManager _stateManager;
    private readonly ILocalDurableJobManager _jobManager;
    private readonly TimeProvider _timeProvider;
    private readonly List<SourceState> _sources = [];
    private readonly Dictionary<string, SourceState> _byJobName = new(StringComparer.Ordinal);

    /// <summary>Initializes a new instance of the <see cref="GrainReminderCoordinator"/> class.</summary>
    public GrainReminderCoordinator(
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
            journaledStateManager.RegisterMessageParticipant(this);
        }
    }

    /// <summary>
    /// Gets a value indicating whether the grain's journal storage supports atomic message writes.
    /// </summary>
    public bool SupportsMessageWrites => _stateManager is JournaledStateManager { SupportsMessageWrites: true };

    /// <summary>Registers a reminder source on this grain activation.</summary>
    /// <param name="source">The reminder source.</param>
    /// <param name="recheckDelay">Optional minimum recheck clamp for immediate/past due work (defaults to 1 second).</param>
    /// <exception cref="ArgumentException">A source with the same <see cref="IGrainReminderSource.JobName"/> is already registered.</exception>
    public void RegisterSource(IGrainReminderSource source, TimeSpan? recheckDelay = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(source.JobName, nameof(source));
        var delay = recheckDelay ?? DefaultRecheckDelay;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(delay, TimeSpan.Zero, nameof(recheckDelay));

        if (_byJobName.ContainsKey(source.JobName))
        {
            throw new ArgumentException($"A reminder source for job '{source.JobName}' is already registered on this grain.", nameof(source));
        }

        var state = new SourceState(source, delay);
        _sources.Add(state);
        _byJobName.Add(source.JobName, state);
    }

    /// <summary>
    /// Ensures a reminder is covered during <c>PrepareWriteAsync</c>: when message writes are supported, returns
    /// synchronously with zero <c>ScheduleJobAsync</c> calls (emission happens atomically in write capture); otherwise
    /// delegates directly to <see cref="EnsureBackingReminderAsync"/>.
    /// </summary>
    public ValueTask EnsureFallbackReminderAsync(
        IGrainReminderSource source,
        DateTimeOffset dueTime,
        CancellationToken shutdownToken,
        CancellationToken cancellationToken)
    {
        if (SupportsMessageWrites)
        {
            return ValueTask.CompletedTask;
        }

        return EnsureBackingReminderAsync(source, dueTime, shutdownToken, cancellationToken);
    }

    /// <summary>
    /// Ensures a backing durable job via <see cref="ILocalDurableJobManager.ScheduleJobAsync"/> will wake this grain
    /// for <paramref name="source"/> no later than <paramref name="dueTime"/> (clamped to at least <c>now + RecheckDelay</c>).
    /// </summary>
    public async ValueTask EnsureBackingReminderAsync(
        IGrainReminderSource source,
        DateTimeOffset dueTime,
        CancellationToken shutdownToken,
        CancellationToken cancellationToken)
    {
        var entry = GetRequiredState(source);
        var now = _timeProvider.GetUtcNow();
        dueTime = ClampDueTime(now, dueTime, entry.RecheckDelay);

        if (entry.ReminderJobId is not null && entry.ReminderDueTime <= dueTime)
        {
            return;
        }

        if (entry.Scheduling is null || entry.SchedulingEpoch != entry.ReminderEpoch || entry.SchedulingDueTime > dueTime)
        {
            entry.SchedulingEpoch = entry.ReminderEpoch;
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
            if (epoch == entry.ReminderEpoch && (entry.ReminderJobId is null || job.DueTime < entry.ReminderDueTime))
            {
                entry.ReminderJobId = job.Id;
                entry.ReminderDueTime = job.DueTime;
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
    /// Tracks <c>(ReminderJobId, ReminderDueTime)</c> exclusively for backing durable jobs
    /// (<c>!context.IsEphemeralReminder</c>), adopting an arriving backing job whenever it is the only tracked job,
    /// matches the tracked job, or fires earlier than the tracked job, while returning <c>Completed</c> for redundant
    /// later backing jobs.
    /// </summary>
    public async ValueTask<DurableJobRunResult> ExecuteJobAsync(
        IGrainReminderSource source,
        IJobRunContext context,
        Func<CancellationToken, ValueTask> runWorkAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(runWorkAsync);

        var entry = GetRequiredState(source);
        var isEphemeral = context.IsEphemeralReminder;
        var jobId = context.Job.Id;
        if (!isEphemeral && entry.ReminderJobId is null)
        {
            entry.ReminderJobId = jobId;
            entry.ReminderDueTime = context.Job.DueTime;
        }

        try
        {
            await runWorkAsync(cancellationToken).ConfigureAwait(true);
        }
        catch
        {
            if (!isEphemeral)
            {
                CompleteBackingReminder(entry, jobId);
            }

            throw;
        }

        var now = _timeProvider.GetUtcNow();
        if (source.GetNextDueTime(now) is not { } rawDue)
        {
            if (!isEphemeral)
            {
                CompleteBackingReminder(entry, jobId);
            }

            return DurableJobRunResult.Completed;
        }

        var next = rawDue > now ? rawDue : AddClamped(now, entry.RecheckDelay);
        if (isEphemeral)
        {
            // Ephemeral open-ledger reminders never mutate backing-job tracking.
            return DurableJobRunResult.RescheduleAt(next);
        }

        // Backing job: adopt if untracked, same job, or fires strictly earlier than the currently tracked backing job.
        if (entry.ReminderJobId is null || entry.ReminderJobId == jobId || next < entry.ReminderDueTime)
        {
            entry.ReminderJobId = jobId;
            entry.ReminderDueTime = next;
            return DurableJobRunResult.RescheduleAt(next);
        }

        // Redundant later backing job: leave the earlier tracked backing job intact and complete this one.
        entry.ReminderEpoch++;
        return DurableJobRunResult.Completed;
    }

    /// <summary>Gets the currently tracked backing durable job ID for <paramref name="source"/>, if any; for tests.</summary>
    internal string? GetTrackedBackingJobId(IGrainReminderSource source) => GetRequiredState(source).ReminderJobId;

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
    void IMessageWriteParticipant.OnRecoveryCompleted()
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
    void IMessageWriteParticipant.CaptureMessages(JournaledStateManager stateManager)
    {
        var now = _timeProvider.GetUtcNow();
        foreach (var entry in _sources)
        {
            if (entry.Source.GetNextDueTime(now) is { } rawDue)
            {
                var clamped = ClampDueTime(now, rawDue, entry.RecheckDelay);
                if (entry.LastEmittedDueTime is { } prev && now < prev && clamped >= prev)
                {
                    // Existing earlier reminder already covers this write on the current ledger.
                    continue;
                }

                var payload = ReminderMessageCodec.Encode(new ReminderRecord(_grainContext.GrainId, entry.Source.JobName, clamped.UtcTicks));
                stateManager.StageMessageWrite(new JournalMessageRecord(ReminderMessageCodec.ChannelName, payload));
                entry.PendingHadPendingWork = true;
                entry.PendingEmittedDueTime = clamped;
                entry.HasPendingCapture = true;
            }
            else if (entry.HadPendingWork)
            {
                var payload = ReminderMessageCodec.Encode(new ReminderRecord(_grainContext.GrainId, entry.Source.JobName, DueTimeUtcTicks: null));
                stateManager.StageMessageWrite(new JournalMessageRecord(ReminderMessageCodec.ChannelName, payload));
                entry.PendingHadPendingWork = false;
                entry.PendingEmittedDueTime = null;
                entry.HasPendingCapture = true;
            }
        }
    }

    /// <inheritdoc />
    void IMessageWriteParticipant.OnWriteCompleted()
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
    void IMessageWriteParticipant.OnWriteFailedOrReset(bool clearPendingWork)
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

    private SourceState GetRequiredState(IGrainReminderSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (_byJobName.TryGetValue(source.JobName, out var state))
        {
            return state;
        }

        throw new InvalidOperationException($"Reminder source '{source.JobName}' is not registered with GrainReminderCoordinator.");
    }

    private static void CompleteBackingReminder(SourceState entry, string jobId)
    {
        if (entry.ReminderJobId == jobId)
        {
            entry.ReminderJobId = null;
        }

        entry.ReminderEpoch++;
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

    private sealed class SourceState(IGrainReminderSource source, TimeSpan recheckDelay)
    {
        public IGrainReminderSource Source { get; } = source;

        public TimeSpan RecheckDelay { get; } = recheckDelay;

        // Atomic message emission tracking.
        public bool HadPendingWork;
        public DateTimeOffset? LastEmittedDueTime;
        public bool HasPendingCapture;
        public bool PendingHadPendingWork;
        public DateTimeOffset? PendingEmittedDueTime;

        // Backing durable job tracking (!IsEphemeralReminder).
        public string? ReminderJobId;
        public DateTimeOffset ReminderDueTime;
        public int ReminderEpoch;
        public Task<DurableJob>? Scheduling;
        public DateTimeOffset SchedulingDueTime;
        public int SchedulingEpoch;
    }
}
