namespace Orleans.Durable.Storage.Engine.Ledgers.Store;

/// <summary>
/// Configures when a LedgerStore checkpoints its slot table (ledgerstore.md §8.8). These settings are runtime policy and are
/// never persisted in a replica's configuration.
/// </summary>
public sealed record LedgerStoreCheckpointOptions
{
    /// <summary>
    /// The longest <see cref="CheckpointInterval"/>: the longest wait a timer accepts, 2<sup>32</sup> − 2 milliseconds.
    /// </summary>
    internal static readonly TimeSpan MaxCheckpointInterval = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    /// <summary>
    /// Gets or sets the interval after which a replica with changed leaves writes a checkpoint, positive and at most about 49.7
    /// days, the longest wait a timer accepts.
    /// </summary>
    public TimeSpan CheckpointInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets the WAL bytes between the checkpoint in force's replay start and the latest durable record after which a
    /// replica writes a checkpoint early, bounding the WAL a recovery replays.
    /// </summary>
    public long CheckpointReplayBytes { get; set; } = 1L << 30;

    /// <summary>
    /// Gets or sets the number of generation files the checkpoint in force may reference before the oldest one's live leaves
    /// are copied forward.
    /// </summary>
    public int MaxCheckpointFiles { get; set; } = 32;

    /// <summary>
    /// Gets the final checkpoints a close writes at once (ledgerstore.md §8.10). Each is a few small writes and flushes of
    /// its own replica's files, so a close's time is dominated by flush latency, which overlapping a few hides; eight cuts a
    /// node's close by about as much while bounding the leaves encoded in memory and the files open at once to those of
    /// eight replicas. Tests whose file-system traces replay from a seed set one, so the trace does not depend on how the
    /// concurrent checkpoints interleave.
    /// </summary>
    internal int FinalCheckpointConcurrency { get; init; } = 8;

    /// <summary>Throws unless each setting is within its range (ledgerstore.md §8.8), naming the setting.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A setting is out of its range.</exception>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(CheckpointInterval, TimeSpan.Zero, nameof(CheckpointInterval));

        // Maintenance waits out the interval with a timer, which would otherwise throw and stop maintenance.
        ArgumentOutOfRangeException.ThrowIfGreaterThan(CheckpointInterval, MaxCheckpointInterval, nameof(CheckpointInterval));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(CheckpointReplayBytes, nameof(CheckpointReplayBytes));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxCheckpointFiles, 2, nameof(MaxCheckpointFiles));
    }
}
