namespace Orleans.Durable.Storage.Engine.Journals;

/// <summary>The bound of journals.md §8.4 whose startup check a <see cref="JournalStore"/> failed.</summary>
public enum JournalStoreLimit
{
    /// <summary>The largest batch admission builds passes the replica's payload limit.</summary>
    PayloadLimit,

    /// <summary><c>MaxBodyBytes</c> passes the archive store's <c>MaxAppendBytes</c>.</summary>
    MaxAppendBytes,

    /// <summary><c>MaxQueuedBytes</c> holds less than the largest batch.</summary>
    MaxQueuedBytes,

    /// <summary>The writers' <c>MaxProposalBytes</c> holds less than the largest batch.</summary>
    MaxProposalBytes,

    /// <summary>The writers' <c>MaxSegmentKeys</c> is less than <c>1 + MaxLocalAppendsPerWrite</c>.</summary>
    MaxSegmentKeys,
}
