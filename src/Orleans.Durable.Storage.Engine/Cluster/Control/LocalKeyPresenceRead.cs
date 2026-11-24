namespace Orleans.Durable.Storage.Engine.Cluster.Control;

/// <summary>
/// Outcome of an O(1) local key-index presence probe (<see cref="ILocalKeyPresenceLookup.TryHasKeyEntryAsync"/>).
/// </summary>
public readonly union LocalKeyPresenceRead(
    LocalKeyPresenceRead.Absent,
    LocalKeyPresenceRead.Present,
    LocalKeyPresenceRead.Unindexed)
{
    /// <summary>The local node's caught-up key index proves no row for the key exists in the ledger through <c>T</c>.</summary>
    public sealed record Absent
    {
        private Absent()
        {
        }

        /// <summary>Gets the singleton instance.</summary>
        public static Absent Instance { get; } = new();
    }

    /// <summary>The local node's caught-up key index reports at least one row for the key in the ledger.</summary>
    public sealed record Present
    {
        private Present()
        {
        }

        /// <summary>Gets the singleton instance.</summary>
        public static Present Instance { get; } = new();
    }

    /// <summary>The ledger is not locally indexed or not caught up to <c>T</c>, so the caller must scan sequentially.</summary>
    public sealed record Unindexed
    {
        private Unindexed()
        {
        }

        /// <summary>Gets the singleton instance.</summary>
        public static Unindexed Instance { get; } = new();
    }
}
