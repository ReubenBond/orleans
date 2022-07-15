using System;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace Orleans.Utilities;

public static class BufferWriterExtensions
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Write<TBufferWriter>(this ref TBufferWriter writer, ReadOnlySequence<byte> input) where TBufferWriter : struct, IBufferWriter<byte>
    {
        foreach (var segment in input)
        {
            writer.Write(segment.Span);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Write<TBufferWriter>(ref this TBufferWriter writer, ReadOnlySpan<byte> value) where TBufferWriter : struct, IBufferWriter<byte>
    {
        Span<byte> destination = writer.GetSpan();

        // Fast path, try copying to the available memory directly
        if (value.Length <= destination.Length)
        {
            value.CopyTo(destination);
            writer.Advance(value.Length);
        }
        else
        {
            WriteMultiSegment(ref writer, value, destination);
        }
    }

    private static void WriteMultiSegment<TBufferWriter>(ref TBufferWriter writer, in ReadOnlySpan<byte> source, Span<byte> destination) where TBufferWriter : struct, IBufferWriter<byte>
    {
        ReadOnlySpan<byte> input = source;
        while (true)
        {
            int writeSize = Math.Min(destination.Length, input.Length);
            input.Slice(0, writeSize).CopyTo(destination);
            writer.Advance(writeSize);
            input = input.Slice(writeSize);
            if (input.Length > 0)
            {
                destination = writer.GetSpan();

                continue;
            }

            return;
        }
    }
}
