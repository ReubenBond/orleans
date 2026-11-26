using System.Collections.Immutable;
using System.Globalization;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Engine.Archive;

/// <summary>
/// A key's write for a segment (archive.md §2.1, §4): a pure value of the key's rows in the segment, which a drain writes
/// once. It carries its segment's identity, the token it starts from, which the key's defining row in the segment
/// stores, and its commands: an opener, the last <c>Replace</c> or <c>Delete</c> of the key in the segment, and the key's
/// later <c>Append</c>s; or, with no opener, an append run of the key's <c>Append</c>s.
/// </summary>
public sealed class ArchiveWrite
{
    /// <summary>Initializes a new instance of the <see cref="ArchiveWrite"/> class.</summary>
    /// <param name="segment">The segment's identity.</param>
    /// <param name="token">The token the write starts from.</param>
    /// <param name="commands">The commands: at most one opener, first, and then <c>Append</c>s.</param>
    /// <param name="superseded">The key's rows in the segment before the write's opener, which it supersedes; 0 without one.</param>
    /// <exception cref="ArgumentException">An opener is not the first command, or rows are superseded with no opener.</exception>
    public ArchiveWrite(LedgerSegmentId segment, RowToken token, ImmutableArray<ArchiveCommand> commands, int superseded = 0)
    {
        commands = commands.IsDefault ? [] : commands;
        ArgumentOutOfRangeException.ThrowIfNegative(superseded);
        if (superseded > 0 && (commands.IsEmpty || !commands[0].IsOpener))
        {
            throw new ArgumentException("Only an opener supersedes the rows before it in its segment.", nameof(superseded));
        }

        for (var index = 1; index < commands.Length; index++)
        {
            if (commands[index].IsOpener)
            {
                throw new ArgumentException(
                    string.Create(CultureInfo.InvariantCulture, $"Only a write's first command opens it; command {index} is a {commands[index].Kind}."),
                    nameof(commands));
            }
        }

        Segment = segment;
        Token = token;
        Commands = commands;
        Superseded = superseded;
    }

    /// <summary>Gets the identity of the write's segment, <c>(ledger, start slot)</c>.</summary>
    public LedgerSegmentId Segment { get; }

    /// <summary>Gets the token the write starts from: the token its key's previous write leaves (JS-I10).</summary>
    public RowToken Token { get; }

    /// <summary>Gets the write's commands, in order.</summary>
    public ImmutableArray<ArchiveCommand> Commands { get; }

    /// <summary>
    /// Gets the number of the key's rows in the segment before the write's opener, which the opener supersedes: they are no
    /// commands of the write, but a provider's <c>Plan</c> may count them, so that every state the key's rows pass through
    /// in the segment has its own token (archive.md §3).
    /// </summary>
    public int Superseded { get; }

    /// <summary>Gets a value indicating whether the write is an opener: its first command is a <c>Replace</c> or a <c>Delete</c>.</summary>
    public bool IsOpener => Commands.Length > 0 && Commands[0].IsOpener;

    /// <summary>
    /// Returns the write of a key's rows in one segment, in tag order (archive.md §2.1): its segment and token, the first
    /// row's, and its commands, from the last <c>Replace</c> or <c>Delete</c>, which supersedes the rows before it, counted
    /// in <see cref="Superseded"/>, or every row's when it has neither. A row before the opener is judged held by the
    /// opener's position, 1; each row from
    /// the opener on by its own, <c>index - (rows.Length - Commands.Length) + 1</c>.
    /// </summary>
    /// <param name="rows">The rows, at least one, all of the first row's segment; the write borrows a <c>Delete</c>'s body.</param>
    /// <exception cref="ArgumentException"><paramref name="rows"/> is empty.</exception>
    /// <exception cref="InvalidOperationException">A row is a held <c>Delete</c>, whose tombstone it no longer carries.</exception>
    public static ArchiveWrite OfRows(ReadOnlySpan<ArchiveRow> rows)
    {
        if (rows.IsEmpty)
        {
            throw new ArgumentException("A write holds at least one row.", nameof(rows));
        }

        var builder = new ArchiveWriteBuilder(rows[0].Segment, rows[0].Token);
        foreach (var row in rows)
        {
            _ = builder.Add(row.ToCommand(), row.SupersededBefore);
        }

        return builder.ToWrite();
    }
    /// <inheritdoc/>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"({Segment.Ledger}, {Segment.Start}) from {Token}: [{string.Join(", ", Commands)}]{(Superseded > 0 ? $" after {Superseded} superseded" : "")}");
}
