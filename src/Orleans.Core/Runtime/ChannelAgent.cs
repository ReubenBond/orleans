using System.Threading;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime
{
    internal abstract class ChannelAgent<T> : AsynchAgent
    {
        private readonly Channel<T> channel;
        private int count;

        protected ChannelAgent(string nameSuffix, ExecutorService executorService, ILoggerFactory loggerFactory)
            : base(nameSuffix, executorService, loggerFactory)
        {
            this.channel = Channel.CreateUnbounded<T>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });
        }

        public int Count => this.count;

        public void QueueRequest(T request)
        {
            if (State != ThreadState.Running)
            {
                Log.LogWarning($"Invalid usage attempt of {Name} agent in {State.ToString()} state");
                return;
            }

            if (this.channel.Writer.TryWrite(request)) Interlocked.Increment(ref this.count);
        }

        public sealed override void OnStart()
        {
            this.executor.QueueWorkItem(_ =>
            { 
                var reader = this.channel.Reader;
                var ct = this.Cts.Token;
                while (true)
                {
                    while (reader.TryRead(out var item))
                    {
                        Interlocked.Decrement(ref this.count);
                        this.Process(item);
                    }

                    var waitTask = reader.WaitToReadAsync(ct);
                    var more = waitTask.AsTask().GetAwaiter().GetResult();
                    if (!more)
                    {
                        if (this.Log.IsEnabled(LogLevel.Debug)) this.Log.LogDebug("Processed all items in queue.");
                        return;
                    }
                }
            });
        }

        protected abstract void Process(T request);

        public override void Stop()
        {
            this.channel.Writer.TryComplete();
            base.Stop();
        }
    }
}
