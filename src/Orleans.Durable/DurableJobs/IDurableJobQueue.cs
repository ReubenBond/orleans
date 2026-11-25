using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using Orleans.Runtime;

namespace Orleans.Durable.Jobs;

/// <summary>
/// Represents a request to schedule a grain-local durable job through <see cref="IDurableJobQueue"/>.
/// </summary>
public readonly struct GrainJobRequest
{
    /// <summary>Gets the maximum allowed character length of an explicit <see cref="JobId"/>.</summary>
    public const int MaxJobIdLength = 128;

    /// <summary>Gets the maximum allowed character length of <see cref="JobName"/>.</summary>
    public const int MaxJobNameLength = 128;

    /// <summary>
    /// Gets the optional caller-supplied job identifier for idempotent scheduling.
    /// When <see langword="null"/>, a unique identifier is generated automatically.
    /// </summary>
    public string? JobId { get; init; }

    /// <summary>
    /// Gets the non-empty name of the job for identification and handler routing.
    /// </summary>
    public required string JobName { get; init; }

    /// <summary>
    /// Gets the date and time when the job should be executed.
    /// </summary>
    public required DateTimeOffset DueTime { get; init; }

    /// <summary>
    /// Gets optional string metadata associated with the job.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    /// <summary>
    /// Gets the optional W3C <c>traceparent</c> value captured for distributed tracing.
    /// </summary>
    public string? TraceParent { get; init; }

    /// <summary>
    /// Gets the optional W3C <c>tracestate</c> value captured for distributed tracing.
    /// </summary>
    public string? TraceState { get; init; }

    internal void Validate(int maxMetadataBytesPerJob, string paramName)
    {
        if (string.IsNullOrWhiteSpace(JobName))
        {
            throw new ArgumentException("Job name must not be null or whitespace.", paramName);
        }

        if (JobName.Length > MaxJobNameLength)
        {
            throw new ArgumentException($"Job name length ({JobName.Length}) exceeds the maximum of {MaxJobNameLength} characters.", paramName);
        }

        if (string.Equals(JobName, DurableJobQueue.ReminderJobName, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Job name '{JobName}' is reserved by the durable job queue.", paramName);
        }

        if (JobId is not null)
        {
            if (string.IsNullOrWhiteSpace(JobId))
            {
                throw new ArgumentException("Job id must not be whitespace when specified.", paramName);
            }

            if (JobId.Length > MaxJobIdLength)
            {
                throw new ArgumentException($"Job id length ({JobId.Length}) exceeds the maximum of {MaxJobIdLength} characters.", paramName);
            }
        }

        if (Metadata is not null)
        {
            var totalBytes = 0;
            foreach (var (key, value) in Metadata)
            {
                if (key is null)
                {
                    throw new ArgumentException("Job metadata keys must not be null.", paramName);
                }

                totalBytes += Encoding.UTF8.GetByteCount(key);
                if (value is not null)
                {
                    totalBytes += Encoding.UTF8.GetByteCount(value);
                }

                if (totalBytes > maxMetadataBytesPerJob)
                {
                    throw new ArgumentException(
                        $"Job metadata UTF-8 byte count exceeds MaxMetadataBytesPerJob ({maxMetadataBytesPerJob} bytes).",
                        paramName);
                }
            }
        }
    }
}

/// <summary>
/// Represents a pending grain-local durable job stored in a grain's <see cref="IDurableJobQueue"/>.
/// </summary>
[GenerateSerializer]
[Alias("Orleans.Durable.Jobs.PendingGrainJob")]
public sealed class PendingGrainJob
{
    /// <summary>Gets the unique identifier of this pending job within the grain.</summary>
    [Id(0)]
    public required string Id { get; init; }

    /// <summary>Gets the name of the job for identification and handler routing.</summary>
    [Id(1)]
    public required string Name { get; init; }

    /// <summary>Gets the time at which this job is due to execute.</summary>
    [Id(2)]
    public DateTimeOffset DueTime { get; init; }

    /// <summary>Gets optional metadata associated with this job.</summary>
    [Id(3)]
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    /// <summary>Gets the W3C <c>traceparent</c> value associated with this job.</summary>
    [Id(4)]
    public string? TraceParent { get; init; }

    /// <summary>Gets the W3C <c>tracestate</c> value associated with this job.</summary>
    [Id(5)]
    public string? TraceState { get; init; }

    /// <summary>Gets the number of times this job has been dequeued for execution, including retries.</summary>
    [Id(6)]
    public int DequeueCount { get; init; }

    /// <summary>Gets the monotonic execution generation used to guard post-await cancellation and rescheduling.</summary>
    [Id(7)]
    public long ExecutionGeneration { get; init; }

    internal DurableJob ToDurableJob(GrainId targetGrainId) =>
        new()
        {
            Id = Id,
            Name = Name,
            DueTime = DueTime,
            TargetGrainId = targetGrainId,
            ShardId = DurableJobQueue.LocalShardId,
            Metadata = Metadata,
            TraceParent = TraceParent,
            TraceState = TraceState,
            ExecutionGeneration = ExecutionGeneration,
        };
}

/// <summary>
/// Configuration options for <see cref="IDurableJobQueue"/>.
/// </summary>
public sealed class DurableJobQueueOptions
{
    /// <summary>
    /// Gets or sets the maximum number of pending jobs allowed in a single grain's durable job queue.
    /// Defaults to <c>1024</c>.
    /// </summary>
    public int MaxPendingJobs { get; set; } = 1024;

    /// <summary>
    /// Gets or sets the maximum total UTF-8 byte length of metadata keys and values allowed per scheduled job.
    /// Defaults to <c>4096</c> bytes.
    /// </summary>
    public int MaxMetadataBytesPerJob { get; set; } = 4 * 1024;

    /// <summary>
    /// Gets or sets the initial retry delay after a clean transient handler failure.
    /// Defaults to 1 second.
    /// </summary>
    public TimeSpan RetryInitialInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Gets or sets the maximum retry delay after repeated clean transient handler failures.
    /// Defaults to 1 minute.
    /// </summary>
    public TimeSpan RetryMaxInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Gets or sets the maximum number of retries after clean transient handler failures before the job is dropped.
    /// Defaults to <c>5</c>.
    /// </summary>
    public int MaxRetryCount { get; set; } = 5;

    /// <summary>
    /// Gets or sets an optional retry policy delegate invoked when a job handler fails cleanly with no dirty journaled state.
    /// Returning a <see cref="DateTimeOffset"/> reschedules the job at that time; returning <see langword="null"/> removes it.
    /// </summary>
    public Func<IJobRunContext, Exception, DateTimeOffset?>? ShouldRetry { get; set; }

    internal void Validate()
    {
        if (MaxPendingJobs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxPendingJobs), MaxPendingJobs, "MaxPendingJobs must be positive.");
        }

        if (MaxMetadataBytesPerJob is < 0 or > 64 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxMetadataBytesPerJob), MaxMetadataBytesPerJob, "MaxMetadataBytesPerJob must be between 0 and 65536 bytes.");
        }

        if (RetryInitialInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(RetryInitialInterval), RetryInitialInterval, "RetryInitialInterval must be positive.");
        }

        if (RetryMaxInterval < RetryInitialInterval)
        {
            throw new ArgumentOutOfRangeException(nameof(RetryMaxInterval), RetryMaxInterval, "RetryMaxInterval must be greater than or equal to RetryInitialInterval.");
        }

        if (MaxRetryCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxRetryCount), MaxRetryCount, "MaxRetryCount must be non-negative.");
        }
    }
}

/// <summary>
/// Handles execution of grain-local durable jobs from <see cref="IDurableJobQueue"/> with an explicit
/// <see cref="DurableJobRunResult"/> disposition (such as <see cref="DurableJobRunResult.Completed"/> or
/// <see cref="DurableJobRunResult.RescheduleAt"/>).
/// </summary>
public interface IDurableJobQueueHandler
{
    /// <summary>
    /// Executes a grain-local durable job and returns its disposition.
    /// </summary>
    /// <param name="context">The execution context for the job.</param>
    /// <param name="attemptCancellationToken">A token requesting cancellation of the current attempt.</param>
    /// <returns>The disposition of the job run.</returns>
    ValueTask<DurableJobRunResult> ExecuteJobAsync(IJobRunContext context, CancellationToken attemptCancellationToken);
}

/// <summary>
/// Provides an outbox-style, grain-local durable job queue that commits atomically alongside a grain's journaled state.
/// </summary>
/// <remarks>
/// Jobs staged through <see cref="Schedule"/> and <see cref="Cancel"/> become durable when the grain commits its
/// <see cref="Journaling.IJournaledStateManager"/> via <see cref="Journaling.IDurableStateManager.WriteStateAsync"/>.
/// When the underlying journal storage supports atomic companion writes, the queue's self-wakeup commits in the same
/// single quorum write as the grain's state without writing to a separate durable-job shard.
/// </remarks>
public interface IDurableJobQueue
{
    /// <summary>
    /// Gets the currently pending jobs in this grain's queue, reflecting staged and committed state.
    /// </summary>
    IReadOnlyCollection<PendingGrainJob> PendingJobs { get; }

    /// <summary>
    /// Gets the number of pending jobs in this grain's queue.
    /// </summary>
    int Count { get; }

    /// <summary>
    /// Synchronously stages a durable job to be committed with the grain's next journal write.
    /// </summary>
    /// <param name="request">The job request.</param>
    /// <returns>The job identifier (either <see cref="GrainJobRequest.JobId"/> when specified, or a generated identifier).</returns>
    /// <exception cref="ArgumentException">The request has an invalid job name, job id, or metadata exceeding limits.</exception>
    /// <exception cref="InvalidOperationException">
    /// The queue has reached <see cref="DurableJobQueueOptions.MaxPendingJobs"/>, an existing pending job with the same
    /// <see cref="GrainJobRequest.JobId"/> has conflicting parameters, or the queue is unavailable.
    /// </exception>
    string Schedule(GrainJobRequest request);

    /// <summary>
    /// Synchronously stages cancellation of a pending job for the grain's next journal write.
    /// </summary>
    /// <param name="jobId">The identifier of the job to cancel.</param>
    /// <returns><see langword="true"/> if the job was pending and cancellation was staged; otherwise, <see langword="false"/>.</returns>
    bool Cancel(string jobId);

    /// <summary>
    /// Attempts to retrieve a pending job by its identifier.
    /// </summary>
    /// <param name="jobId">The job identifier.</param>
    /// <param name="job">When this method returns <see langword="true"/>, the pending job.</param>
    /// <returns><see langword="true"/> if a job with <paramref name="jobId"/> is pending; otherwise, <see langword="false"/>.</returns>
    bool TryGetJob(string jobId, [NotNullWhen(true)] out PendingGrainJob? job);

    /// <summary>
    /// Registers an explicit <see cref="IDurableJobHandler"/> used to execute due jobs in this grain's queue.
    /// When no explicit handler is registered, the grain instance itself is invoked if it implements
    /// <see cref="IDurableJobQueueHandler"/> or <see cref="IDurableJobHandler"/>.
    /// </summary>
    /// <param name="handler">The handler to invoke for due jobs.</param>
    /// <returns>This queue.</returns>
    IDurableJobQueue RegisterHandler(IDurableJobHandler handler);

    /// <summary>
    /// Registers an explicit <see cref="IDurableJobQueueHandler"/> used to execute due jobs in this grain's queue with
    /// explicit <see cref="DurableJobRunResult"/> dispositions.
    /// </summary>
    /// <param name="handler">The handler to invoke for due jobs.</param>
    /// <returns>This queue.</returns>
    IDurableJobQueue RegisterHandler(IDurableJobQueueHandler handler);
}
