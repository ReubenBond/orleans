using System.Globalization;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Journals;

namespace Orleans.Durable.Storage;

/// <summary>
/// Moves the replica calls' payloads and DATA replies across the composition's wire boundary (orleans.md §5.12, OR-I18):
/// a grain message carries Orleans' own <see cref="OrleansBuffer"/>, whose pages come from Orleans' pool, and every Durable Storage
/// layer takes Durable Storage's <see cref="ArcBuffer"/>, whose pages come from Durable Storage's. Each crossing copies the bytes once, so no
/// Orleans message refers to a Durable Storage page and no Durable Storage layer holds an Orleans one.
/// </summary>
internal static class WireBuffers
{
    /// <summary>
    /// Copies <paramref name="received"/>, a buffer a grain message delivered, into a buffer from Durable Storage's page pool, which
    /// the caller owns, and releases <paramref name="received"/> on every path, leaving it empty so that releasing it again
    /// is harmless.
    /// </summary>
    public static ArcBuffer MoveToArcBuffer(ref OrleansBuffer received)
    {
        try
        {
            var length = received.Length;
            if (length == 0)
            {
                return default;
            }

            var first = received.First!;
            if (length <= first.Length - received.Offset)
            {
                return ArcBuffer.CopyFrom(first.AsSpan(received.Offset, length));
            }

            using var writer = new ArcBufferWriter(length);
            foreach (var span in received)
            {
                writer.Write(span);
            }

            return writer.ConsumeSlice(length);
        }
        finally
        {
            received.Dispose();
            received = default;
        }
    }

    /// <summary>
    /// Copies <paramref name="source"/> into an exact-sized unpinned Orleans buffer for a grain message to carry; the
    /// caller keeps <paramref name="source"/>, and owns the copy.
    /// </summary>
    public static OrleansBuffer CopyToOrleans(ArcBuffer source)
    {
        var length = source.Length;
        if (length == 0)
        {
            return default;
        }

        // Wrap an exact-sized unpinned array so Orleans' codec writes one segment and the copy Orleans keeps across
        // silos (OR-I18) is collected as an ordinary array without renting/leaking 32 KiB ArrayPool arrays or POH pages.
        var bytes = GC.AllocateUninitializedArray<byte>(length);
        _ = source.CopyTo(bytes);
        return OrleansBuffer.Wrap(bytes);
    }

    /// <summary>
    /// Encodes <paramref name="record"/> directly into an exact-sized unpinned Orleans buffer for a grain message to
    /// carry, avoiding an intermediate Durable Storage buffer copy; the caller keeps <paramref name="record"/>, and owns
    /// the copy.
    /// </summary>
    public static OrleansBuffer CopyToOrleans(JournalEntryRecord record)
    {
        if (record.Length == 0)
        {
            throw new InvalidOperationException("The default JournalEntryRecord holds no record.");
        }

        var length = checked((int)record.Length);
        var bytes = GC.AllocateUninitializedArray<byte>(length);
        var written = record.WriteTo(bytes);
        return OrleansBuffer.Wrap(bytes.AsMemory(0, written));
    }

    /// <summary>
    /// Copies <paramref name="length"/> bytes from <paramref name="offset"/> of the content <paramref name="chunks"/>
    /// hold in order, a forwarded read's page, into an exact-sized unpinned Orleans buffer for a grain message to carry;
    /// the caller keeps the chunks, and owns the copy.
    /// </summary>
    public static OrleansBuffer CopyToOrleans(ReadOnlySpan<ArcBuffer> chunks, long offset, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (length == 0)
        {
            return default;
        }

        var bytes = GC.AllocateUninitializedArray<byte>(length);
        var destination = bytes.AsSpan();
        var written = 0;
        var end = offset + length;
        long position = 0;
        foreach (ref readonly var chunk in chunks)
        {
            var next = position + chunk.Length;
            if (next > offset)
            {
                var start = (int)(Math.Max(offset, position) - position);
                var count = (int)(Math.Min(end, next) - position - start);
                if (start == 0 && count == chunk.Length)
                {
                    written += chunk.CopyTo(destination[written..]);
                }
                else
                {
                    var skip = start;
                    var remaining = count;
                    foreach (var segment in chunk)
                    {
                        if (skip >= segment.Length)
                        {
                            skip -= segment.Length;
                            continue;
                        }

                        var slice = segment[skip..];
                        skip = 0;
                        if (slice.Length > remaining)
                        {
                            slice = slice[..remaining];
                        }

                        slice.CopyTo(destination[written..]);
                        written += slice.Length;
                        remaining -= slice.Length;
                        if (remaining == 0)
                        {
                            break;
                        }
                    }
                }
            }

            position = next;
            if (position >= end)
            {
                break;
            }
        }

        if (position < end)
        {
            throw new ArgumentOutOfRangeException(nameof(length), length, string.Create(CultureInfo.InvariantCulture, $"The chunks hold {position} bytes, less than {length} from {offset}."));
        }

        return OrleansBuffer.Wrap(bytes.AsMemory(0, written));
    }
}
