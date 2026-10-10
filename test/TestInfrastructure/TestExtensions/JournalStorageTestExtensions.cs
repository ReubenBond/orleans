using System.Buffers;
using Orleans.Serialization.Buffers;

#pragma warning disable ORLEANSEXP005

namespace Orleans.Journaling;

public static class JournalStorageTestExtensions
{
    public static async ValueTask AppendBytesAsync(this IJournalStorage storage, ReadOnlySequence<byte> bytes, CancellationToken cancellationToken)
    {
        using var writer = new ArcBufferWriter();
        writer.Write(bytes);
        using var buffer = writer.PeekSlice(writer.Length);
        await storage.AppendAsync(buffer, cancellationToken);
    }

    public static async ValueTask ReplaceBytesAsync(this IJournalStorage storage, ReadOnlySequence<byte> bytes, CancellationToken cancellationToken)
    {
        using var writer = new ArcBufferWriter();
        writer.Write(bytes);
        using var buffer = writer.PeekSlice(writer.Length);
        await storage.ReplaceAsync(buffer, cancellationToken);
    }
}
