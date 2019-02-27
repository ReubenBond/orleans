using System;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;

namespace Orleans.Runtime.Messaging
{
    internal abstract class ConnectionMessageReceiver
    {
        private readonly IMessageSerializer serializer;

        protected ConnectionMessageReceiver(ConnectionContext connection, IMessageSerializer serializer)
        {
            this.Connection = connection;
            this.serializer = serializer;
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
                input = this.Connection.Transport.Input;
                var requiredBytes = 0;
                Message message = default;
                while (true)
                {
                    var readResultTask = input.ReadAsync();
                    var readResult = readResultTask.IsCompletedSuccessfully ? readResultTask.GetAwaiter().GetResult() : await readResultTask;
                    
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
                            catch (Exception readException)
                            {
                                this.OnReceiveMessageFail(message, readException);
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
            }
        }
    }
}
