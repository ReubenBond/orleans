using System;
using System.Net;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Messaging;
using Orleans.Serialization.Invocation;
using Orleans.Serialization.Buffers;
using Orleans.Connections.Transport;
using Orleans.Connections;
using Orleans.Runtime.Internal;
using System.Collections.Generic;

namespace Orleans.Runtime.Messaging
{
    internal abstract class Connection
    {
        private static readonly UnboundedChannelOptions OutgoingMessageChannelOptions = new()
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        };

        private readonly ConnectionCommon _shared;
        //private readonly Channel<MessageWriteRequest> _outboundMessages;
        //private readonly ChannelWriter<MessageWriteRequest> _outboundMessageWriter;
        private readonly TaskCompletionSource<int> _transportConnectionClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<int> _initializationTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly string _id;
        private readonly MessageTransport _transport;
        private Task _processIncomingTask;
        private Task _processOutgoingTask;
        private Task _closeTask;

        protected Connection(
            MessageTransport transport,
            ConnectionCommon shared)
        {
            _id = CorrelationIdGenerator.GetNextId();
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _shared = shared;
            //_outboundMessages = Channel.CreateUnbounded<MessageWriteRequest>(OutgoingMessageChannelOptions);
            //_outboundMessageWriter = _outboundMessages.Writer;

            _transport.Closed.Register(static state => ((Connection)state).OnTransportConnectionClosed(), this);

            RemoteEndpoint = NormalizeEndpoint(Context.RemoteEndpoint);
            LocalEndpoint = NormalizeEndpoint(Context.LocalEndpoint);
        }

        public string ConnectionId => _id;
        public virtual EndPoint RemoteEndpoint { get; }
        public virtual EndPoint LocalEndpoint { get; }
        protected MessageTransport Context => _transport;
        protected ConnectionTrace Log => _shared.ConnectionTrace;
        protected MessagingTrace MessagingTrace => _shared.MessagingTrace;
        protected abstract ConnectionDirection ConnectionDirection { get; }
        protected MessageFactory MessageFactory => _shared.MessageFactory;
        protected abstract IMessageCenter MessageCenter { get; }

        public bool IsValid => _closeTask is null;

        public Task Initialized => _initializationTcs.Task;

        /// <summary>
        /// Start processing this connection.
        /// </summary>
        /// <returns>A <see cref="Task"/> which completes when the connection terminates and has completed processing.</returns>
        public async Task RunAsync()
        {
            Exception error = default;
            try
            {
                await RunAsyncCore();
            }
            catch (Exception exception)
            {
                error = exception;
            }
            finally
            {
                await CloseAsync(error);
            }
        }

        protected virtual async Task RunAsyncCore()
        {
            using (new ExecutionContextSuppressor())
            {
                _processIncomingTask = ProcessIncoming();
                _processOutgoingTask = Task.CompletedTask;// ProcessOutgoing();
            }

            _initializationTcs.TrySetResult(0);
            await Task.WhenAll(_processIncomingTask, _processOutgoingTask);
        }

        /// <summary>
        /// Called immediately prior to transporting a message.
        /// </summary>
        /// <param name="msg"></param>
        /// <returns>Whether or not to continue transporting the message.</returns>
        protected abstract bool PrepareMessageForSend(Message msg);

        protected abstract void RetryMessage(Message msg, Exception ex = null);

        public Task CloseAsync(Exception exception)
        {
            StartClosing(exception);
            return _closeTask;
        }

        private void OnTransportConnectionClosed()
        {
            StartClosing(new ConnectionAbortedException("Underlying connection closed"));
            _transportConnectionClosed.SetResult(0);
        }

        private void StartClosing(Exception exception)
        {
            if (_closeTask is not null)
            {
                return;
            }

            var task = new Task<Task>(CloseAsync);
            if (Interlocked.CompareExchange(ref _closeTask, task.Unwrap(), null) is not null)
            {
                return;
            }

            _initializationTcs.TrySetException(exception ?? new ConnectionAbortedException("Connection initialization failed"));

            if (Log.IsEnabled(LogLevel.Information))
            {
                Log.LogInformation(
                    exception,
                    "Closing connection {Connection}",
                    this);
            }

            task.Start(TaskScheduler.Default);
        }

        /// <summary>
        /// Close the connection. This method should only be called by <see cref="StartClosing(Exception)"/>.
        /// </summary>
        private async Task CloseAsync()
        {
            NetworkingInstruments.OnClosedSocket(ConnectionDirection);

            // Signal the outgoing message processor to exit gracefully.
            //_outboundMessageWriter.TryComplete();

            // Close the underlying message transport
            await _transport.CloseAsync(new ConnectionAbortedException());

            // Try to gracefully stop the reader/writer loops, if they are running.
            if (_processIncomingTask is { IsCompleted: false } incoming)
            {
                try
                {
                    await incoming;
                }
                catch (Exception processIncomingException)
                {
                    // Swallow any exceptions here.
                    Log.LogWarning(processIncomingException, "Exception processing incoming messages on connection {Connection}", this);
                }
            }

            if (_processOutgoingTask is { IsCompleted: false } outgoing)
            {
                try
                {
                    await outgoing;
                }
                catch (Exception processOutgoingException)
                {
                    // Swallow any exceptions here.
                    Log.LogWarning(processOutgoingException, "Exception processing outgoing messages on connection {Connection}", this);
                }
            }

            try
            {
                await _transport.DisposeAsync();
            }
            catch (Exception abortException)
            {
                // Swallow any exceptions here.
                Log.LogWarning(abortException, "Exception terminating connection {Connection}", this);
            }

            // Reroute enqueued messages.
            /*
            var i = 0;
            while (_outboundMessages.Reader.TryRead(out var sendWorkItem))
            {
                if (i == 0 && Log.IsEnabled(LogLevel.Information))
                {
                    Log.LogInformation(
                        "Rerouting messages for remote endpoint {EndPoint}",
                        RemoteEndpoint?.ToString() ?? "(never connected)");
                }

                ++i;

                var message = sendWorkItem.Message;
                sendWorkItem.Reset();
                RetryMessage(message);
            }

            if (i > 0 && Log.IsEnabled(LogLevel.Information))
            {
                Log.LogInformation(
                    "Rerouted {Count} messages for remote endpoint {EndPoint}",
                    i,
                    RemoteEndpoint?.ToString() ?? "(never connected)");
            }
            */
        }

        public virtual void Send(Message message)
        {
            if (!PrepareMessageForSend(message))
            {
                return;
            }

            var handler = _shared.MessageHandlerShared.GetSendMessageHandler();
            try
            {
                handler.Initialize(message);
            }
            catch (Exception exception)
            {
                handler.Reset();
                OnMessageSerializationFailure(message, exception);
                return;
            }

            if (!_transport.WriteAsync(handler))
            {
                handler.Reset();
                RerouteMessage(message);
                return;
            }
        }

        public override string ToString() => $"[Local: {LocalEndpoint}, Remote: {RemoteEndpoint}, ConnectionId: {_id}]";

        internal protected abstract void OnReceivedMessage(Message message);

        protected abstract void OnSendMessageFailure(Message message, string error);

        private async Task ProcessIncoming()
        {
            await Task.Yield();

            Exception error = default;
            MessageReadRequest readRequest = RentHandler();
            try
            {
                while (true)
                {
                    if (!_transport.ReadAsync(readRequest))
                    {
                        // Connection closed.
                        error = new ConnectionAbortedException();
                        break;
                    }

                    await readRequest.Completed;

HandleCompletedRequest:
                    if (readRequest.UnconsumedLength > 0)
                    {
                        // Copy the excess data for the next request.
                        var excessBuffer = new PooledBuffer();
                        readRequest.Unconsumed.CopyTo(ref excessBuffer);

                        // Dispatch the current request.
                        ThreadPool.UnsafeQueueUserWorkItem(readRequest, preferLocal: true);

                        // Assign the excess data to the next request.
                        readRequest = RentHandler();
                        readRequest.SetBuffer(in excessBuffer);
                        if (readRequest.OnProgress(0))
                        {
                            goto HandleCompletedRequest;
                        }
                    }
                    else
                    {
                        // Dispatch the request.
                        ThreadPool.UnsafeQueueUserWorkItem(readRequest, preferLocal: true);
                        readRequest = RentHandler();
                    }
                }
            }
            catch (Exception exception)
            {
                error = exception;
            }
            finally
            {
                if (error is { })
                {
                    Log.LogWarning(
                        error,
                        "Exception while processing messages from remote endpoint {EndPoint}",
                        RemoteEndpoint);
                }

                StartClosing(error);
            }

            MessageReadRequest RentHandler()
            {
                MessageReadRequest readRequest = _shared.MessageHandlerShared.GetReceiveMessageHandler();
                readRequest.SetConnection(this);
                return readRequest;
            }
        }

        /*
        private async Task ProcessOutgoing()
        {
            await Task.Yield();

            var outboundQueue = _outboundMessages.Reader;
            Exception error = default;
            Queue<MessageWriteRequest> processingRequests = new();
            try
            {
                while (true)
                {
                    var more = await outboundQueue.WaitToReadAsync();
                    while (processingRequests.TryPeek(out var request) && request.Completed.IsCompleted)
                    {
                        _ = processingRequests.Dequeue();
                        await request.Completed;
                        request.Reset();
                    }

                    if (!more)
                    {
                        break;
                    }

                    MessageWriteRequest message = default;
                    while (outboundQueue.TryRead(out message))
                    {
                        processingRequests.Enqueue(message);
                        if (!_transport.WriteAsync(message))
                        {
                            error = new ConnectionAbortedException();
                            break;
                        }
                    }

                    if (error is not null)
                    {
                        break;
                    }
                }
            }
            catch (Exception exception)
            {
                if (IsValid)
                {
                    Log.LogWarning(
                        exception,
                        "Exception while processing messages to remote endpoint {EndPoint}",
                        RemoteEndpoint);
                }

                error = exception;
            }
            finally
            {
                StartClosing(error);
            }
        }
        */

        private void RerouteMessage(Message message)
        {
            if (Log.IsEnabled(LogLevel.Information))
            {
                Log.LogInformation(
                    "Rerouting message {Message} from remote endpoint {EndPoint}",
                    message,
                    RemoteEndpoint?.ToString() ?? "(never connected)");
            }

            ThreadPool.UnsafeQueueUserWorkItem(state =>
            {
                var (t, msg) = ((Connection, Message))state;
                t.RetryMessage(msg);
            }, (this, message));
        }

        private static EndPoint NormalizeEndpoint(EndPoint endpoint)
        {
            if (endpoint is not IPEndPoint ep) return endpoint;

            // Normalize endpoints
            if (ep.Address.IsIPv4MappedToIPv6)
            {
                return new IPEndPoint(ep.Address.MapToIPv4(), ep.Port);
            }

            return ep;
        }

        private void OnMessageSerializationFailure(Message message, Exception exception)
        {
            // we only get here if we failed to serialize the msg (or any other catastrophic failure).
            // Request msg fails to serialize on the sender, so we just enqueue a rejection msg.
            // Response msg fails to serialize on the responding silo, so we try to send an error response back.
            Log.LogWarning(
                (int)ErrorCode.Messaging_SerializationError,
                exception,
                "Unexpected error serializing message {Message}",
                message);

            MessagingInstruments.OnFailedSentMessage(message);

            if (message.Direction == Message.Directions.Request)
            {
                var response = MessageFactory.CreateResponseMessage(message);
                response.Result = Message.ResponseTypes.Error;
                response.BodyObject = Response.FromException(exception);

                MessageCenter.DispatchLocalMessage(response);
            }
            else if (message.Direction == Message.Directions.Response && message.RetryCount < MessagingOptions.DEFAULT_MAX_MESSAGE_SEND_RETRIES)
            {
                // If we failed sending an original response, turn the response body into an error and reply with it.
                // unless we have already tried sending the response multiple times.
                message.Result = Message.ResponseTypes.Error;
                message.BodyObject = Response.FromException(exception);
                ++message.RetryCount;

                Send(message);
            }
            else
            {
                Log.LogWarning(
                    (int)ErrorCode.Messaging_OutgoingMS_DroppingMessage,
                    exception,
                    "Dropping message which failed during serialization: {Message}",
                    message);

                MessagingInstruments.OnDroppedSentMessage(message);
            }
        }
    }
}
