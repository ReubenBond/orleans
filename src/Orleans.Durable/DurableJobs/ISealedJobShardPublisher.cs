using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Durable.Jobs;

/// <summary>
/// Publishes deterministic, sealed, unowned durable job shards for time-bucketed wakeup or job drainage.
/// </summary>
public interface ISealedJobShardPublisher
{
    /// <summary>Gets the configured durable job shard bucket duration used to partition due times.</summary>
    TimeSpan ShardDuration { get; }

    /// <summary>
    /// Atomically publishes a sealed, unowned job shard with a deterministic identity and initial jobs.
    /// </summary>
    /// <param name="deterministicId">
    /// A deterministic suffix uniquely identifying this sealed shard within its time bucket (for example,
    /// <c>drain-{ledgerId:D20}-{segmentStart:D20}-{bucketUtcTicks:D19}</c>).
    /// </param>
    /// <param name="minDueTime">The inclusive start of the shard's time bucket.</param>
    /// <param name="maxDueTime">The inclusive end of the shard's time bucket.</param>
    /// <param name="jobs">The non-empty, canonically ordered jobs to publish in the shard.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>
    /// <see langword="true"/> if the sealed shard was newly published; <see langword="false"/> if a shard with the
    /// same deterministic identity already existed.
    /// </returns>
    Task<bool> PublishSealedShardAsync(
        string deterministicId,
        DateTimeOffset minDueTime,
        DateTimeOffset maxDueTime,
        IReadOnlyList<ScheduleJobRequest> jobs,
        CancellationToken cancellationToken);
}
