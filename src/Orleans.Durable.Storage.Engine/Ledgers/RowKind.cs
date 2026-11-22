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
}

/// <summary>The defined row kinds.</summary>
internal static class RowKinds
{
    /// <summary>Returns whether <paramref name="kind"/> is <see cref="RowKind.Append"/>, <see cref="RowKind.Replace"/>, or <see cref="RowKind.Delete"/>.</summary>
    public static bool IsDefined(RowKind kind) => kind is RowKind.Append or RowKind.Replace or RowKind.Delete;
}
