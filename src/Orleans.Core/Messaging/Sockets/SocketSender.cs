using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Net.Sockets;

namespace Orleans.Runtime.Messaging
{
    internal sealed class SocketSender : SocketSenderReceiverBase
    {
        private List<ArraySegment<byte>> bufferList;

        public SocketSender(Socket socket, PipeScheduler scheduler) : base(socket, scheduler)
        {
        }

        public SocketAwaitableEventArgs SendAsync(ReadOnlySequence<byte> buffers)
        {
            if (buffers.IsSingleSegment)
            {
                return SendAsync(buffers.First);
            }

            if (!Array.Empty<byte>().Equals(awaitableEventArgs.Buffer))
            {
                awaitableEventArgs.SetBuffer(null, 0, 0);
            }

            awaitableEventArgs.BufferList = GetBufferList(buffers);

            if (!socket.SendAsync(awaitableEventArgs))
            {
                awaitableEventArgs.Complete();
            }

            return awaitableEventArgs;
        }

        private SocketAwaitableEventArgs SendAsync(ReadOnlyMemory<byte> memory)
        {
            // The BufferList getter is much less expensive then the setter.
            if (awaitableEventArgs.BufferList != null)
            {
                awaitableEventArgs.BufferList = null;
            }

            var array = memory.GetArray();
            awaitableEventArgs.SetBuffer(array.Array, array.Offset, array.Count);

            if (!socket.SendAsync(awaitableEventArgs))
            {
                awaitableEventArgs.Complete();
            }

            return awaitableEventArgs;
        }

        private List<ArraySegment<byte>> GetBufferList(ReadOnlySequence<byte> buffer)
        {
            Debug.Assert(!buffer.IsEmpty);
            Debug.Assert(!buffer.IsSingleSegment);

            if (bufferList == null)
            {
                bufferList = new List<ArraySegment<byte>>();
            }
            else
            {
                // Buffers are pooled, so it's OK to root them until the next multi-buffer write.
                bufferList.Clear();
            }

            foreach (var b in buffer)
            {
                bufferList.Add(b.GetArray());
            }

            return bufferList;
        }
    }
}
