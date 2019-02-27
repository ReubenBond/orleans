using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;

namespace Orleans.Runtime.Messaging
{
    internal abstract class ConnectionPreambleReceiver
    {
        private const int MaxPreambleLength = 1024;

        public abstract Task ReadPreamble(ConnectionContext connection);

        protected async Task<GrainId> ReadPreambleInternal(ConnectionContext connection)
        {
            var input = connection.Transport.Input;

            var readResult = await input.ReadAsync().ConfigureAwait(false);
            var buffer = readResult.Buffer;
            CheckForCompletion(ref readResult);
            while (buffer.Length < 4)
            {
                input.AdvanceTo(buffer.Start, buffer.End);
                readResult = await input.ReadAsync().ConfigureAwait(false);
                buffer = readResult.Buffer;
                CheckForCompletion(ref readResult);
            }

            int ReadLength(ref ReadOnlySequence<byte> b)
            {
                Span<byte> lengthBytes = stackalloc byte[4];
                b.Slice(0, 4).CopyTo(lengthBytes);
                b = b.Slice(4);
                return BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
            }

            var length = ReadLength(ref buffer);
            if (length > MaxPreambleLength)
            {
                throw new InvalidOperationException($"Remote connection sent preamble length of {length}, which is greater than maximum allowed size of {MaxPreambleLength}.");
            }

            while (buffer.Length < length)
            {
                input.AdvanceTo(buffer.Start, buffer.End);
                readResult = await input.ReadAsync().ConfigureAwait(false);
                buffer = readResult.Buffer;
                CheckForCompletion(ref readResult);
            }

            var grainIdBytes = new byte[Math.Min(length, 1024)];

            buffer.Slice(0, length).CopyTo(grainIdBytes);
            input.AdvanceTo(buffer.GetPosition(length));
            return GrainIdExtensions.FromByteArray(grainIdBytes);
        }

        private static void CheckForCompletion(ref ReadResult readResult)
        {
            if (readResult.IsCanceled || readResult.IsCompleted) throw new InvalidOperationException("Connection terminated prematurely");
        }
    }
}
