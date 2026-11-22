using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Engine.Journals;

/// <summary>
/// A key's state and planning state as one value (journals.md §8.3), which the cold-primary property compares between
/// a warm primary and a load: the fold's version, existence, tombstone, and content length; the recorded segment and the
/// key's token there; and the token its next write starts from.
/// </summary>
/// <param name="Version">The fold's version.</param>
/// <param name="Exists">Whether the journal exists.</param>
/// <param name="DeletedAt">The tombstone, when the journal is deleted.</param>
/// <param name="ContentLength">The content's length.</param>
/// <param name="Segment">The recorded segment, or <see langword="null"/> before the key's next row records one.</param>
/// <param name="Token">The key's token in the recorded segment, or, with none, the next write's.</param>
/// <param name="NextToken">The token the key's next write in another segment starts from: <c>End()</c> of its write.</param>
internal readonly record struct JournalWarm(JournalVersion Version, bool Exists, DateTimeOffset? DeletedAt, long ContentLength, LedgerSegmentId? Segment, RowToken Token, RowToken NextToken)
{
    /// <summary>Returns the value of a key's state and planning state.</summary>
    public static JournalWarm Of(JournalState? state, JournalPlanning planning) =>
        new(state?.CurrentVersion ?? JournalVersion.Origin, state?.Exists ?? false, state?.DeletedAt, state?.ContentLength ?? 0, planning.Segment, planning.Token, planning.Written.End());
}
