using System;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime.Messaging
{
    internal abstract class ConnectionMessageReceiver
    {
        private readonly IMessageSerializer serializer;
        private readonly ILogger<ConnectionMessageReceiver> log;

        protected ConnectionMessageReceiver(
            ConnectionContext connection,
            IMessageSerializer serializer,
            ILogger<ConnectionMessageReceiver> log)
        {
            this.Connection = connection;
            this.serializer = serializer;
            this.log = log;
        }

        protected ConnectionContext Connection { get; }

        public Task Run() => Task.Run(this.Process);

        protected abstract void OnReceivedMessage(Message message);

        protected abstract void OnReceiveMessageFail(Message message, Exception exception);

        private async Task Process()
        {
            Exception error = default;
            PipeReader input = default;
            try
            {
                if (this.log.IsEnabled(LogLevel.Information))
                {
                    this.log.LogInformation(
                        "Starting to process messages from remote endpoint {RemoteEndPoint} to local endpoint {LocalEndPoint} on connection {ConnectionId}.",
                        this.Connection.GetRemoteEndPoint(),
                        this.Connection.GetLocalEndPoint(),
                        this.Connection.ConnectionId);
                }

                input = this.Connection.Transport.Input;
                var requiredBytes = 0;
                Message message = default;
                while (true)
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
                                requiredBytes = this.serializer.TryRead(ref buffer, out message);
                                if (requiredBytes == 0)
                                {
                                    this.OnReceivedMessage(message);
                                }
                            }
                            catch (Exception exception)
                            {
                                this.log.LogWarning(
                                    "Exception reading message {Message} from remote endpoint {RemoteEndPoint} to local endpoint {LocalEndPoint} on connection {ConnectionId}: {Exception}",
                                    message,
                                    this.Connection.GetRemoteEndPoint(),
                                    this.Connection.GetLocalEndPoint(),
                                    this.Connection.ConnectionId,
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
            catch (Exception exception)
            {
                this.log.LogWarning(
                    "Exception processing messages from remote endpoint {EndPoint} on connection {ConnectionId}: {Exception}",
                    this.Connection.GetRemoteEndPoint(),
                    this.Connection.ConnectionId,
                    exception);

                if (!(exception is ThreadAbortException) && !(exception is OperationCanceledException)) error = exception;
            }
            finally
            {
                if (error != null)
                {
                    input.Complete(error);
                    this.Connection.Abort(new ConnectionAbortedException(
                        $"Exception in {nameof(ConnectionMessageReceiver)}, see {nameof(Exception.InnerException)}.",
                        error));
                }
                else
                {
                    input.Complete();
                    this.Connection.Abort();
                }

                if (this.log.IsEnabled(LogLevel.Information))
                {
                    this.log.LogInformation(
                        "Completed processing messages from remote endpoint {EndPoint} on connection {ConnectionId}",
                        this.Connection?.GetRemoteEndPoint(),
                        this.Connection.ConnectionId);
                }
            }
        }
    }
}
