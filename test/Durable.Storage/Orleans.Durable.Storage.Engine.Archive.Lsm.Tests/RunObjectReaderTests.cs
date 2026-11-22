using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Durable.Storage.Engine.ObjectStore.Memory;
using Orleans.Durable.Storage.Testing;
using static Orleans.Durable.Storage.Testing.SharedVectors;

namespace Orleans.Durable.Storage.Engine.Archive.Lsm.Tests;

/// <summary>
/// Reads run objects through the object store by the <see cref="SectionRef"/>s the manifest would name (archive §7):
/// sections written under any settings read back; a tail which contradicts its reference, its object's name, or ends
/// before it, and a broken block behind a clean tail, are corrupt, each with its reason; an excluded key reads nothing;
/// a reference past the bound on an object's length breaches it; and an absent object reads as absent.
/// </summary>
public sealed class RunObjectReaderTests : IDisposable
{
    private static readonly UInt128 Id = UInt128.Parse("0123456789abcdef0123456789abcdef", NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _leaks.Dispose();

    /// <summary>
    /// Sections written under several block sizes and Bloom settings, in objects of one store, each read back: a reader
    /// needs neither setting, so a change of either applies to new sections only (archive §8.7).
    /// </summary>
    [Fact]
    public async Task SectionsWrittenUnderOtherSettingsReadBack()
    {
        var store = new InMemoryObjectStore();
        var data = RunObjectSamples.FromVector(RunObjectSamples.Vector("run-objects.json", "A drain of ledger 3 from slot 812 into partition 0011/4 in 64-byte blocks: a key whose rows span blocks, so the second block continues the first, a row longer than the block size in a block of its own, a Replace and a Delete, and empty payloads."));
        var written = new List<(SectionRef Reference, RunTrailer Trailer)>();
        RunSectionSettings[] settings = [new(1, 1), new(64, 14), new(512, 64), new ArchiveOptions().SectionSettings];
        for (var index = 0; index < settings.Length; index++)
        {
            var name = data.Name(Id + (UInt128)index);
            using var encoded = (data with { Settings = settings[index] }).Encode();
            _ = await store.CreateAsync(name, encoded.Content, null, Token);
            written.Add((encoded.SectionRefs(name)[0], encoded.Trailer));
        }

        foreach (var (reference, trailer) in written)
        {
            await RunObjectSamples.AssertReadsBackAsync(store, reference, data.Sections[0], trailer);
        }
    }

    [Fact]
    public async Task AnAbsentObjectReadsAsAbsent()
    {
        var store = new InMemoryObjectStore();
        var data = SmallObject();
        using var encoded = data.Encode();
        var name = data.Name(Id);
        var reference = encoded.SectionRefs(name)[0];
        Assert.Null(await RunObjectReader.ReadTailAsync(store, reference, Token));
        Assert.Null(await RunObjectReader.ReadTrailerAsync(store, new ListedObject(name, encoded.Content.Length), Token));

        _ = await store.CreateAsync(name, encoded.Content, null, Token);
        var tail = Assert.IsType<RunTail>(await RunObjectReader.ReadTailAsync(store, reference, Token));
        await store.DeleteAsync(name, Token);
        Assert.Null(await RunObjectReader.ReadRowsAsync(store, name, tail.Sections[0], data.Sections[0].Rows[0].Key, Token));
        Assert.Null(await RunObjectReader.ReadSectionAsync(store, name, tail.Sections[0], Token));
    }

    public static TheoryData<string, string> Contradictions => new()
    {
        { "offset", RunObjectReader.SectionNotListed },
        { "length one short", RunObjectReader.SectionNotListed },
        { "length one over", RunObjectReader.SectionNotListed },
        { "partition", RunObjectReader.SectionNotListed },
        { "lowest tag alone", RunObjectReader.SectionNotListed },
        { "highest tag alone", RunObjectReader.SectionNotListed },
        { "tail one byte early", RunObjectReader.TailMismatch },
        { "tail one byte late", RunObjectReader.TailMismatch },
        { "tail past the object", "end-of-object" },
        { "another drain's name", RunObjectReader.NameMismatch },
        { "a compaction's name", RunObjectReader.NameMismatch },
    };

    /// <summary>
    /// A reference which the trailer contradicts, in any one field, or a name which does not agree with the trailer's
    /// condition, is corrupt with the reason of the check it fails; an object which ends before the reference is corrupt.
    /// </summary>
    [Theory]
    [MemberData(nameof(Contradictions))]
    public async Task AReferenceWhichTheTailContradictsIsCorrupt(string field, string reason)
    {
        var data = SmallObject();
        using var encoded = data.Encode();
        var (store, name) = await RunObjectSamples.UploadAsync(data, encoded);
        var reference = encoded.SectionRefs(name)[0];
        Assert.NotNull(await RunObjectReader.ReadTailAsync(store, reference, Token));

        var (offset, length, partition, minTag, maxTag, tail) = (reference.Offset, reference.Length, reference.Partition, reference.MinTag, reference.MaxTag, reference.Tail);
        var other = new Tag(new LedgerId(3), new Slot(9), 0);
        var objectName = name;
        switch (field)
        {
            case "offset":
                offset++;
                length--;
                break;
            case "length one short":
                length--;
                break;
            case "length one over":
                length++;
                break;
            case "partition":
                partition = partition.Children.Lower;
                break;
            case "lowest tag alone":
                minTag = new Tag(new LedgerId(3), new Slot(0), 1);
                break;
            case "highest tag alone":
                maxTag = other;
                break;
            case "tail one byte early":
                tail = new ObjectRange(tail.Offset - 1, tail.Length + 1);
                break;
            case "tail one byte late":
                tail = new ObjectRange(tail.Offset + 1, tail.Length - 1);
                break;
            case "tail past the object":
                tail = new ObjectRange(tail.Offset, tail.Length + 1);
                break;
            case "another drain's name":
                objectName = RunObjectNames.Drain(new LedgerId(3), new Slot(1), Id);
                _ = await store.CreateAsync(objectName, encoded.Content, null, Token);
                break;
            case "a compaction's name":
                objectName = RunObjectNames.Compaction(partition, Id);
                _ = await store.CreateAsync(objectName, encoded.Content, null, Token);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(field), field, "No such field.");
        }

        var contradicted = new SectionRef(objectName, offset, length, partition, minTag, maxTag, tail);
        var corrupt = await Assert.ThrowsAsync<ArchiveCorruptException>(async () => await RunObjectReader.ReadTailAsync(store, contradicted, Token));
        if (reason == "end-of-object")
        {
            _ = Assert.IsType<EndOfStreamException>(corrupt.InnerException);
        }
        else
        {
            Assert.Equal(reason, DecodeRejection.ReasonOf(corrupt.InnerException!));
        }
    }

    /// <summary>
    /// A reference whose tail ends past the bound on an object's length breaches the bound, which is no corruption.
    /// </summary>
    [Fact]
    public async Task AReferencePastTheBoundBreachesIt()
    {
        var store = new InMemoryObjectStore();
        var data = SmallObject();
        using var encoded = data.Encode();
        var reference = encoded.SectionRefs(data.Name(Id))[0];
        var past = new SectionRef(reference.Object, reference.Offset, reference.Length, reference.Partition, reference.MinTag, reference.MaxTag, new ObjectRange(RunObjectFormat.MaxObjectLength - 10L, 11));
        _ = await Assert.ThrowsAsync<InvalidOperationException>(async () => await RunObjectReader.ReadTailAsync(store, past, Token));
    }

    public static TheoryData<string> BlockRejections =>
    [
        "A block whose CRC-32C does not match its bytes.",
        "A block whose row's payload ends past the block.",
        "A row whose tag is of ledger 0.",
        "A block of 5 bytes, one byte of a row and its CRC-32C.",
        "A block whose second row precedes its first in tag order.",
    ];

    /// <summary>
    /// A block which breaks a rule, in an object whose tail reads, is corrupt (outcomes.md §8) to a read of a key in it
    /// and to a read of its whole section, each with the rule's reason.
    /// </summary>
    [Theory]
    [MemberData(nameof(BlockRejections))]
    public async Task ABrokenBlockBehindACleanTailIsCorruptToEveryRead(string vector)
    {
        var element = RunObjectSamples.Vector("run-object-rejections.json", vector);
        var bytes = Hex(element);
        var reason = Text(element, "reason");
        var trailerLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(bytes.Length - 8));
        var trailer = Assert.IsType<RunTrailer>(RunObjectFormat.DecodeTrailer(new ReadOnlySequence<byte>(bytes.AsMemory(bytes.Length - trailerLength)), bytes.Length - trailerLength).Value);
        var name = trailer.Condition switch
        {
            RunObjectCondition.Drain drain => RunObjectNames.Drain(drain.Ledger, drain.From, Id),
            RunObjectCondition.Compaction => RunObjectNames.Compaction(trailer.Sections[0].Partition, Id),
            null => throw new System.Diagnostics.UnreachableException(),
        };
        var store = new InMemoryObjectStore();
        using (var content = ArcBuffer.Wrap(bytes))
        {
            _ = await store.CreateAsync(name, content, null, Token);
        }

        var reference = trailer.Sections[0].ToSectionRef(name, new ObjectRange(trailer.MetadataOffset, trailer.ObjectLength - trailer.MetadataOffset));
        var section = Assert.IsType<RunTail>(await RunObjectReader.ReadTailAsync(store, reference, Token)).Sections[0];
        var keyRead = await Assert.ThrowsAsync<ArchiveCorruptException>(async () => await RunObjectReader.ReadRowsAsync(store, name, section, section.Index.Blocks[0].FirstKey, Token));
        Assert.Equal(reason, DecodeRejection.ReasonOf(keyRead.InnerException!));
        var sectionRead = await Assert.ThrowsAsync<ArchiveCorruptException>(async () => await RunObjectReader.ReadSectionAsync(store, name, section, Token));
        Assert.Equal(reason, DecodeRejection.ReasonOf(sectionRead.InnerException!));
    }

    /// <summary>
    /// A key which the section's partition or Bloom filter excludes reads nothing and reads no bytes of the object; a key
    /// the filter admits reads its blocks once.
    /// </summary>
    [Fact]
    public async Task AnExcludedKeyReadsNoBytes()
    {
        var data = SmallObject();
        using var encoded = data.Encode();
        var (inner, name) = await RunObjectSamples.UploadAsync(data, encoded);
        var store = new ProbeObjectStore(inner);
        var tail = Assert.IsType<RunTail>(await RunObjectReader.ReadTailAsync(store, encoded.SectionRefs(name)[0], Token));
        var section = tail.Sections[0];
        var reads = store.Calls.Count;

        // 0x40 lies outside partition 0011/4; the excluded key is the first in it which the filter does not admit.
        var excluded = Enumerable.Range(0, 4096).Select(static key => new byte[] { 0x30, (byte)key, (byte)(key >> 8) }).First(key => !section.Filter.MayContain(key));
        foreach (var key in new[] { [0x40], excluded })
        {
            using var rows = Assert.IsType<RunRows>(await RunObjectReader.ReadRowsAsync(store, name, section, key, Token));
            Assert.Equal(0, rows.Rows.Length);
        }

        Assert.Equal(reads, store.Calls.Count);
        using (var rows = Assert.IsType<RunRows>(await RunObjectReader.ReadRowsAsync(store, name, section, data.Sections[0].Rows[0].Key, Token)))
        {
            Assert.Equal(1, rows.Rows.Length);
        }

        Assert.Equal(reads + 1, store.Calls.Count);
    }

    /// <summary>
    /// A range's read: a range which shares no key with the section reads nothing and no bytes; one which contains the
    /// section's partition reads the whole section in one read; each half of the partition reads exactly the blocks the
    /// block index names for it, in one read, and only its rows, the halves together every row; a corrupt block in a
    /// half's blocks is corrupt; and an absent object reads as absent.
    /// </summary>
    [Fact]
    public async Task ARangeReadsOnlyItsBlocksAndRows()
    {
        var data = RunObjectSamples.FromVector(RunObjectSamples.Vector("run-objects.json", "A drain of ledger 3 from slot 812 into partition 0011/4 in 64-byte blocks: a key whose rows span blocks, so the second block continues the first, a row longer than the block size in a block of its own, a Replace and a Delete, and empty payloads."));
        using var encoded = data.Encode();
        var (inner, name) = await RunObjectSamples.UploadAsync(data, encoded);
        var store = new ProbeObjectStore(inner);
        var section = Assert.IsType<RunTail>(await RunObjectReader.ReadTailAsync(store, encoded.SectionRefs(name)[0], Token)).Sections[0];
        var partition = section.Entry.Partition;
        var reads = store.RangedReads.Count;

        using (var rows = Assert.IsType<RunRows>(await RunObjectReader.ReadRangeAsync(store, name, section, KeyRange.Root.Children.Upper, Token)))
        {
            Assert.Equal(0, rows.Rows.Length);
        }

        Assert.Equal(reads, store.RangedReads.Count);
        using (var rows = Assert.IsType<RunRows>(await RunObjectReader.ReadRangeAsync(store, name, section, KeyRange.Root, Token)))
        {
            Assert.Equal(data.Sections[0].Rows.Length, rows.Rows.Length);
        }

        Assert.Equal((name, section.Entry.Offset, (int)section.Entry.Length), store.RangedReads.Last());
        var read = 0;
        foreach (var half in new[] { partition.Children.Lower, partition.Children.Upper })
        {
            var blocks = section.Covering(half);
            reads = store.RangedReads.Count;
            using var rows = Assert.IsType<RunRows>(await RunObjectReader.ReadRangeAsync(store, name, section, half, Token));
            RunObjectSamples.AssertRows([.. data.Sections[0].Rows.Where(row => half.Contains(row.Key))], rows.Rows);
            read += rows.Rows.Length;
            Assert.Equal(blocks.Count == 0 ? [] : [(name, blocks.Offset, (int)blocks.Length)], store.RangedReads.Skip(reads));
        }

        Assert.Equal(data.Sections[0].Rows.Length, read);
        var lower = section.Covering(partition.Children.Lower);
        Assert.True(lower.Count > 0, "The lower half holds rows.");
        var bytes = encoded.Content.ToArray();
        bytes[lower.Offset] ^= 1;
        await inner.DeleteAsync(name, Token);
        using (var corrupt = ArcBuffer.Wrap(bytes))
        {
            _ = await inner.CreateAsync(name, corrupt, null, Token);
        }

        var thrown = await Assert.ThrowsAsync<ArchiveCorruptException>(async () => await RunObjectReader.ReadRangeAsync(store, name, section, partition.Children.Lower, Token));
        Assert.Equal(RunObjectFormat.CrcMismatch, DecodeRejection.ReasonOf(thrown.InnerException!));
        await inner.DeleteAsync(name, Token);
        Assert.Null(await RunObjectReader.ReadRangeAsync(store, name, section, partition.Children.Lower, Token));
    }

    /// <summary>
    /// The sweeper's read of a trailer, by the length a listing gave, reads the trailer and refuses one whose condition
    /// does not agree with the object's name.
    /// </summary>
    [Fact]
    public async Task ATrailerReadsByItsObjectsListedLength()
    {
        var data = SmallObject();
        using var encoded = data.Encode();
        var (store, name) = await RunObjectSamples.UploadAsync(data, encoded);
        var listed = Assert.Single(await store.ListAsync(string.Empty, cancellationToken: Token).ToListAsync(Token));
        Assert.Equal(new ListedObject(name, encoded.Content.Length), listed);
        Assert.Equal(encoded.Trailer, await RunObjectReader.ReadTrailerAsync(store, listed, Token));

        var other = RunObjectNames.Compaction(KeyRange.Root, Id);
        _ = await store.CreateAsync(other, encoded.Content, null, Token);
        var corrupt = await Assert.ThrowsAsync<ArchiveCorruptException>(async () => await RunObjectReader.ReadTrailerAsync(store, listed with { Name = other }, Token));
        Assert.Equal(RunObjectReader.NameMismatch, DecodeRejection.ReasonOf(corrupt.InnerException!));
    }

    /// <summary>
    /// A trailer which gives its length as more than <see cref="RunObjectFormat.MaxTrailerLength"/> is corrupt, though
    /// the object is long enough to hold it.
    /// </summary>
    [Fact]
    public async Task ATrailerPastItsBoundIsCorrupt()
    {
        var store = new InMemoryObjectStore();
        var name = RunObjectNames.Compaction(KeyRange.Root, Id);
        var bytes = new byte[RunObjectFormat.MaxTrailerLength + 64];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(bytes.Length - 8), RunObjectFormat.MaxTrailerLength + 1);
        using (var content = ArcBuffer.Wrap(bytes))
        {
            _ = await store.CreateAsync(name, content, null, Token);
        }

        var corrupt = await Assert.ThrowsAsync<ArchiveCorruptException>(async () => await RunObjectReader.ReadTrailerAsync(store, new ListedObject(name, bytes.Length), Token));
        Assert.Equal(RunObjectFormat.LengthMismatch, DecodeRejection.ReasonOf(corrupt.InnerException!));
    }

    private static RunObjectData SmallObject() =>
        new(
            [new RunSectionData(
                new KeyRange([0x30], 4),
                [
                    new RunRowData([0x31], new Tag(new LedgerId(3), new Slot(0), 0), RowKind.Append, [0xaa]),
                    new RunRowData([0x32], new Tag(new LedgerId(3), new Slot(1), 0), RowKind.Replace, []),
                ])],
            new RunObjectCondition.Drain(new LedgerId(3), Slot.Zero),
            new ArchiveOptions().SectionSettings);
}
