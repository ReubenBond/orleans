using System.Buffers;
using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Archive.Logs;
using Orleans.Durable.Storage.Engine.Archive.Logs.Control;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Testing;

/// <summary>
/// A test's drain into archive logs (archive-logs.md §8.1, §8.2): each key's write of a segment, computed from its rows
/// as a drain computes it, written through <see cref="ArchiveLogWriter"/> with the rows' own bodies as its content.
/// </summary>
internal static class ArchiveLogDrains
{
    /// <summary>
    /// Writes each key's write of one segment's <paramref name="rows"/>, in tag order, which the drain borrows, and
    /// returns each key's verdict, by its key in hexadecimal.
    /// </summary>
    public static async Task<Dictionary<string, ArchiveLogConditionOutcome>> DrainAsync(ArchiveLogWriter writer, ArchiveLogNames names, IReadOnlyList<KeyedRow> rows, CancellationToken cancellationToken)
    {
        var verdicts = new Dictionary<string, ArchiveLogConditionOutcome>(StringComparer.Ordinal);
        foreach (var group in rows.GroupBy(static row => Convert.ToHexString(row.Key), StringComparer.Ordinal))
        {
            var keyRows = group.Select(static row => row.Row).ToArray();
            var write = ArchiveWrite.OfRows(keyRows);
            var content = ContentOf(keyRows.AsSpan(keyRows.Length - write.Commands.Length));
            verdicts[group.Key] = await writer.WriteAsync(names.Of(group.First().Key), write, content, cancellationToken);
        }

        return verdicts;
    }

    /// <summary>Returns the bodies of a write's rows, a <c>Delete</c> having none, chained without copying.</summary>
    public static ReadOnlySequence<byte> ContentOf(ReadOnlySpan<ArchiveRow> rows)
    {
        var chain = new SequenceChain();
        foreach (var row in rows)
        {
            if (row.Kind != RowKind.Delete)
            {
                chain.Add(row.Body.AsReadOnlySequence());
            }
        }

        return chain.ToSequence();
    }
}

/// <summary>
/// Chains memory segments, without copying them, into one <see cref="ReadOnlySequence{T}"/>, which is valid while the
/// memory each segment names is.
/// </summary>
internal sealed class SequenceChain
{
    private Link? _first;
    private Link? _last;

    /// <summary>Gets the chain's length in bytes.</summary>
    public long Length { get; private set; }

    /// <summary>Returns the sequence of <paramref name="pieces"/>, each its own segment.</summary>
    public static ReadOnlySequence<byte> Of(params byte[][] pieces)
    {
        var chain = new SequenceChain();
        foreach (var piece in pieces)
        {
            chain.Add(piece);
        }

        return chain.ToSequence();
    }

    /// <summary>Appends <paramref name="memory"/>; an empty one adds nothing.</summary>
    public void Add(ReadOnlyMemory<byte> memory)
    {
        if (memory.IsEmpty)
        {
            return;
        }

        var link = new Link(memory, Length);
        if (_last is null)
        {
            _first = link;
        }
        else
        {
            _last.SetNext(link);
        }

        _last = link;
        Length += memory.Length;
    }

    /// <summary>Appends each segment of <paramref name="sequence"/>.</summary>
    public void Add(ReadOnlySequence<byte> sequence)
    {
        foreach (var memory in sequence)
        {
            Add(memory);
        }
    }

    /// <summary>Returns the chained segments as one sequence.</summary>
    public ReadOnlySequence<byte> ToSequence() =>
        _first is null || _last is null ? ReadOnlySequence<byte>.Empty : new(_first, 0, _last, _last.Memory.Length);

    private sealed class Link : ReadOnlySequenceSegment<byte>
    {
        public Link(ReadOnlyMemory<byte> memory, long runningIndex)
        {
            Memory = memory;
            RunningIndex = runningIndex;
        }

        public void SetNext(Link next) => Next = next;
    }
}
