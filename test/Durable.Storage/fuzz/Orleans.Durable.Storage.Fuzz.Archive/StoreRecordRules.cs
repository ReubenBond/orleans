using System.Text;
using System.Text.Unicode;

namespace Orleans.Durable.Storage.Fuzz.Archive;

/// <summary>A store's record as the format's rules give it: its ID's digits, its layout, its store fields, and the caller's parameters.</summary>
public sealed record RuleStoreRecord(string Id, string Layout, IReadOnlyDictionary<string, string> Fields, IReadOnlyDictionary<string, string> Parameters);

/// <summary>What the format's rules give for some bytes: the reason of the first rule they break, or the record they hold.</summary>
public sealed record StoreRecordReading(string? Reason, RuleStoreRecord? Record)
{
    public static StoreRecordReading Rejected(string reason) => new(reason, null);
}

/// <summary>
/// One reading of the store record's rules (archive.md §7), written apart from <c>ArchiveStoreRecord</c>: the oracle of the
/// <c>archive-store-record</c> target. It walks the bytes, not decoded text: the bytes are strict UTF-8; lines are
/// separated by a line feed; each line is a key of <c>[a-z0-9-]</c>, or <c>caller.</c> and one, an equals sign, and a
/// value; no key repeats; the first line is <c>id</c> with 32 lowercase hexadecimal digits, not all zero; and the second is
/// <c>layout</c> with a non-empty value. It checks them in that order, a line's key before its repetition, line by line.
/// </summary>
public static class StoreRecordRules
{
    private static ReadOnlySpan<byte> CallerPrefix => "caller."u8;

    /// <summary>Reads <paramref name="value"/> as the format's rules give it.</summary>
    public static StoreRecordReading Read(ReadOnlySpan<byte> value)
    {
        if (!Utf8.IsValid(value))
        {
            return StoreRecordReading.Rejected("bad-utf8");
        }

        var lines = new List<(string Key, string Value)>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var rest = value;
        while (true)
        {
            var end = rest.IndexOf((byte)'\n');
            var line = end < 0 ? rest : rest[..end];
            var equals = line.IndexOf((byte)'=');
            if (equals < 0 || !IsKey(line[..equals]))
            {
                return StoreRecordReading.Rejected("bad-line");
            }

            var key = Encoding.UTF8.GetString(line[..equals]);
            if (!keys.Add(key))
            {
                return StoreRecordReading.Rejected("duplicate-field");
            }

            lines.Add((key, Encoding.UTF8.GetString(line[(equals + 1)..])));
            if (end < 0)
            {
                break;
            }

            rest = rest[(end + 1)..];
        }

        if (lines[0].Key != "id" || !IsId(lines[0].Value))
        {
            return StoreRecordReading.Rejected("bad-id");
        }

        if (lines.Count < 2 || lines[1].Key != "layout" || lines[1].Value.Length == 0)
        {
            return StoreRecordReading.Rejected("bad-layout");
        }

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, field) in lines.Skip(2))
        {
            if (key.StartsWith("caller.", StringComparison.Ordinal))
            {
                parameters[key["caller.".Length..]] = field;
            }
            else
            {
                fields[key] = field;
            }
        }

        return new StoreRecordReading(null, new RuleStoreRecord(lines[0].Value, lines[1].Value, fields, parameters));
    }

    private static bool IsKey(ReadOnlySpan<byte> key)
    {
        if (key.StartsWith(CallerPrefix))
        {
            key = key[CallerPrefix.Length..];
        }

        if (key.IsEmpty)
        {
            return false;
        }

        foreach (var b in key)
        {
            if (b is not (>= (byte)'a' and <= (byte)'z' or >= (byte)'0' and <= (byte)'9' or (byte)'-'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsId(string digits)
    {
        if (digits.Length != 32)
        {
            return false;
        }

        var zero = true;
        foreach (var digit in digits)
        {
            if (digit is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
            {
                return false;
            }

            zero &= digit == '0';
        }

        return !zero;
    }
}
