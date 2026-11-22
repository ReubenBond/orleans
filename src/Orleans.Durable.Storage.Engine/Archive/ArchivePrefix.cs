using System.Diagnostics;
using System.Globalization;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Engine.Archive;

/// <summary>
/// A key's prefix in an archive store (archive.md §2.1, §4): the part of its history the store holds, as the token its
/// next write starts from, its version base, whether a <c>Replace</c> opened its base, its tombstone, its content, and
/// the provider's coverage, which <see cref="IArchiveStore.Holds"/> reads. It owns its content; its caller disposes it.
/// </summary>
public sealed class ArchivePrefix : IDisposable
{
    /// <summary>Initializes a new instance of the <see cref="ArchivePrefix"/> class, which retains its own lease on <paramref name="content"/>.</summary>
    /// <param name="token">The token the key's next write starts from.</param>
    /// <param name="versionBase">
    /// The base a rebase takes: non-empty, and, among the prefixes a provider answers with a map value which lists no
    /// ledger holding a row of the key, advancing with every write the provider applies and never repeating while the
    /// provider holds the key (HI-I4).
    /// </param>
    /// <param name="hasBase">Whether a <c>Replace</c> opened the prefix's base, an empty one included.</param>
    /// <param name="tombstone">
    /// The tombstone of the <c>Delete</c> which opened the base, only while no <c>Append</c> follows it; otherwise
    /// <see langword="null"/>.
    /// </param>
    /// <param name="content">The key's content, of which the prefix retains its own lease; the caller keeps its own.</param>
    /// <param name="coverage">The provider's private state which its <see cref="IArchiveStore.Holds"/> reads.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="versionBase"/> is empty, or a tombstone is empty or comes with <paramref name="hasBase"/> or
    /// non-empty <paramref name="content"/>: a <c>Delete</c> base followed by nothing holds no content.
    /// </exception>
    public ArchivePrefix(RowToken token, RowToken versionBase, bool hasBase, ReadOnlyMemory<byte>? tombstone, ArcBuffer content, object? coverage)
    {
        if (versionBase.IsEmpty)
        {
            throw new ArgumentException("A prefix's version base is non-empty: only Absent rebases to Origin.", nameof(versionBase));
        }

        if (tombstone is { IsEmpty: true })
        {
            throw new ArgumentException("An empty Delete records no tombstone.", nameof(tombstone));
        }

        if (tombstone is not null && (hasBase || content.Length > 0))
        {
            throw new ArgumentException("A tombstone's prefix is a Delete base which no row follows: no Replace opened it, and it holds no content.", nameof(tombstone));
        }

        Token = token;
        VersionBase = versionBase;
        HasBase = hasBase;
        Tombstone = tombstone;
        Content = new ArchiveContent(content);
        Coverage = coverage;
    }

    /// <summary>Gets the token the key's next write starts from, which the provider's <c>Plan</c> takes.</summary>
    public RowToken Token { get; }

    /// <summary>Gets the base a rebase takes (archive.md §8.3), which advances with every write the provider applies.</summary>
    public RowToken VersionBase { get; }

    /// <summary>Gets a value indicating whether a <c>Replace</c> opened the prefix's base, an empty one included.</summary>
    public bool HasBase { get; }

    /// <summary>
    /// Gets the tombstone of the <c>Delete</c> which opened the base, present only while the key is deleted: no
    /// <c>Append</c> follows that <c>Delete</c>.
    /// </summary>
    public ReadOnlyMemory<byte>? Tombstone { get; }

    /// <summary>Gets the key's content, which the prefix owns.</summary>
    public ArchiveContent Content { get; }

    /// <summary>Gets the provider's private coverage, which only its <see cref="IArchiveStore.Holds"/> reads.</summary>
    public object? Coverage { get; }

    /// <summary>
    /// Folds a store's rows of a key, in strictly increasing tag order, into a prefix (archive.md §2.1): the base is the
    /// last <c>Replace</c> or <c>Delete</c>, which supersedes the rows before it; the content is a <c>Replace</c> base's
    /// body and each later <c>Append</c>'s, concatenated, or, with no base, every row's; <c>HasBase</c> is a <c>Replace</c>
    /// base, an empty one included; and the tombstone is a <c>Delete</c> base's non-empty body while no <c>Append</c>
    /// follows it.
    /// </summary>
    /// <param name="rows">The rows, at least one, which the fold borrows.</param>
    /// <param name="token">The prefix's token.</param>
    /// <param name="versionBase">The prefix's version base, non-empty.</param>
    /// <param name="coverage">The provider's coverage.</param>
    /// <returns>The prefix, whose content the fold copies; the caller disposes it.</returns>
    /// <exception cref="ArgumentException"><paramref name="rows"/> is empty, or <paramref name="versionBase"/> is.</exception>
    /// <exception cref="NotSupportedException">The content would pass <see cref="int.MaxValue"/> bytes (deferred.md).</exception>
    public static ArchivePrefix Fold(ReadOnlySpan<TaggedRow> rows, RowToken token, RowToken versionBase, object? coverage)
    {
        if (rows.IsEmpty)
        {
            throw new ArgumentException("A prefix folds at least one row.", nameof(rows));
        }

        Debug.Assert(IsStrictlyIncreasing(rows), "A prefix folds its rows in strictly increasing tag order.");
        var start = 0;
        for (var index = rows.Length - 1; index >= 0; index--)
        {
            if (rows[index].Row.Kind is RowKind.Replace or RowKind.Delete)
            {
                start = index;
                break;
            }
        }

        var baseRow = rows[start].Row;
        if (start == rows.Length - 1)
        {
            if (baseRow.Kind == RowKind.Delete)
            {
                ReadOnlyMemory<byte>? deleteTombstone = baseRow.Body.Length > 0 ? (ReadOnlyMemory<byte>?)baseRow.Body.ToArray() : null;
                return new ArchivePrefix(token, versionBase, hasBase: false, deleteTombstone, default, coverage);
            }

            if (baseRow.Body.Length == 0)
            {
                return new ArchivePrefix(token, versionBase, baseRow.Kind == RowKind.Replace, tombstone: null, default, coverage);
            }
        }

        var length = 0L;
        foreach (var row in rows[start..])
        {
            length += row.Row.Kind == RowKind.Delete ? 0 : row.Row.Body.Length;
        }

        CheckContentLength(length);
        using var writer = new ArcBufferWriter();
        foreach (var row in rows[start..])
        {
            if (row.Row.Kind != RowKind.Delete)
            {
                writer.Write(row.Row.Body.AsReadOnlySequence());
            }
        }

        using var content = writer.Length == 0 ? default : writer.ConsumeSlice(writer.Length);
        return new ArchivePrefix(token, versionBase, baseRow.Kind == RowKind.Replace, tombstone: null, content, coverage);
    }

    /// <summary>
    /// Throws unless a key's content of <paramref name="length"/> bytes fits a prefix, whose content is at most
    /// <see cref="int.MaxValue"/> bytes (deferred.md, content above 2 GiB).
    /// </summary>
    /// <exception cref="NotSupportedException">The content would pass the bound.</exception>
    public static void CheckContentLength(long length)
    {
        if (length > int.MaxValue)
        {
            throw new NotSupportedException(string.Create(
                CultureInfo.InvariantCulture,
                $"A key's content of {length} bytes passes the {int.MaxValue}-byte bound of an archive prefix's content."));
        }
    }

    /// <summary>
    /// Returns a prefix whose content is <paramref name="content"/>'s bytes, as a store's read answered them, of which the
    /// prefix retains its own lease; the caller keeps <paramref name="content"/> and disposes the prefix, or hands it to
    /// <see cref="ArchiveStoreRead.Read"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The fields break the constructor's rules.</exception>
    public static ArchivePrefix Of(RowToken token, RowToken versionBase, bool hasBase, ReadOnlyMemory<byte>? tombstone, ArcBuffer content, object? coverage) =>
        new(token, versionBase, hasBase, tombstone, content, coverage);

    /// <summary>Returns a prefix with the same fields and its own lease on the content; the caller disposes it.</summary>
    public ArchivePrefix Retain()
    {
        using var content = Content.Retain();
        return new ArchivePrefix(Token, VersionBase, HasBase, Tombstone, content, Coverage);
    }

    /// <summary>Releases the content.</summary>
    public void Dispose() => Content.Dispose();

    private static bool IsStrictlyIncreasing(ReadOnlySpan<TaggedRow> rows)
    {
        for (var index = 1; index < rows.Length; index++)
        {
            if (rows[index - 1].Tag >= rows[index].Tag)
            {
                return false;
            }
        }

        return true;
    }
}
