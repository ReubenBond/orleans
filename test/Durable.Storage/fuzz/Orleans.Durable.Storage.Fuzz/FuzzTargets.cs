using Orleans.Durable.Storage.Fuzz.Archive;
using Orleans.Durable.Storage.Fuzz.Archive.Logs;
using Orleans.Durable.Storage.Fuzz.Journals;
using Orleans.Durable.Storage.Fuzz.Ledgers;
using Orleans.Durable.Storage.Fuzz.MetaStore;
using Orleans.Durable.Storage.Fuzz.Orleans;
using Orleans.Durable.Storage.Fuzz.Wal;

namespace Orleans.Durable.Storage.Fuzz;

/// <summary>
/// The decoders under fuzzing, one target per decoder of durable or wire data (verification.md §6.1), from every
/// layer. A format gains its target with its decoder.
/// </summary>
internal static class FuzzTargets
{
    public static IReadOnlyList<FuzzTarget> All { get; } = [.. WalTargets.All, .. MetaStoreTargets.All, .. LedgerStoreTargets.All, .. LedgersTargets.All, .. ArchiveTargets.All, .. ArchiveLogsTargets.All, .. JournalsTargets.All, .. OrleansTargets.All];

    public static FuzzTarget? Find(string name) => All.FirstOrDefault(target => target.Name == name);

    /// <summary>
    /// Returns the longest input a campaign of <paramref name="target"/> generates: its own bound, or its longest seed,
    /// so a campaign mutates every seed whole.
    /// </summary>
    public static int MaxLength(FuzzTarget target, GoldenVectors vectors) =>
        target.Seeds(vectors).Select(static seed => seed.Length).Append(target.MaxLength).Max();
}
