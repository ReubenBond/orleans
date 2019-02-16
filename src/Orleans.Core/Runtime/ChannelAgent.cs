using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime
{
    internal abstract class ChannelAgent<T> : TaskSchedulerAgent
    {
        private readonly Channel<T> channel;
        private int count;

        protected ChannelAgent(string nameSuffix, ILoggerFactory loggerFactory)
            : base(nameSuffix, loggerFactory)
        {
            this.channel = Channel.CreateUnbounded<T>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = true
            });
        }

        public int Count => this.count;

        public void QueueRequest(T request)
        {
            if (State != AgentState.Running)
            {
                Log.LogWarning($"Invalid usage attempt of {Name} agent in {State.ToString()} state");
                return;
            }

            if (this.channel.Writer.TryWrite(request)) Interlocked.Increment(ref this.count);
        }

        protected override async Task Run()
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
                var more = waitTask.IsCompleted ? waitTask.GetAwaiter().GetResult() : await waitTask.ConfigureAwait(false);
                if (!more)
                {
                    if (this.Log.IsEnabled(LogLevel.Debug)) this.Log.LogDebug("Processed all items in queue.");
                    return;
                }
            }
        }

        protected abstract void Process(T request);

        public override void Stop()
        {
            this.channel.Writer.TryComplete();
            base.Stop();
        }
    }
}
