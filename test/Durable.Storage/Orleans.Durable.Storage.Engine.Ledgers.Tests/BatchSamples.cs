using System.Buffers;
using System.Globalization;
using CsCheck;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Fuzz.Ledgers;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests;

/// <summary>A row's fields as plain values, which tests generate, compare, and turn into owned <see cref="Row"/>s.</summary>
internal sealed record RowSample(RowKind Kind, byte[] Key, byte[] Body)
{
    public static RowSample Of(ResolvedRow row)
    {
        var body = row.Body.ToArray();
        Assert.Equal(body.Length, row.BodyLength);
        return new(row.Kind, row.Key.ToArray(), body);
    }

    public static RowSample Of(Row row) => new(row.Kind, row.Key.ToArray(), row.Body.ToArray());

    public static RowSample Of(ResolvedRuleRow row) => new((RowKind)row.Kind, row.Key, row.Body);

    /// <summary>Makes an owned row, with its body in pages of <paramref name="pageLengths"/> bytes, the fewest by default.</summary>
    public Row ToRow(int[]? pageLengths = null)
    {
        using var body = PagedBuffers.Create(Body, pageLengths ?? []);
        return new(Key, Kind, body);
    }

    /// <summary>Returns the row's fields as text, so rows compare by value.</summary>
    public string Describe() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{Kind} {Convert.ToHexStringLower(Key)} {Convert.ToHexStringLower(Body)}");
}

/// <summary>A resolved row's fields as plain values: its tag, key, kind, version, token, and body.</summary>
internal sealed record ResolvedSample(long Slot, int Index, byte[] Key, RowKind Kind, byte[] VersionBase, ulong VersionCount, byte[] Token, byte[] Body)
{
    public static ResolvedSample Of(ResolvedRow row) =>
        new(row.Tag.Slot.Value, row.Tag.Index, row.Key.ToArray(), row.Kind, row.Version.Base.ToArray(), row.Version.Count, row.Token.ToArray(), row.Body.ToArray());

    public static ResolvedSample Of(ResolvedRuleRow row) =>
        new(row.Slot, row.Index, row.Key, (RowKind)row.Kind, row.VersionBase, row.VersionCount, row.Token, row.Body);

    public string Describe() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{Slot}/{Index} {Kind} {Convert.ToHexStringLower(Key)} ({Convert.ToHexStringLower(VersionBase)}, {VersionCount}) {Convert.ToHexStringLower(Token)} {Convert.ToHexStringLower(Body)}");
}

/// <summary>
/// One batch of a generated segment: whether it starts a segment, and its rows, each with the version base, count, and
/// token its key's defining row stores when it defines it.
/// </summary>
internal sealed record PlannedBatch(bool Start, IReadOnlyList<PlannedRow> Rows);

/// <summary>A planned row: its kind, key, and body, and the version and token it passes, which its defining row stores.</summary>
internal sealed record PlannedRow(RowKind Kind, byte[] Key, byte[] Body, byte[] VersionBase, ulong VersionCount, byte[] Token);

/// <summary>Generators and helpers of the batch and segment tests.</summary>
internal static class BatchSamples
{
    public static readonly Gen<RowSample> RowSamples = Gen.Select(
        Gen.Enum<RowKind>(),
        Gen.Frequency((6, Gen.Byte.Array[1, 24]), (1, Gen.Byte.Array[250, 300])),
        Gen.Frequency((2, Gen.Const(Array.Empty<byte>())), (6, Gen.Byte.Array[1, 64]), (1, Gen.Byte.Array[200, 2_000])),
        static (kind, key, body) => new RowSample(kind, key, body));

    /// <summary>Rows whose keys repeat, so a batch both defines and references keys.</summary>
    public static readonly Gen<RowSample[]> Rows = Gen.Frequency(
        (3, RowSamples.Array[1, 6]),
        (2, Gen.Select(RowSamples.Array[1, 3], Gen.Int[0, 5].Array[1, 6]).Select(static (keys, picks) =>
            picks.Select(pick => keys[pick % keys.Length] with { Body = [(byte)pick] }).ToArray())));

    /// <summary>Page lengths for a division of a batch into pages: often a few bytes, so fields span pages.</summary>
    public static readonly Gen<int[]> PageLengths = Gen.Frequency(
        (1, Gen.Const(Array.Empty<int>())),
        (3, Gen.Int[1, 40].Array[1, 40]),
        (1, Gen.Int[1, 3].Array[1, 400]));

    /// <summary>A key's version base or token: often empty, sometimes the longest.</summary>
    public static readonly Gen<byte[]> Tokens = Gen.Frequency(
        (3, Gen.Const(Array.Empty<byte>())),
        (3, Gen.Byte.Array[1, 8]),
        (1, Gen.Byte.Array[RowToken.MaxLength]));

    /// <summary>A version count: often small, sometimes near the <c>u64</c> maximum.</summary>
    public static readonly Gen<ulong> Counts = Gen.Frequency(
        (6, Gen.ULong[0, 300]),
        (1, Gen.ULong[ulong.MaxValue - 4, ulong.MaxValue - 1]));

    /// <summary>
    /// Segments: batches over a small pool of keys, which later batches reference, and which start flags and markers
    /// divide into segments.
    /// </summary>
    public static readonly Gen<PlannedBatch[]> Segments =
        Gen.Select(
            Gen.Byte.Array[1, 6].Array[1, 5],
            Gen.Select(Tokens, Counts, Tokens).Array[5])
        .SelectMany(static (keys, versions) =>
            Gen.Select(
                Gen.Frequency((8, Gen.Const(false)), (1, Gen.Const(true))),
                Gen.Select(Gen.Enum<RowKind>(), Gen.Int[0, 4], Gen.Frequency((2, Gen.Const(Array.Empty<byte>())), (3, Gen.Byte.Array[1, 40]))).Array[0, 5])
            .Array[1, 8]
            .Select(batches => batches.Select((batch, index) =>
            {
                var (start, rows) = batch;
                var planned = rows.Select(row =>
                {
                    var (kind, pick, body) = row;
                    var key = keys[pick % keys.Length];
                    var (versionBase, count, token) = versions[pick % versions.Length];
                    return new PlannedRow(kind, key, body, versionBase, count, token);
                }).ToArray();

                // Slot 0 starts a segment, and a batch with no rows is a marker, which does.
                return new PlannedBatch(index == 0 || start || planned.Length == 0, planned);
            }).ToArray()));

    /// <summary>
    /// Encodes <paramref name="samples"/> into one batch which starts a segment, each body in pages of
    /// <paramref name="bodyPageLengths"/> bytes, the fewest by default: each key's first row defines it with the version
    /// <c>(empty, 0)</c> and the empty token, and its later rows reference it.
    /// </summary>
    public static byte[] Encode(IReadOnlyList<RowSample> samples, int[]? bodyPageLengths = null) =>
        Encode(samples, bodyPageLengths, out _);

    /// <summary>
    /// Encodes <paramref name="samples"/> as <see cref="Encode(IReadOnlyList{RowSample}, int[])"/> does, and returns the
    /// number of pages the encoder wrote the batch into.
    /// </summary>
    public static byte[] Encode(IReadOnlyList<RowSample> samples, int[]? bodyPageLengths, out int pages)
    {
        var rows = samples.Select(sample => sample.ToRow(bodyPageLengths)).ToList();
        try
        {
            using var encoded = BatchView.Encode(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(rows));
            pages = 0;
            foreach (var _ in encoded)
            {
                pages++;
            }

            return encoded.ToArray();
        }
        finally
        {
            foreach (var row in rows)
            {
                row.Dispose();
            }
        }
    }

    /// <summary>
    /// Encodes a planned segment's batches at slots from <paramref name="firstSlot"/>, each through a
    /// <see cref="LedgerBatch"/> over one dictionary per segment, as a writer does: a key's first row in its segment
    /// defines it with its planned version and token, and later rows reference it with the version the dictionary holds.
    /// </summary>
    public static List<byte[]> EncodeSegment(IReadOnlyList<PlannedBatch> batches, long firstSlot = 0)
    {
        var encoded = new List<byte[]>(batches.Count);
        var dictionary = new LedgerSegmentDictionary();
        var start = firstSlot;
        for (var index = 0; index < batches.Count; index++)
        {
            var slot = firstSlot + index;
            var batch = batches[index];
            if (batch.Start)
            {
                dictionary.Release(keepCapacity: false);
                start = slot;
            }

            var segment = batch.Start
                ? new LedgerBatchSegment(IsStart: true, Offset: 0, OrdinalBase: 0, dictionary)
                : new LedgerBatchSegment(IsStart: false, (ulong)(slot - start), (ulong)dictionary.Count, dictionary);
            using var encoder = new LedgerBatch(KeyRange.Root, int.MaxValue, batch.Rows.Count, segment);
            foreach (var row in batch.Rows)
            {
                var before = dictionary.Contains(row.Key) ? encoder.HeldVersion(row.Key) : new RowVersion(new RowToken(row.VersionBase), row.VersionCount);
                _ = encoder.AddRow(row.Key, row.Kind, before, new RowToken(row.Token), new ReadOnlySequence<byte>(row.Body));
            }

            using var bytes = encoder.Complete();
            encoded.Add(bytes.ToArray());
        }

        return encoded;
    }

    /// <summary>
    /// Returns whether a planned segment's rows stay within the <c>u64</c> count, so a writer can write it: a count
    /// overflow is a <see cref="LedgerBatch.BeginRow"/> precondition.
    /// </summary>
    public static bool Writable(IReadOnlyList<PlannedBatch> batches)
    {
        var counts = new Dictionary<string, ulong>(StringComparer.Ordinal);
        foreach (var batch in batches)
        {
            if (batch.Start)
            {
                counts.Clear();
            }

            foreach (var row in batch.Rows)
            {
                var name = Convert.ToHexString(row.Key);
                var count = counts.TryGetValue(name, out var held) ? held : row.VersionCount;
                if (count == ulong.MaxValue)
                {
                    return false;
                }

                counts[name] = count + 1;
            }
        }

        return true;
    }

    /// <summary>Decodes a valid batch which starts a segment, alone, as the rows it resolves, as plain values.</summary>
    public static RowSample[] Decode(ArcBuffer buffer)
    {
        var rejection = BatchView.Decode(buffer, out var batch);
        Assert.Null(rejection);
        using var segment = new LedgerSegmentView(new LedgerId(1));
        Assert.Null(segment.Advance(Slot.Zero, batch));
        var rows = new List<RowSample>(batch.Count);
        foreach (var row in segment.Rows)
        {
            Assert.Equal(rows.Count, row.Tag.Index);
            rows.Add(RowSample.Of(row));
        }

        Assert.Equal(batch.Count, rows.Count);
        return [.. rows];
    }

    /// <summary>
    /// Decodes batches at slots from <paramref name="firstSlot"/>, each from pages of <paramref name="pageLengths"/>,
    /// through one <see cref="LedgerSegmentView"/>, filtered to <paramref name="key"/> when one is given: the resolved
    /// rows, or the first rejection with its batch's index.
    /// </summary>
    public static (int FailedBatch, string? Reason, List<ResolvedSample> Rows) DecodeSegment(
        IReadOnlyList<byte[]> batches,
        long firstSlot = 0,
        int[]? pageLengths = null,
        byte[]? key = null,
        int from = 0)
    {
        var rows = new List<ResolvedSample>();
        using var view = key is null ? new LedgerSegmentView(new LedgerId(1)) : new LedgerSegmentView(new LedgerId(1), key);
        for (var index = from; index < batches.Count; index++)
        {
            using var buffer = PagedBuffers.Create(batches[index], pageLengths ?? []);
            var rejection = BatchView.Decode(buffer, out var batch) ?? view.Advance(new Slot(firstSlot + index), batch);
            if (rejection is not null)
            {
                return (index, Reason(rejection), rows);
            }

            foreach (var row in view.Rows)
            {
                rows.Add(ResolvedSample.Of(row));
            }
        }

        return (-1, null, rows);
    }

    public static void AssertRows(IEnumerable<RowSample> expected, IEnumerable<RowSample> actual) =>
        Assert.Equal(expected.Select(static row => row.Describe()), actual.Select(static row => row.Describe()));

    public static void AssertResolved(IEnumerable<ResolvedSample> expected, IEnumerable<ResolvedSample> actual) =>
        Assert.Equal(expected.Select(static row => row.Describe()), actual.Select(static row => row.Describe()));

    /// <summary>Returns the reason code a rejection carries, under the key every format's decoder uses (conventions.md §15).</summary>
    public static string? Reason(InvalidDataException? rejection) => rejection?.Data["orleans.durable.storage.decode.reason"] as string;

    /// <summary>
    /// Returns the reasons of the rule table which follows <paramref name="format"/>'s paragraph in ledgers.md §7, in the
    /// order a decoder checks them, read from the spec.
    /// </summary>
    /// <param name="format">The start of the paragraph which introduces the table, such as <c>**Batch rules.**</c>.</param>
    public static string[] SpecReasons(string format = "**Batch rules.**")
    {
        var lines = File.ReadAllLines(Path.Combine(SharedVectors.RepositoryRoot(), "docs", "spec", "components", "ledgers.md"));
        var start = Array.FindIndex(lines, line => line.StartsWith(format, StringComparison.Ordinal));
        Assert.True(start >= 0, $"ledgers.md §7 has no paragraph {format}.");
        var header = Array.IndexOf(lines, "| Reason | Rule |", start);
        Assert.True(header >= 0, $"ledgers.md §7 has no table of reasons after {format}.");
        return
        [
            .. lines.Skip(header + 2)
                .TakeWhile(static line => line.StartsWith('|'))
                .Select(static line => line.Split('|', StringSplitOptions.TrimEntries)[1].Trim('`')),
        ];
    }

    /// <summary>Returns the distinct reasons of a rejection vector file, in file order.</summary>
    public static string[] VectorReasons(string file = "batch-rejections.json") =>
        [.. SharedVectors.Cases("ledgers", file).Select(static vector => SharedVectors.Text(vector, "reason")).Distinct(StringComparer.Ordinal)];
}
