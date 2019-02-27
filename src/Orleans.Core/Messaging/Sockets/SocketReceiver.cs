using System;
using System.IO.Pipelines;
using System.Net.Sockets;

namespace Orleans.Runtime.Messaging
{
    internal sealed class SocketReceiver : SocketSenderReceiverBase
    {
        public SocketReceiver(Socket socket, PipeScheduler scheduler) : base(socket, scheduler)
        {
        }

        public SocketAwaitableEventArgs WaitForDataAsync()
        {
#if NETCOREAPP2_1
            this.awaitableEventArgs.SetBuffer(Memory<byte>.Empty);
#else
            this.awaitableEventArgs.SetBuffer(Array.Empty<byte>(), 0, 0);
#endif

            if (!this.socket.ReceiveAsync(this.awaitableEventArgs))
            {
                this.awaitableEventArgs.Complete();
            }

            return this.awaitableEventArgs;
        }

        public SocketAwaitableEventArgs ReceiveAsync(Memory<byte> buffer)
        {
#if NETCOREAPP2_1
            this.awaitableEventArgs.SetBuffer(buffer);
#else
            var array = buffer.GetArray();
            this.awaitableEventArgs.SetBuffer(array.Array, array.Offset, array.Count);
#endif

            if (!this.socket.ReceiveAsync(this.awaitableEventArgs))
            {
                this.awaitableEventArgs.Complete();
            }

            return this.awaitableEventArgs;
        }
    }
}
