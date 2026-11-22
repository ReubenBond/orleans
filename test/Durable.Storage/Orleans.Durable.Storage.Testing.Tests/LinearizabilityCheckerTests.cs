using System.Globalization;
using Microsoft.Extensions.Time.Testing;
using Operation = Orleans.Durable.Storage.Testing.HistoryOperation<Orleans.Durable.Storage.Testing.Tests.RegisterInput, int>;
using RegisterEvent = Orleans.Durable.Storage.Testing.HistoryEvent<Orleans.Durable.Storage.Testing.Tests.RegisterInput, int>;

namespace Orleans.Durable.Storage.Testing.Tests;

/// <summary>
/// The checker's verdicts on Porcupine's hand-written histories (porcupine_test.go), and its partitioning,
/// cancellation, and input validation.
/// </summary>
public sealed class LinearizabilityCheckerTests
{
    /// <summary>
    /// Porcupine's <c>TestRegisterModel</c>, from MIT 6.824's 2017 quiz 2, section VII: a get may return the put's value
    /// while the put is in flight, and a concurrent later get may still return the initial value; but once a get has
    /// returned the new value, a get which starts after it may not return the old one.
    /// </summary>
    [Fact]
    public void RegisterHistoriesFromPorcupine()
    {
        Operation[] linearizable =
        [
            new(0, RegisterInput.Put(100), 0, 0, 100),
            new(1, RegisterInput.Get, 25, 100, 75),
            new(2, RegisterInput.Get, 30, 0, 60),
        ];
        Operation[] illegal =
        [
            new(0, RegisterInput.Put(200), 0, 0, 100),
            new(1, RegisterInput.Get, 10, 200, 30),
            new(2, RegisterInput.Get, 40, 0, 90),
        ];

        Assert.Equal(LinearizabilityVerdict.Ok, LinearizabilityChecker.Check(RegisterModel.Instance, linearizable, TestContext.Current.CancellationToken));
        Assert.Equal(LinearizabilityVerdict.Illegal, LinearizabilityChecker.Check(RegisterModel.Instance, illegal, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// The events form of <see cref="RegisterHistoriesFromPorcupine"/>.
    /// </summary>
    [Fact]
    public void RegisterEventHistoriesFromPorcupine()
    {
        RegisterEvent[] linearizable =
        [
            Call(0, 0, RegisterInput.Put(100)),
            Call(1, 1, RegisterInput.Get),
            Call(2, 2, RegisterInput.Get),
            Return(2, 0),
            Return(1, 100),
            Return(0, 0),
        ];
        RegisterEvent[] illegal =
        [
            Call(0, 0, RegisterInput.Put(200)),
            Call(1, 1, RegisterInput.Get),
            Return(1, 200),
            Call(2, 2, RegisterInput.Get),
            Return(2, 0),
            Return(0, 0),
        ];

        Assert.Equal(LinearizabilityVerdict.Ok, LinearizabilityChecker.Check(RegisterModel.Instance, linearizable, TestContext.Current.CancellationToken));
        Assert.Equal(LinearizabilityVerdict.Illegal, LinearizabilityChecker.Check(RegisterModel.Instance, illegal, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Porcupine's <c>TestZeroDuration</c>: an operation may call and return at one instant, and intervals are closed, so
    /// operations sharing an instant are concurrent.
    /// </summary>
    [Fact]
    public void ZeroDurationOperations()
    {
        Operation[] linearizable =
        [
            new(0, RegisterInput.Put(100), 0, 0, 100),
            new(1, RegisterInput.Get, 25, 100, 75),
            new(2, RegisterInput.Get, 30, 0, 30),
            new(3, RegisterInput.Get, 30, 0, 30),
        ];
        Operation[] illegal =
        [
            new(0, RegisterInput.Put(200), 0, 0, 100),
            new(1, RegisterInput.Get, 10, 200, 10),
            new(2, RegisterInput.Get, 10, 200, 10),
            new(3, RegisterInput.Get, 40, 0, 90),
        ];
        Operation[] touching =
        [
            new(0, RegisterInput.Put(1), 0, 0, 10),
            new(1, RegisterInput.Get, 10, 0, 20),
        ];

        Assert.Equal(LinearizabilityVerdict.Ok, LinearizabilityChecker.CheckVerbose(RegisterModel.Instance, linearizable, TestContext.Current.CancellationToken).Verdict);
        Assert.Equal(LinearizabilityVerdict.Illegal, LinearizabilityChecker.CheckVerbose(RegisterModel.Instance, illegal, TestContext.Current.CancellationToken).Verdict);
        Assert.Equal(LinearizabilityVerdict.Ok, LinearizabilityChecker.Check(RegisterModel.Instance, touching, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Porcupine's <c>TestCheckNoPartitions</c>: an empty history is linearizable and has no partitions.
    /// </summary>
    [Fact]
    public void AnEmptyHistoryIsLinearizable()
    {
        var analysis = LinearizabilityChecker.CheckVerbose(KvModel.Instance, Array.Empty<HistoryOperation<KvInput, string>>(), TestContext.Current.CancellationToken);

        Assert.Equal(LinearizabilityVerdict.Ok, analysis.Verdict);
        Assert.Empty(analysis.Partitions);
        Assert.Equal(LinearizabilityVerdict.Ok, LinearizabilityChecker.Check(RegisterModel.Instance, Array.Empty<Operation>(), TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// An operation whose response time is unbounded may take effect anywhere after its invocation, or nowhere.
    /// </summary>
    [Fact]
    public void AnUnboundedOperationMayTakeEffectLateOrNever()
    {
        Operation[] seen =
        [
            new(0, RegisterInput.Put(7), 0, 0, HistoryOperation.Unbounded),
            new(1, RegisterInput.Get, 10, 0, 20),
            new(1, RegisterInput.Get, 30, 7, 40),
        ];
        Operation[] unseen =
        [
            new(0, RegisterInput.Put(7), 0, 0, HistoryOperation.Unbounded),
            new(1, RegisterInput.Get, 10, 0, 20),
            new(1, RegisterInput.Get, 30, 0, 40),
        ];
        Operation[] retracted =
        [
            new(0, RegisterInput.Put(7), 0, 0, HistoryOperation.Unbounded),
            new(1, RegisterInput.Get, 10, 7, 20),
            new(1, RegisterInput.Get, 30, 0, 40),
        ];

        Assert.Equal(LinearizabilityVerdict.Ok, LinearizabilityChecker.Check(RegisterModel.Instance, seen, TestContext.Current.CancellationToken));
        Assert.Equal(LinearizabilityVerdict.Ok, LinearizabilityChecker.Check(RegisterModel.Instance, unseen, TestContext.Current.CancellationToken));
        Assert.Equal(LinearizabilityVerdict.Illegal, LinearizabilityChecker.Check(RegisterModel.Instance, retracted, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Partitions follow the order of their first operation and keep history order; an operation's identifier is its
    /// index in its partition, and a linearizable partition's linearization orders every operation once.
    /// </summary>
    [Fact]
    public void PartitionsKeepHistoryOrderAndLinearizeEveryOperation()
    {
        HistoryOperation<KvInput, string>[] history =
        [
            new(0, KvInput.Put("y", "1"), 0, string.Empty, 10),
            new(1, KvInput.Put("x", "a"), 5, string.Empty, 15),
            new(0, KvInput.Get("y"), 20, "1", 30),
            new(1, KvInput.Append("x", "b"), 20, string.Empty, 30),
            new(2, KvInput.Get("x"), 25, "ab", 40),
        ];

        var analysis = LinearizabilityChecker.CheckVerbose(KvModel.Instance, history, TestContext.Current.CancellationToken);

        Assert.Equal(LinearizabilityVerdict.Ok, analysis.Verdict);
        Assert.Equal([[history[0], history[2]], [history[1], history[3], history[4]]], analysis.Partitions.Select(static partition => partition.Operations.ToArray()));
        Assert.Equal([0, 1], Assert.Single(analysis.Partitions[0].PartialLinearizations));
        Assert.Equal([0, 1, 2], Assert.Single(analysis.Partitions[1].PartialLinearizations));
    }

    /// <summary>
    /// Porcupine's <c>TestRegisterModelMetadata</c> and <c>TestVisualizationEventMetadata</c>: operations keep their
    /// metadata, and a response's metadata replaces its invocation's.
    /// </summary>
    [Fact]
    public void MetadataFollowsItsOperation()
    {
        Operation[] operations =
        [
            new(0, RegisterInput.Put(100), 0, 0, 100, "meta1"),
            new(1, RegisterInput.Get, 25, 100, 75, "meta2"),
            new(2, RegisterInput.Get, 30, 0, 60, "meta3"),
        ];
        RegisterEvent[] events =
        [
            Call(0, 0, RegisterInput.Put(100), "write-call-meta-IGNORED"),
            Call(1, 1, RegisterInput.Get, "read-call-meta"),
            Call(2, 2, RegisterInput.Get),
            Return(1, 100),
            Return(2, 100, "read-return-meta"),
            Return(0, 0, "write-return-meta"),
        ];

        var fromOperations = LinearizabilityChecker.CheckVerbose(RegisterModel.Instance, operations, TestContext.Current.CancellationToken);
        var fromEvents = LinearizabilityChecker.CheckVerbose(RegisterModel.Instance, events, TestContext.Current.CancellationToken);

        Assert.Equal(LinearizabilityVerdict.Ok, fromOperations.Verdict);
        Assert.Equal(["meta1", "meta2", "meta3"], Assert.Single(fromOperations.Partitions).Operations.Select(static operation => operation.Metadata));
        Assert.Equal(LinearizabilityVerdict.Ok, fromEvents.Verdict);
        Assert.Equal(
            [
                new Operation(0, RegisterInput.Put(100), 0, 0, 5, "write-return-meta"),
                new Operation(1, RegisterInput.Get, 1, 100, 3, "read-call-meta"),
                new Operation(2, RegisterInput.Get, 2, 100, 4, "read-return-meta"),
            ],
            Assert.Single(fromEvents.Partitions).Operations);
    }

    /// <summary>
    /// An event history is checked as operations whose timestamps are the events' positions, and an identifier may be
    /// reused once its operation has returned.
    /// </summary>
    [Fact]
    public void EventsAreOperationsTimedByPosition()
    {
        RegisterEvent[] events =
        [
            Call(7, 0, RegisterInput.Put(1)),
            Return(7, 0),
            Call(7, 0, RegisterInput.Get),
            Call(3, 1, RegisterInput.Put(2)),
            Return(7, 1),
            Return(3, 0),
        ];

        var analysis = LinearizabilityChecker.CheckVerbose(RegisterModel.Instance, events, TestContext.Current.CancellationToken);

        Assert.Equal(LinearizabilityVerdict.Ok, analysis.Verdict);
        Assert.Equal(
            [
                new Operation(0, RegisterInput.Put(1), 0, 0, 1),
                new Operation(0, RegisterInput.Get, 2, 1, 4),
                new Operation(1, RegisterInput.Put(2), 3, 0, 5),
            ],
            Assert.Single(analysis.Partitions).Operations);
        Assert.Equal([0, 1, 2], Assert.Single(analysis.Partitions[0].PartialLinearizations));
    }

    [Fact]
    public void MalformedHistoriesAreRejected()
    {
        _ = Assert.Throws<ArgumentException>(() => LinearizabilityChecker.Check(RegisterModel.Instance, [new Operation(0, RegisterInput.Get, 10, 0, 9)], TestContext.Current.CancellationToken));
        _ = Assert.Throws<ArgumentException>(() => LinearizabilityChecker.Check(RegisterModel.Instance, [Return(0, 0)], TestContext.Current.CancellationToken));
        _ = Assert.Throws<ArgumentException>(() => LinearizabilityChecker.Check(RegisterModel.Instance, [Call(0, 0, RegisterInput.Get)], TestContext.Current.CancellationToken));
        _ = Assert.Throws<ArgumentException>(
            () => LinearizabilityChecker.Check(RegisterModel.Instance, [Call(0, 0, RegisterInput.Get), Call(0, 1, RegisterInput.Get), Return(0, 0), Return(0, 0)], TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// A cancelled check is <see cref="LinearizabilityVerdict.Unknown"/>, whether cancelled before it starts or during the
    /// search, here by a deadline on a virtual clock which each step advances.
    /// </summary>
    [Fact]
    public void ACancelledCheckIsUnknown()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Equal(LinearizabilityVerdict.Unknown, LinearizabilityChecker.Check(RegisterModel.Instance, [new Operation(0, RegisterInput.Get, 0, 0, 1)], cancelled.Token));

        var time = new FakeTimeProvider();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1), time);
        var model = new ClockAdvancingModel<string, KvInput, string>(KvModel.Instance, time);
        var history = HardPartition("b", 0);

        Assert.Equal(LinearizabilityVerdict.Unknown, LinearizabilityChecker.Check(model, history, deadline.Token));
        Assert.True(deadline.IsCancellationRequested);
        Assert.InRange(model.Steps, 1_000, 1_001);
    }

    /// <summary>
    /// Partitions are searched in turns, so a partition which takes exponential time to refute does not delay finding
    /// one which is refuted at once; the first illegal partition ends the check, as in Porcupine's
    /// <c>checkParallel</c>, and partitions still being searched are unknown.
    /// </summary>
    [Fact]
    public void AHardPartitionDoesNotDelayAnIllegalOne()
    {
        var model = new ClockAdvancingModel<string, KvInput, string>(KvModel.Instance, new FakeTimeProvider());
        HistoryOperation<KvInput, string>[] history =
        [
            .. HardPartition("b", 2),
            new(0, KvInput.Put("a", "1"), 0, string.Empty, 10),
            new(1, KvInput.Get("a"), 20, "2", 30),
        ];

        var analysis = LinearizabilityChecker.CheckVerbose(model, history, stopAtFirstIllegal: true, TestContext.Current.CancellationToken);

        Assert.Equal(LinearizabilityVerdict.Illegal, analysis.Verdict);
        Assert.Equal([LinearizabilityVerdict.Unknown, LinearizabilityVerdict.Illegal], analysis.Partitions.Select(static partition => partition.Verdict));
        Assert.Equal([0], Assert.Single(analysis.Partitions[1].PartialLinearizations));
        Assert.InRange(model.Steps, 3, LinearizabilityChecker.Quantum + 2);
        Assert.Equal(LinearizabilityVerdict.Illegal, LinearizabilityChecker.Check(KvModel.Instance, history, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// A partition whose last operation is linearized on the last step of a turn has finished, so a later illegal
    /// partition does not leave it unknown.
    /// </summary>
    [Fact]
    public void APartitionCompletedOnTheLastStepOfATurnIsLinearizable()
    {
        HistoryOperation<KvInput, string>[] history =
        [
            .. Enumerable.Range(0, LinearizabilityChecker.Quantum).Select(static index => new HistoryOperation<KvInput, string>(
                0, KvInput.Put("a", index.ToString(CultureInfo.InvariantCulture)), 2L * index, string.Empty, 2L * index + 1)),
            new(1, KvInput.Put("b", "1"), 0, string.Empty, 10),
            new(2, KvInput.Get("b"), 20, "2", 30),
        ];

        var analysis = LinearizabilityChecker.CheckVerbose(KvModel.Instance, history, stopAtFirstIllegal: true, TestContext.Current.CancellationToken);

        Assert.Equal([LinearizabilityVerdict.Ok, LinearizabilityVerdict.Illegal], analysis.Partitions.Select(static partition => partition.Verdict));
        Assert.Equal(Enumerable.Range(0, LinearizabilityChecker.Quantum), Assert.Single(analysis.Partitions[0].PartialLinearizations));
    }

    /// <summary>
    /// A cancellation during a step ends the search before the step is used, as Porcupine's <c>checkSingle</c> checks
    /// its context after each step.
    /// </summary>
    [Fact]
    public void ACancellationDuringAStepIsUnknown()
    {
        using var cancellation = new CancellationTokenSource();
        var model = new CancellingModel(cancellation);

        Assert.Equal(LinearizabilityVerdict.Unknown, LinearizabilityChecker.Check(model, [new Operation(0, RegisterInput.Put(1), 0, 0, 1)], cancellation.Token));
        Assert.Equal(1, model.Steps);
    }

    /// <summary>
    /// Twenty concurrent appends and a get of a value no order of them produces: the search must try every order.
    /// </summary>
    private static HistoryOperation<KvInput, string>[] HardPartition(string key, int firstClient) =>
    [
        .. Enumerable.Range(0, 20).Select(index => new HistoryOperation<KvInput, string>(
            firstClient + index, KvInput.Append(key, ((char)('a' + index)).ToString()), 0, string.Empty, 100)),
        new(firstClient + 20, KvInput.Get(key), 0, "impossible", 100),
    ];

    private static RegisterEvent Call(int id, int clientId, RegisterInput input, object? metadata = null) =>
        new() { Kind = HistoryEventKind.Call, Id = id, ClientId = clientId, Input = input, Metadata = metadata };

    private static RegisterEvent Return(int id, int output, object? metadata = null) =>
        new() { Kind = HistoryEventKind.Return, Id = id, Output = output, Metadata = metadata };

    /// <summary>
    /// The register model, cancelling a check during its first step.
    /// </summary>
    private sealed class CancellingModel(CancellationTokenSource cancellation) : LinearizabilityModel<int, RegisterInput, int>
    {
        public int Steps { get; private set; }

        public override int Init() => RegisterModel.Instance.Init();

        public override bool Step(int state, RegisterInput input, int output, out int next)
        {
            Steps++;
            cancellation.Cancel();
            return RegisterModel.Instance.Step(state, input, output, out next);
        }
    }

    /// <summary>
    /// Advances a virtual clock by a millisecond on every step, so a deadline on that clock cancels a check after a known
    /// number of steps.
    /// </summary>
    private sealed class ClockAdvancingModel<TState, TInput, TOutput>(LinearizabilityModel<TState, TInput, TOutput> model, FakeTimeProvider time)
        : LinearizabilityModel<TState, TInput, TOutput>
        where TInput : notnull
    {
        public int Steps { get; private set; }

        public override IEqualityComparer<TInput>? PartitionComparer => model.PartitionComparer;

        public override IEqualityComparer<TState> StateComparer => model.StateComparer;

        public override TState Init() => model.Init();

        public override bool Step(TState state, TInput input, TOutput output, out TState next)
        {
            Steps++;
            time.Advance(TimeSpan.FromMilliseconds(1));
            return model.Step(state, input, output, out next);
        }
    }
}
