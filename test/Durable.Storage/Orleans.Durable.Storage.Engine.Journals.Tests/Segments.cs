using System.Buffers;

namespace Orleans.Durable.Storage.Engine.Journals.Tests;

/// <summary>Lays bytes out as a sequence of segments, as a record read across buffer pages arrives.</summary>
internal static class Segments
{
    /// <summary>Returns <paramref name="bytes"/> as segments of <paramref name="length"/> bytes each, the last holding what is left.</summary>
    public static ReadOnlySequence<byte> Of(ReadOnlySpan<byte> bytes, int length)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(length, 1);
        if (bytes.Length <= length)
        {
            return new ReadOnlySequence<byte>(bytes.ToArray());
        }

        var first = new Segment(bytes[..length].ToArray(), 0);
        var last = first;
        for (var offset = length; offset < bytes.Length; offset += length)
        {
            last = last.Append(bytes[offset..Math.Min(offset + length, bytes.Length)].ToArray());
        }

        return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(byte[] bytes, long runningIndex)
        {
            Memory = bytes;
            RunningIndex = runningIndex;
        }

        public Segment Append(byte[] bytes)
        {
            var next = new Segment(bytes, RunningIndex + Memory.Length);
            Next = next;
            return next;
        }
    }
}
