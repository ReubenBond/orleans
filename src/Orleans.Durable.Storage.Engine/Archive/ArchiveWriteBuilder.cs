using System.Collections.Immutable;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Engine.Archive;

/// <summary>
/// The one definition of a key's write from its rows in a segment (archive.md §2.1), taken a row at a time in tag order:
/// an opener, a <c>Replace</c> or a <c>Delete</c>, supersedes the commands before it, which the write then counts in
/// <see cref="ArchiveWrite.Superseded"/>. A history read's merge and a drain build each key's write with it, so the two
/// agree (HI-I1).
/// </summary>
internal sealed class ArchiveWriteBuilder(LedgerSegmentId segment, RowToken token)
{
    private readonly ImmutableArray<ArchiveCommand>.Builder _commands = ImmutableArray.CreateBuilder<ArchiveCommand>();

    /// <summary>Gets the segment's identity.</summary>
    public LedgerSegmentId Segment { get; } = segment;

    /// <summary>Gets the token the write starts from, which the key's defining row in the segment stores.</summary>
    public RowToken Token { get; } = token;

    /// <summary>Gets the rows an opener superseded so far.</summary>
    public int Superseded { get; private set; }

    /// <summary>Gets the write's commands so far.</summary>
    public int Count => _commands.Count;

    /// <summary>Takes the key's next row's command, and returns how many commands it superseded, which the caller releases.</summary>
    public int Add(ArchiveCommand command, int supersededBefore = 0)
    {
        var superseded = 0;
        if (command.IsOpener)
        {
            if (_commands.Count > 0)
            {
                superseded = _commands.Count;
                Superseded = checked(Superseded + superseded);
                _commands.Clear();
            }

            if (supersededBefore > 0)
            {
                Superseded = checked(Superseded + supersededBefore);
            }
        }

        _commands.Add(command);
        return superseded;
    }

    /// <summary>Returns the write of the rows taken.</summary>
    public ArchiveWrite ToWrite() => new(Segment, Token, _commands.ToImmutable(), Superseded);
}
