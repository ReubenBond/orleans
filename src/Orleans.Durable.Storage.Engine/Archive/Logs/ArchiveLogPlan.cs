using System.Diagnostics;
using System.Globalization;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Engine.Archive.Logs;

/// <summary>
/// Packs an append run's commands into units (archive-logs.md §8.2): on blocks, greedily, in command order, into units
/// of up to <see cref="ArchiveLogLayout.MaxAppendBytes"/>, so no command spans units; on chunks, the whole run as one
/// unit. The plan's fold and the writer's requests share it, so a write's units are its token's.
/// </summary>
internal struct ArchiveLogUnitPacker(ArchiveLogLayout layout)
{
    /// <summary>The bytes in the current unit, or -1 before the first.</summary>
    private long _fill = -1;

    /// <summary>Gets the layout the packer packs for.</summary>
    public readonly ArchiveLogLayout Layout => layout;

    /// <summary>Returns whether an <c>Append</c> of <paramref name="length"/> bytes starts a new unit.</summary>
    public readonly bool Starts(int length) =>
        _fill < 0 || layout.Packing == ArchiveLogPacking.Blocks && _fill + length > layout.MaxAppendBytes;

    /// <summary>Takes an <c>Append</c> of <paramref name="length"/> bytes; returns whether it started a new unit.</summary>
    public bool Add(int length)
    {
        var starts = Starts(length);
        _fill = starts ? length : _fill + length;
        return starts;
    }

    /// <summary>
    /// Throws unless an append run can carry <paramref name="command"/>, an <c>Append</c>: non-empty, as Journals keeps
    /// every <c>Append</c>, and within <see cref="ArchiveLogLayout.MaxAppendBytes"/>, as admission keeps it.
    /// </summary>
    /// <exception cref="ArgumentException">The command is empty or above the store's largest <c>Append</c>.</exception>
    public static void CheckAppend(ArchiveLogLayout layout, ArchiveCommand command)
    {
        Debug.Assert(command.Kind == RowKind.Append, "An append run holds Appends.");
        if (command.ContentLength == 0 || command.ContentLength > (layout.MaxAppendBytes ?? int.MaxValue))
        {
            throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"An append run carries Appends of 1 to {layout.MaxAppendBytes ?? int.MaxValue} bytes, not {command.ContentLength}."),
                nameof(command));
        }
    }

    /// <summary>
    /// Returns the boundaries of the units an append run of <paramref name="appends"/> packs into, from
    /// <paramref name="start"/>: <c>start = q₀ &lt; q₁ &lt; … &lt; q_m</c>, the run's end.
    /// </summary>
    /// <exception cref="ArgumentException">An <c>Append</c> is empty or above the store's largest.</exception>
    public static ulong[] Boundaries(ArchiveLogLayout layout, ulong start, ReadOnlySpan<ArchiveCommand> appends)
    {
        var boundaries = new List<ulong>(appends.Length + 1) { start };
        var packer = new ArchiveLogUnitPacker(layout);
        var end = start;
        foreach (var command in appends)
        {
            CheckAppend(layout, command);
            if (packer.Add(command.ContentLength) && end != start)
            {
                boundaries.Add(end);
            }

            end = checked(end + (ulong)command.ContentLength);
        }

        boundaries.Add(end);
        return [.. boundaries];
    }
}

/// <summary>
/// The fold of archive logs' <c>Plan</c> (archive-logs.md §2.2, §7.2): an opener from generation <c>g</c>, <c>Origin</c>
/// being <c>g = 0</c>, gives <c>(g + 1, r, 0, 0)</c>, its appends landing in its base, where <c>r</c> counts the rows it
/// superseded, itself, and its appends, so that each state the key's rows in the segment pass through has its own token;
/// an append run on generation <c>g</c> from <c>(g, r, P, b)</c>, <c>Origin</c> being a run on generation 1 from
/// <c>(1, 0, 0, 0)</c>, gives <c>(g, r, P + Σℓ, b + units)</c>; and an empty write gives its own token. It admits an
/// append run's command while its
/// packed units stay within <see cref="ArchiveLogLayout.MaxUnits"/>. Its state is the position and the packer alone, within
/// <see cref="ArchivePlan.MaxStateBytes"/>: before any command the position is the token's, and a position of generation 0
/// is <c>Origin</c>, since every other token names a generation of at least 1.
/// </summary>
internal sealed class ArchiveLogPlan : ArchivePlan
{
    private ArchiveLogUnitPacker _packer;
    private ulong _generation;
    private ulong _baseRows;
    private ulong _length;
    private ulong _units;
    private bool _opened;

    /// <summary>Initializes a new instance of the <see cref="ArchiveLogPlan"/> class from <paramref name="from"/>.</summary>
    public ArchiveLogPlan(ArchiveLogLayout layout, ArchiveLogPosition from)
    {
        _packer = new ArchiveLogUnitPacker(layout);
        (_generation, _baseRows, _length, _units) = (from.Generation, from.BaseRows, from.Length, from.Units);
    }

    /// <inheritdoc/>
    public override RowToken End() => ArchiveLogTokens.Encode(new ArchiveLogPosition(_generation, _baseRows, _length, _units));

    /// <inheritdoc/>
    protected override void Apply(ArchiveCommand command)
    {
        if (command.IsOpener)
        {
            // An opener raises the generation, and an Append the length, so the plan lies above the token (HI-I3).
            _generation = Add(_generation, 1, ArchiveLogTokens.MaxWide);
            (_baseRows, _length, _units, _opened) = (Add((ulong)Superseded, 1, ArchiveLogTokens.MaxNarrow), 0, 0, true);
            return;
        }

        if (_opened)
        {
            // An Append after the opener lands in its base, and is one more row the base folds.
            _baseRows = Add(_baseRows, 1, ArchiveLogTokens.MaxNarrow);
            return;
        }

        ArchiveLogUnitPacker.CheckAppend(_packer.Layout, command);
        if (_generation == 0)
        {
            _generation = 1;
        }

        if (_packer.Add(command.ContentLength))
        {
            _units = Add(_units, 1, ArchiveLogTokens.MaxNarrow);
        }

        _length = Add(_length, (ulong)command.ContentLength, ArchiveLogTokens.MaxWide);
    }


    /// <inheritdoc/>
    protected override bool AdmitsNext(ArchiveCommand command)
    {
        if (command.IsOpener || _opened)
        {
            return true;
        }

        ArchiveLogUnitPacker.CheckAppend(_packer.Layout, command);
        var units = _packer.Starts(command.ContentLength) ? _units + 1 : _units;
        return _packer.Layout.MaxUnits is not { } limit || units <= limit;
    }

    /// <summary>Returns <paramref name="value"/> plus <paramref name="increment"/>, a field of the position the plan reaches, within <paramref name="bound"/>.</summary>
    /// <exception cref="ArchiveCorruptException">The sum passes the field's bound: archive logs could not have minted the write's token.</exception>
    private static ulong Add(ulong value, ulong increment, ulong bound) =>
        bound < value || bound - value < increment
            ? throw new ArchiveCorruptException("Archive logs could not have minted the write's token: no chain passes the largest position.")
            : value + increment;
}
