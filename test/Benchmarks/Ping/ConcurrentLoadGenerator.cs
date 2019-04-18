using System;
using System.Threading.Tasks;
using System.Threading.Channels;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Benchmarks.Ping
{
    public sealed class ConcurrentLoadGenerator<TState>
    {
        private static readonly double TimestampToTicks = TimeSpan.TicksPerSecond / (double)Stopwatch.Frequency;

        private class WorkBlock
        {
            public long StartTimestamp { get; set; }
            public long EndTimestamp { get; set; }
            public TimeSpan Elapsed => TimeSpan.FromTicks((long)((this.EndTimestamp - this.StartTimestamp) * TimestampToTicks));
            public int Remaining { get; set; }
            public int Successes { get; set; }
            public int Failures { get; set; }
            public int Completed => this.Successes + this.Failures;
            public double RequestsPerSecond => this.Completed / this.Elapsed.TotalSeconds;

            public void RecordStart() => this.StartTimestamp = Stopwatch.GetTimestamp();

            public void RecordSuccess()
            {
                ++this.Successes;
                if (--this.Remaining == 0) this.EndTimestamp = Stopwatch.GetTimestamp();
            }

            public void RecordFailure()
            {
                ++this.Failures;
                if (--this.Remaining == 0) this.EndTimestamp = Stopwatch.GetTimestamp();
            }
        }

        /*
        private class Superblock
        {
            public List<WorkBlock> Blocks { get; } = new List<WorkBlock>();

            public double AverageRequestsPerSecond
            {
                get
                {
                    var commonStartTimestamp = this.Blocks.Max(b => b.StartTimestamp);
                    var commonEndTimestamp = this.Blocks.Min(b => b.EndTimestamp);
                    long commonTimestampDelta = commonEndTimestamp - commonStartTimestamp;
                    double requestsInCommonTime = 0;

                    foreach (var block in this.Blocks)
                    {
                        var delta = block.EndTimestamp - block.StartTimestamp;
                        var deltaFraction = commonTimestampDelta / (double)delta;
                        requestsInCommonTime += deltaFraction * block.Completed;
                    }

                    return requestsInCommonTime / TimeSpan.FromTicks((long)(commonTimestampDelta * TimestampToTicks)).TotalSeconds;
                }
            }
        }
        */

        private Channel<WorkBlock> completedBlocks;
        private readonly Func<TState, Task> issueRequest;
        private readonly Func<int, TState> getStateForWorker;
        private readonly Task[] tasks;
        private readonly TState[] states;
        private readonly int numWorkers;
        private readonly int blocksPerWorker;
        private readonly int requestsPerBlock;

        public ConcurrentLoadGenerator(int maxConcurrency, int blocksPerWorker, int requestsPerBlock, Func<TState, Task> issueRequest, Func<int, TState> getStateForWorker)
        {
            this.numWorkers = maxConcurrency;
            this.blocksPerWorker = blocksPerWorker;
            this.requestsPerBlock = requestsPerBlock;
            this.issueRequest = issueRequest;
            this.getStateForWorker = getStateForWorker;
            this.tasks = new Task[maxConcurrency];
            this.states = new TState[maxConcurrency];
        }

        public async Task Warmup()
        {
            this.ResetBetweenRuns();
            var completedBlockReader = this.completedBlocks.Reader;

            for (var i = 0; i < this.numWorkers; i++)
            {
                this.states[i] = getStateForWorker(i);
                this.tasks[i] = this.RunWorker(this.states[i], this.requestsPerBlock, 3);
            }

            // Wait for warmup to complete.
            await Task.WhenAll(this.tasks);

            // Ignore warmup blocks.
            while (completedBlockReader.TryRead(out _)) ;
        }

        private void ResetBetweenRuns()
        {
            this.completedBlocks = Channel.CreateUnbounded<WorkBlock>(
                new UnboundedChannelOptions
                {
                    SingleReader = true,
                    SingleWriter = false,
                    AllowSynchronousContinuations = false
                });
        }

        public async Task Run()
        {
            this.ResetBetweenRuns();
            var completedBlockReader = this.completedBlocks.Reader;

            // Start the run.
            var stopwatch = ValueStopwatch.StartNew();
            for (var i = 0; i < this.numWorkers; i++)
            {
                this.tasks[i] = this.RunWorker(this.states[i], this.requestsPerBlock, this.blocksPerWorker);
            }

            var completion = Task.WhenAll(this.tasks);
            _ = Task.Run(async () => { try { await completion; } catch { } finally { this.completedBlocks.Writer.Complete(); } });
            var blocks = new List<WorkBlock>(this.numWorkers * this.blocksPerWorker);
            var reportInterval = TimeSpan.FromSeconds(5);
            var lastReportTime = DateTime.UtcNow;
            var lastReportBlockCount = 0;
            var blocksPerReport = this.numWorkers * this.blocksPerWorker / 10;
            var nextReportBlockCount = blocksPerReport;
            while (!completion.IsCompleted)
            {
                var more = await completedBlockReader.WaitToReadAsync();
                if (!more) break;
                while (completedBlockReader.TryRead(out var block))
                {
                    blocks.Add(block);
                }

                var now = DateTime.UtcNow;
                if (blocks.Count >= nextReportBlockCount)
                {
                    nextReportBlockCount += blocksPerReport;
                    var latestReport = PrintReport(lastReportBlockCount);
                    var totalReport = PrintReport(0);
                    Console.Write($"{latestReport}".PadRight(40));
                    Console.WriteLine($"Total: {totalReport}");
                    lastReportBlockCount = blocks.Count;
                    lastReportTime = now;
                }
            }

            stopwatch.Stop();
            Console.WriteLine("Total: " + PrintReport(0));

            string PrintReport(int statingBlockIndex)
            {
                if (blocks.Count == 0) return "No blocks completed";
                var successes = 0;
                var failures = 0;
                long completed = 0;
                var reportBlocks = 0;
                long minStartTime = long.MaxValue;
                long maxEndTime = long.MinValue;
                for (var i = statingBlockIndex; i < blocks.Count; i++)
                {
                    var b = blocks[i];
                    ++reportBlocks;
                    successes += b.Successes;
                    failures += b.Failures;
                    completed += b.Completed;
                    if (b.StartTimestamp < minStartTime) minStartTime = b.StartTimestamp;
                    if (b.EndTimestamp > maxEndTime) maxEndTime = b.EndTimestamp;
                }

                var totalTime = TimeSpan.FromTicks((long)((maxEndTime - minStartTime) * TimestampToTicks));
                var ratePerSecond = (long)(completed / totalTime.TotalSeconds);
                var failureString = failures == 0 ? string.Empty : $" Failed: {failures}";
                return $"{ratePerSecond,6}/s ({successes,8} reqs in {totalTime.TotalSeconds,6:0.000}s){failureString}";
            }
        }

        private async Task RunWorker(TState state, int requestsPerBlock, int numBlocks)
        {
            var completedBlockWriter = this.completedBlocks.Writer;
            while (numBlocks > 0)
            {
                var workBlock = new WorkBlock() { Remaining = requestsPerBlock };
                workBlock.RecordStart();
                while (workBlock.Remaining > 0)
                {
                    Exception error = default;
                    try
                    {
                        await this.issueRequest(state).ConfigureAwait(false);

                    }
                    catch (Exception exception)
                    {
                        error = exception;
                    }
                    finally
                    {
                        if (error != null)
                        {
                            workBlock.RecordFailure();
                        }
                        else
                        {
                            workBlock.RecordSuccess();
                        }
                    }
                }

                await completedBlockWriter.WriteAsync(workBlock);
                --numBlocks;
            }
        }
    }
}