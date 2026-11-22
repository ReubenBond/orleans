using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Testing;

/// <summary>
/// The history fold, the one reading of archive.md §2.1 every test shares, apart from the code: a key's state after its
/// commands, from nothing or from a store's prefix.
/// </summary>
internal static class ArchiveOracle
{
    /// <summary>Returns the state the fold of <paramref name="rows"/>, a key's commands in tag order, leaves.</summary>
    public static ArchiveFold Fold<T>(IEnumerable<T> rows, Func<T, RowKind> kind, Func<T, byte[]> body) =>
        ArchiveFold.Nothing.Apply(rows, kind, body);

    /// <summary>
    /// Returns the state a history read gives: <paramref name="prefix"/>, or nothing for <c>Absent</c>, followed by
    /// <paramref name="read"/>'s rows the prefix does not hold, in tag order.
    /// </summary>
    public static ArchiveFold Fold(ArchivePrefix? prefix, ReadOnlySpan<ArchiveRow> read)
    {
        var state = ArchiveFold.Of(prefix);
        foreach (var row in read)
        {
            if (!row.IsHeld)
            {
                state = state.Apply(row.Kind, row.Body.ToArray());
            }
        }

        return state;
    }
}

/// <summary>
/// A key's state under the history fold (archive.md §2.1): whether the key has history, its content, whether a
/// <c>Replace</c> opened its base, and its tombstone while it is deleted.
/// </summary>
/// <param name="Exists">Whether the key has any command.</param>
/// <param name="Content">The key's content.</param>
/// <param name="HasBase">Whether a <c>Replace</c> opened the base.</param>
/// <param name="Tombstone">The tombstone of the <c>Delete</c> which opened the base while no <c>Append</c> follows it.</param>
internal sealed record ArchiveFold(bool Exists, byte[] Content, bool HasBase, byte[]? Tombstone)
{
    /// <summary>Gets the state of a key with no history.</summary>
    public static ArchiveFold Nothing { get; } = new(false, [], false, null);

    /// <summary>Returns the state a store's prefix holds, or <see cref="Nothing"/> for <c>Absent</c>.</summary>
    public static ArchiveFold Of(ArchivePrefix? prefix) =>
        prefix is null ? Nothing : new(true, prefix.Content.ToArray(), prefix.HasBase, prefix.Tombstone?.ToArray());

    /// <summary>Returns the state after one more command.</summary>
    public ArchiveFold Apply(RowKind kind, byte[] body) => kind switch
    {
        RowKind.Append => new(true, [.. Content, .. body], HasBase, null),
        RowKind.Replace => new(true, body, true, null),
        RowKind.Delete => new(true, [], false, body.Length == 0 ? null : body),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>Returns the state after <paramref name="rows"/>, in order.</summary>
    public ArchiveFold Apply<T>(IEnumerable<T> rows, Func<T, RowKind> kind, Func<T, byte[]> body)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(body);
        var state = this;
        foreach (var row in rows)
        {
            state = state.Apply(kind(row), body(row));
        }

        return state;
    }

    /// <summary>Returns whether the two states hold the same content, base, and tombstone, existing or not.</summary>
    public bool SameAs(ArchiveFold other) =>
        Content.AsSpan().SequenceEqual(other.Content)
        && HasBase == other.HasBase
        && Tombstone is null == other.Tombstone is null
        && Tombstone.AsSpan().SequenceEqual(other.Tombstone);

    /// <inheritdoc/>
    public override string ToString() =>
        $"(exists {Exists}, content {Convert.ToHexStringLower(Content)}, has base {HasBase}, tombstone {(Tombstone is null ? "none" : Convert.ToHexStringLower(Tombstone))})";
}
