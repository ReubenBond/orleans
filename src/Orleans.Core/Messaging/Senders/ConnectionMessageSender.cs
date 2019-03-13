using System;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;

namespace Orleans.Runtime.Messaging
{
    /// <summary>
    /// Sends messages to a connection.
    /// </summary>
    internal sealed class ConnectionMessageSender
    {
        internal static readonly object ContextItemKey = new object();
        private static readonly UnboundedChannelOptions ChannelOptions = new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        };

        private readonly Channel<Message> messages;
        private readonly ChannelWriter<Message> writer;
        private readonly IMessageCenter messageCenter;
        private readonly IMessageSerializer serializer;
        private ConnectionContext connection;

        public ConnectionMessageSender(
            IMessageCenter messageCenter,
            IMessageSerializer messageSerializer)
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

        public void Abort()
        {
            if (this.writer.TryComplete())
            {
                if (this.connection == null) this.RerouteMessages();
                else
                {
                    this.connection.Abort();
                }
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
            Exception error = default;
            PipeWriter output = default;
            try
            {
                output = this.connection.Transport.Output;
                var reader = this.messages.Reader;
                while (true)
                {
                    var moreTask = reader.WaitToReadAsync();
                    var more = moreTask.IsCompleted ? moreTask.GetAwaiter().GetResult() : await moreTask;
                    if (!more)
                    {
                        break;
                    }

                    Message message = default;
                    try
                    {
                        if (reader.TryRead(out message) && this.messageCenter.PrepareMessageForSend(message))
                        {
                            this.serializer.Write(ref output, message);
                        }
                    }
                    catch (Exception exception) when (message != default)
                    {
                        this.messageCenter.OnMessageSerializationFailure(message, exception);
                    }

                    var flushTask = output.FlushAsync();
                    var flushResult = flushTask.IsCompleted ? flushTask.GetAwaiter().GetResult() : await flushTask;
                    if (flushResult.IsCompleted || flushResult.IsCanceled) break;
                }
            }
            catch (Exception exception)
            {
                if (!(exception is ThreadAbortException) && !(exception is OperationCanceledException)) error = exception;
            }
            finally
            {
                if (error != null)
                {
                    output.Complete(error);
                    this.connection.Abort(
                        new ConnectionAbortedException($"Exception in {nameof(ConnectionMessageSender)}, see {nameof(Exception.InnerException)}.",
                        error));
                }
                else
                {
                    output.Complete();
                }

                this.Abort();
                this.RerouteMessages();
            }
        }

        private void RerouteMessages()
        {
            ThreadPool.UnsafeQueueUserWorkItem(
                _ =>
                {
                    var reader = this.messages.Reader;
                    while (reader.TryRead(out var message))
                    {
                        this.messageCenter.RetryMessage(message);
                    }
                },
                null);
        }

        private void RerouteMessage(Message message)
        {
            ThreadPool.UnsafeQueueUserWorkItem(
                msg => this.messageCenter.RetryMessage((Message)msg),
                message);
        }
    }
}
