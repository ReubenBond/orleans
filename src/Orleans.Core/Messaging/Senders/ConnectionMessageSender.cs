using System;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;

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
        private readonly ILogger<ConnectionMessageSender> log;
        private ConnectionContext connection;

        public ConnectionMessageSender(
            IMessageCenter messageCenter,
            IMessageSerializer messageSerializer,
             ILogger<ConnectionMessageSender> log)
        {
            this.messages = Channel.CreateUnbounded<Message>(ChannelOptions);
            this.writer = this.messages.Writer;
            this.messageCenter = messageCenter;
            this.serializer = messageSerializer;
            this.log = log;
        }

        public Task Run(ConnectionContext connection)
        {
            if (this.connection != null) throw new InvalidOperationException($"{nameof(ConnectionContext)} already set on this instance.");
            this.connection = connection;
            return Task.Run(this.Process);
        }

        public void Abort()
        {
            if (this.log.IsEnabled(LogLevel.Information))
            {
                this.log.LogInformation(
                    "Aborting connection with remote endpoint {EndPoint} and id {ConnectionId}.",
                    this.connection?.GetRemoteEndPoint(),
                    this.connection?.ConnectionId);
            }

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
                if (this.log.IsEnabled(LogLevel.Information))
                {
                    this.log.LogInformation(
                        "Starting to process messages to remote endpoint {EndPoint} on connection {ConnectionId}.",
                        this.connection.GetRemoteEndPoint(),
                        this.connection.ConnectionId);
                }

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
                        while (reader.TryRead(out message) && this.messageCenter.PrepareMessageForSend(message))
                        {
                            this.serializer.Write(ref output, message);
                        }
                    }
                    catch (Exception exception) when (message != default)
                    {
                        this.log.LogWarning(
                            "Exception writing message {Message} to remote endpoint {EndPoint} on connection {ConnectionId}: {Exception}",
                            message,
                            this.connection?.GetRemoteEndPoint(),
                            this.connection.ConnectionId,
                            exception);
                        this.messageCenter.OnMessageSerializationFailure(message, exception);
                    }

                    var flushTask = output.FlushAsync();
                    var flushResult = flushTask.IsCompleted ? flushTask.GetAwaiter().GetResult() : await flushTask;
                    if (flushResult.IsCompleted || flushResult.IsCanceled)
                    {
                        break;
                    }
                }
            }
            catch (Exception exception)
            {
                this.log.LogWarning(
                    "Exception processing messages to remote endpoint {EndPoint} on connection {ConnectionId}: {Exception}",
                    this.connection.GetRemoteEndPoint(),
                    this.connection.ConnectionId,
                    exception);

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

                if (this.log.IsEnabled(LogLevel.Information))
                {
                    this.log.LogInformation(
                        "Completed processing messages to remote endpoint {EndPoint} on connection {ConnectionId}",
                        this.connection.GetRemoteEndPoint(),
                        this.connection.ConnectionId);
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
                    if (this.log.IsEnabled(LogLevel.Information))
                    {
                        this.log.LogInformation(
                            "Rerouting messages from remote endpoint {EndPoint} on connection {ConnectionId}",
                            this.connection?.GetRemoteEndPoint()?.ToString() ?? "(never connected)",
                            this.connection?.ConnectionId ?? "none");
                    }

                    var reader = this.messages.Reader;
                    var count = 0;
                    while (reader.TryRead(out var message))
                    {
                        ++count;
                        this.messageCenter.RetryMessage(message);
                    }

                    if (this.log.IsEnabled(LogLevel.Information))
                    {
                        this.log.LogInformation(
                            "Rerouted {Count} messages from remote endpoint {EndPoint} on connection {ConnectionId}",
                            count,
                            this.connection?.GetRemoteEndPoint()?.ToString() ?? "(never connected)",
                            this.connection?.ConnectionId ?? "none");
                    }
                },
                null);
        }

        private void RerouteMessage(Message message)
        {
            // !!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!
            // !!!!!!!! CHANGE TO DEBUG LEVEL !!!!!!!!!
            // !!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!
            if (this.log.IsEnabled(LogLevel.Information))
            {
                this.log.LogInformation(
                    "Rerouting message {Message} from remote endpoint {EndPoint} on connection {ConnectionId}",
                    message,
                    this.connection?.GetRemoteEndPoint()?.ToString() ?? "(never connected)",
                    this.connection?.ConnectionId ?? "none");
            }

            ThreadPool.UnsafeQueueUserWorkItem(
                msg => this.messageCenter.RetryMessage((Message)msg),
                message);
        }
    }
}
