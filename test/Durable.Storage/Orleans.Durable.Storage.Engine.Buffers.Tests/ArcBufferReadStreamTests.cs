using System.Buffers;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Buffers.Tests;

public sealed class ArcBufferReadStreamTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    [Fact]
    public void ReadAndSeek_MultiPageSequencePreservesPositionAndBytes()
    {
        using var writer = new ArcBufferWriter();
        var expected = Pattern(ArcBufferWriter.MinimumPageSize * 2 + 37);
        writer.Write(expected);
        using var payload = writer.PeekSlice(writer.Length);
        using var stream = new ArcBufferReadStream(payload);
        var first = new byte[19];

        Assert.True(stream.CanRead);
        Assert.True(stream.CanSeek);
        Assert.False(stream.CanWrite);
        Assert.Equal(expected.Length, stream.Length);
        Assert.Equal(first.Length, stream.Read(first, 0, first.Length));
        Assert.Equal(expected.AsSpan(0, first.Length).ToArray(), first);
        Assert.Equal(first.Length, stream.Position);

        Assert.Equal(expected.Length - 11, stream.Seek(-11, SeekOrigin.End));
        var last = new byte[20];
        Assert.Equal(11, stream.Read(last));
        Assert.Equal(expected.AsSpan(expected.Length - 11).ToArray(), last.AsSpan(0, 11).ToArray());
        Assert.Equal(0, stream.Read(last));
        Assert.Equal(expected.Length, stream.Position);

        Assert.Equal(7, stream.Seek(7, SeekOrigin.Begin));
        Assert.Equal(3, stream.Seek(-4, SeekOrigin.Current));
        stream.Position = ArcBufferWriter.MinimumPageSize - 1;
        var straddle = new byte[2];
        Assert.Equal(2, stream.Read(straddle));
        Assert.Equal(expected.AsSpan(ArcBufferWriter.MinimumPageSize - 1, 2).ToArray(), straddle);
    }

    [Fact]
    public void Stream_ReferencesSourcePagesWithoutCopying()
    {
        using var writer = new ArcBufferWriter();
        writer.Write("before"u8);
        using var payload = writer.PeekSlice(writer.Length);
        using var stream = new ArcBufferReadStream(payload);

        writer.WriteAt(0, "after!"u8);
        var actual = new byte[6];
        Assert.Equal(actual.Length, stream.Read(actual));

        Assert.Equal("after!"u8.ToArray(), actual);
        Assert.Equal(actual.Length, stream.Position);
    }

    /// <summary>
    /// The stream holds its own lease, so an SDK which reads a request's content after its caller released the buffer,
    /// or seeks back and reads it again as a retry does, reads the payload, not pages the pool reused (in Debug builds,
    /// poisoned); once the stream is disposed, its pages return to the pool and every member throws.
    /// </summary>
    /// <remarks>
    /// The pool hands out the pages returned last first, so the writer which follows the release would take the
    /// payload's pages had the stream not kept them.
    /// </remarks>
    [Fact]
    public void AReadAfterTheCallerReleasedItsBufferReadsThePayload()
    {
        var expected = Pattern(ArcBufferWriter.MinimumPageSize * 3 + 5);
        ArcBufferReadStream stream;
        using (var writer = new ArcBufferWriter())
        {
            writer.Write(expected);
            using var payload = writer.ConsumeSlice(writer.Length);
            stream = new ArcBufferReadStream(payload);
        }

        try
        {
            using var reused = new ArcBufferWriter();
            reused.Write(new byte[expected.Length]);
            Assert.Equal(expected.Length, stream.Length);
            var actual = new byte[expected.Length];
            stream.ReadExactly(actual);
            Assert.Equal(expected, actual);

            reused.Write(new byte[expected.Length]);
            Assert.Equal(0, stream.Seek(0, SeekOrigin.Begin));
            Array.Clear(actual);
            stream.ReadExactly(actual);
            Assert.Equal(expected, actual);
        }
        finally
        {
            stream.Dispose();
        }

        Assert.False(stream.CanRead);
        Assert.Throws<ObjectDisposedException>(() => stream.Read(new byte[1]));
        Assert.Throws<ObjectDisposedException>(() => stream.Seek(0, SeekOrigin.Begin));
        Assert.Throws<ObjectDisposedException>(() => stream.Length);
        Assert.Throws<ObjectDisposedException>(() => stream.Position);
        stream.Dispose();
    }

    /// <summary>
    /// A send which races its caller's disposal of the stream, as an upload the caller cancelled does: every read
    /// returns the payload's bytes or throws <see cref="ObjectDisposedException"/>, never another page's, and the pages
    /// return to the pool once the stream is disposed.
    /// </summary>
    [Fact]
    public async Task AReadRacingDisposalReturnsThePayloadOrThrows()
    {
        var expected = Pattern(ArcBufferWriter.MinimumPageSize * 8 + 3);
        for (var round = 0; round < 200; round++)
        {
            ArcBufferReadStream stream;
            using (var writer = new ArcBufferWriter())
            {
                writer.Write(expected);
                using var payload = writer.ConsumeSlice(writer.Length);
                stream = new ArcBufferReadStream(payload);
            }

            using var started = new ManualResetEventSlim();
            var reader = Task.Run(() =>
            {
                var chunk = new byte[97];
                started.Set();
                try
                {
                    while (true)
                    {
                        Assert.Equal(expected.Length, stream.Length);
                        var position = stream.Position;
                        var read = stream.Read(chunk);
                        if (read == 0)
                        {
                            _ = stream.Seek(0, SeekOrigin.Begin);
                            continue;
                        }

                        Assert.Equal(expected.AsSpan((int)position, read).ToArray(), chunk.AsSpan(0, read).ToArray());
                    }
                }
                catch (ObjectDisposedException)
                {
                }
            }, TestContext.Current.CancellationToken);

            started.Wait(TestContext.Current.CancellationToken);
            stream.Dispose();
            using var reused = new ArcBufferWriter();
            reused.Write(new byte[expected.Length]);
            await reader;
        }
    }

    /// <summary>
    /// A payload of many segments read a few bytes at a time: each read continues from the cursor, so the reads step
    /// through each page once, linear in the payload's length. Debug builds count the steps.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(4096)]
    public void AMultiSegmentPayloadReadInSmallChunksStepsLinearlyThroughItsSegments(int chunk)
    {
        var expected = Pattern(256 * ArcBufferWriter.MinimumPageSize + 3);
        using var writer = new ArcBufferWriter();
        for (var offset = 0; offset < expected.Length; offset += ArcBufferWriter.MinimumPageSize)
        {
            writer.Write(expected.AsSpan(offset, Math.Min(ArcBufferWriter.MinimumPageSize, expected.Length - offset)));
        }

        using var payload = writer.PeekSlice(writer.Length);
        var segments = 0;
        foreach (var _ in payload.AsReadOnlySequence())
        {
            segments++;
        }

        Assert.True(segments >= 256, $"The payload spans {segments} segments.");
        using var stream = new ArcBufferReadStream(payload);
        var actual = new byte[expected.Length];
#if DEBUG
        var before = ArcBuffer.PageSegmentEnumerator.DebugSteps;
#endif
        for (var offset = 0; offset < actual.Length;)
        {
            offset += stream.Read(actual.AsSpan(offset, Math.Min(chunk, actual.Length - offset)));
        }

        Assert.Equal(0, stream.Read(new byte[1]));
#if DEBUG
        Assert.Equal(segments, ArcBuffer.PageSegmentEnumerator.DebugSteps - before);
#endif
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task EmptyReadAndCancellation_HaveNoSideEffects()
    {
        using var writer = new ArcBufferWriter();
        using var payload = writer.PeekSlice(0);
        using var stream = new ArcBufferReadStream(payload);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var destination = new byte[4];

        Assert.Equal(
            0,
            await stream.ReadAtLeastAsync(
                destination,
                minimumBytes: 1,
                throwOnEndOfStream: false,
                TestContext.Current.CancellationToken));
        Assert.Equal(0, stream.Position);
        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await stream.ReadExactlyAsync(destination, cancellation.Token));
        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public void InvalidMutationAndBounds_AreRejected()
    {
        using var writer = new ArcBufferWriter();
        writer.Write("abc"u8);
        using var payload = writer.PeekSlice(writer.Length);
        using var stream = new ArcBufferReadStream(payload);

        Assert.Throws<ArgumentOutOfRangeException>(() => stream.Position = -1);
        Assert.Throws<ArgumentOutOfRangeException>(() => stream.Position = 4);
        Assert.Throws<ArgumentOutOfRangeException>(() => stream.Seek(1, SeekOrigin.End));
        Assert.Throws<ArgumentOutOfRangeException>(() => stream.Seek(0, (SeekOrigin)99));
        Assert.Throws<NotSupportedException>(() => stream.SetLength(1));
        Assert.Throws<NotSupportedException>(() => stream.Write([1], 0, 1));
        Assert.Equal(0, stream.Position);
        Assert.Equal(3, stream.Length);
    }

    [Fact]
    public async Task CopyFromReadsAStreamToItsEndIntoPages()
    {
        var expected = Pattern(3 * ArcBufferWriter.MinimumPageSize + 11);
        using var source = new MemoryStream(expected);
        using var writer = new ArcBufferWriter(expected.Length);

        await ArcBufferWriteStream.CopyFromAsync(source, writer, TestContext.Current.CancellationToken);

        using var written = writer.ConsumeSlice(writer.Length);
        Assert.Equal(expected, written.ToArray());
    }

    [Fact]
    public async Task TheWriteStreamAppendsEveryWriteToItsWriter()
    {
        var expected = Pattern(3 * ArcBufferWriter.MinimumPageSize + 11);
        using var writer = new ArcBufferWriter(expected.Length);
        using (var sink = new ArcBufferWriteStream(writer))
        {
            sink.Write(expected, 0, 5);
            await sink.WriteAsync(expected.AsMemory(5, ArcBufferWriter.MinimumPageSize), TestContext.Current.CancellationToken);
            await sink.WriteAsync(expected, 5 + ArcBufferWriter.MinimumPageSize, expected.Length - 5 - ArcBufferWriter.MinimumPageSize, TestContext.Current.CancellationToken);
            Assert.Equal(expected.Length, sink.Position);
            Assert.False(sink.CanRead);
            Assert.Throws<NotSupportedException>(() => sink.Seek(0, SeekOrigin.Begin));
            sink.Dispose();
            Assert.Throws<ObjectDisposedException>(() => sink.Write([1]));
        }

        using var written = writer.ConsumeSlice(writer.Length);
        Assert.Equal(expected, written.ToArray());
    }

    private static byte[] Pattern(int length) =>
        [.. Enumerable.Range(0, length).Select(static index => (byte)((index * 31 + 17) & byte.MaxValue))];
}
