using System.Diagnostics;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Engine.Journals;

/// <summary>
/// An entry's kind (journals.md §2.1, §4.2). Its value is the kind's byte in an entry record (journals.md §7).
/// </summary>
public enum JournalEntryKind : byte
{
    /// <summary>Adds a non-empty payload; a <see cref="RowKind.Append"/> row whose body is the payload.</summary>
    Append = 0,

    /// <summary>Supersedes every earlier entry with one snapshot payload; a <see cref="RowKind.Replace"/> row whose body is the snapshot.</summary>
    Replace = 1,

    /// <summary>Ends the content and leaves a tombstone, its deletion time; a <see cref="RowKind.Delete"/> row whose 8-byte body is the tombstone.</summary>
    Delete = 2,

    /// <summary>Ends a deleted journal's tombstone, leaving it absent; an empty <see cref="RowKind.Delete"/> row.</summary>
    Expired = 3,
}

/// <summary>The defined entry kinds and the row kinds they map onto.</summary>
internal static class JournalEntryKinds
{
    /// <summary>Returns whether <paramref name="kind"/> is one of the four entry kinds.</summary>
    public static bool IsDefined(JournalEntryKind kind) => kind <= JournalEntryKind.Expired;

    /// <summary>Returns the row kind of an entry of <paramref name="kind"/> (journals.md §4.2).</summary>
    public static RowKind RowKindOf(JournalEntryKind kind) => kind switch
    {
        JournalEntryKind.Append => RowKind.Append,
        JournalEntryKind.Replace => RowKind.Replace,
        JournalEntryKind.Delete or JournalEntryKind.Expired => RowKind.Delete,
        _ => throw new UnreachableException("An entry's kind is defined."),
    };
}
