using System;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;

namespace Orleans.Runtime.Messaging
{
    internal abstract class ConnectionPreambleSender
    {
        public abstract Task WritePreamble(ConnectionContext connection);

        protected Task WritePreambleInternal(ConnectionContext connection, GrainId grainId)
        {
            var output = connection.Transport.Output;
            var grainIdByteArray = grainId.ToByteArray();

            Span<byte> bytes = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, grainIdByteArray.Length);
            var length = bytes.Length + grainIdByteArray.Length;
            var buffer = output.GetSpan(length);
            bytes.CopyTo(buffer);
            new ReadOnlySpan<byte>(grainIdByteArray).CopyTo(buffer.Slice(sizeof(int)));
            output.Advance(length);
            var flushTask = output.FlushAsync();

            if (flushTask.IsCompletedSuccessfully) return Task.CompletedTask;
            return FlushAsync(flushTask);

            async Task FlushAsync(ValueTask<FlushResult> task)
            {
                await task.ConfigureAwait(false);
            }
        }
    }
}
