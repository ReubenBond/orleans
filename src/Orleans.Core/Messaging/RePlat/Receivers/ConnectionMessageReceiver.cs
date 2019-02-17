using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;

namespace Orleans.Runtime.Messaging
{
    internal abstract class ConnectionMessageReceiver
    {
        private readonly ConnectionContext connection;
        private readonly IMessageSerializer serializer;
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();

        protected ConnectionMessageReceiver(ConnectionContext connection, IMessageSerializer serializer)
        {
            this.connection = connection;
            this.serializer = serializer;
        }

        public void Abort()
        {
            ThreadPool.UnsafeQueueUserWorkItem(cts => ((CancellationTokenSource)cts).Cancel(), this.cancellation);
        }

        public Task Run() => Task.Run(this.Process);

        protected abstract void OnReceivedMessage(Message message);

        private async Task Process()
        {
            var input = this.connection.Transport.Input;
            var error = default(Exception);
            try
            {
                var requiredBytes = 0;
                while (!this.cancellation.IsCancellationRequested)
                {
                    var readResultTask = input.ReadAsync(this.cancellation.Token);
                    var readResult = readResultTask.IsCompletedSuccessfully ? readResultTask.GetAwaiter().GetResult() : await readResultTask.ConfigureAwait(false);
                    
                    var buffer = readResult.Buffer;
                    
                    if (buffer.Length >= requiredBytes)
                    {
                        do
                        {
                            requiredBytes = this.serializer.TryRead(ref buffer, out var message);
                            if (requiredBytes == 0)
                            {
                                this.OnReceivedMessage(message);
                            }
                        } while (requiredBytes == 0);
                    }

                    if (readResult.IsCanceled || readResult.IsCompleted) break;
                    input.AdvanceTo(buffer.Start, buffer.End);
                }
            }
            catch (Exception exception)
            {
                error = exception;
            }
            finally
            {
                this.Abort();

                if (error != null)
                {
                    this.connection.Abort(new ConnectionAbortedException($"Exception in {nameof(ConnectionMessageReceiver)}, see {nameof(Exception.InnerException)}.", error));
                }
                else
                {
                    this.connection.Abort();
                }
            }
        }
    }
}
