using System.Buffers;
using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Orleans.Durable.Journaling;
using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Archive.Memory;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Journals;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Control;
using Orleans.Durable.Storage.Engine.MetaStore;
using Orleans.Durable.Storage.Testing;
using Orleans.Runtime;
using Orleans.Storage;
using static Orleans.Durable.Storage.Tests.NodeRegistrySamples;

namespace Orleans.Durable.Storage.Tests;

/// <summary>
/// The journal router's outcome table (orleans.md §5.5, OR-I7) and Orleans storage (§5.5), each case scripted at the
/// owner node's grain: a write settles only as <c>Accepted</c>, <c>Rejected</c>, <c>ReplaceRequired</c>, or
/// <c>Ambiguous</c>; <c>Moved</c> follows its owner, at once only once in a row, <c>Fenced</c> re-reads the map,
/// <c>Throttled</c> resends to the same primary after the backoff, and Orleans' refusals, matched exactly, are resent; only a
/// failure before the send proves no effect, and every other failure of a send is <c>Ambiguous</c>; corruption, a corrupt
/// map among it, is thrown; cancellation ends the resends; each lane holds its calls to its node until their deadline. A
/// read is read again on every failure but cancellation, corruption, and a reply it fails to decode, gathers a forwarded
/// journal's pages, reads again from the start after <c>Expired</c>, and releases every Orleans buffer it receives
/// (OR-I18). The storage keeps Orleans' rules: no write before a read, a read which finds the version changed refused
/// before it delivers anything, early refusals before any send, every unsettled write an exception, and the replace latch.
/// </summary>
public sealed class JournalRouterTests : IAsyncLifetime
{
    private static readonly NodeId Self = Node(1);
    private static readonly NodeId Primary = Node(2);
    private static readonly NodeId Other = Node(3);
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();
    private readonly OrleansBufferLedger _received = new();
    private readonly ScriptedConditionalRegister _register = new();
    private readonly LocalRouting _routing = new();
    private readonly InMemoryArchiveStore _archive = new();
    private readonly ScriptedGrains _grains;
    private int _registryRereads;
    private JournalRouter? _router;

    public JournalRouterTests() => _grains = new ScriptedGrains(_received);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private JournalRouter Router => _router!;

    public async ValueTask InitializeAsync()
    {
        await CommitAsync(_register, new LedgerMapChange.Install([], [Opening(Primary)], 1));
        _routing.Publish(Self, await LedgerMapView.OpenAsync(_register, Token), _archive);
        _router = NewRouter();
    }

    public ValueTask DisposeAsync()
    {
        _archive.Dispose();
        _received.Dispose();
        _leaks.Dispose();
        return ValueTask.CompletedTask;
    }

    public static TheoryData<string> SettledCases() => ["Accepted", "Rejected", "ReplaceRequired", "Ambiguous"];

    public static TheoryData<string> ResentCases() =>
        ["NotThisNode", "PlacementRefusal", "RejectedPlacementRefusal", "OverloadRejection", "RejectedOverload", "Throttled", "Unavailable", "Fenced", "MovedToNone", "MovedToTheNodeAsked", "MovedToSelf"];

    public static TheoryData<string> AmbiguousFailures() =>
        ["LostReply", "ResponseTimeout", "SiloUnavailable", "CalleeDisposed", "CalleeDefect", "CalleeArgument", "UndecodableReply", "NestedOverload", "NestedPlacementRefusal", "ForwardQueueTimeoutFromTheCallee"];

    public static TheoryData<string> ReadResentCases() =>
        ["MovedToOther", "MovedToNone", "MovedToTheNodeAsked", "MovedToSelf", "NotThisNode", "PlacementRefusal", "OverloadRejection", "RejectedOverload", "ForwardQueueTimeout", "Busy", "LostReply", "ResponseTimeout", "SiloUnavailable", "CalleeArgument"];

    /// <summary>OR-I7: <c>Accepted</c>, <c>Rejected</c>, <c>ReplaceRequired</c>, and <c>Ambiguous</c> settle a write at the first send.</summary>
    [Theory]
    [MemberData(nameof(SettledCases))]
    public async Task ASettlingAnswerSettlesTheWriteWithoutAResend(string answer)
    {
        var outcome = Answer(answer);
        _grains.Script(Primary, _ => Task.FromResult(outcome));

        var settled = await WriteAsync(Router, Token);

        Assert.Equal(outcome.Value, settled.Value);
        Assert.Equal([Primary], _grains.Calls);
    }

    /// <summary>
    /// OR-I7: each answer which reports a write without effect, and each refusal by Orleans which proves one, at the top
    /// level or directly inside its message rejection, is resent: here to the map's primary, which accepts it the second
    /// time. <c>Fenced</c>, <c>Unavailable</c>, and a <c>Moved</c> naming no owner, the node asked, or this node re-read the map first;
    /// <c>NotThisNode</c> and a placement refusal re-read the registry and the map; the rest re-read neither.
    /// </summary>
    [Theory]
    [MemberData(nameof(ResentCases))]
    public async Task AnAnswerWithoutEffectIsResent(string answer)
    {
        // This silo's own Journals fail any write which reaches them, as one following a Moved into this node would.
        await using var own = await OwnPrimary.StartAsync(_archive);
        own.Journals.BeforeCall = static () => throw new InvalidOperationException("A write routed to this node reaches its own Journals.");
        var router = NewRouter(journals: own.Journals);
        var reads = _register.Reads;
        _grains.Script(Primary, _ => answer switch
        {
            "PlacementRefusal" => Task.FromException<JournalForwardedWriteOutcome>(new NodePlacementRefusedException(Primary, registered: null)),
            "RejectedPlacementRefusal" => Task.FromException<JournalForwardedWriteOutcome>(Rejection(new NodePlacementRefusedException(Primary, registered: null))),
            "OverloadRejection" => Task.FromException<JournalForwardedWriteOutcome>(new LimitExceededException("The test's silo sheds the call.")),
            "RejectedOverload" => Task.FromException<JournalForwardedWriteOutcome>(Rejection(new LimitExceededException("The test's silo sheds the call."))),
            "MovedToTheNodeAsked" => Task.FromResult<JournalForwardedWriteOutcome>(new JournalMoved(Primary)),
            "MovedToSelf" => Task.FromResult<JournalForwardedWriteOutcome>(new JournalMoved(Self)),
            _ => Task.FromResult(Answer(answer)),
        });
        _grains.Script(Primary, _ => Task.FromResult(Answer("Accepted")));

        _ = Assert.IsType<JournalWriteOutcome.Accepted>((await WriteAsync(router, Token)).Value);

        Assert.Equal([Primary, Primary], _grains.Calls);
        var rereadsRegistry = answer is "NotThisNode" or "PlacementRefusal" or "RejectedPlacementRefusal";
        Assert.Equal(rereadsRegistry || answer is "Fenced" or "Unavailable" or "MovedToNone" or "MovedToTheNodeAsked" or "MovedToSelf", _register.Reads > reads);
        Assert.Equal(rereadsRegistry ? 1 : 0, _registryRereads);
    }

    /// <summary>OR-I7: a <c>Moved</c> naming another node is resent to that node at once, which the map does not name.</summary>
    [Fact]
    public async Task AMovedFollowsItsOwner()
    {
        var reads = _register.Reads;
        _grains.Script(Primary, _ => Task.FromResult<JournalForwardedWriteOutcome>(new JournalMoved(Other)));
        _grains.Script(Other, _ => Task.FromResult(Answer("Accepted")));

        _ = Assert.IsType<JournalWriteOutcome.Accepted>((await WriteAsync(Router, Token)).Value);

        Assert.Equal([Primary, Other], _grains.Calls);
        Assert.Equal(reads, _register.Reads);
    }

    /// <summary>
    /// orleans.md §5.5: nodes whose views name each other are followed at once only once in a row. On a clock which only
    /// the test moves, the follow from the primary to the node it names goes without a delay, and the second follow, back
    /// to the primary, waits for the backoff and re-reads the map, whose primary then accepts the write.
    /// </summary>
    [Fact]
    public async Task NodesWhoseViewsNameEachOtherAreFollowedAtOnceOnlyOnceInARow()
    {
        var time = new FakeTimeProvider();
        var router = NewRouter(time: time);
        _grains.Script(Primary, _ => Task.FromResult<JournalForwardedWriteOutcome>(new JournalMoved(Other)));
        _grains.Script(Other, _ => Task.FromResult<JournalForwardedWriteOutcome>(new JournalMoved(Primary)));
        _grains.Script(Primary, _ => Task.FromResult(Answer("Accepted")));

        var write = WriteAsync(router, Token);
        await UntilAsync(() => _grains.Calls.Count == 2, "the follow to the node the primary names");
        await Task.Delay(TimeSpan.FromMilliseconds(100), TimeProvider.System, Token);
        Assert.Equal([Primary, Other], _grains.Calls);
        var reads = _register.Reads;
        await UntilAsync(() => write.IsCompleted, "the write to settle", () => time.Advance(TimeSpan.FromMilliseconds(1)));

        _ = Assert.IsType<JournalWriteOutcome.Accepted>((await write).Value);
        Assert.Equal([Primary, Other, Primary], _grains.Calls);
        Assert.True(_register.Reads > reads);
    }

    /// <summary>
    /// orleans.md §5.5, §5.9: a write answered <c>Throttled</c>, and a read answered <c>Busy</c>, are resent to the node
    /// which answered, not to the map's primary, only once the backoff passes on a clock which only the test moves, and
    /// without a map re-read; the <c>Moved</c> before them is followed with no delay.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AThrottledWriteAndABusyReadAreResentToTheNodeWhichAnsweredAfterTheBackoff(bool write)
    {
        var time = new FakeTimeProvider();
        var router = NewRouter(time: time);
        Task settled;
        Func<List<NodeId>> calls;
        if (write)
        {
            _grains.Script(Primary, _ => Task.FromResult<JournalForwardedWriteOutcome>(new JournalMoved(Other)));
            _grains.Script(Other, _ => Task.FromResult(Answer("Throttled")));
            _grains.Script(Other, _ => Task.FromResult(Answer("Accepted")));
            settled = WriteAsync(router, Token);
            calls = () => _grains.Calls;
        }
        else
        {
            _grains.ScriptRead(Primary, () => new JournalMoved(Other));
            _grains.ScriptRead(Other, () => JournalForwardedRead.Busy.Instance);
            _grains.ScriptRead(Other, () => Found([5], snapshot: null));
            settled = ReadBytesAsync(router);
            calls = () => _grains.Reads;
        }

        await UntilAsync(() => calls().Count == 2, "the follow to the node the primary names");
        var reads = _register.Reads;
        await Task.Delay(TimeSpan.FromMilliseconds(100), TimeProvider.System, Token);
        Assert.Equal(2, calls().Count);
        await UntilAsync(() => settled.IsCompleted, "the resend to settle", () => time.Advance(TimeSpan.FromMilliseconds(1)));

        await settled;
        Assert.Equal([Primary, Other, Other], calls());
        Assert.Equal(reads, _register.Reads);
    }

    /// <summary>
    /// OR-I7, OR-I16, journals.md §2.2: every other failure of a send leaves the outcome unknown: a lost reply, a response
    /// timeout, an unavailable silo, the callee's disposal, a callee defect or <see cref="ArgumentException"/>, a reply the
    /// router fails to decode, a refusal by Orleans nested inside a callee's failure, and a lane's timeout which the callee,
    /// not the lane, raised. The write settles <c>Ambiguous</c>, without a resend.
    /// </summary>
    [Theory]
    [MemberData(nameof(AmbiguousFailures))]
    public async Task AnyOtherFailureOfASendIsAmbiguous(string failure)
    {
        _grains.Script(Primary, _ => Task.FromException<JournalForwardedWriteOutcome>(Failure(failure)));

        _ = Assert.IsType<JournalWriteOutcome.Ambiguous>((await WriteAsync(Router, Token)).Value);

        Assert.Equal([Primary], _grains.Calls);
    }

    /// <summary>
    /// orleans.md §5.5, OR-I7: when this node is the primary, its own Journals' refusal before admission,
    /// <see cref="ArgumentException"/>, which <c>JournalStore.WriteAsync</c> documents, is thrown as it is, without effect,
    /// and corruption too; any other failure of the Journals, an <see cref="InvalidDataException"/> among them, which they do
    /// not promise before admission, settles the write <c>Ambiguous</c>.
    /// </summary>
    [Theory]
    [InlineData("Argument")]
    [InlineData("Invalid")]
    [InlineData("Corrupt")]
    [InlineData("Defect")]
    public async Task TheSilosOwnJournalsRefusalsBeforeAdmissionAreThrown(string failure)
    {
        await using var own = await OwnPrimary.StartAsync(_archive);
        own.Journals.BeforeCall = () => throw failure switch
        {
            "Argument" => new ArgumentException("The test's Journals refuse the key."),
            "Invalid" => new InvalidDataException("The test's Journals refuse the record."),
            "Corrupt" => new JournalStoreCorruptException("The test's history contradicts itself."),
            _ => new InvalidOperationException("The test's Journals failed after admission."),
        };
        var router = NewRouter(routing: own.Routing, journals: own.Journals);

        var thrown = await Record.ExceptionAsync(() => WriteAsync(router, Token));

        if (failure is "Defect" or "Invalid")
        {
            Assert.Null(thrown);
        }
        else
        {
            Assert.IsType(failure == "Argument" ? typeof(ArgumentException) : typeof(JournalStoreCorruptException), thrown);
        }

        Assert.Empty(_grains.Calls);
    }

    /// <summary>
    /// journals.md §6, OR-I7: a write which the silo's own Journals fail with <see cref="JournalStoreUnavailableException"/>
    /// had no effect, so the router re-reads the map and resends it, never answering <c>Ambiguous</c>: here the resend
    /// reaches the Journals again, whose refusal of it then ends the write.
    /// </summary>
    [Fact]
    public async Task AWriteTheSilosOwnJournalsFindUnavailableIsResent()
    {
        await using var own = await OwnPrimary.StartAsync(_archive);
        var calls = 0;
        own.Journals.BeforeCall = () => ++calls == 1
            ? throw new JournalStoreUnavailableException("The test's ledger quorum is out of reach.", new AggregateException(new TimeoutException("The test's replica missed its deadline.")))
            : throw new ArgumentException("The test's Journals refuse the resend.");
        var router = NewRouter(routing: own.Routing, journals: own.Journals);

        _ = await Assert.ThrowsAsync<ArgumentException>(() => WriteAsync(router, Token));

        Assert.Equal(2, calls);
        Assert.Empty(_grains.Calls);
    }

    /// <summary>Corruption is its own class: the router throws it as it is.</summary>
    [Fact]
    public async Task CorruptionIsThrown()
    {
        _grains.Script(Primary, _ => Task.FromException<JournalForwardedWriteOutcome>(new JournalStoreCorruptException("The test's history contradicts itself.")));

        _ = await Assert.ThrowsAsync<JournalStoreCorruptException>(() => WriteAsync(Router, Token));
    }

    /// <summary>The caller's cancellation ends the resends and reports <see cref="OperationCanceledException"/>.</summary>
    [Fact]
    public async Task CancellationEndsTheResends()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        _grains.Default = (_, calls) =>
        {
            if (calls == 3)
            {
                cancellation.Cancel();
            }

            return Task.FromResult<JournalForwardedWriteOutcome>(LocalJournalWriteOutcome.Throttled.Instance);
        };

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WriteAsync(Router, cancellation.Token));
        Assert.InRange(_grains.Calls.Count, 3, 4);
    }

    /// <summary>
    /// A send which ends with the caller's own cancellation reports <see cref="OperationCanceledException"/>, which asserts
    /// nothing, after the one send: neither <c>Ambiguous</c> nor a resend.
    /// </summary>
    [Fact]
    public async Task ACancellationDuringASendEndsTheWrite()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        _grains.Script(Primary, _ =>
        {
            cancellation.Cancel();
            return Task.FromException<JournalForwardedWriteOutcome>(new OperationCanceledException(cancellation.Token));
        });

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WriteAsync(Router, cancellation.Token));
        Assert.Equal([Primary], _grains.Calls);
    }

    /// <summary>A forwarded write carries the record verbatim: the primary's grain receives the bytes the writing silo encoded.</summary>
    [Fact]
    public async Task AForwardedWriteCarriesTheRecordVerbatim()
    {
        byte[]? received = null;
        _grains.Script(Primary, entry =>
        {
            received = entry.Record.ToArray();
            return Task.FromResult(Answer("Accepted"));
        });
        using var payload = ArcBuffer.Wrap("an entry"u8.ToArray());
        using var record = JournalEntryRecord.Append(JournalRowKey.Encode("k"), JournalVersion.Origin, payload);
        using var expected = record.ToArcBuffer();

        _ = await Router.WriteAsync("k", JournalRowKey.Encode("k"), record, Token).WaitAsync(Bound, Token);

        Assert.Equal(expected.ToArray(), received);
    }

    /// <summary>
    /// orleans.md §5.5, §5.12, OR-I17: each node has its own write lane, bounded by <c>MaxForwardCallBytes</c>, and its own
    /// read lane, bounded by <c>MaxForwardReadCalls</c>; a lane is the same for every call to its node.
    /// </summary>
    [Fact]
    public void EachNodeHasItsOwnLanesBoundedByTheOptions()
    {
        var budgets = new ForwardingBudgets(TestReplicaLimits.Options(static options => (options.MaxForwardCallBytes, options.MaxForwardReadCalls) = (5 << 20, 3)), TimeProvider.System);

        Assert.Same(budgets.Writes(Primary), budgets.Writes(Primary));
        Assert.NotSame(budgets.Writes(Primary), budgets.Writes(Other));
        Assert.NotSame(budgets.Reads(Primary), budgets.Reads(Other));
        Assert.NotSame(budgets.Writes(Primary), budgets.Reads(Primary));
        Assert.Equal(5 << 20, budgets.Writes(Primary).MaxBytes);
        Assert.Equal(3, budgets.Reads(Primary).MaxCalls);
    }

    /// <summary>
    /// orleans.md §5.5, §5.12, OR-I17: a write waits in its destination's write lane while the bytes outstanding there,
    /// here one record's, are full; at its deadline, on a clock which only the test moves, it leaves the lane unsent and is
    /// resent, never <c>Ambiguous</c>, waiting in the lane again; once the first write is answered, both are accepted, the
    /// second sent once.
    /// </summary>
    [Fact]
    public async Task AWriteWaitingInAFullLaneIsResentAtItsDeadline()
    {
        var time = new FakeTimeProvider();
        var router = NewRouter(TestReplicaLimits.Options(static options => options.MaxForwardCallBytes = RecordLength()), time: time);
        var held = new TaskCompletionSource<JournalForwardedWriteOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        _grains.Script(Primary, _ => held.Task);
        _grains.Script(Primary, _ => Task.FromResult(Answer("Accepted")));
        var lane = router.Budgets.Writes(Primary);

        var first = WriteAsync(router, Token);
        await UntilAsync(() => _grains.Calls.Count == 1, "the first write to be sent");
        var second = WriteAsync(router, Token);
        await UntilAsync(() => lane.Waiting == 1, "the second write to wait in the lane");
        time.Advance(TestReplicaLimits.Of().Deadline + TimeSpan.FromSeconds(1));
        await UntilAsync(() => lane.Waiting == 0, "the second write to leave the lane at its deadline");
        await UntilAsync(() => lane.Waiting == 1, "the second write to wait in the lane again", () => time.Advance(TimeSpan.FromMilliseconds(1)));

        Assert.False(second.IsCompleted);
        Assert.Equal([Primary], _grains.Calls);
        held.SetResult(Answer("Accepted"));
        _ = Assert.IsType<JournalWriteOutcome.Accepted>((await first).Value);
        _ = Assert.IsType<JournalWriteOutcome.Accepted>((await second).Value);
        Assert.Equal([Primary, Primary], _grains.Calls);
    }

    /// <summary>
    /// The read lane's twin: a read's first page waits while <c>MaxForwardReadCalls</c>, here one, are outstanding to its
    /// owner; at its deadline it leaves the lane unasked and is read again, and once the first read is answered, both are.
    /// </summary>
    [Fact]
    public async Task AReadWaitingInAFullLaneIsResentAtItsDeadline()
    {
        var time = new FakeTimeProvider();
        var router = NewRouter(TestReplicaLimits.Options(static options => options.MaxForwardReadCalls = 1), time: time);
        var held = new TaskCompletionSource<JournalForwardedRead>(TaskCreationOptions.RunContinuationsAsynchronously);
        _grains.ScriptReadAsync(Primary, () => held.Task);
        _grains.ScriptRead(Primary, () => Found([2], snapshot: null));
        var lane = router.Budgets.Reads(Primary);

        var first = ReadBytesAsync(router);
        await UntilAsync(() => _grains.Reads.Count == 1, "the first read to be asked");
        var second = ReadBytesAsync(router);
        await UntilAsync(() => lane.Waiting == 1, "the second read to wait in the lane");
        time.Advance(TestReplicaLimits.Of().Deadline + TimeSpan.FromSeconds(1));
        await UntilAsync(() => lane.Waiting == 0, "the second read to leave the lane at its deadline");
        await UntilAsync(() => lane.Waiting == 1, "the second read to wait in the lane again", () => time.Advance(TimeSpan.FromMilliseconds(1)));

        Assert.False(second.IsCompleted);
        Assert.Equal([Primary], _grains.Reads);
        held.SetResult(Found([1], snapshot: null));
        Assert.Equal([1], await first);
        Assert.Equal([2], await second);
        Assert.Equal(0, _registryRereads);
    }

    /// <summary>A write cancelled while it waits in a full lane ends with <see cref="OperationCanceledException"/>, unsent.</summary>
    [Fact]
    public async Task ACancellationInAFullLaneEndsTheWriteUnsent()
    {
        var router = NewRouter(TestReplicaLimits.Options(static options => options.MaxForwardCallBytes = RecordLength()));
        var held = new TaskCompletionSource<JournalForwardedWriteOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        _grains.Script(Primary, _ => held.Task);
        var lane = router.Budgets.Writes(Primary);
        var first = WriteAsync(router, Token);
        await UntilAsync(() => _grains.Calls.Count == 1, "the first write to be sent");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var second = WriteAsync(router, cancellation.Token);
        await UntilAsync(() => lane.Waiting == 1, "the second write to wait in the lane");

        cancellation.Cancel();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        Assert.Equal(0, lane.Waiting);
        held.SetResult(Answer("Accepted"));
        _ = Assert.IsType<JournalWriteOutcome.Accepted>((await first).Value);
        Assert.Equal([Primary], _grains.Calls);
    }

    /// <summary>
    /// orleans.md §5.5: a map re-read which finds a value that is no ledger map throws it as corruption, and the write it
    /// was for is not sent again.
    /// </summary>
    [Fact]
    public async Task ACorruptMapIsThrown()
    {
        _grains.Script(Primary, _ =>
        {
            _register.Put([0xff, 0xff, 0xff]);
            return Task.FromResult(Answer("Fenced"));
        });

        _ = await Assert.ThrowsAsync<InvalidDataException>(() => WriteAsync(Router, Token));
        Assert.Equal([Primary], _grains.Calls);
    }

    /// <summary>A map re-read which fails, as an unavailable MetaStore's does, is read again after a backoff.</summary>
    [Fact]
    public async Task AMapWhichFailsToReadIsReadAgain()
    {
        var register = new FailingReads(_register);
        var routing = new LocalRouting();
        routing.Publish(Self, await LedgerMapView.OpenAsync(register, Token), _archive);
        var router = NewRouter(routing: routing);
        _grains.Script(Primary, _ =>
        {
            register.FailNext(1);
            return Task.FromResult(Answer("Fenced"));
        });
        _grains.Script(Primary, _ => Task.FromResult(Answer("Accepted")));

        _ = Assert.IsType<JournalWriteOutcome.Accepted>((await WriteAsync(router, Token)).Value);
        Assert.Equal(1, register.Failed);
        Assert.Equal([Primary, Primary], _grains.Calls);
    }

    /// <summary>
    /// A key over which the view lists no open ledger, as before bootstrap, waits and reads the map again until it lists
    /// one, whose primary then takes the write.
    /// </summary>
    [Fact]
    public async Task AKeyWithNoOpenLedgerWaitsForTheMapToListOne()
    {
        var register = new ScriptedConditionalRegister();
        var routing = new LocalRouting();
        routing.Publish(Self, await LedgerMapView.OpenAsync(register, Token), _archive);
        await CommitAsync(register, new LedgerMapChange.Install([], [Opening(Primary)], 1));
        var router = NewRouter(routing: routing);
        _grains.Script(Primary, _ => Task.FromResult(Answer("Accepted")));

        _ = Assert.IsType<JournalWriteOutcome.Accepted>((await WriteAsync(router, Token)).Value);
        Assert.Equal([Primary], _grains.Calls);
    }

    /// <summary>
    /// orleans.md §5.5: once the silo's runtime stopped, a map re-read ends the call rather than resending: a write settles
    /// with the gate's stopped answer, <c>Moved</c> naming no owner, which the storage reports as
    /// <see cref="ObjectDisposedException"/> without effect, and a read throws <see cref="ObjectDisposedException"/>.
    /// </summary>
    [Fact]
    public async Task AMapReadAfterTheRuntimeStoppedEndsTheCall()
    {
        var register = new FailingReads(_register);
        var routing = new LocalRouting();
        routing.Publish(Self, await LedgerMapView.OpenAsync(register, Token), _archive);
        var router = NewRouter(routing: routing);
        register.Disposed = true;
        _grains.Script(Primary, _ => Task.FromResult(Answer("Fenced")));

        Assert.Equal(new JournalMoved(Owner: null), (await WriteAsync(router, Token)).Value);
        Assert.Equal([Primary], _grains.Calls);

        _grains.ScriptRead(Primary, () => new JournalMoved(Owner: null));
        _ = await Assert.ThrowsAsync<ObjectDisposedException>(() => ReadBytesAsync(router));

        var storage = new DurableJournalStorage(new JournalId("k"), router, routing, 1 << 20, "json-lines", TimeProvider.System);
        register.Disposed = false;
        _grains.ScriptRead(Primary, () => Found([], snapshot: null, exists: false));
        await storage.ReadAsync(new Consumer(), Token).AsTask().WaitAsync(Bound, Token);
        register.Disposed = true;
        _grains.Script(Primary, _ => Task.FromResult(Answer("Fenced")));
        _ = await Assert.ThrowsAsync<ObjectDisposedException>(() => storage.AppendAsync(Sequence(1), Token).AsTask().WaitAsync(Bound, Token));
    }

    /// <summary>
    /// orleans.md §5.5: a backoff is the exponential ceiling, <c>min(first × 2^attempt, longest)</c>, times between a half
    /// and one as the random draw goes, so it never shrinks toward nothing and never passes the longest.
    /// </summary>
    [Theory]
    [InlineData(0, 0.0, 5)]
    [InlineData(0, 0.999999, 10)]
    [InlineData(3, 0.0, 40)]
    [InlineData(3, 0.5, 60)]
    [InlineData(3, 0.999999, 80)]
    [InlineData(40, 0.0, 500)]
    [InlineData(40, 0.999999, 1000)]
    public void ABackoffIsEqualJitterUnderAnExponentialCeiling(int attempt, double random, int milliseconds) =>
        Assert.Equal(TimeSpan.FromMilliseconds(milliseconds).TotalMilliseconds, JournalRouter.Backoff(attempt, TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(1), random).TotalMilliseconds, 0.01);

    /// <summary>
    /// orleans.md §5.5: a forwarded journal of more than one page is gathered from its pages, each asked for once the previous
    /// one arrived; an <c>Expired</c> page reads the journal again from the start; and every buffer received is released.
    /// </summary>
    [Fact]
    public async Task AForwardedReadGathersItsPagesAndReadsAgainAfterExpired()
    {
        var content = Content(2 * JournalForwardedRead.PageBytes + 7);
        var snapshot = Guid.NewGuid();
        _grains.ScriptRead(Primary, () => Found(content, snapshot));
        _grains.ScriptPage(Primary, (_, offset) => Page(content, offset));
        _grains.ScriptPage(Primary, (_, _) => JournalForwardedPage.Expired.Instance);
        _grains.ScriptRead(Primary, () => Found(content, snapshot));
        _grains.ScriptPage(Primary, (_, offset) => Page(content, offset));
        _grains.ScriptPage(Primary, (_, offset) => Page(content, offset));

        var read = await Router.ReadAsync("k", JournalRowKey.Encode("k"), Token).WaitAsync(Bound, Token);
        using (read.Content)
        {
            Assert.Equal(content, Bytes(read.Content));
            Assert.Equal((JournalVersion.Origin, true), (read.Version, read.Exists));
        }
    }

    /// <summary>
    /// orleans.md §5.5: a read has no effect beyond a pin, so each answer and each failure of its call but its caller's
    /// cancellation, corruption, and a reply it fails to decode reads it again. <c>Moved</c> naming another node follows it;
    /// a <c>Moved</c> naming no owner, the node asked, or this node re-reads the map; <c>NotThisNode</c>, a placement
    /// refusal, and every transport failure re-read the registry and the map; an overload rejection, a lane's timeout, and
    /// <c>Busy</c> re-read neither.
    /// </summary>
    [Theory]
    [MemberData(nameof(ReadResentCases))]
    public async Task AReadWithoutEffectIsReadAgain(string answer)
    {
        var reads = _register.Reads;
        var again = answer == "MovedToOther" ? Other : Primary;
        switch (answer)
        {
            case "MovedToOther" or "MovedToNone" or "MovedToTheNodeAsked" or "MovedToSelf" or "NotThisNode" or "Busy":
                _grains.ScriptRead(Primary, () => answer switch
                {
                    "MovedToOther" => new JournalMoved(Other),
                    "MovedToNone" => new JournalMoved(Owner: null),
                    "MovedToTheNodeAsked" => new JournalMoved(Primary),
                    "MovedToSelf" => new JournalMoved(Self),
                    "NotThisNode" => NotThisNode.Instance,
                    _ => JournalForwardedRead.Busy.Instance,
                });
                break;
            default:
                _grains.ScriptRead(Primary, Failure(answer));
                break;
        }

        _grains.ScriptRead(again, () => Found([7], snapshot: null));

        Assert.Equal([7], await ReadBytesAsync(Router));

        Assert.Equal([Primary, again], _grains.Reads);
        var rereadsRegistry = answer is "NotThisNode" or "PlacementRefusal" or "LostReply" or "ResponseTimeout" or "SiloUnavailable" or "CalleeArgument";
        Assert.Equal(rereadsRegistry || answer is "MovedToNone" or "MovedToTheNodeAsked" or "MovedToSelf", _register.Reads > reads);
        Assert.Equal(rereadsRegistry ? 1 : 0, _registryRereads);
    }

    /// <summary>
    /// orleans.md §5.5, OR-I16: a read is not read again after a reply it fails to decode, corruption, or the owner's
    /// refusal in its answer: each is thrown, after the one call.
    /// </summary>
    [Theory]
    [InlineData("UndecodableReply", typeof(InvalidDataException))]
    [InlineData("Corrupt", typeof(JournalStoreCorruptException))]
    [InlineData("RefusedInvalid", typeof(InvalidDataException))]
    [InlineData("RefusedArgument", typeof(ArgumentException))]
    public async Task AReadWhichFailsToDecodeMeetsCorruptionOrIsRefusedThrows(string answer, Type thrown)
    {
        switch (answer)
        {
            case "RefusedInvalid":
                _grains.ScriptRead(Primary, () => new JournalCallRefused(JournalRefusal.Invalid, "refused"));
                break;
            case "RefusedArgument":
                _grains.ScriptRead(Primary, () => new JournalCallRefused(JournalRefusal.Argument, "no journal key"));
                break;
            default:
                _grains.ScriptRead(Primary, Failure(answer));
                break;
        }

        Assert.IsType(thrown, await Record.ExceptionAsync(() => ReadBytesAsync(Router)));
        Assert.Equal([Primary], _grains.Reads);
    }

    /// <summary>
    /// A later page answered <c>NotThisNode</c> comes from an activation off the snapshot's node, so the reading silo asks
    /// it for no release, which would not reach the snapshot, and reads the journal again after a registry and map re-read.
    /// </summary>
    [Fact]
    public async Task ALaterPagesNotThisNodeReadsAgainWithoutARelease()
    {
        var content = Content(JournalForwardedRead.PageBytes + 7);
        _grains.ScriptRead(Primary, () => Found(content, Guid.NewGuid()));
        _grains.ScriptPage(Primary, (_, _) => NotThisNode.Instance);
        _grains.ScriptRead(Primary, () => Found(content, Guid.NewGuid()));
        _grains.ScriptPage(Primary, (_, offset) => Page(content, offset));

        Assert.Equal(content, await ReadBytesAsync(Router));

        Assert.Empty(_grains.Released);
        Assert.Equal([Primary, Primary], _grains.Reads);
        Assert.Equal(1, _registryRereads);
    }

    /// <summary>
    /// A later page whose call fails, here as an unavailable silo, releases the read's pinned snapshot and reads the
    /// journal again from the start.
    /// </summary>
    [Fact]
    public async Task ALaterPageWhichFailsReleasesTheSnapshotAndReadsAgain()
    {
        var content = Content(JournalForwardedRead.PageBytes + 7);
        var snapshot = Guid.NewGuid();
        _grains.ScriptRead(Primary, () => Found(content, snapshot));
        _grains.ScriptPage(Primary, new SiloUnavailableException("The test's silo left."));
        _grains.ScriptRead(Primary, () => Found(content, Guid.NewGuid()));
        _grains.ScriptPage(Primary, (_, offset) => Page(content, offset));

        Assert.Equal(content, await ReadBytesAsync(Router));

        Assert.Equal([snapshot], _grains.Released);
    }

    /// <summary>
    /// The local path, as the remote one (orleans.md §5.5): a read of the silo's own Journals which meets the archive
    /// store's unavailability, as a history read does while a journal's rows lie in it, is read again after a backoff,
    /// sending nothing to another node, until an answer or a refusal ends it; here the Journals refuse the key after three
    /// failures. <c>OrleansControllerTests.AReadOfAJournalInTheArchiveWaitsForTheStoreAndCompletesOnceItAnswers</c> shows
    /// such a read completing once the store answers.
    /// </summary>
    [Fact]
    public async Task ALocalReadWhichMeetsTheArchivesUnavailabilityIsReadAgain()
    {
        await using var own = await OwnPrimary.StartAsync(_archive);
        var calls = 0;
        var refusal = new ArgumentException("The test's Journals refuse the key.");
        own.Journals.BeforeCall = () => Interlocked.Increment(ref calls) <= 3
            ? Task.FromException(new IOException("The test's object store refuses every call."))
            : Task.FromException(refusal);
        var router = NewRouter(routing: own.Routing, journals: own.Journals);

        Assert.Same(refusal, await Assert.ThrowsAsync<ArgumentException>(() => ReadBytesAsync(router)));

        Assert.Equal(4, calls);
        Assert.Empty(_grains.Reads);
    }

    /// <summary>
    /// The local path, as the remote one: corruption, invalid data, and the Journals' refusal of the key end a read of the
    /// silo's own Journals at once, thrown as they are.
    /// </summary>
    [Theory]
    [InlineData("Argument")]
    [InlineData("Invalid")]
    [InlineData("Corrupt")]
    [InlineData("ArchiveCorrupt")]
    public async Task ALocalReadWhichMeetsCorruptionOrARefusalThrowsIt(string failure)
    {
        await using var own = await OwnPrimary.StartAsync(_archive);
        var calls = 0;
        Exception thrown = failure switch
        {
            "Argument" => new ArgumentException("The test's Journals refuse the key."),
            "Invalid" => new InvalidDataException("The test's history is not one."),
            "Corrupt" => new JournalStoreCorruptException("The test's history contradicts itself."),
            _ => new ArchiveCorruptException("The test's archive contradicts itself."),
        };
        own.Journals.BeforeCall = () =>
        {
            _ = Interlocked.Increment(ref calls);
            return Task.FromException(thrown);
        };
        var router = NewRouter(routing: own.Routing, journals: own.Journals);

        Assert.Same(thrown, await Assert.ThrowsAnyAsync<Exception>(() => ReadBytesAsync(router)));

        Assert.Equal(1, calls);
        Assert.Empty(_grains.Reads);
    }

    /// <summary>
    /// The local path: a read the silo's own Journals answer <c>Moved</c> naming no owner, as stopped Journals do, backs off,
    /// reads the map again, and follows the primary the newer map names.
    /// </summary>
    [Fact]
    public async Task ALocalReadAnsweredMovedReadsTheMapAgain()
    {
        var register = new ScriptedConditionalRegister();
        await CommitAsync(register, new LedgerMapChange.Install([], [Opening(Self)], 1));
        var routing = new LocalRouting();
        routing.Publish(Self, await LedgerMapView.OpenAsync(register, Token), _archive);
        await CommitAsync(register, new LedgerMapChange.Install([new LedgerClosing(new LedgerId(1), Slot.Zero)], [Opening(Primary)], 2));
        var reads = register.Reads;
        var router = NewRouter(routing: routing);
        _grains.ScriptRead(Primary, () => Found([3], snapshot: null));

        Assert.Equal([3], await ReadBytesAsync(router));

        Assert.Equal([Primary], _grains.Reads);
        Assert.True(register.Reads > reads);
    }

    /// <summary>
    /// orleans.md §5.5, §5.12, OR-I17: forwarded writes and first read pages wait in separate lanes to their destination
    /// node, so a write is sent while a first page holds its whole lane, here one call, and a second read's first page waits
    /// for that lane; a pinned snapshot's later pages take no lane, so a later page is read while another read's first page
    /// holds the lane.
    /// </summary>
    [Fact]
    public async Task WritesAndFirstPagesTakeSeparateLanesAndLaterPagesTakeNone()
    {
        var router = NewRouter(TestReplicaLimits.Options(static options => options.MaxForwardReadCalls = 1));
        var content = Content(JournalForwardedRead.PageBytes + 7);
        Task<JournalWriteOutcome>? write = null;
        Task<RoutedRead>? second = null;
        var (writeSentDuringFirstPage, secondWaited) = (false, false);
        var laterPage = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _grains.Script(Primary, _ => Task.FromResult(Answer("Accepted")));
        _grains.ScriptRead(Primary, () =>
        {
            write = WriteAsync(router, Token);
            writeSentDuringFirstPage = _grains.Calls.Count == 1;
            second = router.ReadAsync("k", JournalRowKey.Encode("k"), Token);
            secondWaited = !second.IsCompleted && router.Budgets.Reads(Primary).Waiting == 1;
            return Found(content, Guid.NewGuid());
        });
        _grains.ScriptReadAsync(Primary, async () =>
        {
            await laterPage.Task.WaitAsync(Bound, Token);
            return Found([1, 2, 3], snapshot: null);
        });
        _grains.ScriptPage(Primary, (_, offset) =>
        {
            laterPage.SetResult();
            return Page(content, offset);
        });

        var first = await router.ReadAsync("k", JournalRowKey.Encode("k"), Token).WaitAsync(Bound, Token);
        using (first.Content)
        {
            Assert.Equal(content, Bytes(first.Content));
        }

        var other = await second!.WaitAsync(Bound, Token);
        using (other.Content)
        {
            Assert.Equal([1, 2, 3], Bytes(other.Content));
        }

        Assert.True(writeSentDuringFirstPage);
        Assert.True(secondWaited);
        _ = Assert.IsType<JournalWriteOutcome.Accepted>((await write!.WaitAsync(Bound, Token)).Value);
    }

    /// <summary>
    /// orleans.md §5.5, §7.2: the owner's refusal of a write is thrown as its kind's exception, <c>Argument</c> as
    /// <see cref="ArgumentException"/> and <c>Invalid</c> as <see cref="InvalidDataException"/>, without a resend.
    /// </summary>
    [Theory]
    [InlineData(JournalRefusal.Argument)]
    [InlineData(JournalRefusal.Invalid)]
    internal async Task TheOwnersRefusalOfAWriteIsThrownAsItsKind(JournalRefusal kind)
    {
        _grains.Script(Primary, _ => Task.FromResult<JournalForwardedWriteOutcome>(new JournalCallRefused(kind, "refused")));

        var failure = await Record.ExceptionAsync(() => WriteAsync(Router, Token));

        Assert.IsType(kind == JournalRefusal.Argument ? typeof(ArgumentException) : typeof(InvalidDataException), failure);
        Assert.Equal([Primary], _grains.Calls);
    }

    /// <summary>
    /// orleans.md §5.5, §7.2: the reading silo refuses, with <see cref="InvalidDataException"/>, a page whose length is not
    /// the page its offset asks for, and a refused page; a read which ends so, or by its caller's cancellation, while its
    /// snapshot is pinned releases the snapshot, and one whose snapshot expired or whose last page released it does not.
    /// </summary>
    [Theory]
    [InlineData("short")]
    [InlineData("long")]
    [InlineData("refused")]
    [InlineData("cancelled")]
    public async Task AReadWhichEndsWhileItsSnapshotIsPinnedReleasesIt(string ending)
    {
        var content = Content(2 * JournalForwardedRead.PageBytes + 7);
        var snapshot = Guid.NewGuid();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        _grains.ScriptRead(Primary, () => Found(content, snapshot));
        if (ending == "long")
        {
            // The last page, of 7 bytes, arrives with one more.
            _grains.ScriptPage(Primary, (_, offset) => Page(content, offset));
        }

        _grains.ScriptPage(Primary, (_, offset) => ending switch
        {
            "short" => new JournalForwardedPage.Page(_received.Track(WireBytes.Of(content.AsSpan((int)offset, 10)))),
            "long" => new JournalForwardedPage.Page(_received.Track(WireBytes.Of(content.AsSpan((int)offset - 1, 8)))),
            "refused" => new JournalCallRefused(JournalRefusal.Argument, "an offset outside the snapshot"),
            _ => Cancelled(),
        });

        var failure = await Record.ExceptionAsync(() => Router.ReadAsync("k", JournalRowKey.Encode("k"), cancellation.Token).WaitAsync(Bound, Token));

        if (ending == "cancelled")
        {
            _ = Assert.IsAssignableFrom<OperationCanceledException>(failure);
        }
        else
        {
            _ = Assert.IsType<InvalidDataException>(failure);
        }

        Assert.Equal([snapshot], _grains.Released);

        JournalForwardedPage Cancelled()
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        }
    }

    /// <summary>
    /// orleans.md §7.2: the reading silo refuses, with <see cref="InvalidDataException"/>, a first page which breaks the
    /// forwarded read's rules, a snapshot exactly when more pages follow and a first page of
    /// <c>min(PageBytes, ContentLength)</c> bytes, releasing a snapshot it names.
    /// </summary>
    [Theory]
    [InlineData("SnapshotForOnePage")]
    [InlineData("NoSnapshotForMore")]
    [InlineData("ShortFirstPage")]
    public async Task AFirstPageWhichBreaksTheReadsRulesIsRefused(string shape)
    {
        var snapshot = Guid.NewGuid();
        _grains.ScriptRead(Primary, () => shape switch
        {
            "SnapshotForOnePage" => Found([1, 2, 3], snapshot),
            "NoSnapshotForMore" => Found(Content(JournalForwardedRead.PageBytes + 1), snapshot: null),
            _ => new JournalForwardedRead.Found(JournalVersion.Origin, true, 3, ReplaceRequested: false, _received.Track(WireBytes.Of(1, 2)), Snapshot: null),
        });

        _ = Assert.IsType<InvalidDataException>(await Record.ExceptionAsync(() => ReadBytesAsync(Router)));

        Assert.Equal(shape == "SnapshotForOnePage" ? [snapshot] : [], _grains.Released);
    }

    /// <summary>A read whose snapshot expired, or whose last page released it, asks for no release.</summary>
    [Fact]
    public async Task AReadWhoseSnapshotIsReleasedAsksForNoRelease()
    {
        var content = Content(JournalForwardedRead.PageBytes + 7);
        _grains.ScriptRead(Primary, () => Found(content, Guid.NewGuid()));
        _grains.ScriptPage(Primary, (_, _) => JournalForwardedPage.Expired.Instance);
        _grains.ScriptRead(Primary, () => Found(content, Guid.NewGuid()));
        _grains.ScriptPage(Primary, (_, offset) => Page(content, offset));

        _ = await ReadBytesAsync(Router);

        Assert.Empty(_grains.Released);
    }

    /// <summary>
    /// orleans.md §5.5 (Q5): a journal of one page arrives whole in its first page, which the read's content holds as it
    /// was received, uncopied, until the content is disposed.
    /// </summary>
    [Fact]
    public async Task AForwardedReadOfOnePageIsItsFirstPage()
    {
        var page = _received.Track(WireBytes.Of("one page"u8));
        _grains.ScriptRead(Primary, () => new JournalForwardedRead.Found(JournalVersion.Origin, true, page.Length, ReplaceRequested: false, page, Snapshot: null));

        var read = await Router.ReadAsync("k", JournalRowKey.Encode("k"), Token).WaitAsync(Bound, Token);
        Assert.False(WireBytes.IsReleased(page));
        using (read.Content)
        {
            Assert.Equal("one page"u8.ToArray(), Bytes(read.Content));
        }

        Assert.True(WireBytes.IsReleased(page));
    }

    /// <summary>
    /// The storage's rules (orleans.md §5.5): a write before a read fails with <see cref="InvalidOperationException"/>; an
    /// empty <c>Append</c> is refused with <see cref="ArgumentOutOfRangeException"/>, as Orleans' contract names, and a body
    /// above <c>MaxBodyBytes</c>, an <c>Append</c>'s or a <c>Replace</c>'s, with <see cref="ArgumentException"/> naming the
    /// caller's parameter, before any send; and the metadata members throw <see cref="NotSupportedException"/>.
    /// </summary>
    [Fact]
    public async Task TheStorageRefusesWritesBeforeAReadAndEarlyRefusalsBeforeAnySend()
    {
        IJournalStorage storage = Storage(maxBodyBytes: 4);

        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => storage.AppendAsync(Sequence(1), Token).AsTask().WaitAsync(Bound, Token));
        _grains.ScriptRead(Primary, () => Found([], snapshot: null, exists: false));
        await storage.ReadAsync(new Consumer(), Token).AsTask().WaitAsync(Bound, Token);
        Assert.Equal("value", (await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => storage.AppendAsync(default, Token).AsTask().WaitAsync(Bound, Token))).ParamName);
        Assert.Equal("value", (await Assert.ThrowsAsync<ArgumentException>(() => storage.AppendAsync(Sequence(1, 2, 3, 4, 5), Token).AsTask().WaitAsync(Bound, Token))).ParamName);
        Assert.Equal("value", (await Assert.ThrowsAsync<ArgumentException>(() => storage.ReplaceAsync(Sequence(1, 2, 3, 4, 5), Token).AsTask().WaitAsync(Bound, Token))).ParamName);
        Assert.Empty(_grains.Calls);
        _ = await Assert.ThrowsAsync<NotSupportedException>(() => storage.GetMetadataAsync(Token).AsTask());
        _ = await Assert.ThrowsAsync<NotSupportedException>(() => storage.CreateIfNotExistsAsync(null, Token).AsTask());
        _ = await Assert.ThrowsAsync<NotSupportedException>(() => storage.UpdateMetadataAsync(null, null, null, Token).AsTask());
    }

    /// <summary>
    /// orleans.md §5.5, OR-I7: a journal whose key the archive store cannot name, here an archive store which can name no
    /// key, is refused with <see cref="ArgumentException"/> before any send, so it has no effect.
    /// </summary>
    [Fact]
    public async Task AKeyTheArchiveStoreCannotNameIsRefusedBeforeAnySend()
    {
        var routing = new LocalRouting();
        routing.Publish(Self, (await _routing.ReadyAsync(Token)).View, new NamelessArchive(_archive));
        IJournalStorage storage = new DurableJournalStorage(new JournalId("k"), Router, routing, 1 << 20, "json-lines", TimeProvider.System);
        _grains.ScriptRead(Primary, () => Found([], snapshot: null, exists: false));
        await storage.ReadAsync(new Consumer(), Token).AsTask().WaitAsync(Bound, Token);

        _ = await Assert.ThrowsAsync<ArgumentException>(() => storage.AppendAsync(Sequence(1), Token).AsTask().WaitAsync(Bound, Token));
        _ = await Assert.ThrowsAsync<ArgumentException>(() => storage.ReplaceAsync(Sequence(1), Token).AsTask().WaitAsync(Bound, Token));
        _ = await Assert.ThrowsAsync<ArgumentException>(() => storage.DeleteAsync(Token).AsTask().WaitAsync(Bound, Token));
        Assert.Empty(_grains.Calls);
    }

    /// <summary>
    /// orleans.md §5.5, OR-I8: a read delivers its content once, completed, with the silo's format key and the version as
    /// its ETag; an absent journal reads with <c>JournalMetadata.Empty</c>; and a consumer which leaves bytes unread fails
    /// the read.
    /// </summary>
    [Fact]
    public async Task AReadDeliversItsContentAndMetadata()
    {
        _grains.ScriptRead(Primary, () => Found("abc"u8.ToArray(), snapshot: null));
        var consumer = new Consumer();
        await Storage().ReadAsync(consumer, Token).AsTask().WaitAsync(Bound, Token);
        Assert.Equal("abc"u8.ToArray(), consumer.Bytes);
        Assert.Equal((JournalVersion.Origin.ToString(), "json-lines"), (consumer.Metadata!.ETag, consumer.Metadata.FormatKey));

        _grains.ScriptRead(Primary, () => Found([], snapshot: null, exists: false));
        var absent = new Consumer();
        await Storage().ReadAsync(absent, Token).AsTask().WaitAsync(Bound, Token);
        Assert.Same(JournalMetadata.Empty, absent.Metadata);

        _grains.ScriptRead(Primary, () => Found("abc"u8.ToArray(), snapshot: null));
        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => Storage().ReadAsync(new Consumer(readsNothing: true), Token).AsTask().WaitAsync(Bound, Token));
    }

    /// <summary>OR-I8: an entry's record carries the Orleans bytes unwrapped as its body, at the version the instance read.</summary>
    [Fact]
    public async Task AWriteSendsTheEncodedRecord()
    {
        var storage = Storage();
        _grains.ScriptRead(Primary, () => Found("abc"u8.ToArray(), snapshot: null));
        await storage.ReadAsync(new Consumer(), Token).AsTask().WaitAsync(Bound, Token);
        byte[]? sent = null;
        _grains.Script(Primary, entry =>
        {
            sent = entry.Record.ToArray();
            return Task.FromResult(Answer("Accepted"));
        });

        await storage.AppendAsync(Sequence(1), Token).AsTask().WaitAsync(Bound, Token);

        using var payload = ArcBuffer.Wrap(new byte[] { 1 });
        using var record = JournalEntryRecord.Append(JournalRowKey.Encode("k"), JournalVersion.Origin, payload);
        using var expected = record.ToArcBuffer();
        Assert.Equal(expected.ToArray(), sent);
    }

    /// <summary>
    /// OR-I7: only <c>Accepted</c> completes a write. <c>Rejected</c> is <see cref="InconsistentStateException"/> with the
    /// stored version and the instance's as its ETags, and <c>ReplaceRequired</c> one too, which latches the replace
    /// request; <c>Ambiguous</c> is <see cref="JournalWriteOutcomeUnknownException"/>, which asserts nothing; neither
    /// <c>Rejected</c> nor <c>Ambiguous</c> touches the latch, set or not.
    /// </summary>
    [Theory]
    [InlineData("Rejected", false)]
    [InlineData("Rejected", true)]
    [InlineData("ReplaceRequired", false)]
    [InlineData("Ambiguous", false)]
    [InlineData("Ambiguous", true)]
    public async Task OnlyAcceptedCompletesAWrite(string answer, bool latched)
    {
        var storage = Storage();
        _grains.ScriptRead(Primary, () => Found([], snapshot: null, exists: false, replaceRequested: latched));
        await storage.ReadAsync(new Consumer(), Token).AsTask().WaitAsync(Bound, Token);
        _grains.Script(Primary, _ => Task.FromResult(Answer(answer)));
        if (answer == "Rejected")
        {
            // The read after the rejection finds content other than the instance's, so the rejection stands.
            _grains.ScriptRead(Primary, () => Found([9], snapshot: null));
        }

        var failure = await Record.ExceptionAsync(() => storage.AppendAsync(Sequence(1), Token).AsTask().WaitAsync(Bound, Token));

        switch (answer)
        {
            case "Rejected":
                var rejected = Assert.IsType<InconsistentStateException>(failure);
                Assert.Equal(("BAEAAAQA", JournalVersion.Origin.ToString()), (rejected.StoredEtag, rejected.CurrentEtag));
                break;
            case "ReplaceRequired":
                _ = Assert.IsType<InconsistentStateException>(failure);
                break;
            default:
                Assert.Equal("k", Assert.IsType<JournalWriteOutcomeUnknownException>(failure).Journal);
                break;
        }

        Assert.Equal(latched || answer == "ReplaceRequired", storage.IsCompactionRequested);
    }

    /// <summary>
    /// Orleans' read validation (orleans.md §5.5): a later read through the instance at the version it holds delivers
    /// its content, and one which finds the version changed is refused before it delivers anything.
    /// </summary>
    [Fact]
    public async Task AReadAtTheInstancesVersionDeliversAndAChangedOneIsRefusedBeforeDelivering()
    {
        var storage = Storage();
        var accepted = JournalVersion.Parse("BAEAAAQA");
        _grains.ScriptRead(Primary, () => Found("abc"u8.ToArray(), snapshot: null));
        await storage.ReadAsync(new Consumer(), Token).AsTask().WaitAsync(Bound, Token);
        _grains.Script(Primary, _ => Task.FromResult<JournalForwardedWriteOutcome>(new JournalWriteOutcome.Accepted(accepted, ReplaceRequested: false)));
        await storage.AppendAsync(Sequence(1), Token).AsTask().WaitAsync(Bound, Token);

        _grains.ScriptRead(Primary, () => Found("abc\u0001"u8.ToArray(), snapshot: null, version: accepted));
        var again = new Consumer();
        await storage.ReadAsync(again, Token).AsTask().WaitAsync(Bound, Token);
        Assert.Equal("abc\u0001"u8.ToArray(), again.Bytes);
        Assert.Equal(accepted.ToString(), again.Metadata!.ETag);

        _grains.ScriptRead(Primary, () => Found("abc"u8.ToArray(), snapshot: null));
        var stale = new Consumer();
        _ = await Assert.ThrowsAsync<InconsistentStateException>(() => storage.ReadAsync(stale, Token).AsTask().WaitAsync(Bound, Token));
        Assert.Null(stale.Bytes);
    }

    /// <summary>
    /// The replace latch (orleans.md §5.5, OR-I7): <c>ReplaceRequired</c> sets <c>IsCompactionRequested</c>, an
    /// <c>Append</c> accepted meanwhile keeps it, an accepted <c>Replace</c> or <c>Delete</c> which asks for none clears it,
    /// and an accepted write which asks for a replace sets it again.
    /// </summary>
    [Theory]
    [InlineData("Replace")]
    [InlineData("Delete")]
    public async Task ReplaceRequiredLatchesTheReplaceRequestUntilAReplaceOrDeleteIsAccepted(string clearing)
    {
        var storage = Storage();
        _grains.ScriptRead(Primary, () => Found([], snapshot: null, exists: false));
        await storage.ReadAsync(new Consumer(), Token).AsTask().WaitAsync(Bound, Token);
        Assert.False(storage.IsCompactionRequested);

        _grains.Script(Primary, _ => Task.FromResult(Answer("ReplaceRequired")));
        _ = await Assert.ThrowsAsync<InconsistentStateException>(() => storage.AppendAsync(Sequence(1), Token).AsTask().WaitAsync(Bound, Token));
        Assert.True(storage.IsCompactionRequested);
        _grains.Script(Primary, _ => Task.FromResult(Answer("Accepted")));
        await storage.AppendAsync(Sequence(1), Token).AsTask().WaitAsync(Bound, Token);
        Assert.True(storage.IsCompactionRequested);
        _grains.Script(Primary, _ => Task.FromResult(Answer("Accepted")));
        await (clearing == "Replace" ? storage.ReplaceAsync(Sequence(1), Token).AsTask().WaitAsync(Bound, Token) : storage.DeleteAsync(Token).AsTask().WaitAsync(Bound, Token));
        Assert.False(storage.IsCompactionRequested);
        _grains.Script(Primary, _ => Task.FromResult<JournalForwardedWriteOutcome>(new JournalWriteOutcome.Accepted(JournalVersion.Origin, ReplaceRequested: true)));
        await storage.AppendAsync(Sequence(1), Token).AsTask().WaitAsync(Bound, Token);
        Assert.True(storage.IsCompactionRequested);
    }

    /// <summary>
    /// orleans.md §5.5, HI-I5: an instance answered <c>ReplaceRequired</c> is fenced, and the writer recovers through a new
    /// instance, whose read carries the replace request a refusing store asks for, so it answers
    /// <c>IsCompactionRequested</c> before any write; its later read without the request keeps the latch.
    /// </summary>
    [Fact]
    public async Task AWriterRefusedAnAppendReadsTheReplaceRequestThroughANewInstance()
    {
        var refused = Storage();
        _grains.ScriptRead(Primary, () => Found([], snapshot: null, exists: false));
        await refused.ReadAsync(new Consumer(), Token).AsTask().WaitAsync(Bound, Token);
        _grains.Script(Primary, _ => Task.FromResult(Answer("ReplaceRequired")));
        _ = await Assert.ThrowsAsync<InconsistentStateException>(() => refused.AppendAsync(Sequence(1), Token).AsTask().WaitAsync(Bound, Token));

        var recovering = Storage();
        _grains.ScriptRead(Primary, () => Found([], snapshot: null, exists: false, replaceRequested: true));
        await recovering.ReadAsync(new Consumer(), Token).AsTask().WaitAsync(Bound, Token);
        Assert.True(recovering.IsCompactionRequested);
        _grains.ScriptRead(Primary, () => Found([], snapshot: null, exists: false));
        await recovering.ReadAsync(new Consumer(), Token).AsTask().WaitAsync(Bound, Token);
        Assert.True(recovering.IsCompactionRequested);
    }

    /// <summary>The latch is each instance's own (Q2): another instance of the journal, whose read asks for none, answers no request.</summary>
    [Fact]
    public async Task EachInstanceOfAJournalHasItsOwnLatch()
    {
        var asked = Storage();
        var other = Storage();
        _grains.ScriptRead(Primary, () => Found([], snapshot: null, exists: false, replaceRequested: true));
        _grains.ScriptRead(Primary, () => Found([], snapshot: null, exists: false));

        await asked.ReadAsync(new Consumer(), Token).AsTask().WaitAsync(Bound, Token);
        await other.ReadAsync(new Consumer(), Token).AsTask().WaitAsync(Bound, Token);

        Assert.True(asked.IsCompactionRequested);
        Assert.False(other.IsCompactionRequested);
    }

    /// <summary>
    /// orleans.md §5.5, OR-I7, #352: a write rejected while the journal holds the instance's content under another name,
    /// as after its rows drop and its primary rebases it, reads the journal once and is sent once more, conditional on the
    /// version just read, which its acceptance completes. The instance's content is its read's followed by each accepted
    /// write's: an <c>Append</c> extends it, a <c>Replace</c> starts it again, and a <c>Delete</c> empties it.
    /// </summary>
    /// <param name="kind">The accepted write before the rejected one.</param>
    [Theory]
    [InlineData("Append")]
    [InlineData("Replace")]
    [InlineData("Delete")]
    public async Task ARejectedWriteWhoseJournalHoldsTheInstancesContentIsSentOnceMoreAtTheVersionRead(string kind)
    {
        var storage = Storage();
        _grains.ScriptRead(Primary, () => Found("ab"u8.ToArray(), snapshot: null));
        await storage.ReadAsync(new Consumer(), Token).AsTask().WaitAsync(Bound, Token);
        _grains.Script(Primary, _ => Task.FromResult(Answer("Accepted")));
        switch (kind)
        {
            case "Append":
                await storage.AppendAsync(new ReadOnlySequence<byte>("c"u8.ToArray()), Token).AsTask().WaitAsync(Bound, Token);
                break;
            case "Replace":
                await storage.ReplaceAsync(new ReadOnlySequence<byte>("xy"u8.ToArray()), Token).AsTask().WaitAsync(Bound, Token);
                break;
            default:
                await storage.DeleteAsync(Token).AsTask().WaitAsync(Bound, Token);
                break;
        }

        var rebased = JournalVersion.Parse("BAIAAAQA");
        var sent = new List<byte[]>();
        _grains.Script(Primary, entry =>
        {
            sent.Add(entry.Record.ToArray());
            return Task.FromResult(Answer("Rejected"));
        });
        byte[] held = kind switch { "Append" => "abc"u8.ToArray(), "Replace" => "xy"u8.ToArray(), _ => [] };
        _grains.ScriptRead(Primary, () => Found(held, snapshot: null, exists: kind != "Delete", version: rebased));
        _grains.Script(Primary, entry =>
        {
            sent.Add(entry.Record.ToArray());
            return Task.FromResult<JournalForwardedWriteOutcome>(new JournalWriteOutcome.Accepted(JournalVersion.Parse("BAIAAAQB"), ReplaceRequested: false));
        });

        await storage.AppendAsync(Sequence(1), Token).AsTask().WaitAsync(Bound, Token);

        Assert.Equal([Encoded(JournalVersion.Origin, 1), Encoded(rebased, 1)], sent);
    }

    /// <summary>
    /// OR-I7: a rejected write whose read after the rejection finds the content changed, as another writer leaves it, stands
    /// as <see cref="InconsistentStateException"/> with the version the rejection reported, and is not sent again.
    /// </summary>
    [Fact]
    public async Task ARejectedWriteWhoseJournalsContentChangedIsNotSentAgain()
    {
        var storage = Storage();
        _grains.ScriptRead(Primary, () => Found("ab"u8.ToArray(), snapshot: null));
        await storage.ReadAsync(new Consumer(), Token).AsTask().WaitAsync(Bound, Token);
        _grains.Script(Primary, _ => Task.FromResult(Answer("Rejected")));
        _grains.ScriptRead(Primary, () => Found("ad"u8.ToArray(), snapshot: null, version: JournalVersion.Parse("BAIAAAQA")));

        var rejected = await Assert.ThrowsAsync<InconsistentStateException>(() => storage.AppendAsync(Sequence(1), Token).AsTask().WaitAsync(Bound, Token));

        Assert.Equal("BAEAAAQA", rejected.StoredEtag);
        Assert.Equal([Primary], _grains.Calls);
    }

    /// <summary>
    /// OR-I7: the write is sent once more at most: a second <c>Rejected</c>, as when the journal changed between the read and
    /// the write, stands as <see cref="InconsistentStateException"/>; and the read after the rejection fails as any read
    /// does, corruption surfacing as itself, with the write sent once.
    /// </summary>
    /// <param name="reread">The read after the rejection: <c>Same</c> content, or one which finds the history corrupt.</param>
    [Theory]
    [InlineData("Same")]
    [InlineData("Corrupt")]
    public async Task TheWriteIsSentOnceMoreAtMostAndAFailedReadFailsAsAReadDoes(string reread)
    {
        var storage = Storage();
        _grains.ScriptRead(Primary, () => Found("ab"u8.ToArray(), snapshot: null));
        await storage.ReadAsync(new Consumer(), Token).AsTask().WaitAsync(Bound, Token);
        _grains.Script(Primary, _ => Task.FromResult(Answer("Rejected")));
        if (reread == "Same")
        {
            _grains.ScriptRead(Primary, () => Found("ab"u8.ToArray(), snapshot: null, version: JournalVersion.Parse("BAIAAAQA")));
            _grains.Script(Primary, _ => Task.FromResult(Answer("Rejected")));
            _ = await Assert.ThrowsAsync<InconsistentStateException>(() => storage.AppendAsync(Sequence(1), Token).AsTask().WaitAsync(Bound, Token));
            Assert.Equal([Primary, Primary], _grains.Calls);
        }
        else
        {
            _grains.ScriptReadAsync(Primary, static () => Task.FromException<JournalForwardedRead>(new JournalStoreCorruptException("The test's history contradicts itself.")));
            _ = await Assert.ThrowsAsync<JournalStoreCorruptException>(() => storage.AppendAsync(Sequence(1), Token).AsTask().WaitAsync(Bound, Token));
            Assert.Equal([Primary], _grains.Calls);
        }
    }

    private static byte[] Encoded(JournalVersion expected, params byte[] body)
    {
        using var payload = ArcBuffer.Wrap(body);
        using var record = JournalEntryRecord.Append(JournalRowKey.Encode("k"), expected, payload);
        using var bytes = record.ToArcBuffer();
        return bytes.ToArray();
    }

    private static JournalForwardedWriteOutcome Answer(string answer) => answer switch
    {
        "Accepted" => new JournalWriteOutcome.Accepted(JournalVersion.Origin, ReplaceRequested: false),
        "Rejected" => new JournalWriteOutcome.Rejected(JournalVersion.Parse("BAEAAAQA")),
        "ReplaceRequired" => JournalWriteOutcome.ReplaceRequired.Instance,
        "Ambiguous" => JournalWriteOutcome.Ambiguous.Instance,
        "Fenced" => JournalWriteOutcome.Fenced.Instance,
        "Throttled" => LocalJournalWriteOutcome.Throttled.Instance,
        "Unavailable" => LocalJournalWriteOutcome.Unavailable.Instance,
        "NotThisNode" => NotThisNode.Instance,
        "MovedToNone" => new JournalMoved(Owner: null),
        _ => throw new ArgumentOutOfRangeException(nameof(answer), answer, "No such answer."),
    };

    private static Exception Failure(string failure) => failure switch
    {
        "LostReply" => new OrleansException("The test's reply was lost."),
        "ResponseTimeout" => new TimeoutException("The test's response timed out."),
        "SiloUnavailable" => new SiloUnavailableException("The test's silo left."),
        "CalleeDisposed" => new ObjectDisposedException("JournalStore"),
        "CalleeArgument" => new ArgumentOutOfRangeException("offset", "The test's callee failed with an argument exception."),
        "UndecodableReply" => new InvalidDataException("The test's reply failed to decode."),
        "NestedOverload" => new OrleansException("The test's callee failed.", new InvalidOperationException("A call it made was shed.", new LimitExceededException("The test's silo sheds the call."))),
        "NestedPlacementRefusal" => new OrleansException("The test's callee failed.", new NodePlacementRefusedException(Other, registered: null)),
        "ForwardQueueTimeoutFromTheCallee" or "ForwardQueueTimeout" => new JournalForwardQueueTimeoutException(Primary, TimeSpan.FromSeconds(30)),
        "PlacementRefusal" => new NodePlacementRefusedException(Primary, registered: null),
        "OverloadRejection" => new LimitExceededException("The test's silo sheds the call."),
        "RejectedOverload" => Rejection(new LimitExceededException("The test's silo sheds the call.")),
        "Corrupt" => new JournalStoreCorruptException("The test's history contradicts itself."),
        _ => new InvalidOperationException("The test's callee failed."),
    };

    private static LedgerOpening Opening(NodeId primary) =>
        new(KeyRange.Root, [primary, .. new[] { Self, Primary, Other }.Where(node => node != primary)], 4, new PrimaryClaim(primary, SequencerInstanceId.New()));

    /// <summary>Commits <paramref name="change"/> to <paramref name="register"/> through a view of its own, which no router holds.</summary>
    private static async Task CommitAsync(IConditionalRegister register, LedgerMapChange change)
    {
        var view = await LedgerMapView.OpenAsync(register, Token);
        await using var committer = new LedgerMapCommitter(register, 1 << 20, view);
        _ = Assert.IsType<LedgerMapCommitOutcome.Applied>((await committer.CommitAsync(change, Token)).Value);
    }

    private static byte[] Content(int length) => [.. Enumerable.Range(0, length).Select(static index => (byte)(index % 251))];

    private static int RecordLength()
    {
        using var payload = ArcBuffer.Wrap("abc"u8.ToArray());
        using var record = JournalEntryRecord.Append(JournalRowKey.Encode("k"), JournalVersion.Origin, payload);
        return checked((int)record.Length);
    }

    /// <summary>Returns Orleans' message rejection of a call whose cause is <paramref name="inner"/>, as Orleans raises it.</summary>
    private static OrleansMessageRejectionException Rejection(Exception inner) =>
        (OrleansMessageRejectionException)Activator.CreateInstance(typeof(OrleansMessageRejectionException), BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, binder: null, ["The test's call was rejected.", inner], culture: null)!;

    private static async Task UntilAsync(Func<bool> condition, string what, Action? step = null)
    {
        var started = TimeProvider.System.GetTimestamp();
        while (!condition())
        {
            Assert.True(TimeProvider.System.GetElapsedTime(started) < Bound, $"Timed out waiting for {what}.");
            step?.Invoke();
            await Task.Delay(TimeSpan.FromMilliseconds(5), TimeProvider.System, Token);
        }
    }

    private JournalForwardedRead Found(byte[] content, Guid? snapshot, bool exists = true, bool replaceRequested = false, JournalVersion? version = null) =>
        new JournalForwardedRead.Found(version ?? JournalVersion.Origin, exists, content.Length, replaceRequested, _received.Track(WireBytes.Of(content.AsSpan(0, Math.Min(content.Length, JournalForwardedRead.PageBytes)))), snapshot);

    private JournalForwardedPage Page(byte[] content, long offset) =>
        new JournalForwardedPage.Page(_received.Track(WireBytes.Of(content.AsSpan((int)offset, (int)Math.Min(JournalForwardedRead.PageBytes, content.Length - offset)))));

    private static ReadOnlySequence<byte> Sequence(params byte[] bytes) => new(bytes);

    private static byte[] Bytes(OrleansBufferWriter content)
    {
        var slice = content.Reader.PeekSlice(content.Length);
        try
        {
            return slice.ToArray();
        }
        finally
        {
            slice.Dispose();
        }
    }

    private DurableJournalStorage Storage(int maxBodyBytes = 1 << 20) => new(new JournalId("k"), Router, _routing, maxBodyBytes, "json-lines", TimeProvider.System);

    private static async Task<JournalWriteOutcome> WriteAsync(JournalRouter router, CancellationToken cancellationToken)
    {
        using var payload = ArcBuffer.Wrap("abc"u8.ToArray());
        using var record = JournalEntryRecord.Append(JournalRowKey.Encode("k"), JournalVersion.Origin, payload);
        return await router.WriteAsync("k", JournalRowKey.Encode("k"), record, cancellationToken).WaitAsync(Bound, Token);
    }

    private static async Task<byte[]> ReadBytesAsync(JournalRouter router)
    {
        var read = await router.ReadAsync("k", JournalRowKey.Encode("k"), Token).WaitAsync(Bound, Token);
        using (read.Content)
        {
            return Bytes(read.Content);
        }
    }

    private JournalRouter NewRouter(DurableStorageOptions? options = null, LocalRouting? routing = null, LocalJournals? journals = null, TimeProvider? time = null, ILogger<JournalRouter>? logger = null) => new(
        routing ?? _routing,
        journals ?? new LocalJournals(new NodePressure(TimeSpan.FromSeconds(1), TimeProvider.System, NullLogger<NodePressure>.Instance)),
        () => _ = Interlocked.Increment(ref _registryRereads),
        new ForwardingBudgets(options ?? TestReplicaLimits.Options(), time ?? TimeProvider.System),
        TestReplicaLimits.Of(),
        _grains.For,
        time ?? TimeProvider.System,
        logger ?? NullLogger<JournalRouter>.Instance,
        (TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(20)));

    /// <summary>A journal storage consumer which reads every byte it is given, and keeps them and the metadata.</summary>
    private sealed class Consumer(bool readsNothing = false) : IJournalStorageConsumer
    {
        public byte[]? Bytes { get; private set; }

        public IJournalMetadata? Metadata { get; private set; }

        public void Read(JournalBufferReader reader, IJournalMetadata? metadata)
        {
            Metadata = metadata;
            if (readsNothing)
            {
                return;
            }

            Bytes = [.. Bytes ?? [], .. reader.ToArray()];
            reader.Skip(reader.Length);
        }
    }

    /// <summary>An archive store which can name no key, as a provider refuses a key beyond its naming limits.</summary>
    private sealed class NamelessArchive(IArchiveStore inner) : IArchiveStore
    {
        public ArchiveStoreKind Kind => inner.Kind;

        public int? MaxAppendBytes => inner.MaxAppendBytes;

        public ValueTask<ArchiveStoreRead> ReadAsync(ReadOnlyMemory<byte> key, LedgerMapValue map, CancellationToken cancellationToken) => inner.ReadAsync(key, map, cancellationToken);

        public ValueTask<ArchiveStoreRecord> StoreRecordAsync(IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken) => inner.StoreRecordAsync(parameters, cancellationToken);

        public ArchivePlan Begin(RowToken token) => inner.Begin(token);

        public bool Holds(ArchivePrefix prefix, ArchiveWrite write, int count) => inner.Holds(prefix, write, count);

        public bool IsReplaceRequested(RowToken token) => inner.IsReplaceRequested(token);

        public bool AdmitsKey(ReadOnlySpan<byte> key) => false;
    }

    /// <summary>A register whose next reads fail, as an unavailable MetaStore's do.</summary>
    private sealed class FailingReads(IConditionalRegister inner) : IConditionalRegister
    {
        private int _failures;

        public int Failed { get; private set; }

        /// <summary>Gets or sets whether reads find the register disposed, as a stopped runtime's do.</summary>
        public bool Disposed { get; set; }

        public void FailNext(int reads) => Volatile.Write(ref _failures, reads);

        public ValueTask<MetaStoreEntry?> ReadAsync(CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            if (Interlocked.Decrement(ref _failures) >= 0)
            {
                Failed++;
                throw new IOException("The test's MetaStore is unavailable.");
            }

            return inner.ReadAsync(cancellationToken);
        }

        public ValueTask<RegisterWriteOutcome> WriteAsync(MetaStoreVersion? expectedVersion, ReadOnlySequence<byte> value, CancellationToken cancellationToken = default) =>
            inner.WriteAsync(expectedVersion, value, cancellationToken);
    }

    /// <summary>
    /// This silo's own Journals, serving over a map in which the silo's node is the primary, whose calls the test fails
    /// before the store as the Journals' refusals would.
    /// </summary>
    private sealed class OwnPrimary : IAsyncDisposable
    {
        private LedgerReader? _reader;
        private LedgerWriterRegistry? _writers;
        private JournalStore? _store;

        public LocalJournals Journals { get; } = new(new NodePressure(TimeSpan.FromSeconds(1), TimeProvider.System, NullLogger<NodePressure>.Instance));

        public LocalRouting Routing { get; } = new();

        public static async Task<OwnPrimary> StartAsync(IArchiveStore archive)
        {
            var register = new ScriptedConditionalRegister();
            await CommitAsync(register, new LedgerMapChange.Install([], [Opening(Self)], 1));
            var view = await LedgerMapView.OpenAsync(register, Token);
            var primary = new OwnPrimary();
            primary._reader = new LedgerReader(view, new NoReplicas(), TimeProvider.System, Self);
            primary._writers = new LedgerWriterRegistry(primary._reader, 32 << 20, LedgerWriterOptions.Default, TimeProvider.System, NullLoggerFactory.Instance);
            primary._store = new JournalStore(primary._writers, archive, new JournalStoreOptions(), TimeProvider.System);
            Assert.True(primary.Journals.Serve(primary._store, view, Self));
            primary.Routing.Publish(Self, view, archive);
            return primary;
        }

        public async ValueTask DisposeAsync()
        {
            Journals.Stop();
            await _store!.DisposeAsync();
            await _writers!.DisposeAsync();
            await _reader!.DisposeAsync();
        }
    }

    /// <summary>
    /// Each node's journal grain, answering each call from its node's script in order, recording the nodes called, and
    /// tracking each forwarded write's record, the router's sender copy, which the router releases.
    /// </summary>
    private sealed class ScriptedGrains(OrleansBufferLedger received)
    {
        private readonly OrleansBufferLedger _received = received;
        private readonly ConcurrentDictionary<NodeId, ConcurrentQueue<Func<JournalForwardedEntry, Task<JournalForwardedWriteOutcome>>>> _writes = new();
        private readonly ConcurrentDictionary<NodeId, ConcurrentQueue<Func<Task<JournalForwardedRead>>>> _reads = new();
        private readonly ConcurrentDictionary<NodeId, ConcurrentQueue<Func<Guid, long, Task<JournalForwardedPage>>>> _pages = new();
        private readonly ConcurrentQueue<NodeId> _calls = new();
        private readonly ConcurrentQueue<NodeId> _readCalls = new();
        private readonly ConcurrentQueue<Guid> _released = new();

        /// <summary>Gets the nodes each write was sent to, in order.</summary>
        public List<NodeId> Calls => [.. _calls];

        /// <summary>Gets the nodes each read's first page was asked of, in order.</summary>
        public List<NodeId> Reads => [.. _readCalls];

        /// <summary>Gets the snapshots the reading silo released, in order.</summary>
        public List<Guid> Released => [.. _released];

        /// <summary>Gets or sets the answer of a write no script is left for, given the calls so far; none by default.</summary>
        public Func<JournalForwardedEntry, int, Task<JournalForwardedWriteOutcome>>? Default { get; set; }

        public void Script(NodeId node, Func<JournalForwardedEntry, Task<JournalForwardedWriteOutcome>> answer) => _writes.GetOrAdd(node, static _ => new()).Enqueue(answer);

        public void ScriptRead(NodeId node, Func<JournalForwardedRead> answer) => ScriptReadAsync(node, () => Task.FromResult(answer()));

        public void ScriptRead(NodeId node, Exception failure) => ScriptReadAsync(node, () => Task.FromException<JournalForwardedRead>(failure));

        public void ScriptReadAsync(NodeId node, Func<Task<JournalForwardedRead>> answer) => _reads.GetOrAdd(node, static _ => new()).Enqueue(answer);

        public void ScriptPage(NodeId node, Func<Guid, long, JournalForwardedPage> answer) => _pages.GetOrAdd(node, static _ => new()).Enqueue((snapshot, offset) => Task.FromResult(answer(snapshot, offset)));

        public void ScriptPage(NodeId node, Exception failure) => _pages.GetOrAdd(node, static _ => new()).Enqueue((_, _) => Task.FromException<JournalForwardedPage>(failure));

        public IJournalStoreGrain For(NodeId node) => new Grain(this, node);

        private sealed class Grain(ScriptedGrains grains, NodeId node) : IJournalStoreGrain
        {
            public Task<JournalForwardedWriteOutcome> WriteAsync(JournalForwardedEntry entry, CancellationToken cancellationToken)
            {
                _ = grains._received.Track(entry.Record);
                grains._calls.Enqueue(node);
                return grains._writes.TryGetValue(node, out var script) && script.TryDequeue(out var answer)
                    ? answer(entry)
                    : grains.Default?.Invoke(entry, grains._calls.Count) ?? throw new InvalidOperationException($"No write is scripted at node {node}.");
            }

            public Task<JournalForwardedRead> ReadAsync(string key, CancellationToken cancellationToken)
            {
                grains._readCalls.Enqueue(node);
                return grains._reads.TryGetValue(node, out var script) && script.TryDequeue(out var answer)
                    ? answer()
                    : throw new InvalidOperationException($"No read is scripted at node {node}.");
            }

            public Task<JournalForwardedPage> ReadPageAsync(Guid snapshot, long offset, CancellationToken cancellationToken) =>
                grains._pages.TryGetValue(node, out var script) && script.TryDequeue(out var answer)
                    ? answer(snapshot, offset)
                    : throw new InvalidOperationException($"No page is scripted at node {node}.");

            public Task ReleaseSnapshotAsync(Guid snapshot)
            {
                grains._released.Enqueue(snapshot);
                return Task.CompletedTask;
            }
        }
    }
}
