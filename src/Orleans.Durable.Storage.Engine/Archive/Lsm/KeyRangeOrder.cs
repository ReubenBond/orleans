using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Engine.Archive.Lsm;

/// <summary>
/// Orders keys against a dyadic range, whose keys form an interval of the bytewise key order (identity.md §4).
/// </summary>
internal static class KeyRangeOrder
{
    /// <summary>
    /// Returns a negative number when <paramref name="key"/> lies below every key of <paramref name="range"/>, 0 when the
    /// range contains it, and a positive number when it lies above every key of the range.
    /// </summary>
    /// <remarks>
    /// It compares the key's first <see cref="KeyRange.Depth"/> bits, 0 past its end as containment reads them, with the
    /// prefix. Those bits never decrease along the key order, so the comparison orders the key against the range's
    /// interval.
    /// </remarks>
    public static int Compare(ReadOnlySpan<byte> key, KeyRange range)
    {
        var prefix = range.Prefix;
        var wholeBytes = range.Depth / 8;
        for (var index = 0; index < wholeBytes; index++)
        {
            var keyByte = index < key.Length ? key[index] : (byte)0;
            if (keyByte != prefix[index])
            {
                return keyByte < prefix[index] ? -1 : 1;
            }
        }

        var partialBits = range.Depth % 8;
        if (partialBits == 0)
        {
            return 0;
        }

        var last = (byte)((wholeBytes < key.Length ? key[wholeBytes] : 0) & (0xff << (8 - partialBits)));
        return last.CompareTo(prefix[wholeBytes]);
    }
}
