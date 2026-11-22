using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Hashing;
using CsCheck;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Durable.Storage.Fuzz.Archive;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Archive.Lsm.Tests;

/// <summary>
/// Properties of the run object's format (archive §7) over generated objects, under block sizes from 1 byte to 256 KiB
/// and 1 to 64 Bloom bits per key: every object reads back through the reader, whole and by key; blocks split where the
/// writer's rule splits them, and a key's candidate blocks are those which hold it; the trailer alone reads back by the
/// object's listed length; and every mutated object is judged as the format's independent reading of the rules judges
/// it.
/// </summary>
public sealed class RunObjectPropertyTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    /// <summary>The ways a mutation changes an object.</summary>
    public enum MutationKind
    {
        /// <summary>Flips a bit anywhere.</summary>
        Flip,

        /// <summary>Flips a bit of the trailer, whose fields a flip anywhere seldom reaches.</summary>
        TrailerFlip,

        /// <summary>Cuts bytes from the start or the end.</summary>
        Cut,

        /// <summary>Adds bytes at the start or the end.</summary>
        Extend,

        /// <summary>Sets an integer field of 1, 2, 4, or 8 bytes at any position to a bound.</summary>
        Field,
    }

    /// <summary>
    /// Gets mutations: each one's kind, and choices of position, bit, and value.
    /// </summary>
    private static Gen<(MutationKind Kind, int Position, int Bit, int Value)> Mutations { get; } =
        Gen.Select(
            Gen.Frequency((3, Gen.Const(MutationKind.Flip)), (3, Gen.Const(MutationKind.TrailerFlip)), (1, Gen.Const(MutationKind.Cut)), (1, Gen.Const(MutationKind.Extend)), (4, Gen.Const(MutationKind.Field))),
            Gen.Int.NonNegative,
            Gen.Int[0, 7],
            Gen.Int.NonNegative);

    public void Dispose() => _leaks.Dispose();

    /// <summary>
    /// Every object reads back as the format's independent reading reads it, and through the reader by each of its
    /// sections' references: the tail, every key's rows and a key next to each which no row has, and the whole section.
    /// The writer's CRC-32C is that of the object's bytes.
    /// </summary>
    [Fact]
    public Task EveryObjectReadsBackThroughTheReader() =>
        RunObjectSamples.Objects.SampleAsync(
            data => _leaks.InScopeAsync(async () =>
            {
                using var encoded = data.Encode();
                var bytes = encoded.Content.ToArray();
                Assert.Equal(Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, bytes), encoded.Crc32C);
                using (var decoded = Assert.IsType<RunObject>(RunObjectFormat.Decode(encoded.Content).Value))
                {
                    Assert.Equal(encoded.Trailer, decoded.Trailer);
                    Assert.True(RunObjectTarget.Matches(decoded, Assert.IsType<RunRuleObject>(RunObjectRules.Read(bytes).Object)));
                }

                var (store, name) = await RunObjectSamples.UploadAsync(data, encoded);
                var references = encoded.SectionRefs(name);
                for (var index = 0; index < references.Length; index++)
                {
                    await RunObjectSamples.AssertReadsBackAsync(store, references[index], data.Sections[index], encoded.Trailer);
                }
            }),
            iter: 200);

    /// <summary>
    /// Each section's blocks are those the writer's rule makes, computed apart from the writer: a block starts before a
    /// row which would make a non-empty block longer than the block size, so each block of more than one row holds at
    /// most the block size, and a block is continued exactly when its first key is the previous block's last row's.
    /// </summary>
    [Fact]
    public void BlocksSplitWhereTheBlockSizeSplitsThem() =>
        RunObjectSamples.Objects.Sample(
            data => _leaks.InScope(() =>
            {
                using var encoded = data.Encode();
                using var decoded = Assert.IsType<RunObject>(RunObjectFormat.Decode(encoded.Content).Value);
                for (var index = 0; index < data.Sections.Length; index++)
                {
                    var blocks = Blocks(data.Sections[index].Rows, data.Settings.BlockBytes);
                    var read = decoded.Sections[index].Metadata.Index.Blocks;
                    Assert.Equal(blocks.Select(static block => (long)block.Length), read.Select(static block => block.Length));
                    Assert.Equal(blocks.Select(static block => block.Continues), read.Select(static block => block.Continues));
                    Assert.All(blocks.Where(static block => block.Rows.Count > 1), block => Assert.True(block.Length <= data.Settings.BlockBytes));
                }
            }),
            iter: 300);

    /// <summary>
    /// A key's candidate blocks, from the block index and its continuation flags, are exactly the blocks which hold the
    /// key's rows, by a linear scan of the rows; for a key no row has, at most one block.
    /// </summary>
    [Fact]
    public void ACandidateBlockRangeIsTheBlocksWhichHoldTheKey() =>
        RunObjectSamples.Objects.Sample(
            data => _leaks.InScope(() =>
            {
                using var encoded = data.Encode();
                using var decoded = Assert.IsType<RunObject>(RunObjectFormat.Decode(encoded.Content).Value);
                for (var index = 0; index < data.Sections.Length; index++)
                {
                    var blocks = Blocks(data.Sections[index].Rows, data.Settings.BlockBytes);
                    foreach (var key in data.Sections[index].ProbeKeys())
                    {
                        var holding = blocks.Select((block, number) => (block, number)).Where(pair => pair.block.Rows.Exists(row => row.Key.AsSpan().SequenceEqual(key))).Select(static pair => pair.number).ToArray();
                        var candidates = decoded.Sections[index].Metadata.Index.Candidates(key);
                        if (holding.Length > 0)
                        {
                            Assert.Equal(holding, Enumerable.Range(candidates.First, candidates.Count));
                        }
                        else
                        {
                            Assert.True(candidates.Count <= 1, "A key no row has has at most one candidate block.");
                        }
                    }
                }
            }),
            iter: 300);

    /// <summary>
    /// For every range down to four bits below a section's partition, the key order against the range agrees with
    /// containment and never decreases along the section's rows; the blocks the block index names for the range are
    /// exactly those which hold its rows and at most the one before them, every block after the first starting in the
    /// range; their bytes are their span; and the range's read of them gives exactly its rows, in order.
    /// </summary>
    [Fact]
    public void ARangeReadGivesExactlyTheRangesRows() =>
        RunObjectSamples.Objects.Sample(
            data => _leaks.InScope(() =>
            {
                using var encoded = data.Encode();
                using var decoded = Assert.IsType<RunObject>(RunObjectFormat.Decode(encoded.Content).Value);
                for (var index = 0; index < data.Sections.Length; index++)
                {
                    var section = data.Sections[index];
                    var metadata = decoded.Sections[index].Metadata;
                    var blocks = Blocks(section.Rows, data.Settings.BlockBytes);
                    foreach (var range in Descendants(section.Partition, 4))
                    {
                        int[] orders = [.. section.Rows.Select(row => Math.Sign(KeyRangeOrder.Compare(row.Key, range)))];
                        Assert.Equal(section.Rows.Select(row => range.Contains(row.Key)), orders.Select(static order => order == 0));
                        Assert.Equal(orders.Order(), orders);

                        var expected = section.Rows.Where(row => range.Contains(row.Key)).ToArray();
                        var covering = metadata.Covering(range);
                        int[] holding = [.. blocks.Select((block, number) => (block, number)).Where(pair => pair.block.Rows.Exists(row => range.Contains(row.Key))).Select(static pair => pair.number)];
                        Assert.All(holding, number => Assert.InRange(number, covering.First, covering.First + covering.Count - 1));
                        Assert.True(covering.Count <= holding.Length + 1, $"The read of {range} names {covering.Count} blocks, of which {holding.Length} hold its rows.");
                        Assert.All(Enumerable.Range(covering.First + 1, Math.Max(0, covering.Count - 1)), number => Assert.True(range.Contains(blocks[number].Rows[0].Key), $"Block {number} of {range}'s read starts outside it."));
                        if (covering.Count == 0)
                        {
                            Assert.Empty(expected);
                            continue;
                        }

                        var span = blocks.Take(covering.First).Sum(static block => (long)block.Length);
                        Assert.Equal((decoded.Sections[index].Metadata.Entry.Offset + span, blocks.Skip(covering.First).Take(covering.Count).Sum(static block => (long)block.Length)), (covering.Offset, covering.Length));
                        using var rows = Assert.IsType<RunRows>(RunObjectFormat.DecodeRange(encoded.Content.UnsafeSlice((int)covering.Offset, (int)covering.Length), metadata, covering.First, covering.Count, range).Value);
                        Assert.Equal(expected.Length, rows.Rows.Length);
                        for (var row = 0; row < expected.Length; row++)
                        {
                            Assert.True(expected[row].Matches(rows.Rows[row]), $"Row {row} of {range} differs.");
                        }
                    }
                }
            }),
            iter: 300);

    /// <summary>Returns <paramref name="partition"/> and its descendants down to <paramref name="levels"/> bits deeper.</summary>
    private static IEnumerable<KeyRange> Descendants(KeyRange partition, int levels)
    {
        IEnumerable<KeyRange> level = [partition];
        for (var depth = 0; depth <= levels; depth++)
        {
            foreach (var range in level)
            {
                yield return range;
            }

            level = [.. level.Where(static range => range.Depth < KeyRange.MaxDepth).SelectMany(static range => (KeyRange[])[range.Children.Lower, range.Children.Upper])];
        }
    }

    /// <summary>
    /// The reader of the trailer alone, by the object's listed length as the sweeper reads it, gives a drain output's
    /// trailer as the writer wrote it, each section's entry counting its rows.
    /// </summary>
    [Fact]
    public Task TheTrailerAloneReadsBackByTheObjectsListedLength() =>
        RunObjectSamples.DrainOutputs.SampleAsync(
            data => _leaks.InScopeAsync(async () =>
            {
                using var encoded = data.Encode();
                var (store, name) = await RunObjectSamples.UploadAsync(data, encoded);
                var listed = Assert.Single(await store.ListAsync(string.Empty, cancellationToken: TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken));
                var trailer = Assert.IsType<RunTrailer>(await RunObjectReader.ReadTrailerAsync(store, listed, TestContext.Current.CancellationToken));
                Assert.Equal(encoded.Trailer, trailer);
                for (var index = 0; index < data.Sections.Length; index++)
                {
                    Assert.Equal((ulong)data.Sections[index].Rows.Length, trailer.Sections[index].RowCount);
                }
            }),
            iter: 200);

    /// <summary>
    /// An object whose blocks span many buffer pages encodes, and reads back whole and by key from those pages and from
    /// one contiguous copy.
    /// </summary>
    [Fact]
    public async Task AnObjectOfManyPagesReadsBack()
    {
        // More than the 1 MiB of the largest page, so even the presized writer spans pages.
        var rows = Enumerable.Range(0, 1_200)
            .Select(static row => new RunRowData(
                [(byte)(row / 3 >> 8), (byte)(row / 3 & 0xff)],
                new Tag(new LedgerId(4), new Slot(row), 0),
                RowKind.Append,
                [.. Enumerable.Repeat((byte)(row % 256), 1000)]))
            .ToArray();
        var data = new RunObjectData(
            [new RunSectionData(KeyRange.Root, rows)],
            new RunObjectCondition.Drain(new LedgerId(4), Slot.Zero),
            new RunSectionSettings(4096, 14));
        using var encoded = data.Encode();
        var pages = 0;
        foreach (var _ in encoded.Content)
        {
            pages++;
        }

        Assert.True(pages > 1, $"The encoding of {encoded.Content.Length} bytes lies in {pages} page.");
        using (var decoded = Assert.IsType<RunObject>(RunObjectFormat.Decode(encoded.Content).Value))
        {
            RunObjectSamples.AssertRows(rows, decoded.Sections[0].Rows.Rows);
        }

        var (store, name) = await RunObjectSamples.UploadAsync(data, encoded);
        await RunObjectSamples.AssertReadsBackAsync(store, encoded.SectionRefs(name)[0], data.Sections[0], encoded.Trailer);
    }

    /// <summary>
    /// Every mutated object, with its checksums as mutated or recomputed, is rejected for the reason
    /// <see cref="RunObjectRules"/> names, or accepted as the object it reads, field by field; the partial decoders rule
    /// as the whole read does for the rules they check; <c>classify</c> reports each outcome, and the outcomes reach the
    /// floor below.
    /// </summary>
    /// <remarks>
    /// The floor holds the outcomes the mutations reach on every seed: over 300 seeds, each reached at least 10 times and
    /// 22 on average, so a run misses one with a chance below one in a billion. Rarer reasons, such as
    /// <c>bad-condition</c> at 3 a run, are left out of the floor, not out of the property, and
    /// <see cref="RunObjectVectorTests.RunObjectRejectionVectorsAreRejectedForTheirReason"/> judges a vector of every
    /// reason as this property judges a case.
    /// </remarks>
    [Fact]
    public void EveryMutatedObjectIsJudgedAsTheRulesJudgeIt()
    {
        var outcomes = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        Gen.Select(RunObjectSamples.Objects, Mutations, Gen.Bool).Sample(
            sample =>
            {
                var (data, mutation, seal) = sample;
                var outcome = "accepted";
                _leaks.InScope(() =>
                {
                    byte[] bytes;
                    using (var encoded = data.Encode())
                    {
                        bytes = Mutate(encoded.Content.ToArray(), mutation);
                    }

                    bytes = seal ? RunObjectRules.Seal(bytes) ?? bytes : bytes;
                    var reading = RunObjectRules.Read(bytes);
                    using var content = ArcBuffer.Wrap(bytes);
                    var decoding = RunObjectFormat.Decode(content);
                    if (decoding.Value is InvalidDataException rejection)
                    {
                        outcome = DecodeRejection.ReasonOf(rejection)!;
                        Assert.Equal(reading.Reason, outcome);
                        RunObjectSamples.AssertPartialDecodersAgree(bytes, outcome);
                        return;
                    }

                    Assert.Null(reading.Reason);
                    RunObjectSamples.AssertPartialDecodersAgree(bytes, null);
                    using var value = Assert.IsType<RunObject>(decoding.Value);
                    Assert.True(RunObjectTarget.Matches(value, reading.Object!), "The decoder accepted the bytes otherwise than the rules read them.");
                });
                _ = outcomes.AddOrUpdate(outcome, 1, static (_, count) => count + 1);
                return outcome;
            },
            TestDiagnostics.WriteLine,
            iter: 4_000);

        Assert.Superset(
            new HashSet<string>(StringComparer.Ordinal)
            {
                "accepted",
                RunObjectFormat.Truncated,
                RunObjectFormat.LengthMismatch,
                RunObjectFormat.CrcMismatch,
                RunObjectFormat.BodyTruncated,
                RunObjectFormat.BadMagic,
                RunObjectFormat.SectionsNotContiguous,
                RunObjectFormat.BadSectionLayout,
                RunObjectFormat.BadTag,
                RunObjectFormat.BlocksMismatch,
                RunObjectFormat.TagsUnordered,

                RunObjectFormat.ConditionMismatch,
                RunObjectFormat.IndexMismatch,
                RunObjectFormat.TagOutsideSpan,
                RunObjectFormat.TagSpanMismatch,
            },
            outcomes.Keys.ToHashSet(StringComparer.Ordinal));
    }

    /// <summary>
    /// <see cref="RunObjectRules.Seal"/> leaves a valid object as it is, and restores every checksum of an object whose
    /// trailer, block indexes, Bloom filters, and blocks each had its CRC-32C zeroed.
    /// </summary>
    [Fact]
    public void SealRestoresEveryChecksum() =>
        RunObjectSamples.Objects.Sample(
            data => _leaks.InScope(() =>
            {
                using var encoded = data.Encode();
                var bytes = encoded.Content.ToArray();
                Assert.Equal(bytes, RunObjectRules.Seal(bytes));
                var zeroed = bytes.ToArray();
                var trailer = encoded.Trailer;
                var ends = trailer.Sections.SelectMany(static entry => new[] { entry.FilterOffset, entry.FilterEnd })
                    .Append(trailer.ObjectLength);
                using var decoded = Assert.IsType<RunObject>(RunObjectFormat.Decode(encoded.Content).Value);
                ends = ends.Concat(decoded.Sections.SelectMany(static section => section.Metadata.Index.Blocks.Select(static block => block.Offset + block.Length)));
                foreach (var end in ends)
                {
                    zeroed.AsSpan((int)end - 4, 4).Clear();
                }

                Assert.NotEqual(bytes, zeroed);
                Assert.Equal(bytes, RunObjectRules.Seal(zeroed));
            }),
            iter: 100);

    /// <summary>
    /// A filter of 14 bits per key admits about the rate its bits give, 0.12% (archive §8.7), over keys it was not built
    /// over; the keys come from a fixed seed, so the rate is the same on every run.
    /// </summary>
    [Fact]
    public void TheBloomFilterAdmitsTheRateItsBitsGive()
    {
        var random = new Random(20261113);
        var hashes = new List<ulong>();
        for (var key = 0; key < 20_000; key++)
        {
            hashes.Add(XxHash3.HashToUInt64(BitConverter.GetBytes(random.NextInt64())));
        }

        var filter = RunBloomFilter.Create(hashes, 14);
        Assert.Equal(10, filter.HashCount);
        Assert.All(hashes, hash => Assert.True(filter.MayContain(hash)));
        var admitted = 0;
        const int Probes = 200_000;
        for (var probe = 0; probe < Probes; probe++)
        {
            admitted += filter.MayContain([0xfe, .. BitConverter.GetBytes(random.NextInt64())]) ? 1 : 0;
        }

        var rate = (double)admitted / Probes;
        Assert.InRange(rate, 0.0005, 0.003);
    }

    public static TheoryData<string> InvalidInputs =>
    [
        "no sections", "a section of no rows", "a default row", "a tag past the last slot", "a key outside the partition",
        "rows out of order", "a drain row of another ledger", "a drain row below its watermark", "a default condition",
        "a compaction of no inputs", "a compaction of unordered inputs", "a block size of 0", "0 bits per key", "65 bits per key",
        "a row with no section open", "a section begun twice", "completion with no section", "completion with a section open",
        "a row after completion",
    ];

    /// <summary>
    /// The writer refuses every input it cannot write, before it writes the object: arguments with
    /// <see cref="ArgumentException"/>, and calls out of order with <see cref="InvalidOperationException"/>.
    /// </summary>
    [Theory]
    [MemberData(nameof(InvalidInputs))]
    public void TheWriterRefusesWhatItCannotWrite(string input)
    {
        var drain = new RunObjectCondition.Drain(new LedgerId(3), new Slot(5));
        var row = new RunRowData([0x10], new Tag(new LedgerId(3), new Slot(5), 0), RowKind.Append, [1]);
        RunSectionData Section(params RunRowData[] rows) => new(KeyRange.Root, rows);
        RunObjectData Data(RunObjectCondition condition, params RunSectionData[] sections) => new(sections, condition, new ArchiveOptions().SectionSettings);
        var exception = Record.Exception(() =>
        {
            switch (input)
            {
                case "no sections":
                    using (Data(drain).Encode())
                    {
                    }

                    break;
                case "a section of no rows":
                    using (Data(drain, Section()).Encode())
                    {
                    }

                    break;
                case "a default row":
                    using (var writer = new RunObjectWriter(drain, new ArchiveOptions().SectionSettings))
                    {
                        writer.BeginSection(KeyRange.Root);
                        writer.Add(default);
                    }

                    break;
                case "a tag past the last slot":
                    using (Data(new RunObjectCondition.Compaction([new(new ObjectName("d/1/0/a"), 0)]), Section(row with { Tag = new Tag(new LedgerId(3), new Slot(long.MaxValue), 0) })).Encode())
                    {
                    }

                    break;
                case "a key outside the partition":
                    using (new RunObjectData([new RunSectionData(new KeyRange([0x80], 1), [row])], drain, new ArchiveOptions().SectionSettings).Encode())
                    {
                    }

                    break;
                case "rows out of order":
                    using (Data(drain, Section(row with { Key = [0x20] }, row)).Encode())
                    {
                    }

                    break;
                case "a drain row of another ledger":
                    using (Data(drain, Section(row with { Tag = new Tag(new LedgerId(4), new Slot(5), 0) })).Encode())
                    {
                    }

                    break;
                case "a drain row below its watermark":
                    using (Data(drain, Section(row with { Tag = new Tag(new LedgerId(3), new Slot(4), 0) })).Encode())
                    {
                    }

                    break;
                case "a default condition":
                    using (Data(default, Section(row)).Encode())
                    {
                    }

                    break;
                case "a compaction of no inputs":
                    using (Data(new RunObjectCondition.Compaction([]), Section(row)).Encode())
                    {
                    }

                    break;
                case "a compaction of unordered inputs":
                    using (Data(new RunObjectCondition.Compaction([new(new ObjectName("d/2/0/a"), 0), new(new ObjectName("d/1/0/a"), 0)]), Section(row)).Encode())
                    {
                    }

                    break;
                case "a block size of 0":
                    using (new RunObjectData([Section(row)], drain, new RunSectionSettings(0, 14)).Encode())
                    {
                    }

                    break;
                case "0 bits per key":
                    using (new RunObjectData([Section(row)], drain, new RunSectionSettings(64, 0)).Encode())
                    {
                    }

                    break;
                case "65 bits per key":
                    using (new RunObjectData([Section(row)], drain, new RunSectionSettings(64, 65)).Encode())
                    {
                    }

                    break;
                case "a row with no section open":
                    using (var writer = new RunObjectWriter(drain, new ArchiveOptions().SectionSettings))
                    {
                        var history = row.ToRow();
                        try
                        {
                            writer.Add(history);
                        }
                        finally
                        {
                            history.Row.Dispose();
                        }
                    }

                    break;
                case "a section begun twice":
                    using (var writer = new RunObjectWriter(drain, new ArchiveOptions().SectionSettings))
                    {
                        writer.BeginSection(KeyRange.Root);
                        writer.BeginSection(KeyRange.Root);
                    }

                    break;
                case "completion with no section":
                    using (var writer = new RunObjectWriter(drain, new ArchiveOptions().SectionSettings))
                    {
                        using var _ = writer.Complete();
                    }

                    break;
                case "completion with a section open":
                    using (var writer = new RunObjectWriter(drain, new ArchiveOptions().SectionSettings))
                    {
                        writer.BeginSection(KeyRange.Root);
                        using var _ = writer.Complete();
                    }

                    break;
                case "a row after completion":
                    using (var writer = new RunObjectWriter(drain, new ArchiveOptions().SectionSettings))
                    {
                        var history = row.ToRow();
                        try
                        {
                            writer.BeginSection(KeyRange.Root);
                            writer.Add(history);
                            writer.EndSection();
                            using var _ = writer.Complete();
                            writer.BeginSection(KeyRange.Root);
                        }
                        finally
                        {
                            history.Row.Dispose();
                        }
                    }

                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(input), input, "No such input.");
            }
        });

        var expected = input switch
        {
            "a section of no rows" or "a row with no section open" or "a section begun twice" or "completion with no section"
                or "completion with a section open" or "a row after completion" => typeof(InvalidOperationException),
            "a block size of 0" or "0 bits per key" or "65 bits per key" => typeof(ArgumentOutOfRangeException),
            _ => typeof(ArgumentException),
        };
        Assert.IsType(expected, exception);
    }

    /// <summary>
    /// Returns the blocks the writer's rule makes of <paramref name="rows"/>, with each block's length, its rows, and
    /// whether it continues the previous block's last key.
    /// </summary>
    private static List<(int Length, bool Continues, List<RunRowData> Rows)> Blocks(RunRowData[] rows, int blockBytes)
    {
        var blocks = new List<(int Length, bool Continues, List<RunRowData> Rows)>();
        RunRowData? previous = null;
        foreach (var row in rows)
        {
            var length = 2 + row.Key.Length + 20 + 1 + 4 + row.Payload.Length;
            if (blocks.Count == 0 || blocks[^1].Length + length > blockBytes)
            {
                blocks.Add((4, previous is not null && previous.Key.AsSpan().SequenceEqual(row.Key), []));
            }

            var block = blocks[^1];
            block.Rows.Add(row);
            blocks[^1] = (block.Length + length, block.Continues, block.Rows);
            previous = row;
        }

        return blocks;
    }

    private static byte[] Mutate(byte[] bytes, (MutationKind Kind, int Position, int Bit, int Value) mutation)
    {
        switch (mutation.Kind)
        {
            case MutationKind.Flip:
                bytes[mutation.Position % bytes.Length] ^= (byte)(1 << mutation.Bit);
                return bytes;
            case MutationKind.TrailerFlip:
                var trailer = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(bytes.Length - 8));
                bytes[bytes.Length - 1 - mutation.Position % trailer] ^= (byte)(1 << mutation.Bit);
                return bytes;
            case MutationKind.Cut:
                return mutation.Bit < 4 ? bytes[..(mutation.Position % (bytes.Length + 1))] : bytes[(mutation.Position % (bytes.Length + 1))..];
            case MutationKind.Extend:
                byte[] extra = [.. Enumerable.Repeat((byte)(mutation.Value & 0xff), 1 + mutation.Bit % 4)];
                return mutation.Value % 2 == 0 ? [.. bytes, .. extra] : [.. extra, .. bytes];
            case MutationKind.Field:
                ulong[] bounds = [0, 1, 2, 4, 5, 7, 8, 15, 16, byte.MaxValue, 256, ushort.MaxValue, int.MaxValue, uint.MaxValue, long.MaxValue - 1, long.MaxValue, ulong.MaxValue];
                int[] widths = [1, 2, 4, 8];
                var width = widths[mutation.Bit % widths.Length];
                if (bytes.Length < width)
                {
                    return bytes;
                }

                var position = mutation.Position % (bytes.Length - width + 1);
                Span<byte> value = stackalloc byte[8];
                BinaryPrimitives.WriteUInt64LittleEndian(value, bounds[mutation.Value % bounds.Length]);
                value[..width].CopyTo(bytes.AsSpan(position));
                return bytes;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation), mutation.Kind, "No such mutation.");
        }
    }
}
