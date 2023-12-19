using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Net;
using System.Numerics;
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

namespace Orleans.Runtime.Messaging
{
    internal abstract class Connection
    {
        private static readonly Counter<long> OverReadBytes;
        private static readonly Counter<int> NumOverReads;

        private readonly ConnectionCommon _shared;
        private readonly TaskCompletionSource<int> _transportConnectionClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<int> _initializationTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly string _id;
        private readonly MessageTransport _transport;
        private Task _processIncomingTask;
        private Task _closeTask;

        static Connection()
        {
            OverReadBytes = Instruments.Meter.CreateCounter<long>("orleans-networking-over-read-bytes");
            NumOverReads = Instruments.Meter.CreateCounter<int>("orleans-networking-over-read-count");
        }

        protected Connection(
            MessageTransport transport,
            ConnectionCommon shared)
        {
            _id = CorrelationIdGenerator.GetNextId();
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _shared = shared;
            _transport.Closed.Register(static state => ((Connection)state).OnTransportConnectionClosed(), this);
        }

        public string ConnectionId => _id;
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

        protected virtual Task RunAsyncCore()
        {
            using (new ExecutionContextSuppressor())
            {
                _processIncomingTask = ProcessIncoming();
            }

            _initializationTcs.TrySetResult(0);
            return _processIncomingTask;
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
            StartClosing(new ConnectionClosedException("Underlying connection closed"));
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

            if (!_initializationTcs.Task.IsCompleted)
            {
                _initializationTcs.TrySetException(exception ?? new ConnectionAbortedException("Connection initialization failed"));
            }

            if (Log.IsEnabled(LogLevel.Information))
            {
                Log.LogInformation(
                    exception is not ConnectionClosedException ? exception : null,
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

            // Close the underlying message transport
            await _transport.CloseAsync(new ConnectionClosedException());

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

            try
            {
                await _transport.DisposeAsync();
            }
            catch (Exception abortException)
            {
                // Swallow any exceptions here.
                Log.LogWarning(abortException, "Exception terminating connection {Connection}", this);
            }
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
                StartClosing(new ConnectionClosedException());
                RerouteMessage(message);
                handler.Reset();
                return;
            }
        }

        public override string ToString() => $"{nameof(Connection)}(Id: {_id}, Transport: {_transport})";

        internal protected abstract void OnReceivedMessage(Message message);

        protected abstract void OnSendMessageFailure(Message message, string error);

        private readonly TaskCompletionSource _startedClosing = new (TaskCreationOptions.RunContinuationsAsynchronously);
        public void OnReadCompleted(Exception error)
        {
            if (error is not null)
            {
                StartClosing(error);
                _startedClosing.TrySetResult();
                return;
            }

            EnqueueRead();
        }

        public void EnqueueRead()
        {
            var request = _shared.MessageHandlerShared.GetReceiveMessageHandler();
            request.SetConnection(this);
            if (!_transport.ReadAsync(request))
            {
                // Connection closed.
                request.Reset();
                StartClosing(new ConnectionClosedException());
                _startedClosing.TrySetResult();
            }
        }

        private async Task ProcessIncoming()
        {
            await Task.Yield();
            EnqueueRead();
            await _startedClosing.Task.ConfigureAwait(false);
        }

        private void RerouteMessage(Message message)
        {
            if (Log.IsEnabled(LogLevel.Information))
            {
                Log.LogInformation(
                    "Rerouting message {Message} from connection {Connection}",
                    message,
                    this);
            }

            ThreadPool.UnsafeQueueUserWorkItem(state =>
            {
                var (t, msg) = ((Connection, Message))state;
                t.RetryMessage(msg);
            }, (this, message));
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
