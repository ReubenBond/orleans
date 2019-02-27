using System;
using System.IO.Pipelines;
using System.Net.Sockets;

namespace Orleans.Runtime.Messaging
{
    internal abstract class SocketSenderReceiverBase : IDisposable
    {
        protected readonly Socket socket;
        protected readonly SocketAwaitableEventArgs awaitableEventArgs;

        protected SocketSenderReceiverBase(Socket socket, PipeScheduler scheduler)
        {
            this.socket = socket;
            this.awaitableEventArgs = new SocketAwaitableEventArgs(scheduler);
        }

        public void Dispose() => this.awaitableEventArgs.Dispose();
    }
}
