namespace Orleans.Durable.Storage.Engine.Archive.Logs;

/// <summary>
/// The options of archive logs (archive-logs.md §4, §8.6): a node's drain concurrency, drain memory, and its share of the
/// cluster's request budget. The keys' log names are not an option: a drain takes them from the archive gate, which
/// checked them against the store's record (<see cref="ArchiveLogGate.Names"/>).
/// </summary>
public sealed record ArchiveLogOptions
{
    /// <summary>Gets the most key writes a node's drains make at once, over every ledger they drain; 64 by default.</summary>
    public int DrainConcurrency { get; init; } = 64;

    /// <summary>
    /// Gets the bytes of segments a node's drains hold at once, shared by every drain of the process; 512 MiB by default.
    /// A hard bound: a segment whose rows pass the whole budget fails its drain with
    /// <see cref="Orleans.Durable.Storage.Engine.Archive.Control.DrainMemoryExceededException"/> (archive-logs.md §8.1).
    /// </summary>
    public long DrainMemoryBytes { get; init; } = 512L << 20;

    /// <summary>
    /// Gets the requests a second the node's drains make at most: the node's share of the cluster's request budget, which
    /// the composition sets and its controller reassigns in proportion to each node's undrained keys (archive-logs.md
    /// §8.6). 1,000 by default, the share of each of ten nodes in a budget of 10,000 a second, half an Azure account's
    /// limit, which a node keeps until the controller assigns its share; <see cref="double.PositiveInfinity"/> paces nothing.
    /// </summary>
    public double DrainRequestsPerSecond { get; init; } = 1_000;

    /// <summary>Checks the options.</summary>
    /// <exception cref="ArgumentOutOfRangeException">An option lies outside its range.</exception>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(DrainConcurrency, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(DrainMemoryBytes, 1);
        CheckRequestsPerSecond(DrainRequestsPerSecond);
    }

    /// <summary>Checks a request rate: positive, and not a NaN.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The rate is not positive.</exception>
    internal static void CheckRequestsPerSecond(double requestsPerSecond)
    {
        if (double.IsNaN(requestsPerSecond) || requestsPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(requestsPerSecond), requestsPerSecond, "A request rate is positive.");
        }
    }
}
