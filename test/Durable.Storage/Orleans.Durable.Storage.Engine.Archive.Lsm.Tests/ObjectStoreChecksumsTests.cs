using System.Buffers;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Durable.Storage.Engine.ObjectStore.Memory;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Archive.Lsm.Tests;

/// <summary>
/// The CRC-32C which crosses the object store's boundary (archive §3): the stores' one helper against independent
/// oracles, and the writers' value, taken when they verify their bytes, which a store then holds a later change to.
/// </summary>
public sealed class ObjectStoreChecksumsTests : IDisposable
{
    private static readonly ObjectName Name = new("d/3/812/0123456789abcdef");

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _leaks.Dispose();

    /// <summary>
    /// The CRC-32C of "123456789" is 0xE3069283, the check value of the Castagnoli parameters, and its base64 is that of
    /// its big-endian bytes, E3 06 92 83, as S3 and Cloud Storage carry it.
    /// </summary>
    [Fact]
    public void TheCheckValueEncodesBigEndian()
    {
        using var content = ArcBuffer.Wrap("123456789"u8.ToArray());

        Assert.Equal(0xE3069283u, ObjectStoreChecksums.Crc32C(content));
        Assert.Equal("4waSgw==", ObjectStoreChecksums.Crc32CBase64(0xE3069283u));
    }

    /// <summary>
    /// A buffer across many pages has the CRC-32C of its bytes laid end to end, through both of the helper's paths.
    /// </summary>
    [Fact]
    public void AMultiSegmentBufferHasTheCrc32COfItsBytes()
    {
        var bytes = new byte[64 * ArcBufferWriter.MinimumPageSize + 5];
        new Random(5).NextBytes(bytes);
        using var writer = new ArcBufferWriter();
        for (var offset = 0; offset < bytes.Length; offset += ArcBufferWriter.MinimumPageSize)
        {
            writer.Write(bytes.AsSpan(offset, Math.Min(ArcBufferWriter.MinimumPageSize, bytes.Length - offset)));
        }

        using var content = writer.PeekSlice(writer.Length);
        Assert.False(content.AsReadOnlySequence().IsSingleSegment);

        var expected = Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, bytes);
        Assert.Equal(expected, ObjectStoreChecksums.Crc32C(content));
        Assert.Equal(expected, ObjectStoreChecksums.Crc32C(new ReadOnlySequence<byte>(bytes)));
    }

    /// <summary>
    /// Back-to-back CRC-32C calls on the same thread over empty, single-segment, and multi-segment buffers reset cleanly
    /// and match the reference hash on every call.
    /// </summary>
    [Fact]
    public void Crc32CResetsAcrossEmptySingleSegmentAndMultiSegmentCallsOnSameThread()
    {
        var emptyExpected = Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, []);
        var smallBytes = "hello-clog"u8.ToArray();
        var smallExpected = Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, smallBytes);
        var largeBytes = new byte[ArcBufferWriter.MinimumPageSize * 2 + 19];
        new Random(42).NextBytes(largeBytes);
        var largeExpected = Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, largeBytes);

        using var small = ArcBuffer.Wrap(smallBytes);
        using var writer = new ArcBufferWriter();
        writer.Write(largeBytes);
        using var large = writer.ConsumeSlice(writer.Length);
        var largeSeq = large.AsReadOnlySequence();
        Assert.False(largeSeq.IsSingleSegment);

        Assert.Equal(emptyExpected, ObjectStoreChecksums.Crc32C(default(ArcBuffer)));
        Assert.Equal(emptyExpected, ObjectStoreChecksums.Crc32C(ReadOnlySequence<byte>.Empty));
        Assert.Equal(largeExpected, ObjectStoreChecksums.Crc32C(large));
        Assert.Equal(smallExpected, ObjectStoreChecksums.Crc32C(small));
        Assert.Equal(emptyExpected, ObjectStoreChecksums.Crc32C(default(ArcBuffer)));
        Assert.Equal(largeExpected, ObjectStoreChecksums.Crc32C(largeSeq));
        Assert.Equal(smallExpected, ObjectStoreChecksums.Crc32C(new ReadOnlySequence<byte>(smallBytes)));
        Assert.Equal(emptyExpected, ObjectStoreChecksums.Crc32C(ReadOnlySequence<byte>.Empty));
    }

    /// <summary>
    /// No writer's CRC-32C and the bytes' own pass the check, and any other throws.
    /// </summary>
    [Fact]
    public void TheCheckRefusesOnlyAnotherCrc32C()
    {
        var bytes = new ReadOnlySequence<byte>("123456789"u8.ToArray());

        ObjectStoreChecksums.ThrowIfChanged(Name, bytes, null);
        ObjectStoreChecksums.ThrowIfChanged(Name, bytes, 0xE3069283u);
        var refused = Assert.Throws<ObjectContentChangedException>(() => ObjectStoreChecksums.ThrowIfChanged(Name, bytes, 0xE3069282u));
        Assert.Contains(Name.Value, refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A byte of a run object changed after its writer verified it, as a stale holder of one of its pages could change
    /// it: the writer's CRC-32C still names the bytes it verified, and the store refuses the changed ones.
    /// </summary>
    [Fact]
    public async Task ARunObjectChangedAfterItsWriterVerifiedItIsRefused()
    {
        var data = RunObjectSamples.FromVector(RunObjectSamples.Vector("run-objects.json", "A drain of ledger 3 from slot 812 into partition 0011/4 in 64-byte blocks: a key whose rows span blocks, so the second block continues the first, a row longer than the block size in a block of its own, a Replace and a Delete, and empty payloads."));
        using var encoded = data.Encode();
        var verified = Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, encoded.Content.ToArray());

        Change(encoded.Content);

        Assert.Equal(verified, encoded.Crc32C);
        var store = new InMemoryObjectStore();
        _ = await Assert.ThrowsAsync<ObjectContentChangedException>(async () => await store.CreateAsync(Name, encoded.Content, encoded.Crc32C, Token));
        Assert.Null(await store.ReadVersionedAsync(Name, Token));
    }

    /// <summary>
    /// A byte of a manifest's encoding changed after its CRC-32C was taken: the store refuses the changed bytes.
    /// </summary>
    [Fact]
    public async Task AManifestChangedAfterItsEncodingIsRefused()
    {
        using var encoded = ManifestFormat.Encode(new ManifestValue(1, new LedgerId(1), [], ManifestChanges.ManySections(10)));
        var taken = Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, encoded.Content.ToArray());

        Change(encoded.Content);

        Assert.Equal(taken, encoded.Crc32C);
        var store = new InMemoryObjectStore();
        _ = await Assert.ThrowsAsync<ObjectContentChangedException>(async () => await store.ReplaceAsync(ObjectName.Manifest, null, encoded.Content, encoded.Crc32C, Token));
        Assert.Null(await store.ReadVersionedAsync(ObjectName.Manifest, Token));
    }

    /// <summary>
    /// Flips a bit of <paramref name="content"/>'s last byte, in place in its page.
    /// </summary>
    private static void Change(ArcBuffer content)
    {
        ReadOnlyMemory<byte> last = default;
        foreach (var segment in content.MemorySegments)
        {
            last = segment;
        }

        MemoryMarshal.AsMemory(last).Span[^1] ^= 1;
    }
}
