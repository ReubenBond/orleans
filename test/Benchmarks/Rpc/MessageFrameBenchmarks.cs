using System.Buffers;
using System.Buffers.Binary;
using BenchmarkDotNet.Attributes;
using Orleans.Runtime.Messaging;

namespace Benchmarks.Rpc;

[Config(typeof(RpcBenchmarkConfig))]
[BenchmarkCategory("Rpc", "Serialization")]
public class MessageFrameBenchmarks
{
    private ReadOnlySequence<byte> _contiguous;
    private ReadOnlySequence<byte> _segmented;

    [GlobalSetup]
    public void Setup()
    {
        var frame = new byte[64];
        BinaryPrimitives.WriteInt32LittleEndian(frame, 42);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), 14);
        _contiguous = new ReadOnlySequence<byte>(frame);

        var first = new Segment(frame.AsMemory(0, 4));
        var second = first.Append(frame.AsMemory(4));
        _segmented = new ReadOnlySequence<byte>(first, 0, second, second.Memory.Length);
    }

    [Benchmark(Baseline = true)]
    public long GeneralSequenceCopy()
    {
        Span<byte> lengthBytes = stackalloc byte[8];
        _contiguous.Slice(_contiguous.Start, lengthBytes.Length).CopyTo(lengthBytes);
        var headerLength = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
        var bodyLength = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes[4..]);
        return ((long)headerLength << 32) | (uint)bodyLength;
    }

    [Benchmark]
    public long ContiguousPrefix()
    {
        MessageSerializer.ReadFrameLengths(_contiguous, out var headerLength, out var bodyLength);
        return ((long)headerLength << 32) | (uint)bodyLength;
    }

    [Benchmark]
    public long SegmentedPrefix()
    {
        MessageSerializer.ReadFrameLengths(_segmented, out var headerLength, out var bodyLength);
        return ((long)headerLength << 32) | (uint)bodyLength;
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(ReadOnlyMemory<byte> memory) => Memory = memory;

        public Segment Append(ReadOnlyMemory<byte> memory)
        {
            var segment = new Segment(memory)
            {
                RunningIndex = RunningIndex + Memory.Length
            };
            Next = segment;
            return segment;
        }
    }
}
