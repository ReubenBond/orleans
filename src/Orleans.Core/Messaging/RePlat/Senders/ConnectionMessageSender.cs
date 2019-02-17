using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;

namespace Orleans.Runtime.Messaging
{
    internal sealed class ConnectionMessageSender : IDisposable
    {
        internal static readonly object ContextItemKey = new object();
        private static readonly UnboundedChannelOptions ChannelOptions = new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = true
        };

        private readonly Channel<Message> messages;
        private readonly ChannelWriter<Message> writer;
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private readonly IMessageCenter messageCenter;
        private readonly IMessageSerializer serializer;
        private ConnectionContext connection;

        public ConnectionMessageSender(IMessageCenter messageCenter, IMessageSerializer messageSerializer)
        {
            this.messages = Channel.CreateUnbounded<Message>(ChannelOptions);
            this.writer = this.messages.Writer;
            this.messageCenter = messageCenter;
            this.serializer = messageSerializer;
        }

        public Task Run(ConnectionContext connection)
        {
            if (this.connection != null) throw new InvalidOperationException($"{nameof(ConnectionContext)} already set on this instance.");
            this.connection = connection;
            return Task.Run(this.Process);
        }

        public void Dispose() => this.Abort();

        public void Abort()
        {
            if (this.writer.TryComplete())
            {
                ThreadPool.UnsafeQueueUserWorkItem(cts => ((CancellationTokenSource)cts).Cancel(), this.cancellation);
            }
        }

        public void Send(Message message)
        {
            if (!this.writer.TryWrite(message))
            {
                this.RerouteMessage(message);
            }
        }
        private async Task Process()
        {
            var output = this.connection.Transport.Output;
            var reader = this.messages.Reader;
            try
            {
                while (!this.cancellation.IsCancellationRequested)
                {
                    var moreTask = reader.WaitToReadAsync();
                    var more = moreTask.IsCompleted ? moreTask.GetAwaiter().GetResult() : await moreTask.ConfigureAwait(false);
                    if (!more)
                    {
                        break;
                    }

                    if (reader.TryRead(out var message))
                    {
                        this.serializer.Write(ref output, message);
                    }

                    var flushTask = output.FlushAsync();
                    var flushResult = flushTask.IsCompleted ? flushTask.GetAwaiter().GetResult() : await flushTask.ConfigureAwait(false);
                    if (flushResult.IsCompleted || flushResult.IsCanceled) break;
                }
            }
            finally
            {
                while (reader.TryRead(out var message))
                {
                    this.RerouteMessage(message);
                }

                this.Abort();
                this.connection.Abort();
            }
        }

        private void RerouteMessage(Message message)
        {
            ThreadPool.UnsafeQueueUserWorkItem(msg => this.messageCenter.SendMessage((Message)msg), message);
        }
    }
}
