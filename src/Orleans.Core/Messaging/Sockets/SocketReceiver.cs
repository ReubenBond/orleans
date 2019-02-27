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
            this.awaitableEventArgs.SetBuffer(Array.Empty<byte>(), 0, 0);

            if (!this.socket.ReceiveAsync(this.awaitableEventArgs))
            {
                this.awaitableEventArgs.Complete();
            }

            return this.awaitableEventArgs;
        }

        public SocketAwaitableEventArgs ReceiveAsync(Memory<byte> buffer)
        {
            var array = buffer.GetArray();
            this.awaitableEventArgs.SetBuffer(array.Array, array.Offset, array.Count);

            if (!this.socket.ReceiveAsync(this.awaitableEventArgs))
            {
                this.awaitableEventArgs.Complete();
            }

            return this.awaitableEventArgs;
        }
    }
}
