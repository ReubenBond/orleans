using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Engine.Cluster.Control;

/// <summary>
/// A node's answer to <see cref="PrepareReleaseRequest"/> (orleans.md §5.6, §5.7, OR-I24).
/// </summary>
internal readonly union PrepareReleaseReply(
    PrepareReleaseReply.Released,
    PrepareReleaseReply.Sealed,
    PrepareReleaseReply.TimedOut,
    PrepareReleaseReply.NotServed,
    PrepareReleaseReply.Declined) : IEquatable<PrepareReleaseReply>
{
    /// <inheritdoc/>
    public bool Equals(PrepareReleaseReply other) => Equals(Value, other.Value);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is PrepareReleaseReply other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => Value?.GetHashCode() ?? 0;

    /// <summary>
    /// The primary drained in-flight writes, confirmed no concurrent seal, and stopped admitting new batches at
    /// <see cref="ContiguousTail"/>. Repeated calls on the same open ledger return this outcome idempotently.
    /// </summary>
    /// <param name="ContiguousTail">The primary's contiguous tail when admission stopped.</param>
    public sealed record Released(Slot ContiguousTail);

    /// <summary>
    /// Another node concurrently sealed the ledger (or the ledger closed/stopped non-cooperatively) before release
    /// completed; draining aborted immediately without waiting for the deadline.
    /// </summary>
    public sealed record Sealed
    {
        /// <summary>Gets the singleton instance.</summary>
        public static Sealed Instance { get; } = new();

        private Sealed()
        {
        }
    }

    /// <summary>In-flight writes or replica confirmation did not complete within the request's deadline.</summary>
    public sealed record TimedOut
    {
        /// <summary>Gets the singleton instance.</summary>
        public static TimedOut Instance { get; } = new();

        private TimedOut()
        {
        }
    }

    /// <summary>This node is not the current open primary serving the requested ledger and range.</summary>
    public sealed record NotServed
    {
        /// <summary>Gets the singleton instance.</summary>
        public static NotServed Instance { get; } = new();

        private NotServed()
        {
        }
    }

    /// <summary>This node is not serving control requests (starting or stopped).</summary>
    public sealed record Declined
    {
        /// <summary>Gets the singleton instance.</summary>
        public static Declined Instance { get; } = new();

        private Declined()
        {
        }
    }
}
