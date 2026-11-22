namespace Orleans.Durable.Storage.Fuzz.Journals;

/// <summary>
/// One reading of a journal version's text form (journals.md §7), written apart from the parser: base64url without
/// padding, whose unused bits are zero, of the base's length (<c>u8</c>, at most 32), the base's bytes, and the count as a
/// shortest varint which fits a <c>u64</c>, ending exactly at the decoded bytes' end.
/// </summary>
public static class VersionRules
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
    private const int MaxBaseLength = 32;

    /// <summary>Reads <paramref name="text"/> as the format's rules give it: the version it is the text of, or null.</summary>
    public static RuleVersion? Read(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0 || text.Length % 4 == 1)
        {
            return null;
        }

        var sextets = new int[text.Length];
        for (var index = 0; index < text.Length; index++)
        {
            sextets[index] = Alphabet.IndexOf(text[index], StringComparison.Ordinal);
            if (sextets[index] < 0)
            {
                return null;
            }
        }

        // A trailing group of 2 characters carries 1 byte and 4 unused bits; one of 3 carries 2 bytes and 2 unused bits.
        var unusedBits = (text.Length % 4) switch
        {
            2 => 4,
            3 => 2,
            _ => 0,
        };
        if ((sextets[^1] & ((1 << unusedBits) - 1)) != 0)
        {
            return null;
        }

        var bytes = new List<byte>();
        var accumulated = 0;
        var bits = 0;
        foreach (var sextet in sextets)
        {
            accumulated = (accumulated << 6) | sextet;
            bits += 6;
            if (bits >= 8)
            {
                bits -= 8;
                bytes.Add((byte)(accumulated >> bits));
                accumulated &= (1 << bits) - 1;
            }
        }

        var decoded = bytes.ToArray();
        var baseLength = decoded[0];
        if (baseLength > MaxBaseLength || decoded.Length < 1 + baseLength)
        {
            return null;
        }

        return EntryRules.TryReadCount(decoded.AsSpan(1 + baseLength), out var count, out var countLength) && 1 + baseLength + countLength == decoded.Length
            ? new RuleVersion(decoded[1..(1 + baseLength)], count)
            : null;
    }
}

/// <summary>A version as <see cref="VersionRules"/> reads it: its base's bytes and its count.</summary>
public sealed record RuleVersion(byte[] Base, ulong Count);
