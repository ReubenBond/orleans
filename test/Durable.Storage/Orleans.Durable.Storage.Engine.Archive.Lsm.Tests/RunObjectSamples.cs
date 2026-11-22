using System.Buffers;
using System.Globalization;
using System.Text.Json;
using CsCheck;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Durable.Storage.Engine.ObjectStore.Memory;
using static Orleans.Durable.Storage.Testing.SharedVectors;

namespace Orleans.Durable.Storage.Engine.Archive.Lsm.Tests;

/// <summary>
/// A row a test writes into a run object, as plain data: the test builds its <see cref="TaggedRow"/> within a page
/// leak scope and disposes it there.
/// </summary>
internal sealed record RunRowData(byte[] Key, Tag Tag, RowKind Kind, byte[] Payload)
{
    public TaggedRow ToRow()
    {
        using var payload = Payload.Length == 0 ? default : ArcBuffer.Wrap(Payload.ToArray());
        return new(Tag, new Row(Key, Kind, payload));
    }

    /// <summary>Returns whether <paramref name="other"/> holds this row's tag, key, kind, and payload.</summary>
    public bool SameAs(RunRowData other) =>
        other.Tag == Tag && other.Kind == Kind && other.Key.AsSpan().SequenceEqual(Key) && other.Payload.AsSpan().SequenceEqual(Payload);

    /// <summary>Returns whether <paramref name="row"/> holds this row's tag, key, kind, and payload.</summary>
    public bool Matches(TaggedRow row) =>
        row.Tag == Tag
        && row.Row.Key.Span.SequenceEqual(Key)
        && row.Row.Kind == Kind
        && row.Row.Body.ToArray().AsSpan().SequenceEqual(Payload);
}

/// <summary>A section a test writes: its partition and its rows, in order of key, then tag.</summary>
internal sealed record RunSectionData(KeyRange Partition, RunRowData[] Rows)
{

    /// <summary>Returns the rows of <paramref name="key"/>, in order.</summary>
    public RunRowData[] RowsOf(byte[] key) => [.. Rows.Where(row => row.Key.AsSpan().SequenceEqual(key))];

    /// <summary>
    /// Returns each key of the section, and next to each a key in its partition which no row has: the key with a zero
    /// byte appended.
    /// </summary>
    public IEnumerable<byte[]> ProbeKeys() =>
        Rows
            .Select(static row => row.Key)
            .SelectMany(static key => new[] { key, [.. key, 0] })
            .Where(key => key.Length <= KeyRange.MaxKeyLength && Partition.Contains(key))
            .DistinctBy(Convert.ToHexString);
}

/// <summary>A run object a test writes: its sections, condition, and settings.</summary>
internal sealed record RunObjectData(RunSectionData[] Sections, RunObjectCondition Condition, RunSectionSettings Settings)
{
    /// <summary>
    /// Encodes the object, building its rows for the call and releasing them after.
    /// </summary>
    public EncodedRunObject Encode()
    {
        var rows = Sections.Select(static section => section.Rows.Select(static row => row.ToRow()).ToArray()).ToArray();
        try
        {
            var inputs = Sections.Select((section, index) => new RunSectionInput(section.Partition, rows[index])).ToArray();
            return RunObjectFormat.Encode(inputs, Condition, Settings);
        }
        finally
        {
            foreach (var row in rows.SelectMany(static section => section))
            {
                row.Row.Dispose();
            }
        }
    }

    /// <summary>
    /// Returns a name for the object which agrees with its condition (identity.md §11), with the random part
    /// <paramref name="id"/>.
    /// </summary>
    public ObjectName Name(UInt128 id) => Condition switch
    {
        RunObjectCondition.Drain drain => RunObjectNames.Drain(drain.Ledger, drain.From, id),
        RunObjectCondition.Compaction => RunObjectNames.Compaction(Sections[0].Partition, id),
        null => throw new System.Diagnostics.UnreachableException(),
    };
}

/// <summary>
/// Run objects for tests: those of the shared vectors, and generated ones, and the helpers which judge them.
/// </summary>
internal static class RunObjectSamples
{
    private const string Area = "archive";

    /// <summary>
    /// Gets generated run objects: drain outputs, whose rows are of the drain's ledger at its watermark or above, and
    /// compaction outputs, of one to three sections, each of rows of one to eight keys in its partition, under block sizes
    /// from 1 byte to 256 KiB and 1 to 64 Bloom bits per key.
    /// </summary>
    public static Gen<RunObjectData> Objects =>
        Gen.Select(Conditions, Gen.OneOfConst(1, 40, 100, 500, 4096, 256 * 1024), Gen.Int[1, 64])
            .SelectMany(static (condition, blockBytes, bitsPerKey) => Sections(condition).Array[1, 3]
                .Select(sections => new RunObjectData(sections, condition, new RunSectionSettings(blockBytes, bitsPerKey))));

    /// <summary>
    /// Gets drain outputs as a drain writes them: one section, at a partition of the ledger's range, whose rows each have
    /// a distinct tag of the ledger at or above the drain's watermark, as each row of a ledger has.
    /// </summary>
    public static Gen<RunObjectData> DrainOutputs =>
        Gen.Select(Gen.ULong[1, 1000], Gen.Long[0, 1 << 20], Gen.Int[0, 12], Gen.Byte.Array[2])
            .SelectMany(static (ledger, from, depth, seed) =>
            {
                var partition = KeyRange.Containing(seed, depth);
                var condition = new RunObjectCondition.Drain(new LedgerId(ledger), new Slot(from));
                return Gen.Select(KeysIn(partition), RowFields(ledger, from)).Array[1, 40].Select(rows =>
                    new RunObjectData(
                        [Section(partition, [.. rows.DistinctBy(static row => row.Item2.Tag)])],
                        condition,
                        new ArchiveOptions().SectionSettings with { BlockBytes = 200 }));
            });

    /// <summary>Gets reference conditions: drains, and compactions of one to three inputs in order.</summary>
    public static Gen<RunObjectCondition> Conditions =>
        Gen.Frequency(
            (1, Gen.Select(Gen.ULong[1, 12], Gen.Long[0, 1000]).Select(static (ledger, from) => (RunObjectCondition)new RunObjectCondition.Drain(new LedgerId(ledger), new Slot(from)))),
            (1, Gen.Select(Gen.Int[0, 3], Gen.Long[0, 1 << 20]).Array[1, 3].Select(static inputs => (RunObjectCondition)new RunObjectCondition.Compaction(
                [.. inputs.Select(static input => new SectionPosition(new ObjectName(string.Create(CultureInfo.InvariantCulture, $"d/{input.Item1 + 1}/0/a")), input.Item2))
                    .Distinct()
                    .Order()]))));

    /// <summary>
    /// Gets keys in <paramref name="partition"/>: random bytes, at least as many as its prefix, whose leading bits are
    /// its prefix's.
    /// </summary>
    public static Gen<byte[]> KeysIn(KeyRange partition)
    {
        var prefixBytes = (partition.Depth + 7) / 8;
        var prefix = partition.Prefix.ToArray();
        var depth = partition.Depth;
        return Gen.Byte.Array[Math.Max(1, prefixBytes), Math.Max(1, prefixBytes) + 6].Select(key =>
        {
            for (var bit = 0; bit < depth; bit++)
            {
                var mask = (byte)(0x80 >> (bit % 8));
                key[bit / 8] = (byte)((key[bit / 8] & ~mask) | (prefix[bit / 8] & mask));
            }

            return key;
        });
    }

    /// <summary>Returns the vector of <paramref name="file"/> whose name is <paramref name="name"/>.</summary>
    public static JsonElement Vector(string file, string name) =>
        Cases(Area, file).Single(vector => Orleans.Durable.Storage.Testing.SharedVectors.Name(vector) == name);

    /// <summary>
    /// Builds a vector's run object: its settings, condition, and sections.
    /// </summary>
    public static RunObjectData FromVector(JsonElement vector)
    {
        var settings = new RunSectionSettings(vector.GetProperty("blockBytes").GetInt32(), vector.GetProperty("bloomBitsPerKey").GetInt32());
        var condition = vector.GetProperty("condition");
        RunObjectCondition parsed = Text(condition, "kind") == "drain"
            ? new RunObjectCondition.Drain(new LedgerId(UInt64(condition, "ledger")), new Slot(Int64(condition, "from")))
            : new RunObjectCondition.Compaction(
                [.. condition.GetProperty("inputs").EnumerateArray().Select(static input => new SectionPosition(new ObjectName(Text(input, "object")), Int64(input, "offset")))]);
        var sections = vector.GetProperty("sections").EnumerateArray().Select(static section =>
        {
            var partition = section.GetProperty("partition");
            var range = new KeyRange(Convert.FromHexString(Text(partition, "prefix")), partition.GetProperty("depth").GetInt32());
            return new RunSectionData(range, [.. section.GetProperty("rows").EnumerateArray().Select(RowFromVector)]);
        });
        return new RunObjectData([.. sections], parsed, settings);
    }

    /// <summary>
    /// Uploads <paramref name="encoded"/> to a new memory store under a name which agrees with its condition.
    /// </summary>
    /// <returns>The store and the object's name.</returns>
    public static async Task<(InMemoryObjectStore Store, ObjectName Name)> UploadAsync(RunObjectData data, EncodedRunObject encoded)
    {
        var store = new InMemoryObjectStore();
        var name = data.Name(UInt128.Parse("0123456789abcdef0123456789abcdef", NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        _ = await store.CreateAsync(name, encoded.Content, encoded.Crc32C, TestContext.Current.CancellationToken);
        return (store, name);
    }

    /// <summary>
    /// Reads a section of an uploaded object back through the reader by its <see cref="SectionRef"/>: its tail, every
    /// key's rows and a key next to each which no row has, and the whole section.
    /// </summary>
    public static async Task AssertReadsBackAsync(IObjectStore store, SectionRef reference, RunSectionData section, RunTrailer written)
    {
        var cancellation = TestContext.Current.CancellationToken;
        var tail = Assert.IsType<RunTail>(await RunObjectReader.ReadTailAsync(store, reference, cancellation));
        Assert.Equal(written, tail.Trailer);
        var metadata = Assert.IsType<RunSectionMetadata>(tail.Find(reference.Offset));
        foreach (var key in section.ProbeKeys())
        {
            using var rows = Assert.IsType<RunRows>(await RunObjectReader.ReadRowsAsync(store, reference.Object, metadata, key, cancellation));
            AssertRows(section.RowsOf(key), rows.Rows);
        }

        using var whole = Assert.IsType<RunRows>(await RunObjectReader.ReadSectionAsync(store, reference.Object, metadata, cancellation));
        AssertRows(section.Rows, whole.Rows);
    }

    internal static void AssertRows(IReadOnlyList<RunRowData> expected, ReadOnlySpan<TaggedRow> actual)
    {
        Assert.Equal(expected.Count, actual.Length);
        for (var row = 0; row < expected.Count; row++)
        {
            Assert.True(expected[row].Matches(actual[row]), $"Row {row} reads back otherwise than it was written.");
        }
    }

    /// <summary>
    /// Asserts that the partial decoders rule on <paramref name="bytes"/> as the whole read does for the rules they check:
    /// the trailer's decoder, then the metadata's, each rejecting with the whole read's reason, or accepting when the whole
    /// read accepts or rejects by a later rule.
    /// </summary>
    internal static void AssertPartialDecodersAgree(byte[] bytes, string? reason)
    {
        var sequence = new ReadOnlySequence<byte>(bytes);
        var trailerDecoding = RunObjectFormat.DecodeTrailerAt(sequence, 0);
        if (trailerDecoding.Value is InvalidDataException trailerRejection)
        {
            Assert.Equal(reason, DecodeRejection.ReasonOf(trailerRejection));
            return;
        }

        var trailer = Assert.IsType<RunTrailer>(trailerDecoding.Value);
        var tail = sequence.Slice(trailer.MetadataOffset);
        Assert.Equal(trailer, Assert.IsType<RunTrailer>(RunObjectFormat.DecodeTrailerAt(tail, trailer.MetadataOffset).Value));
        if (RunObjectFormat.DecodeMetadata(tail.Slice(0, trailer.TrailerOffset - trailer.MetadataOffset), trailer).Value is InvalidDataException metadataRejection)
        {
            Assert.Equal(reason, DecodeRejection.ReasonOf(metadataRejection));
        }
    }

    private static Gen<RunSectionData> Sections(RunObjectCondition condition) =>
        Gen.Select(Gen.Int[0, 12], Gen.Byte.Array[2]).SelectMany(
            (depth, seed) =>
            {
                var partition = KeyRange.Containing(seed, depth);

                // A drain output's rows are of its ledger at its watermark or above; a compaction's of any ledger.
                var rows = condition switch
                {
                    RunObjectCondition.Drain drain => RowFields(drain.Ledger.Value, drain.From.Value),
                    RunObjectCondition.Compaction => Gen.ULong[1, 12].SelectMany(static ledger => RowFields(ledger, 0)),
                    null => throw new System.Diagnostics.UnreachableException(),
                };
                return Gen.Select(KeysIn(partition), rows.Array[1, 4])
                    .Array[1, 8]
                    .Select(keys => Section(partition, [.. keys.SelectMany(static key => key.Item2.Select(row => (key.Item1, row)))]));
            });

    private static Gen<(Tag Tag, RowKind Kind, byte[] Payload)> RowFields(ulong ledger, long from) =>
        Gen.Select(
            Gen.Select(Gen.Long[from, from + 200], Gen.Int[0, 5]).Select(slotAndIndex => new Tag(new LedgerId(ledger), new Slot(slotAndIndex.Item1), slotAndIndex.Item2)),
            Gen.OneOfConst(RowKind.Append, RowKind.Replace, RowKind.Delete),
            Gen.Frequency((8, Gen.Byte.Array[0, 100]), (1, Gen.Byte.Array[200, 400])));

    private static RunSectionData Section(KeyRange partition, (byte[] Key, (Tag Tag, RowKind Kind, byte[] Payload) Row)[] rows)
    {
        var ordered = rows
            .Select(static row => new RunRowData(row.Key, row.Row.Tag, row.Row.Kind, row.Row.Payload))
            .OrderBy(static row => row.Key, ByteOrder.Instance)
            .ThenBy(static row => row.Tag)
            .ToList();

        // A key's rows have distinct tags.
        return new RunSectionData(partition, [.. ordered.Where((row, index) => index == 0 || !row.Key.AsSpan().SequenceEqual(ordered[index - 1].Key) || row.Tag != ordered[index - 1].Tag)]);
    }

    private static RunRowData RowFromVector(JsonElement row)
    {
        var tag = row.GetProperty("tag");
        return new RunRowData(
            Convert.FromHexString(Text(row, "key")),
            new Tag(new LedgerId(UInt64(tag, "ledger")), new Slot(Int64(tag, "slot")), tag.GetProperty("index").GetInt32()),
            Enum.Parse<RowKind>(Text(row, "kind")),
            Convert.FromHexString(Text(row, "payload")));
    }

    /// <summary>Orders byte strings bytewise.</summary>
    private sealed class ByteOrder : IComparer<byte[]>
    {
        public static ByteOrder Instance { get; } = new();

        public int Compare(byte[]? x, byte[]? y) => x.AsSpan().SequenceCompareTo(y);
    }
}
