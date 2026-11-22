using System.Globalization;
using Orleans.Configuration;

namespace Orleans.Durable.Storage;

/// <summary>
/// The limits of the silo's replica calls (orleans.md §5.12, §6, OR-I17), computed and validated once, when the node is
/// built at the silo's start, so a silo whose limits it cannot meet does not start (conventions.md §7). Every silo of a
/// cluster is deployed with the same limits: a callee refuses a payload or reply above its own payload limit.
/// </summary>
internal sealed class ReplicaCallLimits
{
    private ReplicaCallLimits(DurableStorageOptions options, int payloadLimit, TimeSpan deadline)
    {
        MaxReplicaCalls = options.MaxReplicaCalls;
        MaxReplicaCallBytes = options.MaxReplicaCallBytes;
        MaxNodeCalls = options.MaxNodeCalls;
        MaxNodeCallBytes = options.MaxNodeCallBytes;
        MaxNodeControlCalls = options.MaxNodeControlCalls;
        MaxReadReplyBytes = options.MaxReadReplyBytes;
        PayloadLimit = payloadLimit;
        Deadline = deadline;
    }

    /// <summary>The bytes a replica call's message holds beside its payload, which the payload limit leaves room for.</summary>
    public const int CallOverheadBytes = 64 << 10;

    /// <summary>
    /// The longest deadline a call may have: a timer's longest due time, <c>0xFFFFFFFE</c> milliseconds, about 49.7 days,
    /// so every timer a call's deadline arms, its lane wait's, its backoff's, and its reply's hold, accepts it.
    /// </summary>
    public static readonly TimeSpan LongestDeadline = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    /// <summary>Gets the most calls a host admits at once.</summary>
    public int MaxReplicaCalls { get; }

    /// <summary>Gets the payload bytes a host holds at most, requests and DATA replies alike.</summary>
    public long MaxReplicaCallBytes { get; }

    /// <summary>Gets the most data calls a caller keeps outstanding to each node.</summary>
    public int MaxNodeCalls { get; }

    /// <summary>Gets the payload and reply bytes a caller keeps outstanding to each node.</summary>
    public long MaxNodeCallBytes { get; }

    /// <summary>Gets the most contiguous-tail reads and seals a caller keeps outstanding to each node.</summary>
    public int MaxNodeControlCalls { get; }

    /// <summary>Gets the reply a remote whole read reserves before it learns its batch's length.</summary>
    public int MaxReadReplyBytes { get; }

    /// <summary>
    /// Gets the payload limit, <c>min(MaxNodeCallBytes / 2, MaxReplicaCallBytes / 4, MaxMessageBodySize − 64 KiB)</c>: the
    /// longest payload a replica call carries, and the longest reply a read reserves.
    /// </summary>
    public int PayloadLimit { get; }

    /// <summary>
    /// Gets a call's deadline, Orleans' response timeout, from when the call was made: every wait the caller makes counts
    /// against it, and a read's DATA reply holds its host admission at most this long.
    /// </summary>
    public TimeSpan Deadline { get; }

    /// <summary>Computes and validates the limits of <paramref name="options"/> under Orleans' <paramref name="messaging"/>.</summary>
    /// <exception cref="ArgumentException">
    /// The limits leave no positive payload limit, the read reply passes it, a forwarded write's lane holds less than it, or
    /// the response timeout is not positive or passes <see cref="LongestDeadline"/>.
    /// </exception>
    public static ReplicaCallLimits From(DurableStorageOptions options, MessagingOptions messaging)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(messaging);
        options.Validate();
        var limit = Math.Min(Math.Min(options.MaxNodeCallBytes / 2, options.MaxReplicaCallBytes / 4), (long)messaging.MaxMessageBodySize - CallOverheadBytes);
        if (limit <= 0)
        {
            throw new ArgumentException(string.Create(
                CultureInfo.InvariantCulture,
                $"The replica call limits leave no payload: MaxNodeCallBytes {options.MaxNodeCallBytes}, MaxReplicaCallBytes {options.MaxReplicaCallBytes}, and Orleans' MaxMessageBodySize {messaging.MaxMessageBodySize}."));
        }

        if (options.MaxReadReplyBytes > limit)
        {
            throw new ArgumentException(string.Create(
                CultureInfo.InvariantCulture,
                $"MaxReadReplyBytes {options.MaxReadReplyBytes} passes the replica payload limit of {limit}."));
        }

        if (options.MaxForwardCallBytes < limit)
        {
            throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"MaxForwardCallBytes {options.MaxForwardCallBytes} is below the replica payload limit of {limit}, which bounds a forwarded write's record."),
                nameof(options.MaxForwardCallBytes));
        }

        var deadline = messaging.ResponseTimeout;
        if (deadline <= TimeSpan.Zero || deadline > LongestDeadline)
        {
            throw new ArgumentException(string.Create(
                CultureInfo.InvariantCulture,
                $"A replica call's deadline, Orleans' response timeout, is positive and at most a timer's longest due time, {LongestDeadline}, not {deadline}."));
        }

        return new ReplicaCallLimits(options, (int)limit, deadline);
    }
}
