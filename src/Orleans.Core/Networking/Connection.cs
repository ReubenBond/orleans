using System;
using System.IO.Pipelines;
using System.Net;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Networking.Shared;

namespace Orleans.Runtime.Messaging
{
    internal abstract class Connection
    {
        public static readonly Func<ConnectionContext, Task> OnConnectedDelegate = context => OnConnectedAsync(context);
        public static readonly object ContextItemKey = new object();
        private static readonly UnboundedChannelOptions OutgoingMessageChannelOptions = new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        };
        private readonly ConnectionDelegate middleware;
        private readonly IServiceProvider serviceProvider;
        private readonly Channel<Message> outgoingMessages;
        private readonly ChannelWriter<Message> outgoingMessageWriter;

        protected Connection(
            ConnectionContext connection,
            ConnectionDelegate middleware,
            IServiceProvider serviceProvider,
            INetworkingTrace trace)
        {
            this.Context = connection ?? throw new ArgumentNullException(nameof(connection));
            this.middleware = middleware;
            this.serviceProvider = serviceProvider;
            this.Log = trace;
            this.outgoingMessages = Channel.CreateUnbounded<Message>(OutgoingMessageChannelOptions);
            this.outgoingMessageWriter = this.outgoingMessages.Writer;

            // Set the connection on the connection context so that it can be retrieved by the middleware.
            this.Context.Features.Set<Connection>(this);
            this.IsValid = true;
        }

        public ConnectionContext Context { get; }
        protected INetworkingTrace Log { get; }
        protected abstract IMessageCenter MessageCenter { get; }
        protected CancellationToken ConnectionCloseRequested { get; private set; }
        public virtual EndPoint RemoteEndpoint => this.Context.RemoteEndPoint;
        public virtual EndPoint LocalEndpoint => this.Context.LocalEndPoint;
        public bool IsValid { get; private set; }

        public static void ConfigureBuilder(ConnectionBuilder builder) => builder.Run(OnConnectedDelegate);

        public static async Task OnConnectedAsync(ConnectionContext context)
        {
            var connection = context.Features.Get<Connection>();
            await connection.RunInternal().ConfigureAwait(false);
        }

        public async Task Run(CancellationToken cancellationToken)
        {
            this.ConnectionCloseRequested = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, this.Context.ConnectionClosed).Token;
            await this.middleware(this.Context).ConfigureAwait(false);
        }

        protected virtual async Task RunInternal()
        {
            Exception error = default;
            try
            {
                var outgoingTask = Task.Run(this.ProcessOutgoing);
                var incomingTask = Task.Run(this.ProcessIncoming);
                outgoingTask.Ignore();
                incomingTask.Ignore();
                await outgoingTask;
                await incomingTask;
            }
            catch (Exception exception)
            {
                error = exception;
            }
            finally
            {
                try
                {
                    this.CloseInternal();

                    if (error != null && !(error is ConnectionAbortedException))
                    {
                        this.Context.Abort(new ConnectionAbortedException(
                            $"Connection aborted. See {nameof(Exception.InnerException)}",
                            error));
                    }
                    else if (error == null)
                    {
                        this.Context.Abort();
                    }

                    await this.Context.DisposeAsync();
                }
                catch
                {
                }
                finally
                {
                    _ = this.RerouteMessages();
                }
            }
        }

        public void Close()
        {
            if (this.Log.IsEnabled(LogLevel.Information))
            {
                this.Log.LogInformation(
                    "Closing connection with remote endpoint {EndPoint}",
                    this.RemoteEndpoint);
            }

            this.CloseInternal();
        }

        /// <summary>
        /// Called immediately prior to transporting a message.
        /// </summary>
        /// <param name="msg"></param>
        /// <returns>Whether or not to continue transporting the message.</returns>
        protected abstract bool PrepareMessageForSend(Message msg);

        protected abstract void OnMessageSerializationFailure(Message msg, Exception exc);

        protected abstract void RetryMessage(Message msg, Exception ex = null);

        private void CloseInternal()
        {
            try
            {
                this.IsValid = false;

                // Try to gracefully stop the reader/writer loops.
                this.Context.Transport.Input.CancelPendingRead();
                this.Context.Transport.Output.CancelPendingFlush();
                this.outgoingMessageWriter.TryComplete();
            }
            catch
            {
            }
        }

        public void Abort(ConnectionAbortedException exception)
        {
            this.CloseInternal();
            this.Context.Abort(exception);
        }

        public void Send(Message message)
        {
            if (!this.outgoingMessageWriter.TryWrite(message))
            {
                this.RerouteMessage(message);
            }
        }

        protected abstract void OnReceivedMessage(Message message);

        protected abstract void OnReceiveMessageFail(Message message, Exception exception);

        private async Task ProcessIncoming()
        {
            PipeReader input = default;
            var serializer = this.serviceProvider.GetRequiredService<IMessageSerializer>();
            try
            {
                if (this.Log.IsEnabled(LogLevel.Debug))
                {
                    this.Log.LogDebug(
                        "Starting to process messages from remote endpoint {RemoteEndPoint} to local endpoint {LocalEndPoint}",
                        this.RemoteEndpoint,
                        this.LocalEndpoint);
                }

                input = this.Context.Transport.Input;
                var requiredBytes = 0;
                Message message = default;
                var cancellationToken = this.ConnectionCloseRequested;
                while (!cancellationToken.IsCancellationRequested)
                {
                    var readResultTask = input.ReadAsync();
                    var readResult = readResultTask.IsCompletedSuccessfully ? readResultTask.GetAwaiter().GetResult() : await readResultTask.ConfigureAwait(false);

                    var buffer = readResult.Buffer;

                    if (buffer.Length >= requiredBytes)
                    {
                        do
                        {
                            try
                            {
                                requiredBytes = serializer.TryRead(ref buffer, out message);
                                if (requiredBytes == 0)
                                {
                                    this.OnReceivedMessage(message);
                                    message = null;
                                }
                            }
                            catch (Exception exception)
                            {
                                this.Log.LogWarning(
                                    "Exception reading message {Message} from remote endpoint {RemoteEndPoint} to local endpoint {LocalEndPoint}: {Exception}",
                                    message,
                                    this.RemoteEndpoint,
                                    this.LocalEndpoint,
                                    exception);

                                this.OnReceiveMessageFail(message, exception);
                                break;
                            }
                        } while (requiredBytes == 0);
                    }

                    if (readResult.IsCanceled || readResult.IsCompleted) break;
                    input.AdvanceTo(buffer.Start, buffer.End);
                }
            }
            catch (ConnectionAbortedException) { }
            finally
            {
                input.Complete();

                if (this.Log.IsEnabled(LogLevel.Debug))
                {
                    this.Log.LogDebug(
                        "Completed processing messages from remote endpoint {EndPoint}",
                        this.RemoteEndpoint);
                }
            }
        }

        private async Task ProcessOutgoing()
        {
            PipeWriter output = default;
            var serializer = this.serviceProvider.GetRequiredService<IMessageSerializer>();
            try
            {
                output = this.Context.Transport.Output;
                var reader = this.outgoingMessages.Reader;
                if (this.Log.IsEnabled(LogLevel.Debug))
                {
                    this.Log.LogDebug(
                        "Starting to process messages from local endpoint {LocalEndPoint} to remote endpoint {RemoteEndPoint}",
                        this.LocalEndpoint,
                        this.RemoteEndpoint);
                }

                var cancellationToken = this.ConnectionCloseRequested;
                while (!cancellationToken.IsCancellationRequested)
                {
                    var moreTask = reader.WaitToReadAsync();
                    var more = moreTask.IsCompleted ? moreTask.GetAwaiter().GetResult() : await moreTask.ConfigureAwait(false);
                    if (!more)
                    {
                        break;
                    }

                    Message message = default;
                    try
                    {
                        while (reader.TryRead(out message) && this.PrepareMessageForSend(message))
                        {
                            serializer.Write(ref output, message);
                        }
                    }
                    catch (Exception exception) when (message != default)
                    {
                        this.Log.LogWarning(
                            "Exception writing message {Message} to remote endpoint {EndPoint}: {Exception}",
                            message,
                            this.RemoteEndpoint,
                            exception);
                        this.OnMessageSerializationFailure(message, exception);
                    }

                    var flushTask = output.FlushAsync();
                    var flushResult = flushTask.IsCompleted ? flushTask.GetAwaiter().GetResult() : await flushTask.ConfigureAwait(false);
                    if (flushResult.IsCompleted || flushResult.IsCanceled)
                    {
                        break;
                    }
                }
            }
            catch (ConnectionAbortedException) { }
            finally
            {
                output.Complete();

                if (this.Log.IsEnabled(LogLevel.Debug))
                {
                    this.Log.LogDebug(
                        "Completed processing messages to remote endpoint {EndPoint}",
                        this.RemoteEndpoint);
                }
            }
        }

        private async Task RerouteMessages()
        {
            var reader = this.outgoingMessages.Reader;
            var count = 0;
            while (reader.TryRead(out var message))
            {
                if (count == 0)
                {
                    if (this.Log.IsEnabled(LogLevel.Information))
                    {
                        this.Log.LogInformation(
                            "Rerouting messages from remote endpoint {EndPoint}",
                            this.RemoteEndpoint?.ToString() ?? "(never connected)");
                    }

                    // Wait some time before re-sending the first time around.
                    await Task.Delay(TimeSpan.FromSeconds(2));
                }

                ++count;
                this.RetryMessage(message);
            }

            if (count > 0 && this.Log.IsEnabled(LogLevel.Information))
            {
                this.Log.LogInformation(
                    "Rerouted {Count} messages from remote endpoint {EndPoint}",
                    count,
                    this.RemoteEndpoint?.ToString() ?? "(never connected)");
            }
        }

        private void RerouteMessage(Message message)
        {
            if (this.Log.IsEnabled(LogLevel.Debug))
            {
                this.Log.LogDebug(
                    "Rerouting message {Message} from remote endpoint {EndPoint}",
                    message,
                    this.RemoteEndpoint?.ToString() ?? "(never connected)");
            }

            ThreadPool.UnsafeQueueUserWorkItem(
                msg => this.RetryMessage((Message)msg),
                message);
        }
    }
}
