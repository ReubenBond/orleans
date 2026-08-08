using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.TestingHost;
using Orleans.Transactions.TestKit.Correctnesss;

namespace Orleans.Transactions.TestKit
{
    public partial class TransactionRecoveryTestsRunner : TransactionTestRunnerBase
    {
        private static readonly TimeSpan RecoveryTimeout = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan FailureDetectionSchedulingMargin = TimeSpan.FromSeconds(15);
        // reduce to or remove once we fix timeouts abort
        private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

        private readonly TestCluster testCluster;
        private readonly ILogger logger;
        private readonly TimeSpan failureDetectionTimeout;

        protected void Log(string message)
        {
            this.testOutput($"[{DateTime.Now}] {message}");
            LogInformationMessage(this.logger, message);
        }

        private class ExpectedGrainActivity
        {
            public ExpectedGrainActivity(Guid grainId, ITransactionalBitArrayGrain grain)
            {
                this.GrainId = grainId;
                this.Grain = grain;
            }
            public Guid GrainId { get; }
            public ITransactionalBitArrayGrain Grain { get; }
            public BitArrayState Expected { get; } = new BitArrayState();
            public BitArrayState Unambiguous { get; } = new BitArrayState();
            public List<BitArrayState> Actual { get; set; } = null!;
            public async Task GetActual()
            {
                try
                {
                    this.Actual = await this.Grain.Get();
                } catch(Exception)
                {
                    // allow a single retry
                    await Task.Delay(TimeSpan.FromSeconds(30));
                    this.Actual = await this.Grain.Get();
                }
            }
        }

        private sealed record TransactionFailure(int Index, Guid[] GrainIds, Exception Exception, long ObservedAt);

        private sealed record RecoveryResult(
            bool Succeeded,
            bool LastProbeSucceeded,
            int Attempts,
            int RemainingGroupCount,
            int LastTransactionIndex,
            TimeSpan Elapsed);

        public TransactionRecoveryTestsRunner(TestCluster testCluster, Action<string> testOutput)
            : base(testCluster.GrainFactory!, testOutput) // Transaction test clusters initialize a client.
        {
            this.testCluster = testCluster;
            this.logger = this.testCluster.ServiceProvider.GetService<ILogger<TransactionRecoveryTestsRunner>>()!;
            var responseTimeout = this.testCluster.ServiceProvider
                .GetRequiredService<IOptions<ClientMessagingOptions>>()
                .Value
                .ResponseTimeout;
            this.failureDetectionTimeout = responseTimeout + FailureDetectionSchedulingMargin;
        }

        public virtual Task TransactionWillRecoverAfterRandomSiloGracefulShutdown(string transactionTestGrainClassName, int concurrent)
        {
            return TransactionWillRecoverAfterRandomSiloFailure(transactionTestGrainClassName, concurrent, true);
        }

        public virtual Task TransactionWillRecoverAfterRandomSiloUnGracefulShutdown(string transactionTestGrainClassName, int concurrent)
        {
            return TransactionWillRecoverAfterRandomSiloFailure(transactionTestGrainClassName, concurrent, false);
        }

        protected virtual async Task TransactionWillRecoverAfterRandomSiloFailure(string transactionTestGrainClassName, int concurrent, bool gracefulShutdown)
        {
            var index = 0;
            int getIndex() => Interlocked.Increment(ref index) - 1;
            List<ExpectedGrainActivity> txGrains = Enumerable.Range(0, concurrent * 2)
                .Select(i => Guid.NewGuid())
                .Select(grainId => new ExpectedGrainActivity(grainId, TestGrain<ITransactionalBitArrayGrain>(transactionTestGrainClassName, grainId)))
                .ToList();
            //ping all grains to activate them
            await WakeupGrains(txGrains.Select(g=>g.Grain).ToList());
            List<ExpectedGrainActivity>[] transactionGroups = txGrains
                .Select((txGrain, i) => new { index = i, value = txGrain })
                .GroupBy(v => v.index / 2)
                .Select(g => g.Select(i => i.value).ToList())
                .ToArray();
            var txSucceedBeforeInterruption = await AllTxSucceed(transactionGroups, getIndex());
            txSucceedBeforeInterruption.Should().BeTrue();
            await ValidateResults(txGrains, transactionGroups);

            // have transactions in flight when silo goes down
            using var stopProducing = new CancellationTokenSource();
            var firstFailure = new TaskCompletionSource<TransactionFailure>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task producer = RunWhileSucceeding(transactionGroups, getIndex, stopProducing, firstFailure);
            await Task.Delay(TimeSpan.FromSeconds(2));

            if (firstFailure.Task.IsCompleted)
            {
                stopProducing.Cancel();
                await producer;
                var prematureFailure = await firstFailure.Task;
                throw new InvalidOperationException(
                    $"A transaction failed before the silo was terminated. Index: {prematureFailure.Index}. "
                    + $"Groups: {string.Join(":", prematureFailure.GrainIds)}. Exception: {prematureFailure.Exception.GetType().Name}.");
            }

            var siloToTerminate = this.testCluster.Silos[Random.Shared.Next(this.testCluster.Silos.Count)];
            var shutdownMode = gracefulShutdown ? "graceful-stop" : "in-process-kill-shutdown";
            this.Log(
                $"Recovery phase=silo-shutdown requested. Silo={siloToTerminate.SiloAddress} "
                + $"({siloToTerminate.Name}), mode={shutdownMode}. "
                + "The in-process kill mode requests host shutdown through cancellation; it does not terminate a process.");

            var shutdownStartedAt = Stopwatch.GetTimestamp();
            if (gracefulShutdown)
                await this.testCluster.StopSiloAsync(siloToTerminate);
            else
                await this.testCluster.KillSiloAsync(siloToTerminate);
            var shutdownElapsed = Stopwatch.GetElapsedTime(shutdownStartedAt);
            this.Log(
                $"Recovery phase=silo-shutdown completed. Silo={siloToTerminate.SiloAddress}, "
                + $"mode={shutdownMode}, elapsed={shutdownElapsed}. "
                + "Membership and directory convergence are not asserted by this phase.");

            this.Log("Waiting for transactions to stop completing successfully");
            var failureDetectionStartedAt = Stopwatch.GetTimestamp();
            this.Log(
                $"Recovery phase=failure-watchdog started. ClientResponseTimeout="
                + $"{this.failureDetectionTimeout - FailureDetectionSchedulingMargin}, "
                + $"schedulingMargin={FailureDetectionSchedulingMargin}, watchdog={this.failureDetectionTimeout}.");
            await Task.WhenAny(firstFailure.Task, producer, Task.Delay(this.failureDetectionTimeout));
            stopProducing.Cancel();

            // Cancellation only prevents another batch from starting. Every transaction already in flight must settle
            // so that SetBit can record its durable outcome before validation.
            var producerDrainStartedAt = Stopwatch.GetTimestamp();
            this.Log("Recovery phase=producer-drain started. No new transaction batches will be produced.");
            await producer;
            var producerDrainElapsed = Stopwatch.GetElapsedTime(producerDrainStartedAt);
            this.Log($"Recovery phase=producer-drain completed. Elapsed={producerDrainElapsed}.");

            var interruption = firstFailure.Task.IsCompleted ? await firstFailure.Task : null;
            if (interruption is null)
            {
                throw new TimeoutException(
                    $"No transaction failure was observed within the {this.failureDetectionTimeout} watchdog after silo death. "
                    + $"Shutdown elapsed={shutdownElapsed}, producer drain elapsed={producerDrainElapsed}, "
                    + $"failure detection elapsed={Stopwatch.GetElapsedTime(failureDetectionStartedAt)}. "
                    + $"Performed {Volatile.Read(ref index)} transactions on each group.");
            }

            if (interruption.ObservedAt < shutdownStartedAt)
            {
                throw new InvalidOperationException(
                    $"A transaction failed before silo shutdown began. Index: {interruption.Index}. "
                    + $"Groups: {string.Join(":", interruption.GrainIds)}. Exception: {interruption.Exception.GetType().Name}.");
            }

            if (interruption.ObservedAt >= failureDetectionStartedAt
                && Stopwatch.GetElapsedTime(failureDetectionStartedAt, interruption.ObservedAt) > this.failureDetectionTimeout)
            {
                throw new TimeoutException(
                    $"No transaction failure was observed within the {this.failureDetectionTimeout} watchdog after silo death. "
                    + $"The first later failure was at index {interruption.Index} after "
                    + $"{Stopwatch.GetElapsedTime(failureDetectionStartedAt, interruption.ObservedAt)}.");
            }

            var firstFailureAfterShutdownRequest = Stopwatch.GetElapsedTime(shutdownStartedAt, interruption.ObservedAt);
            var firstFailureRelativeToShutdownCompletion = Stopwatch.GetElapsedTime(failureDetectionStartedAt, interruption.ObservedAt);
            this.Log(
                $"Recovery phase=transaction-terminal-failure observed. Index={interruption.Index}, "
                + $"grains={string.Join(":", interruption.GrainIds)}, "
                + $"afterShutdownRequest={firstFailureAfterShutdownRequest}, "
                + $"relativeToShutdownCompletion={firstFailureRelativeToShutdownCompletion}, "
                + $"reportedAfterProducerDrain={producerDrainElapsed}, "
                + $"exception={interruption.Exception.GetType().Name}: {interruption.Exception.Message}.");

            this.Log($"Waiting for system to recover. Performed {Volatile.Read(ref index)} transactions on each group.");
            var recovery = await RecoverTransactions(transactionGroups, getIndex, RecoveryTimeout, RetryDelay);
            this.Log(
                $"Recovery phase=transaction-path-probe completed. Succeeded={recovery.Succeeded}, "
                + $"lastProbeSucceeded={recovery.LastProbeSucceeded}, "
                + $"attempts={recovery.Attempts}, remainingGroups={recovery.RemainingGroupCount}, "
                + $"lastIndex={recovery.LastTransactionIndex}, elapsed={recovery.Elapsed}. "
                + $"Performed {Volatile.Read(ref index)} transactions on each group.");
            recovery.Succeeded.Should().BeTrue(
                $"transactions should recover within {RecoveryTimeout}; "
                + $"the last probe succeeded={recovery.LastProbeSucceeded}, "
                + $"remaining groups={recovery.RemainingGroupCount}, elapsed={recovery.Elapsed}");

            this.Log($"Recovery completed. Performed {Volatile.Read(ref index)} transactions on each group. Validating results.");
            await ValidateResults(txGrains, transactionGroups);
            this.Log("Recovery phase=test-complete. Transaction results validated.");
        }

        private static Task WakeupGrains(List<ITransactionalBitArrayGrain> grains)
        {
            var tasks =  new List<Task>();
            foreach (var grain in grains)
            {
                tasks.Add(grain.Ping());
            }
            return Task.WhenAll(tasks);
        }

        private async Task RunWhileSucceeding(
            List<ExpectedGrainActivity>[] transactionGroups,
            Func<int> getIndex,
            CancellationTokenSource stopProducing,
            TaskCompletionSource<TransactionFailure> firstFailure)
        {
            while (!stopProducing.IsCancellationRequested)
            {
                var failed = await RunAllTxReportFailed(
                    transactionGroups,
                    getIndex(),
                    failure =>
                    {
                        if (firstFailure.TrySetResult(failure))
                        {
                            stopProducing.Cancel();
                        }
                    });

                if (failed is not null)
                {
                    return;
                }
            }
        }

        private async Task<RecoveryResult> RecoverTransactions(
            List<ExpectedGrainActivity>[] transactionGroups,
            Func<int> getIndex,
            TimeSpan timeout,
            TimeSpan retryDelay)
        {
            var startedAt = Stopwatch.GetTimestamp();
            var remainingGroups = transactionGroups;
            var attempts = 0;
            var lastTransactionIndex = -1;

            while (Stopwatch.GetElapsedTime(startedAt) < timeout)
            {
                lastTransactionIndex = getIndex();
                attempts++;
                var attemptStartedAt = Stopwatch.GetTimestamp();
                var failedGroups = await RunAllTxReportFailed(remainingGroups, lastTransactionIndex);
                var attemptElapsed = Stopwatch.GetElapsedTime(attemptStartedAt);
                var elapsed = Stopwatch.GetElapsedTime(startedAt);
                this.Log(
                    $"Recovery phase=transaction-probe attempt={attempts}, index={lastTransactionIndex}, "
                    + $"succeeded={failedGroups is null}, attemptElapsed={attemptElapsed}, totalElapsed={elapsed}.");

                if (failedGroups is null)
                {
                    return new RecoveryResult(
                        elapsed < timeout,
                        true,
                        attempts,
                        0,
                        lastTransactionIndex,
                        elapsed);
                }

                remainingGroups = failedGroups;
                if (elapsed >= timeout)
                {
                    break;
                }

                var delay = retryDelay < timeout - elapsed ? retryDelay : timeout - elapsed;
                await Task.Delay(delay);
            }

            return new RecoveryResult(
                false,
                false,
                attempts,
                remainingGroups.Length,
                lastTransactionIndex,
                Stopwatch.GetElapsedTime(startedAt));
        }

        // Runs all transactions and returns failed;
        private async Task<List<ExpectedGrainActivity>[]?> RunAllTxReportFailed(
            List<ExpectedGrainActivity>[] transactionGroups,
            int index,
            Action<TransactionFailure>? onFailure = null)
        {
            var pending = transactionGroups
                .Select(group => (Task: SetBit(group, index), Group: group))
                .ToList();
            var failedGroups = new List<List<ExpectedGrainActivity>>();
            while (pending.Count > 0)
            {
                var completed = await Task.WhenAny(pending.Select(item => item.Task));
                var completedIndex = pending.FindIndex(item => ReferenceEquals(item.Task, completed));
                var transactionGroup = pending[completedIndex].Group;
                pending.RemoveAt(completedIndex);

                try
                {
                    await completed;
                }
                catch (Exception exception)
                {
                    failedGroups.Add(transactionGroup);
                    onFailure?.Invoke(
                        new TransactionFailure(
                            index,
                            transactionGroup.Select(activity => activity.GrainId).ToArray(),
                            exception,
                            Stopwatch.GetTimestamp()));
                }
            }

            if (failedGroups.Count == 0)
            {
                return null;
            }

            var result = failedGroups.ToArray();
            this.Log(
                $"Some transactions failed. Index: {index}. {result.Length} out of {transactionGroups.Length} failed. "
                + $"Failed groups: {string.Join(", ", result.Select(transactionGroup => string.Join(":", transactionGroup.Select(a => a.GrainId))))}");
            return result;
        }

        private async Task<bool> AllTxSucceed(List<ExpectedGrainActivity>[] transactionGroups, int index)
        {
            // null return indicates none failed
            return (await RunAllTxReportFailed(transactionGroups, index) == null);
        }

        private async Task SetBit(List<ExpectedGrainActivity> grains, int index)
        {
            try
            {
                await this.grainFactory.GetGrain<ITransactionCoordinatorGrain>(Guid.NewGuid()).MultiGrainSetBit(grains.Select(v => v.Grain).ToList(), index);
                grains.ForEach(g =>
                {
                    g.Expected.Set(index, true);
                    g.Unambiguous.Set(index, true);
                });
            }
            catch (OrleansTransactionAbortedException e)
            {
                this.Log($"Some transactions failed. Index: {index}: Exception: {e.GetType().Name}: {e.Message}");
                grains.ForEach(g =>
                {
                    g.Expected.Set(index, false);
                    g.Unambiguous.Set(index, true);
                });
                throw;
            }
            catch (Exception e)
            {
                this.Log($"Ambiguous transaction failure. Index: {index}: Exception: {e.GetType().Name}: {e.Message}");
                grains.ForEach(g =>
                {
                    g.Expected.Set(index, false);
                    g.Unambiguous.Set(index, false);
                });
                throw;
            }
        }

        private async Task ValidateResults(List<ExpectedGrainActivity> txGrains, List<ExpectedGrainActivity>[] transactionGroups)
        {
            await Task.WhenAll(txGrains.Select(a => a.GetActual()));
            this.Log($"Got all {txGrains.Count} actual values");

            bool pass = true;
            foreach (List<ExpectedGrainActivity> transactionGroup in transactionGroups)
            {
                if (transactionGroup.Count == 0) continue;
                BitArrayState first = transactionGroup[0].Actual.FirstOrDefault()!;
                foreach (ExpectedGrainActivity activity in transactionGroup.Skip(1))
                {
                    BitArrayState actual = activity.Actual.FirstOrDefault()!;
                    BitArrayState difference = first ^ actual;
                    if (difference.Value.Any(v => v != 0))
                    {
                        this.Log($"Activity on grain {activity.GrainId} did not match activity on {transactionGroup[0].GrainId}:\n"
                                 + $"{first} ^\n"
                                 + $"{actual} = \n"
                                 + $"{difference}\n"
                                 + $"Activation: {activity.GrainId}");
                        pass = false;
                    }

                }
            }

            int i = 0;
            foreach (ExpectedGrainActivity activity in txGrains)
            {
                BitArrayState expected = activity.Expected;
                BitArrayState unambiguous = activity.Unambiguous;
                BitArrayState unambuguousExpected = expected & unambiguous;
                List<BitArrayState> actual = activity.Actual;
                BitArrayState? first = actual.FirstOrDefault();
                if (first == null)
                {
                    this.Log($"No activity for {i} ({activity.GrainId})");
                    pass = false;
                    continue;
                }

                int j = 0;
                foreach (BitArrayState result in actual)
                {
                    // skip comparing first to first.
                    if (ReferenceEquals(first, result)) continue;
                    // Check if each state is identical to the first state.
                    var difference = result ^ first;
                    if (difference.Value.Any(v => v != 0))
                    {
                        this.Log($"Activity on grain {i}, state {j} did not match 'first':\n"
                                 + $"  {first}\n"
                                 + $"^ {result}\n"
                                 + $"= {difference}\n"
                                 + $"Activation: {activity.GrainId}");
                        pass = false;
                    }

                    j++;
                }

                // Check if the unambiguous portions of the first match.
                var unambiguousFirst = first & unambiguous;
                var unambiguousDifference = unambuguousExpected ^ unambiguousFirst;

                if (unambiguousDifference.Value.Any(v => v != 0))
                {
                    this.Log(
                        $"First state on grain {i} did not match 'expected':\n"
                        + $"  {unambuguousExpected}\n"
                        + $"^ {unambiguousFirst}\n"
                        + $"= {unambiguousDifference}\n"
                        + $"Activation: {activity.GrainId}");
                    pass = false;
                }

                i++;
            }
            this.Log($"Report complete : {pass}");
            pass.Should().BeTrue();
        }

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "{Message}"
        )]
        private static partial void LogInformationMessage(ILogger logger, string message);
    }
}
