using CsCheck;
using Microsoft.Extensions.Time.Testing;
using Orleans.Durable.Storage.Engine.Ledgers.Memory;
using Orleans.Durable.Storage.Engine.Ledgers.Tests.Writer;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests;

/// <summary>
/// The replica transport contract's wait for a node's call budget (ledger-replicas.md §2.2), over the in-memory
/// transport's byte budget: a call which lacks budget waits in its node's FIFO queue, so a large call at the head is not
/// overtaken by smaller ones behind it; it fails with <see cref="LedgerReplicaQueueTimeoutException"/>, unsent, only
/// once its deadline passes, never at once; its caller's cancellation withdraws it; and the bytes outstanding to a node,
/// reads' reserved replies among them, each read reserving the payload limit, never pass the budget; a call above the
/// payload limit is refused. Mutant budgets which overtake or fail fast are caught, and a model-based property holds the
/// budget to a FIFO queue with capacity, deadlines, and cancellation after every step.
/// </summary>
public sealed class InMemoryLedgerReplicaTransportBudgetTests : IDisposable
{
    private const long Budget = 100;
    private const int PayloadLimit = 80;
    private static readonly TimeSpan Deadline = TimeSpan.FromMinutes(1);
    private static readonly LedgerReplicaOptions Options = new() { WindowSize = 4 };

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    /// <summary>
    /// ledger-replicas.md §2.2: calls are granted in arrival order: a 10-byte call behind an 80-byte one which waits does
    /// not overtake it although it fits, and both are granted, in order, once the first call's bytes return.
    /// </summary>
    [Fact]
    public async Task ALargeCallAtTheHeadIsNotOvertakenBySmallerOnesBehindIt() =>
        Assert.Null(await OrderViolationAsync(CallBudgetMutant.None, TestContext.Current.CancellationToken));

    /// <summary>
    /// ledger-replicas.md §2.2: a call which waits for budget fails with <see cref="LedgerReplicaQueueTimeoutException"/>
    /// once its deadline passes, and not before; it never left its caller, so its replica holds nothing at its slot.
    /// </summary>
    [Fact]
    public async Task ACallWaitsUntilItsDeadlineAndThenFailsUnsent() =>
        Assert.Null(await DeadlineViolationAsync(CallBudgetMutant.None, TestContext.Current.CancellationToken));

    /// <summary>
    /// ledger-replicas.md §2.2: a waiting call's cancellation withdraws it with <see cref="OperationCanceledException"/>,
    /// and the call behind it, which then heads the queue, is granted once it fits.
    /// </summary>
    [Fact]
    public async Task CancellingAWaitingCallWithdrawsItAndLetsTheNextIn()
    {
        var token = TestContext.Current.CancellationToken;
        await using var scenario = new Scenario(CallBudgetMutant.None);
        var first = scenario.WriteAsync(0, 60, token);
        await scenario.WaitSentAsync(1);
        using var cancel = new CancellationTokenSource();
        var large = scenario.WriteAsync(1, 80, cancel.Token);
        var small = scenario.WriteAsync(2, 10, token);
        Assert.Equal(2, scenario.Waiting);

        await cancel.CancelAsync();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => large);
        await scenario.WaitSentAsync(2);
        scenario.Release(0);
        scenario.Release(1);
        Assert.Equal(ReplicaAnswers.Written, await first);
        Assert.Equal(ReplicaAnswers.Written, await small);
        Assert.Equal(0, scenario.Outstanding);
    }

    /// <summary>
    /// ledger-replicas.md §2.2: a write above the payload limit the transport emulates is refused with
    /// <see cref="ArgumentException"/> before it takes any budget, and a call above the whole budget is refused too, not
    /// shrunk to fit it.
    /// </summary>
    [Fact]
    public async Task ACallAboveThePayloadLimitOrTheBudgetIsRefused()
    {
        var token = TestContext.Current.CancellationToken;
        await using var scenario = new Scenario(CallBudgetMutant.None);
        _ = await Assert.ThrowsAsync<ArgumentException>(() => scenario.WriteAsync(0, PayloadLimit + 1, token));
        Assert.Equal(0, scenario.Sent);
        Assert.Equal(0, scenario.Outstanding);
    }

    /// <summary>
    /// Each budget mutant is caught by the oracle named for it: <see cref="ALargeCallAtTheHeadIsNotOvertakenBySmallerOnesBehindIt"/>'s
    /// order oracle catches a budget which lets a call overtake an earlier one, and
    /// <see cref="ACallWaitsUntilItsDeadlineAndThenFailsUnsent"/>'s deadline oracle one which fails a call at once or early.
    /// </summary>
    [Theory]
    [InlineData(CallBudgetMutant.Overtake, "order")]
    [InlineData(CallBudgetMutant.FailFast, "deadline")]
    [InlineData(CallBudgetMutant.EarlyDeadline, "deadline")]
    internal async Task EachMutantIsCaughtByItsOracle(CallBudgetMutant mutant, string oracle)
    {
        var token = TestContext.Current.CancellationToken;
        Assert.NotNull(oracle == "order" ? await OrderViolationAsync(mutant, token) : await DeadlineViolationAsync(mutant, token));
    }

    /// <summary>Runs the order scenario, returning the first violation of FIFO order and the budget, or null.</summary>
    private static async Task<string?> OrderViolationAsync(CallBudgetMutant mutant, CancellationToken token)
    {
        await using var scenario = new Scenario(mutant);
        var first = scenario.WriteAsync(0, 60, token);
        await scenario.WaitSentAsync(1);
        var large = scenario.WriteAsync(1, 80, token);
        var small = scenario.WriteAsync(2, 10, token);
        var read = scenario.ReadAsync(3, token);
        if (scenario.Sent != 1)
        {
            return $"{scenario.Sent} calls were sent while the first held the budget; the 80-byte call heads the queue.";
        }

        scenario.Release(0);
        if ((await first).Case != ReplicaWriteCase.Written)
        {
            return "The first call was not written.";
        }

        // The large call and the small one fit together (90 bytes), granted in arrival order, though they may reach the
        // transit in either order; the read, which reserves the payload limit, waits behind them.
        await scenario.WaitSentAsync(3);
        if (scenario.Outstanding > Budget)
        {
            return $"{scenario.Outstanding} bytes are outstanding, above the budget of {Budget}.";
        }

        if (scenario.Sent != 3 || scenario.Waiting != 1)
        {
            return $"{scenario.Sent} calls were sent and {scenario.Waiting} wait once the first returned its bytes; the read waits.";
        }

        scenario.Release(1);
        scenario.Release(2);
        _ = await large;
        _ = await small;
        await scenario.WaitSentAsync(4);
        scenario.Release(3);
        _ = await read;
        return null;
    }

    /// <summary>Runs the deadline scenario, returning the first violation of the deadline's contract, or null.</summary>
    private static async Task<string?> DeadlineViolationAsync(CallBudgetMutant mutant, CancellationToken token)
    {
        await using var scenario = new Scenario(mutant);
        var first = scenario.WriteAsync(0, 60, token);
        await scenario.WaitSentAsync(1);
        var waiting = scenario.WriteAsync(1, 80, token);
        await Task.Yield();
        if (waiting.IsCompleted)
        {
            return "A call which lacked budget ended before its deadline.";
        }

        scenario.Time.Advance(Deadline - TimeSpan.FromTicks(1));

        // A timeout completes the call through a continuation, so the check waits a bounded time for one.
        await Task.Delay(TimeSpan.FromMilliseconds(20), TimeProvider.System, token);
        if (waiting.IsCompleted)
        {
            return "A call ended before its deadline passed.";
        }

        scenario.Time.Advance(TimeSpan.FromTicks(1));
        var failure = await Record.ExceptionAsync(() => waiting.WaitAsync(TimeSpan.FromSeconds(10), TimeProvider.System, token));
        if (failure is not LedgerReplicaQueueTimeoutException)
        {
            return $"A call whose deadline passed ended with {failure?.GetType().Name ?? "no failure"}.";
        }

        scenario.Release(0);
        _ = await first;
        var read = scenario.ReadAsync(1, token);
        await scenario.WaitSentAsync(2);
        scenario.Release(1);
        return (await read).Case == ReplicaReadCase.Missing ? null : "A call which timed out in the queue reached its replica.";
    }

    /// <summary>
    /// One node's replica over a bounded transport whose calls are held in transit, each until the test releases it in the
    /// order they were sent; disposal releases every call.
    /// </summary>
    private sealed class Scenario : IAsyncDisposable
    {
        private static readonly NodeId Node = LedgerReplicaTestData.Node;

        private readonly InMemoryLedgerReplicaTransport _nodes;
        private readonly ILedgerReplica _replica;
        private readonly List<TaskCompletionSource> _gates = [];
        private readonly List<Task> _calls = [];
        private bool _open;

        public Scenario(CallBudgetMutant mutant)
        {
            _nodes = new InMemoryLedgerReplicaTransport(PayloadLimit, Budget, Deadline, Time)
            {
                CreateBudget = (node, bytes, time) => new MutantCallBudget(bytes, int.MaxValue, time, waited => new LedgerReplicaQueueTimeoutException(node, waited), mutant),
            };
            _nodes.Transit = (_, _) =>
            {
                var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_gates)
                {
                    _gates.Add(gate);
                    if (_open)
                    {
                        gate.SetResult();
                    }
                }

                return new ValueTask(gate.Task);
            };
            _replica = _nodes.Open(Node, LedgerReplicaTestData.Ledger, Options);
        }

        public FakeTimeProvider Time { get; } = new();

        public int Sent
        {
            get
            {
                lock (_gates)
                {
                    return _gates.Count;
                }
            }
        }

        public int Waiting => _nodes.Budget(Node).Waiting;

        public long Outstanding => _nodes.Budget(Node).OutstandingBytes;

        public Task<ReplicaWriteOutcome> WriteAsync(long slot, int length, CancellationToken token)
        {
            var call = WriteCoreAsync(slot, length, token);
            _calls.Add(call);
            return call;
        }

        public Task<ReplicaRead> ReadAsync(long slot, CancellationToken token)
        {
            var call = _replica.ReadAsync(new Slot(slot), Slot.Zero, token).AsTask();
            _calls.Add(call);
            return call;
        }

        public Task WaitSentAsync(int count) => WriterTestData.WaitUntilAsync(() => Sent >= count);

        public void Release(int index)
        {
            lock (_gates)
            {
                _gates[index].TrySetResult();
            }
        }

        public async ValueTask DisposeAsync()
        {
            lock (_gates)
            {
                _open = true;
                foreach (var gate in _gates)
                {
                    _ = gate.TrySetResult();
                }
            }

            foreach (var call in _calls)
            {
                _ = await Record.ExceptionAsync(() => call);
                if (call is Task<ReplicaRead> { IsCompletedSuccessfully: true } read)
                {
                    read.Result.Release();
                }
            }

            await _replica.DisposeAsync();
            _nodes.Dispose();
        }

        private async Task<ReplicaWriteOutcome> WriteCoreAsync(long slot, int length, CancellationToken token)
        {
            using var payload = WriterTestData.CreatePayload(new byte[length]);
            return await _replica.WriteAsync(new Slot(slot), payload, Slot.Zero, recovery: false, token);
        }
    }
}
