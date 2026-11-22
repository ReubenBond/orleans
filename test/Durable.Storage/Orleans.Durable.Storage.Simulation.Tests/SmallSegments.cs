using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Simulation.Tests;

/// <summary>
/// Writer options which end segments often within a simulation (ledgers.md §8.2): two keys, a few one-row batches, or
/// tens of simulated milliseconds a segment, so scans rewind, filtered reads cross segment starts, and aged segments end
/// with markers, all under the simulations' faults.
/// </summary>
internal static class SmallSegments
{
    /// <summary>Gets the options: two keys, about three small batches, or 40 ms a segment.</summary>
    public static LedgerWriterOptions Options { get; } = new(
        maxSegmentBytes: 96,
        maxSegmentKeys: 2,
        maxSegmentAge: TimeSpan.FromMilliseconds(40),
        maxProposalBytes: LedgerWriterOptions.DefaultMaxProposalBytes);

    /// <summary>
    /// Returns the seeded source of a process's writers' segment ages, from a stable hash of its name, so a seed replays
    /// them in any process.
    /// </summary>
    public static Random Random(string process)
    {
        var seed = 17;
        foreach (var c in process)
        {
            seed = unchecked(seed * 31 + c);
        }

        return new Random(seed);
    }
}
