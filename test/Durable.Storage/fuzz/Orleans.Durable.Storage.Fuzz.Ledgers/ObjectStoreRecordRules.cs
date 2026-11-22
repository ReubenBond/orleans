namespace Orleans.Durable.Storage.Fuzz.Ledgers;

/// <summary>What the format's rules give for some bytes: the reason of the first rule they break, or the record's fields.</summary>
public sealed record ObjectStoreRecordReading(string? Reason, string? Id, string? Layout)
{
    public static ObjectStoreRecordReading Rejected(string reason) => new(reason, null, null);
}

/// <summary>
/// One reading of the object-ledger store record's rules (object-ledgers.md §7), written apart from
/// <c>ObjectLedgerStoreRecord</c>: the oracle of the <c>ledgers-object-store-record</c> target. It walks the bytes: two lines
/// separated by the one line feed (<c>bad-line</c>); the first <c>id=</c> and 32 lowercase hexadecimal digits, not all
/// zero (<c>bad-id</c>); the second <c>layout=</c> and <c>per-slot</c> or <c>append</c> (<c>bad-layout</c>); in that order.
/// </summary>
public static class ObjectStoreRecordRules
{
    /// <summary>Reads <paramref name="value"/> as the format's rules give it.</summary>
    public static ObjectStoreRecordReading Read(ReadOnlySpan<byte> value)
    {
        var feeds = 0;
        var split = -1;
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == (byte)'\n')
            {
                feeds++;
                split = index;
            }
        }

        if (feeds != 1)
        {
            return ObjectStoreRecordReading.Rejected("bad-line");
        }

        var first = value[..split];
        var second = value[(split + 1)..];
        if (!IsId(first))
        {
            return ObjectStoreRecordReading.Rejected("bad-id");
        }

        var layout = second.SequenceEqual("layout=per-slot"u8) ? "per-slot" : second.SequenceEqual("layout=append"u8) ? "append" : null;
        return layout is null
            ? ObjectStoreRecordReading.Rejected("bad-layout")
            : new ObjectStoreRecordReading(null, System.Text.Encoding.ASCII.GetString(first[3..]), layout);
    }

    private static bool IsId(ReadOnlySpan<byte> line)
    {
        if (line.Length != 35 || line[0] != (byte)'i' || line[1] != (byte)'d' || line[2] != (byte)'=')
        {
            return false;
        }

        var nonZero = false;
        foreach (var digit in line[3..])
        {
            if (digit is not (>= (byte)'0' and <= (byte)'9' or >= (byte)'a' and <= (byte)'f'))
            {
                return false;
            }

            nonZero |= digit != (byte)'0';
        }

        return nonZero;
    }
}
