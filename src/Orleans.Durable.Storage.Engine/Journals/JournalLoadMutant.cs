namespace Orleans.Durable.Storage.Engine.Journals;

/// <summary>A break of a key's load which the version checker or the cold-primary property must catch (journals.md §10).</summary>
internal enum JournalLoadMutant
{
    /// <summary>The fold and the planning state of journals.md §8.3.</summary>
    None,

    /// <summary>Rebases a key whenever the archive holds a prefix of it, live rows or not (<c>rebase-with-live-rows</c>).</summary>
    RebaseWithLiveRows,

    /// <summary>Rebases a key to the version base its first load saw, not its read's own (<c>rebase-on-stale-token</c>).</summary>
    RebaseOnStaleToken,

    /// <summary>Applies the live rows the prefix holds as well as those it does not: a replay without <c>Holds</c>.</summary>
    ReplayWithoutHolds,

    /// <summary>Records no segment for a key whose newest segment is wholly live, so its next row there rolls again: no lazy roll.</summary>
    NoLazyRoll,
}
