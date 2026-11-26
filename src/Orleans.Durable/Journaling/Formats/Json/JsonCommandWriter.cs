using System.Buffers;
using System.Text.Json;

namespace Orleans.Durable.Journaling.Json;

internal static class JsonCommandWriter
{
    [ThreadStatic]
    private static Utf8JsonWriter? t_writer;

    public static void Write<TArg>(JournalStreamWriter writer, TArg argument, Action<Utf8JsonWriter, TArg> writeArrayElementsTo)
    {
        ArgumentNullException.ThrowIfNull(writeArrayElementsTo);
        using var entry = writer.BeginEntry();
        var jsonWriter = t_writer;
        if (jsonWriter is null)
        {
            jsonWriter = new Utf8JsonWriter(entry.Writer);
        }
        else
        {
            t_writer = null;
            jsonWriter.Reset(entry.Writer);
        }

        try
        {
            jsonWriter.WriteStartArray();
            writeArrayElementsTo(jsonWriter, argument);
            jsonWriter.WriteEndArray();
            jsonWriter.Flush();
        }
        finally
        {
            jsonWriter.Reset(EmptyBufferWriter.Instance);
            t_writer ??= jsonWriter;
        }

        entry.Commit();
    }

    private sealed class EmptyBufferWriter : IBufferWriter<byte>
    {
        public static readonly EmptyBufferWriter Instance = new();

        public void Advance(int count) => throw new NotSupportedException();

        public Memory<byte> GetMemory(int sizeHint = 0) => throw new NotSupportedException();

        public Span<byte> GetSpan(int sizeHint = 0) => throw new NotSupportedException();
    }
}
