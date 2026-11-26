using System.Buffers;
using CsCheck;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Buffers.Tests;

public sealed class ArcBufferTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    private const int PageSize = ArcBufferWriter.MinimumPageSize;

    [Fact]
    public void MultiPageWritesRoundTripWithoutFlattening()
    {
        using var writer = new ArcBufferWriter();
        var expected = CreatePayload(PageSize * 3 + 17);
        int[] chunkSizes = [1, 52, 125, 4096];

        for (var offset = 0; offset < expected.Length;)
        {
            var count = Math.Min(chunkSizes[offset % chunkSizes.Length], expected.Length - offset);
            writer.Write(expected.AsSpan(offset, count));
            offset += count;
        }

        using var buffer = writer.PeekSlice(writer.Length);
        var sequence = buffer.AsReadOnlySequence();

        Assert.False(sequence.IsSingleSegment);
        Assert.Equal(expected, sequence.ToArray());
        Assert.Equal(expected, buffer.ToArray());
        Assert.Equal(4, buffer.MemorySegments.Count());
        Assert.Equal(4, buffer.ArraySegments.Count());
    }

    [Fact]
    public void WrapRetainsArrayBackedMemoryWithoutCopying()
    {
        var source = CreatePayload(PageSize * 2);
        var memory = source.AsMemory(17, PageSize);
        var buffer = ArcBuffer.Wrap(memory);
        var retained = buffer.Retain();
        var page = buffer.First;

        Assert.Same(source, page.Array);
        Assert.Equal(17, buffer.Offset);
        Assert.Equal(memory.ToArray(), buffer.ToArray());
        Assert.Throws<InvalidOperationException>(() => page.ResizeLargeSegment(PageSize * 4));

        buffer.Dispose();
        Assert.Equal(memory.ToArray(), retained.ToArray());

        retained.Dispose();
        Assert.Empty(page.Array);
        Assert.Equal(0, page.ReferenceCount);
    }

    [Fact]
    public void WrapOfEmptyMemoryReturnsTheDefaultValue()
    {
        Assert.True(ArcBuffer.Wrap(ReadOnlyMemory<byte>.Empty).IsDefault);
        Assert.True(ArcBuffer.Wrap(CreatePayload(8).AsMemory(3, 0)).IsDefault);
    }

    [Fact]
    public void CopyFromCopiesSpanAndSequenceWithoutLeakingPages()
    {
        Assert.True(ArcBuffer.CopyFrom([]).IsDefault);
        Assert.True(ArcBuffer.CopyFrom(ReadOnlySequence<byte>.Empty).IsDefault);

        var small = CreatePayload(619);
        using (var fromSpan = ArcBuffer.CopyFrom(small))
        {
            Assert.Equal(small, fromSpan.ToArray());
            Assert.Single(fromSpan.Pages);
        }

        var multi = CreatePayload(PageSize * 2 + 123);
        using var writer = new ArcBufferWriter();
        writer.Write(multi.AsSpan(0, PageSize + 17));
        writer.Write(multi.AsSpan(PageSize + 17));
        using var staged = writer.PeekSlice(writer.Length);
        var sequence = staged.AsReadOnlySequence();
        Assert.False(sequence.IsSingleSegment);
        using (var fromSequence = ArcBuffer.CopyFrom(sequence))
        {
            Assert.Equal(multi, fromSequence.ToArray());
            Assert.Single(fromSequence.Pages);
        }

        var huge = CreatePayload(ArcBufferPagePool.LargestPageSize + 512);
        using (var fromHugeSpan = ArcBuffer.CopyFrom(huge))
        {
            Assert.Equal(huge, fromHugeSpan.ToArray());
        }

        using var fromHugeSeq = ArcBuffer.CopyFrom(new ReadOnlySequence<byte>(huge));
        Assert.Equal(huge, fromHugeSeq.ToArray());
    }

    [Fact]
    public void TheDefaultValueReadsAsAnEmptyBuffer()
    {
        var empty = default(ArcBuffer);
        byte[] destination = [1, 2, 3];
        using var writer = new ArcBufferWriter();
        var bufferWriter = new ArrayBufferWriter<byte>();

        Assert.Equal(0, empty.Length);
        Assert.Equal(0, empty.Offset);
        Assert.Equal(0, empty.CopyTo([]));
        Assert.Equal(0, empty.CopyTo(destination));
        Assert.Equal([1, 2, 3], destination);
        empty.CopyTo(writer);
        Assert.Equal(0, writer.Length);
        empty.CopyTo(ref bufferWriter);
        Assert.Equal(0, bufferWriter.WrittenCount);
        Assert.True(empty.AsReadOnlySequence().IsEmpty);
        Assert.Empty(empty.ToArray());
        Assert.Empty(empty.MemorySegments);
        Assert.Empty(empty.ArraySegments);
        Assert.Empty(empty.PageSegments);
        Assert.Empty(empty.Pages);
        var spans = 0;
        foreach (var span in empty)
        {
            spans += span.Length + 1;
        }

        foreach (var span in empty.SpanSegments)
        {
            spans += span.Length + 1;
        }

        Assert.Equal(0, spans);
    }

    [Fact]
    public void TheDefaultValueRetainsMovesAndSlicesToItself()
    {
        var empty = default(ArcBuffer);

        Assert.True(empty.Retain().IsDefault);
        Assert.True(empty.Move().IsDefault);
        Assert.True(empty.Slice(0).IsDefault);
        Assert.True(empty.Slice(0, 0).IsDefault);
        Assert.True(empty.UnsafeSlice(0, 0).IsDefault);
        Assert.Equal("offset", Assert.Throws<ArgumentOutOfRangeException>(() => empty.Slice(1)).ParamName);
        Assert.Equal("length", Assert.Throws<ArgumentOutOfRangeException>(() => empty.Slice(0, 1)).ParamName);
        Assert.Equal("offset", Assert.Throws<ArgumentOutOfRangeException>(() => empty.UnsafeSlice(1, 0)).ParamName);
        Assert.Equal("length", Assert.Throws<ArgumentOutOfRangeException>(() => empty.UnsafeSlice(0, 1)).ParamName);
    }

    [Fact]
    public void TheDefaultValueHasNoPageOrLeaseAndDisposingItDoesNothing()
    {
        var empty = default(ArcBuffer);

        Assert.Throws<InvalidOperationException>(() => empty.First);
        Assert.Throws<InvalidOperationException>(() => empty.Unpin());
        empty.Dispose();
        empty.Dispose();
        Assert.Equal(0, empty.Length);
        Assert.True(empty.Retain().IsDefault);
    }

    [Fact]
    public void AppendPinnedRetainsDataAfterSourceIsDisposed()
    {
        var source = new ArcBufferWriter();
        var expected = CreatePayload(PageSize + 17);
        source.Write(expected);
        var sourceBuffer = source.PeekSlice(source.Length);

        using var destination = new ArcBufferWriter();
        destination.AppendPinned(sourceBuffer);

        source.Dispose();
        sourceBuffer.Dispose();

        using var destinationBuffer = destination.PeekSlice(destination.Length);
        Assert.Equal(expected, destinationBuffer.ToArray());
    }

    [Fact]
    public void EmptySliceRetainsPageAfterSourceIsDisposed()
    {
        var writer = new ArcBufferWriter();
        var buffer = writer.PeekSlice(0);

        writer.Dispose();

        using (buffer)
        {
            Assert.Equal(0, buffer.Length);
            Assert.Empty(buffer.ToArray());
        }
    }

    [Fact]
    public void WriteAtAndTruncateUpdateExistingData()
    {
        using var writer = new ArcBufferWriter();
        writer.Write(CreatePayload(PageSize + 8));

        byte[] replacement = [42, 43, 44, 45];
        writer.WriteAt(PageSize - 2, replacement);
        writer.Truncate(PageSize + 2);

        using var buffer = writer.PeekSlice(writer.Length);
        var actual = buffer.ToArray();

        Assert.Equal(PageSize + 2, actual.Length);
        Assert.Equal(replacement, actual.AsSpan(PageSize - 2, replacement.Length).ToArray());
    }

    [Fact]
    public void ReaderFindsDelimiterAndAdvances()
    {
        using var writer = new ArcBufferWriter();
        writer.Write("first\nsecond"u8);
        var reader = writer.Reader;

        Assert.True(reader.TryReadTo(out var first, (byte)'\n'));
        using (first)
        {
            Assert.Equal("first"u8.ToArray(), first.ToArray());
        }

        Assert.True(reader.IsNext("second"u8, advancePast: true));
        Assert.Equal(0, reader.Length);
    }

    [Fact]
    public void SliceAcrossPagesReturnsRequestedRange()
    {
        using var writer = new ArcBufferWriter();
        var expected = CreatePayload(PageSize * 3);
        writer.Write(expected);
        using var full = writer.PeekSlice(writer.Length);
        using var slice = full.Slice(PageSize - 10, PageSize + 20);

        Assert.Equal(
            expected.AsSpan(PageSize - 10, PageSize + 20).ToArray(),
            slice.AsReadOnlySequence().ToArray());
    }

    [Fact]
    public void InvalidBoundsAreRejected()
    {
        using var writer = new ArcBufferWriter();
        writer.Write([1, 2, 3]);

        Assert.Throws<ArgumentOutOfRangeException>(() => writer.PeekSlice(4));
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.AdvanceReader(4));
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.WriteAt(4, [1]));
    }

    [Fact]
    public void OrdinaryAliasesShareOneDisposableOwnership()
    {
        using var writer = new ArcBufferWriter();
        writer.Write(CreatePayload(PageSize + 1));
        var owner = writer.PeekSlice(writer.Length);
        var alias = owner;
        var pages = owner.Pages.ToArray();

        owner.Dispose();
        alias.Dispose();

        Assert.Throws<InvalidOperationException>(() => alias.ToArray());
        Assert.All(pages, page => Assert.Equal(1, page.ReferenceCount));
        using var writerView = writer.PeekSlice(writer.Length);
        Assert.Equal(PageSize + 1, writerView.Length);
    }

    [Fact]
    public void ConcurrentAliasDisposalUnpinsEveryPageExactlyOnce()
    {
        using var writer = new ArcBufferWriter();
        writer.Write(CreatePayload(PageSize * 3 + 1));
        var owner = writer.PeekSlice(writer.Length);
        var aliases = Enumerable.Repeat(owner, 64).ToArray();
        var pages = owner.Pages.ToArray();

        Parallel.For(0, aliases.Length, index => aliases[index].Dispose());

        Assert.All(pages, page => Assert.Equal(1, page.ReferenceCount));
        Assert.Throws<InvalidOperationException>(() => _ = owner.Length);
    }

    [Fact]
    public void RetainOwnsIndependentPagePins()
    {
        using var writer = new ArcBufferWriter();
        writer.Write(CreatePayload(PageSize + 17));
        var owner = writer.PeekSlice(writer.Length);
        var retained = owner.Retain();
        var pages = owner.Pages.ToArray();

        owner.Dispose();

        Assert.Equal(PageSize + 17, retained.Length);
        Assert.All(pages, page => Assert.Equal(2, page.ReferenceCount));
        retained.Dispose();
        Assert.All(pages, page => Assert.Equal(1, page.ReferenceCount));
    }

    [Fact]
    public void MoveInvalidatesEveryOrdinaryAliasWithoutRepinning()
    {
        using var writer = new ArcBufferWriter();
        writer.Write([1, 2, 3]);
        var owner = writer.PeekSlice(writer.Length);
        var alias = owner;
        var page = owner.First;

        var moved = owner.Move();

        Assert.Throws<InvalidOperationException>(() => alias.ToArray());
        Assert.Equal(2, page.ReferenceCount);
        Assert.Equal([1, 2, 3], moved.ToArray());
        alias.Dispose();
        moved.Dispose();
        Assert.Equal(1, page.ReferenceCount);
    }

    [Fact]
    public void ZeroLengthAliasesShareOneLeaseAndRetainIsIndependent()
    {
        using var writer = new ArcBufferWriter();
        var owner = writer.PeekSlice(0);
        var alias = owner;
        var retained = owner.Retain();
        var page = owner.First;

        owner.Dispose();

        Assert.Throws<InvalidOperationException>(() => _ = alias.Length);
        Assert.Equal(0, retained.Length);
        Assert.Equal(2, page.ReferenceCount);
        retained.Dispose();
        Assert.Equal(1, page.ReferenceCount);
    }

    [Fact]
    public void ReturnedPageCannotBeObservedThroughAStaleAlias()
    {
        var writer = new ArcBufferWriter();
        writer.Write([1, 2, 3]);
        var owner = writer.ConsumeSlice(writer.Length);
        var alias = owner;
        var page = owner.First;
        var token = page.Version;

        owner.Dispose();
        writer.Dispose();

        Assert.NotEqual(token, page.Version);
        Assert.Equal(0, page.ReferenceCount);
        Assert.Throws<InvalidOperationException>(() => alias.ToArray());
        alias.Dispose();
    }

    [Fact]
    public void RetainFailureRollsBackEveryPagePin()
    {
        using var writer = new ArcBufferWriter();
        writer.Write([1]);
        using var owner = writer.PeekSlice(writer.Length);
        var page = owner.First;
        var referenceCount = page.ReferenceCount;

        Assert.Throws<InvalidOperationException>(
            () => _ = new ArcBuffer(page, page.Version, 0, length: 2));

        Assert.Equal(referenceCount, page.ReferenceCount);
        Assert.Equal([1], owner.ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(PageSize - 1)]
    [InlineData(PageSize)]
    [InlineData(PageSize + 1)]
    [InlineData(ArcBufferPagePool.LargestPageSize - 1)]
    [InlineData(ArcBufferPagePool.LargestPageSize)]
    [InlineData(ArcBufferPagePool.LargestPageSize + 1)]
    public void CopyFromBoundaries_PreserveBytesAndPageCountWithoutLeaks(int length) =>
        _leaks.InScope(() =>
        {
            var source = CreatePayload(length);
            var expected = (byte[])source.Clone();

            using (var fromSpan = ArcBuffer.CopyFrom(source))
            {
                Array.Fill(source, (byte)0xEE);
                Assert.Equal(length, fromSpan.Length);
                Assert.Equal(expected, fromSpan.ToArray());
                Assert.Equal(expected, fromSpan.AsReadOnlySequence().ToArray());
                Assert.Equal(length == 0, fromSpan.IsDefault);
                if (length is > 0 and <= ArcBufferPagePool.LargestPageSize)
                {
                    Assert.Single(fromSpan.Pages);
                }
                else if (length > ArcBufferPagePool.LargestPageSize)
                {
                    Assert.True(fromSpan.Pages.Count() > 1);
                }
            }

            // Build a multi-segment sequence with leading/middle/trailing empty segments around the payload.
            var sequence = length == 0
                ? BuildSequence([ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty])
                : BuildSequence([ReadOnlyMemory<byte>.Empty, expected.AsMemory(0, length / 2), ReadOnlyMemory<byte>.Empty, expected.AsMemory(length / 2), ReadOnlyMemory<byte>.Empty]);

            using var fromSequence = ArcBuffer.CopyFrom(sequence);
            Assert.Equal(length, fromSequence.Length);
            Assert.Equal(expected, fromSequence.ToArray());
            Assert.Equal(expected, fromSequence.AsReadOnlySequence().ToArray());
            if (length is > 0 and <= ArcBufferPagePool.LargestPageSize)
            {
                Assert.Single(fromSequence.Pages);
            }
        });

    [Fact]
    public void CopyFromSpan_ArbitraryLengthsAndMutations_PreservesBytesWithoutLeaks()
    {
        var lengthGen = Gen.OneOf(
            Gen.Int[0, 64],
            Gen.Int[PageSize - 16, PageSize + 16],
            Gen.Int[PageSize * 2 - 16, PageSize * 2 + 16],
            Gen.Const(ArcBufferPagePool.LargestPageSize - 1),
            Gen.Const(ArcBufferPagePool.LargestPageSize),
            Gen.Const(ArcBufferPagePool.LargestPageSize + 17));

        Gen.Select(lengthGen, Gen.Byte).Sample((length, seed) => _leaks.InScope(() =>
        {
            var source = new byte[length];
            for (var i = 0; i < source.Length; i++)
            {
                source[i] = unchecked((byte)(seed + i * 31));
            }

            var expected = (byte[])source.Clone();
            using var copied = ArcBuffer.CopyFrom(source);

            // Mutate source immediately to prove full copy independence.
            Array.Fill(source, unchecked((byte)(seed ^ 0xFF)));

            Assert.Equal(length, copied.Length);
            Assert.Equal(expected, copied.ToArray());
            Assert.Equal(expected, copied.AsReadOnlySequence().ToArray());
            var destination = new byte[length];
            Assert.Equal(length, copied.CopyTo(destination));
            Assert.Equal(expected, destination);

            if (length == 0)
            {
                Assert.True(copied.IsDefault);
            }
            else if (length <= ArcBufferPagePool.LargestPageSize)
            {
                Assert.Single(copied.Pages);
            }
        }), iter: 120);
    }

    [Fact]
    public void CopyFromSequence_ArbitrarySegmentSplits_MatchesConcatenationWithoutLeaks()
    {
        var segmentLengthGen = Gen.OneOf(
            Gen.Const(0),
            Gen.Int[1, 32],
            Gen.Int[PageSize - 8, PageSize + 8]);

        Gen.Select(segmentLengthGen.Array[0, 10], Gen.Int[0, 15], Gen.Byte).Sample((segmentLengths, leadOffset, seed) => _leaks.InScope(() =>
        {
            var totalLength = segmentLengths.Sum();
            var expected = new byte[totalLength];
            var backingArrays = new byte[segmentLengths.Length][];
            var memories = new ReadOnlyMemory<byte>[segmentLengths.Length];
            var offset = 0;
            for (var i = 0; i < segmentLengths.Length; i++)
            {
                var segLen = segmentLengths[i];
                var array = new byte[leadOffset + segLen + 3];
                for (var j = 0; j < segLen; j++)
                {
                    var value = unchecked((byte)(seed + (offset + j) * 17 + i));
                    array[leadOffset + j] = value;
                    expected[offset + j] = value;
                }

                backingArrays[i] = array;
                memories[i] = array.AsMemory(leadOffset, segLen);
                offset += segLen;
            }

            var sequence = BuildSequence(memories);
            using var copied = ArcBuffer.CopyFrom(sequence);

            foreach (var array in backingArrays)
            {
                Array.Fill(array, unchecked((byte)(seed ^ 0xAA)));
            }

            Assert.Equal(totalLength, copied.Length);
            Assert.Equal(expected, copied.ToArray());
            Assert.Equal(expected, copied.AsReadOnlySequence().ToArray());
            if (totalLength == 0)
            {
                Assert.True(copied.IsDefault);
            }
            else if (totalLength <= ArcBufferPagePool.LargestPageSize)
            {
                Assert.Single(copied.Pages);
            }
        }), iter: 150);
    }

    private static ReadOnlySequence<byte> BuildSequence(ReadOnlyMemory<byte>[] segments)
    {
        if (segments.Length == 0)
        {
            return ReadOnlySequence<byte>.Empty;
        }

        var first = new MemorySegment(segments[0]);
        var last = first;
        for (var i = 1; i < segments.Length; i++)
        {
            last = last.Append(segments[i]);
        }

        return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
    }

    private sealed class MemorySegment : ReadOnlySequenceSegment<byte>
    {
        public MemorySegment(ReadOnlyMemory<byte> memory) => Memory = memory;

        public MemorySegment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new MemorySegment(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = next;
            return next;
        }
    }

    private static byte[] CreatePayload(int length) =>
        [.. Enumerable.Range(0, length).Select(static value => (byte)(value & byte.MaxValue))];
}
