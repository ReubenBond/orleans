using System;
using System.Threading.Tasks;
using System.Threading.Channels;
using System.Collections.Generic;

namespace Benchmarks.Ping
{
    public sealed class ConcurrentLoadGenerator<TState>
    {
        private class WorkBlock
        {
            public ValueStopwatch Stopwatch { get; set; }
            public int Remaining { get; set; }
            public int Successes { get; set; }
            public int Failures { get; set; }
            public int Completed => this.Successes + this.Failures;

            public void RecordSuccess()
            {
                ++this.Successes;
                if (--this.Remaining == 0) this.Stopwatch.Stop();
            }

            public void RecordFailure()
            {
                ++this.Failures;
                if (--this.Remaining == 0) this.Stopwatch.Stop();
            }
        }

        private readonly Channel<WorkBlock> completedBlocks;
        private readonly Func<TState, Task> issueRequest;
        private readonly Func<int, TState> getStateForWorker;
        private readonly Task[] tasks;
        private readonly int numWorkers;
        private readonly int blocksPerWorker;
        private readonly int requestsPerBlock;

        public ConcurrentLoadGenerator(int maxConcurrency, int blocksPerWorker, int requestsPerBlock, Func<TState, Task> issueRequest, Func<int, TState> getStateForWorker)
        {
            this.completedBlocks = Channel.CreateUnbounded<WorkBlock>(
                new UnboundedChannelOptions
                {
                    SingleReader = true,
                    SingleWriter = false,
                    AllowSynchronousContinuations = false
                });
            this.numWorkers = maxConcurrency;
            this.blocksPerWorker = blocksPerWorker;
            this.requestsPerBlock = requestsPerBlock;
            this.issueRequest = issueRequest;
            this.getStateForWorker = getStateForWorker;
            this.tasks = new Task[maxConcurrency];
        }

        public async Task Run()
        {
            var completedBlockReader = this.completedBlocks.Reader;

            var stopwatch = ValueStopwatch.StartNew();
            for (var i = 0; i < this.numWorkers; i++)
            {
                var state = getStateForWorker(i);
                this.tasks[i] = this.RunWorker(state, this.requestsPerBlock, this.blocksPerWorker);
            }

            var completion = Task.WhenAll(this.tasks);
            _ = Task.Run(async () => { try { await completion; } catch { } finally { this.completedBlocks.Writer.Complete(); } });
            var blocks = new List<WorkBlock>(this.numWorkers * this.blocksPerWorker * this.requestsPerBlock);
            var reportInterval = TimeSpan.FromSeconds(5);
            var lastReportTime = DateTime.UtcNow;
            var lastReportBlockCount = 0;
            while (!completion.IsCompleted)
            {
                var more = await completedBlockReader.WaitToReadAsync();
                if (!more) break;
                while (completedBlockReader.TryRead(out var block))
                {
                    blocks.Add(block);
                }

                var now = DateTime.UtcNow;
                if (now - lastReportTime > reportInterval)
                {
                    PrintReport(lastReportBlockCount, now - lastReportTime);
                    lastReportBlockCount = blocks.Count;
                    lastReportTime = now;
                }
            }

            stopwatch.Stop();
            Console.WriteLine($"Completed in {stopwatch.Elapsed} ({stopwatch.Elapsed.TotalSeconds} seconds)");
            PrintReport(0, stopwatch.Elapsed);

            void PrintReport(int statingBlockIndex, TimeSpan duration)
            {
                var successes = 0;
                var failures = 0;
                double ratePerSecond = 0;
                var reportBlocks = 0;
                for (var i = statingBlockIndex; i < blocks.Count; i++)
                {
                    var b = blocks[i];
                    ++reportBlocks;
                    successes += b.Successes;
                    failures += b.Failures;
                }
                ratePerSecond = (successes + failures) / duration.TotalSeconds;
                Console.WriteLine($"[{stopwatch.Elapsed.TotalSeconds}] {ratePerSecond:0}/s from {reportBlocks} blocks with {successes} successes, {failures} failures.");
            }
        }

        private async Task RunWorker(TState state, int requestsPerBlock, int numBlocks)
        {
            var completedBlockWriter = this.completedBlocks.Writer;
            while (numBlocks > 0)
            {
                var workBlock = new WorkBlock() { Stopwatch = ValueStopwatch.StartNew(), Remaining = requestsPerBlock };
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