using System.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Fuzz.Ledgers;

/// <summary>
/// Encodes what <see cref="LedgerSegmentRules"/> reads through <see cref="LedgerBatch"/>, <c>CLLB</c>'s one encoder
/// (ledgers.md §4), so an oracle checks that the encoder writes exactly the bytes the decoder accepted.
/// </summary>
public static class LedgerBatches
{
    /// <summary>
    /// Encodes one batch alone through <see cref="LedgerBatch"/>, over a dictionary which holds a placeholder key for
    /// each ordinal below its ordinal base that a row references; <see langword="null"/> when a writer's batch could not
    /// hold it, since it defines a key twice or carries a count at the <c>u64</c> maximum, which only its segment refuses.
    /// </summary>
    public static byte[]? Encode(RuleBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var defined = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in batch.Rows)
        {
            if (row.IsDefining && (!defined.Add(Convert.ToHexString(row.Key!)) || row.VersionCount == ulong.MaxValue))
            {
                return null;
            }
        }

        // Implied ordinals past the u64's end wrap, which only a segment's ordinal base check refuses.
        if (batch.OrdinalBase + (UInt128)defined.Count > ulong.MaxValue)
        {
            return null;
        }

        // A placeholder for each earlier ordinal a row references, distinct from every key the batch defines.
        var dictionary = new LedgerSegmentDictionary();
        var placeholders = new Dictionary<ulong, byte[]>();
        foreach (var row in batch.Rows)
        {
            if (!row.IsDefining && row.Ordinal < batch.OrdinalBase && !placeholders.ContainsKey(row.Ordinal))
            {
                var key = Placeholder(row.Ordinal, defined);
                placeholders.Add(row.Ordinal, key);
                dictionary.Define(key, row.Ordinal, default);
            }
        }

        var keys = new Dictionary<ulong, byte[]>(placeholders);
        return Encode(batch, dictionary, keys);
    }

    /// <summary>
    /// Encodes a segment's batches, read by the rules and accepted, through <see cref="LedgerBatch"/> over one dictionary
    /// per segment, as the writer does.
    /// </summary>
    public static IReadOnlyList<byte[]> EncodeSegment(IReadOnlyList<RuleBatch> batches)
    {
        ArgumentNullException.ThrowIfNull(batches);
        var encoded = new List<byte[]>(batches.Count);
        var dictionary = new LedgerSegmentDictionary();
        var keys = new Dictionary<ulong, byte[]>();
        foreach (var batch in batches)
        {
            if (batch.IsStart)
            {
                dictionary.Release(keepCapacity: false);
                keys.Clear();
            }

            encoded.Add(Encode(batch, dictionary, keys) ?? throw new InvalidOperationException("A segment the rules accept holds only batches a writer could write."));
        }

        return encoded;
    }

    private static byte[]? Encode(RuleBatch batch, LedgerSegmentDictionary dictionary, Dictionary<ulong, byte[]> keys)
    {
        var length = (long)LedgerBatch.MaxHeaderLength(batch.Rows.Count);
        foreach (var row in batch.Rows)
        {
            length += LedgerBatch.MaxRowLength(row.Key?.Length ?? 1, row.VersionBase?.Length ?? 0, row.Token?.Length ?? 0, row.Body.Length);
        }

        using var encoder = new LedgerBatch(
            KeyRange.Root,
            int.MaxValue,
            batch.Rows.Count,
            new LedgerBatchSegment(batch.IsStart, batch.Offset, batch.OrdinalBase, dictionary),
            (int)Math.Min(length, 1 << 20));
        foreach (var row in batch.Rows)
        {
            if (row.IsDefining)
            {
                keys[row.Ordinal] = row.Key!;
                _ = encoder.AddRow(row.Key, (RowKind)row.Kind, new RowVersion(new RowToken(row.VersionBase), row.VersionCount), new RowToken(row.Token), new ReadOnlySequence<byte>(row.Body));
            }
            else
            {
                var key = keys[row.Ordinal];
                var held = encoder.HeldVersion(key);
                if (held.Count == ulong.MaxValue)
                {
                    return null;
                }

                _ = encoder.AddRow(key, (RowKind)row.Kind, held, default, new ReadOnlySequence<byte>(row.Body));
            }
        }

        using var bytes = encoder.Complete();
        return bytes.ToArray();
    }

    /// <summary>Returns a key for <paramref name="ordinal"/> which no key of <paramref name="defined"/> equals.</summary>
    private static byte[] Placeholder(ulong ordinal, HashSet<string> defined)
    {
        for (var length = 9; ; length++)
        {
            var key = new byte[length];
            key[0] = 0xff;
            _ = BitConverter.TryWriteBytes(key.AsSpan(1), ordinal);
            if (!defined.Contains(Convert.ToHexString(key)))
            {
                return key;
            }
        }
    }
}
