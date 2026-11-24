namespace Orleans.Durable.Storage.Engine.Archive.Control;

/// <summary>
/// Outcome of <see cref="ILedgerLocalDrainBarrier.EnsureLocalStreamsDrainedAsync"/> (archive.md §2.3, LG-I16).
/// </summary>
public readonly union LocalDrainBarrierOutcome(
    LocalDrainBarrierOutcome.Drained,
    LocalDrainBarrierOutcome.Dropped,
    LocalDrainBarrierOutcome.Backpressure)
{
    /// <summary>Every registered ledger-local stream of the closed ledger has been durably drained through <c>T</c>.</summary>
    public sealed record Drained
    {
        private Drained()
        {
        }

        /// <summary>Gets the singleton instance.</summary>
        public static Drained Instance { get; } = new();
    }

    /// <summary>The closed ledger has already left the ledger map, so no local-stream drain is needed.</summary>
    public sealed record Dropped
    {
        private Dropped()
        {
        }

        /// <summary>Gets the singleton instance.</summary>
        public static Dropped Instance { get; } = new();
    }

    /// <summary>A replica answered backpressure while scanning the ledger; the caller retries on a later pass.</summary>
    public sealed record Backpressure
    {
        private Backpressure()
        {
        }

        /// <summary>Gets the singleton instance.</summary>
        public static Backpressure Instance { get; } = new();
    }
}
