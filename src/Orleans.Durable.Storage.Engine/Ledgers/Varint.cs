using System.Diagnostics;

namespace Orleans.Durable.Storage.Engine.Ledgers;

/// <summary>
/// The batch format's varint: an unsigned LEB128 integer of at most <see cref="MaxLength"/> bytes, in its shortest form
/// (ledgers.md §7).
/// </summary>
internal static class Varint
{
    /// <summary>The most bytes a varint takes.</summary>
    public const int MaxLength = 10;

    /// <summary>What reading a varint found.</summary>
    public enum Status : byte
    {
        /// <summary>A varint in its shortest form which fits a <c>u64</c>.</summary>
        Valid,

        /// <summary>The bytes ended before the varint terminated, within <see cref="MaxLength"/> bytes.</summary>
        Unterminated,

        /// <summary>
        /// Not a varint of the format: <see cref="MaxLength"/> bytes without a terminating one, a 10th byte past the
        /// <c>u64</c>, or a longer form than the shortest.
        /// </summary>
        Invalid,
    }

    /// <summary>Returns the bytes <paramref name="value"/> takes as a varint.</summary>
    public static int Length(ulong value)
    {
        var length = 1;
        while (value >= 0x80)
        {
            value >>= 7;
            length++;
        }

        return length;
    }

    /// <summary>Writes <paramref name="value"/> at the start of <paramref name="destination"/> and returns its length.</summary>
    public static int Write(Span<byte> destination, ulong value)
    {
        var length = 0;
        while (value >= 0x80)
        {
            destination[length++] = (byte)(value & 0x7f | 0x80);
            value >>= 7;
        }

        destination[length++] = (byte)value;
        return length;
    }

    /// <summary>
    /// Reads a varint's next byte, <paramref name="b"/>, the <paramref name="index"/>th from 0, into
    /// <paramref name="value"/>; returns whether the varint continues, or the <see cref="Status"/> it ended with in
    /// <paramref name="status"/>.
    /// </summary>
    public static bool Accumulate(byte b, int index, ref ulong value, out Status status)
    {
        Debug.Assert(index is >= 0 and < MaxLength, "A varint takes at most 10 bytes.");
        if (index == MaxLength - 1 && b > 1)
        {
            // A 10th byte carries only the u64's top bit, and ends the varint.
            status = Status.Invalid;
            return false;
        }

        value |= (ulong)(b & 0x7f) << (7 * index);
        if ((b & 0x80) != 0)
        {
            status = Status.Valid;
            return true;
        }

        // A form longer than the shortest ends with a zero byte.
        status = index > 0 && b == 0 ? Status.Invalid : Status.Valid;
        return false;
    }

    /// <summary>Reads a varint from the start of <paramref name="source"/>, returning its status, value, and length.</summary>
    public static Status Read(ReadOnlySpan<byte> source, out ulong value, out int length)
    {
        value = 0;
        for (var index = 0; index < MaxLength; index++)
        {
            if (index == source.Length)
            {
                length = index;
                return Status.Unterminated;
            }

            if (!Accumulate(source[index], index, ref value, out var status))
            {
                length = index + 1;
                return status;
            }
        }

        length = MaxLength;
        return Status.Invalid;
    }
}
