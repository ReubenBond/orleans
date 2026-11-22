using System.Diagnostics;
using System.Globalization;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Engine.Archive;

/// <summary>
/// The check of a key's defining row's stored token against the token its store's <c>Plan</c> gives the key's write
/// (HI-I4), shared by every store which drains rows: a drain which meets another token holds a writer's programming
/// error, since the writer stores the plan its store gave (journals.md §8.1).
/// </summary>
public static class StoredTokens
{
    /// <summary>
    /// Returns why <paramref name="stored"/>, the token a defining row of <paramref name="segment"/> stores, breaks HI-I4,
    /// or <see langword="null"/> when it is <paramref name="planned"/>, the store's plan of the write.
    /// </summary>
    public static string? Violation(LedgerSegmentId segment, RowToken stored, RowToken planned) =>
        stored == planned
            ? null
            : string.Create(
                CultureInfo.InvariantCulture,
                $"A defining row of segment ({segment.Ledger}, {segment.Start}) stores the token {stored}, and its store plans the write from {planned} (HI-I4).");

    /// <summary>
    /// Asserts that a defining row of <paramref name="segment"/> stores <paramref name="planned"/>, the store's plan of
    /// the write: a Debug assertion, since only a writer's programming error stores another (HI-I4).
    /// </summary>
    public static void AssertPlanned(LedgerSegmentId segment, RowToken stored, RowToken planned)
    {
        if (stored != planned)
        {
            Debug.Fail(Violation(segment, stored, planned));
        }
    }
}
