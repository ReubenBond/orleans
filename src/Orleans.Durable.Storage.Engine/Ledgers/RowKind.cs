namespace Orleans.Durable.Storage.Engine.Ledgers;

/// <summary>
/// How a row changes its key's state (ledgers.md §2.2). Its value is the kind's byte in a batch (ledgers.md §7).
/// </summary>
public enum RowKind : byte
{
    /// <summary>Applies on top of the earlier rows.</summary>
    Append = 0,

    /// <summary>Supersedes every earlier row; the row itself is the new state's base.</summary>
    Replace = 1,

    /// <summary>Supersedes every earlier row and leaves its body, when it has one, as the key's tombstone; an empty body leaves nothing.</summary>
    Delete = 2,

    /// <summary>
    /// Unchecked append to a ledger-local stream addressed by <see cref="LedgerLocalKey"/>; decided atomically in its
    /// batch slot and excluded from archive stores and journal folds (ledgers.md §2.2, LG-I15).
    /// </summary>
    LocalAppend = 3,
}

/// <summary>The defined row kinds.</summary>
internal static class RowKinds
{
    /// <summary>
    /// Returns whether <paramref name="kind"/> is a defined ledger row kind (<see cref="RowKind.Append"/>,
    /// <see cref="RowKind.Replace"/>, <see cref="RowKind.Delete"/>, or <see cref="RowKind.LocalAppend"/>).
    /// </summary>
    public static bool IsDefined(RowKind kind) => kind is RowKind.Append or RowKind.Replace or RowKind.Delete or RowKind.LocalAppend;

    /// <summary>
    /// Returns whether <paramref name="kind"/> is a keyed archive row kind (<see cref="RowKind.Append"/>,
    /// <see cref="RowKind.Replace"/>, or <see cref="RowKind.Delete"/>), excluding <see cref="RowKind.LocalAppend"/>
    /// (LG-I15).
    /// </summary>
    public static bool IsArchiveKind(RowKind kind) => kind is RowKind.Append or RowKind.Replace or RowKind.Delete;
}
