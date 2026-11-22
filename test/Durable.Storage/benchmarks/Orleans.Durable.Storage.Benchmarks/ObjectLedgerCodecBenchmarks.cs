using System.Buffers;
using BenchmarkDotNet.Attributes;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Objects;

namespace Orleans.Durable.Storage.Benchmarks;

/// <summary>
/// Measures <c>CLOE</c> v1 frame and trailer encoding and decoding (object-ledgers.md §7, §10).
/// </summary>
[MemoryDiagnoser]
public class ObjectLedgerCodecBenchmarks
{
    private byte[][] _payloads = null!;
    private byte[] _encodeBuffer = null!;
    private byte[] _encodedExtent = null!;
    private ReadOnlySequence<byte> _segmentedExtent;

    /// <summary>Gets or sets the number of <c>DATA</c> frames per append.</summary>
    [Params(1, 16)]
    public int FramesPerAppend { get; set; } = 1;

    /// <summary>Gets or sets each frame's payload length in bytes.</summary>
    [Params(64, 4096)]
    public int PayloadLength { get; set; } = 64;

    [GlobalSetup]
    public void Setup()
    {
        _payloads = new byte[FramesPerAppend][];
        for (var i = 0; i < FramesPerAppend; i++)
        {
            _payloads[i] = new byte[PayloadLength];
            for (var j = 0; j < PayloadLength; j++)
            {
                _payloads[i][j] = (byte)((i * 31 + j) & 0xFF);
            }
        }

        var totalBytes = FramesPerAppend * (ObjectLedgerExtentFormat.FrameHeaderLength + PayloadLength)
            + ObjectLedgerExtentFormat.TrailerLength;
        _encodeBuffer = new byte[totalBytes];
        _ = EncodeAppendInto(_encodeBuffer);
        _encodedExtent = [.. _encodeBuffer];

        var segmentSize = Math.Min(256, Math.Max(16, _encodedExtent.Length / 4));
        _segmentedExtent = BuildMultiSegment(_encodedExtent, segmentSize);
        if (_segmentedExtent.IsSingleSegment)
        {
            throw new InvalidOperationException("Expected multi-segment ReadOnlySequence for segmented decode benchmark.");
        }
    }

    [Benchmark]
    public int EncodeFramesAndTrailer() => EncodeAppendInto(_encodeBuffer);

    [Benchmark]
    public int DecodeExtent()
    {
        var decoded = ObjectLedgerExtentFormat.DecodeExtent(_encodedExtent);
        return decoded switch
        {
            ObjectLedgerDecodedExtent extent => extent.Appends[0].Frames.Length,
            InvalidDataException error => throw new InvalidOperationException("CLOE extent decode failed.", error),
            null => throw new InvalidOperationException("Unexpected null decode result."),
        };
    }

    [Benchmark]
    public int DecodeExtentRangeSegmented()
    {
        var decoded = ObjectLedgerExtentFormat.DecodeExtentRange(_segmentedExtent, baseOffset: 0);
        return decoded switch
        {
            ObjectLedgerDecodedExtent extent => extent.Appends[0].Frames.Length,
            InvalidDataException error => throw new InvalidOperationException("Segmented CLOE decode failed.", error),
            null => throw new InvalidOperationException("Unexpected null decode result."),
        };
    }

    private int EncodeAppendInto(Span<byte> destination)
    {
        var cursor = 0;
        for (var i = 0; i < FramesPerAppend; i++)
        {
            var slot = new Slot(i);
            var payload = _payloads[i];
            var crc = ObjectLedgerExtentFormat.ComputeFrameCrc32c(ObjectLedgerFrameKind.Data, slot, payload);
            ObjectLedgerExtentFormat.WriteFrameHeader(destination.Slice(cursor, ObjectLedgerExtentFormat.FrameHeaderLength), ObjectLedgerFrameKind.Data, slot, payload.Length, crc);
            cursor += ObjectLedgerExtentFormat.FrameHeaderLength;
            payload.CopyTo(destination.Slice(cursor, payload.Length));
            cursor += payload.Length;
        }

        ObjectLedgerExtentFormat.WriteTrailer(destination.Slice(cursor, ObjectLedgerExtentFormat.TrailerLength), Slot.Zero, FramesPerAppend, startOffset: 0);
        return cursor + ObjectLedgerExtentFormat.TrailerLength;
    }

    private static ReadOnlySequence<byte> BuildMultiSegment(byte[] source, int segmentSize)
    {
        if (source.Length <= segmentSize)
        {
            return new ReadOnlySequence<byte>(source);
        }

        var first = new MemorySegment(source.AsMemory(0, segmentSize));
        var last = first;
        for (var offset = segmentSize; offset < source.Length; offset += segmentSize)
        {
            var length = Math.Min(segmentSize, source.Length - offset);
            last = last.Append(source.AsMemory(offset, length));
        }

        return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
    }

    private sealed class MemorySegment : ReadOnlySequenceSegment<byte>
    {
        public MemorySegment(ReadOnlyMemory<byte> memory)
        {
            Memory = memory;
        }

        public MemorySegment Append(ReadOnlyMemory<byte> memory)
        {
            var segment = new MemorySegment(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = segment;
            return segment;
        }
    }
}
