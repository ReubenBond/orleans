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
    /// Unversioned message row addressed to a <see cref="MessageChannel"/>; decided atomically in its batch slot,
    /// excluded from archive stores and key indices, and folded by <see cref="Archive.Control.IMessageProcessor"/>
    /// during segment drains (ledgers.md §2.2, LG-I15).
    /// </summary>
    Message = 3,
}

/// <summary>The defined row kinds.</summary>
internal static class RowKinds
{
    /// <summary>
    /// Returns whether <paramref name="kind"/> is a defined ledger row kind (<see cref="RowKind.Append"/>,
    /// <see cref="RowKind.Replace"/>, <see cref="RowKind.Delete"/>, or <see cref="RowKind.Message"/>).
    /// </summary>
    public static bool IsDefined(RowKind kind) => kind is RowKind.Append or RowKind.Replace or RowKind.Delete or RowKind.Message;

    /// <summary>
    /// Returns whether <paramref name="kind"/> is a keyed archive row kind (<see cref="RowKind.Append"/>,
    /// <see cref="RowKind.Replace"/>, or <see cref="RowKind.Delete"/>), excluding <see cref="RowKind.Message"/>
    /// (LG-I15).
    /// </summary>
    public static bool IsArchiveKind(RowKind kind) => kind is RowKind.Append or RowKind.Replace or RowKind.Delete;
}
