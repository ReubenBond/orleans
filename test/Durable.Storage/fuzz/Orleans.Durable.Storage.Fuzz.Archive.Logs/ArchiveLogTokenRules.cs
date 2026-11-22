namespace Orleans.Durable.Storage.Fuzz.Archive.Logs;

/// <summary>
/// One reading of archive logs' token rules (archive-logs.md §7.2), written apart from the codec: the oracle of the
/// <c>archive-log-token</c> target. The empty token is <c>Origin</c>; any other is the version byte 1, then <c>g</c>,
/// <c>r</c>, <c>P</c>, and <c>b</c> as unsigned LEB128 varints in their shortest form, each of at most 10 bytes and 64
/// bits, and nothing after them; <c>g</c> is not 0, <c>g</c> and <c>P</c> are below <c>2^56</c> and <c>r</c> and
/// <c>b</c> below <c>2^35</c>, <c>b</c> is 0 exactly when <c>P</c> is, and <c>b</c> is at most <c>P</c>. It checks them
/// in that order.
/// </summary>
public static class ArchiveLogTokenRules
{
    /// <summary>Reads <paramref name="token"/>: the reason of the first rule it breaks, or the position it encodes.</summary>
    public static (string? Reason, (ulong Generation, ulong BaseRows, ulong Length, ulong Units) Position) Read(ReadOnlySpan<byte> token)
    {
        if (token.Length == 0)
        {
            return (null, (0, 0, 0, 0));
        }

        if (token[0] != 1)
        {
            return ("bad-version", default);
        }

        var at = 1;
        var fields = new ulong[4];
        for (var field = 0; field < fields.Length; field++)
        {
            if (Leb128(token, ref at) is not { } value)
            {
                return ("bad-varint", default);
            }

            fields[field] = value;
        }

        return at != token.Length ? ("trailing-bytes", default)
            : fields[0] == 0 ? ("zero-generation", default)
            : fields[0] >= 1UL << 56 || fields[2] >= 1UL << 56 || fields[1] >= 1UL << 35 || fields[3] >= 1UL << 35 ? ("out-of-range", default)
            : fields[3] == 0 != (fields[2] == 0) ? ("bad-units", default)
            : fields[3] > fields[2] ? ("units-above-length", default)
            : (null, (fields[0], fields[1], fields[2], fields[3]));
    }

    /// <summary>Reads one shortest-form LEB128 of at most 64 bits, or <see langword="null"/>.</summary>
    private static ulong? Leb128(ReadOnlySpan<byte> token, ref int at)
    {
        ulong value = 0;
        for (var index = 0; index < 10; index++)
        {
            if (at == token.Length)
            {
                return null;
            }

            var next = token[at++];
            if (index == 9 && next > 1)
            {
                return null;
            }

            value |= (ulong)(next & 0x7f) << (7 * index);
            if ((next & 0x80) == 0)
            {
                return index > 0 && next == 0 ? null : value;
            }
        }

        return null;
    }
}
