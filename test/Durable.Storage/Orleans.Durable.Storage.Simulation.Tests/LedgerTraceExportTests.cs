using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Simulation.Tests;

/// <summary>
/// <see cref="LedgerTraceExport"/> renders a trace as the records LedgerProtocolTrace.qnt binds to the ledger model's
/// actions, deriving across events what no single event shows (test/Durable.Storage/docs/spec/verification.md §5.1).
/// </summary>
public sealed class LedgerTraceExportTests
{
    private const string Node = "replica-0";
    private const string Corrupt = nameof(LedgerReplicaCorruptException);

    private static LedgerId Ledger => SimulatedStorage.Ledger;

    /// <summary>
    /// A writer's append renders as the module the trace specification imports, with the constants the run fixes.
    /// </summary>
    [Fact]
    public void AnAppendRendersAsTheTraceModule()
    {
        LedgerEvent[] events =
        [
            new(0, LedgerEventKind.Acquired, "writer", Ledger) { Address = 0, ContiguousTail = 0 },
            Called(1, "writer", LedgerOperation.WriterWrite, 0),
            Served(1, "writer", LedgerOperation.WriterWrite, 0, nameof(ReplicaAnswers.Written)),
            Answered(1, "writer", LedgerOperation.WriterWrite, nameof(ReplicaAnswers.Written)),
            new(0, LedgerEventKind.Appended, "writer", Ledger) { Address = 0, Answer = nameof(ReplicaAnswers.Written) },
        ];

        var module = LedgerTraceExport.Render(events, Ledger, [Node, "replica-1"], quorum: 1, window: 1);

        Assert.Equal(
            """
            // -*- mode: Bluespec; -*-
            module LedgerTraceData {
              import LedgerProtocolTrace(
                TraceReplicas = Set(0, 1),
                TraceQuorum = 1,
                TraceWindow = 1,
                TraceSlots = 2,
                TraceRecoverers = Set(),
                TraceReaders = Set(),
                TraceFetchers = Set(),
                TraceDuplicates = 3,
                TraceFaults = Set()
              ).* from "../../../specs/ledger-protocol/LedgerProtocolTrace"

              run traceReplay = traceInit.expect(TraceInvariants)
                .then(stepAcquire(0, 0))
                .then(stepCallWrite(0, 0, 0, false))
                .then(stepServeWrite(0, 0, 0, "Written", false, true, [], [], []))
                .then(stepReceiveWrite(0, 0, "Written"))
                .then(stepAppended(0, "Written"))
            }

            """.ReplaceLineEndings(),
            module.ReplaceLineEndings());
    }

    /// <summary>
    /// A read which returns a write's DATA before the node logged the write's answer follows the write: the write
    /// published its record first.
    /// </summary>
    [Fact]
    public void AWriteAnswerLoggedAfterAReadOfItsDataComesFirst()
    {
        var steps = Steps(
            Called(1, "writer", LedgerOperation.WriterWrite, 0),
            Called(2, "reader", LedgerOperation.Read, 0),
            Served(2, "reader", LedgerOperation.Read, 0, nameof(ReplicaReadCase.Batch)),
            Served(1, "writer", LedgerOperation.WriterWrite, 0, nameof(ReplicaAnswers.Written)));

        Assert.True(IndexOf(steps, "stepServeWrite(") < IndexOf(steps, "stepServePeek("));
    }

    /// <summary>
    /// A writer write answered Sealed before the node logged the seal it met follows the seal.
    /// </summary>
    [Fact]
    public void ASealLoggedAfterTheSealedAnswerItCausedComesFirst()
    {
        var steps = Steps(
            new LedgerEvent(0, LedgerEventKind.RecoveryBegan, "recovery", Ledger) { Call = 9, ContiguousTail = 0 },
            Called(1, "recovery", LedgerOperation.Seal, address: null),
            Called(2, "writer", LedgerOperation.WriterWrite, 0),
            Served(2, "writer", LedgerOperation.WriterWrite, 0, nameof(ReplicaAnswers.Sealed)),
            Served(1, "recovery", LedgerOperation.Seal, address: null, LedgerTrace.TailAnswer, new LedgerReplicaTail(Slot.Zero, IsSealed: true)));

        Assert.True(IndexOf(steps, "stepServeSeal(") < IndexOf(steps, "stepServeWrite("));
    }

    /// <summary>
    /// A writer write in flight at its node's crash reached the disk when the node later returns its DATA, and stays in
    /// flight when the node serves it again, sealed; one whose position the node then answers missing or corrupt did not
    /// land.
    /// </summary>
    [Fact]
    public void WritesInFlightAtACrashLandWhenTheNodeLaterReturnsTheirData()
    {
        var steps = Steps(
            Called(1, "writer", LedgerOperation.WriterWrite, 0),
            Called(2, "writer", LedgerOperation.WriterWrite, 1),
            Called(3, "writer", LedgerOperation.WriterWrite, 2),
            Called(4, "writer", LedgerOperation.WriterWrite, 3),
            Restarted(isSealed: true, data: [0, 1], faulty: [3]),
            Served(5, "reader", LedgerOperation.Read, 0, nameof(ReplicaReadCase.Batch)),
            Served(6, "reader", LedgerOperation.Read, 1, nameof(ReplicaReadCase.Batch)),
            Served(2, "writer", LedgerOperation.WriterWrite, 1, nameof(ReplicaAnswers.Sealed)),
            Served(7, "reader", LedgerOperation.Read, 2, nameof(ReplicaReadCase.Missing)),
            Served(4, "writer", LedgerOperation.WriterWrite, 3, Corrupt));

        Assert.Contains(
            "stepRestart(0, true, true, Set(0, 1), [3], [(0, 0, false), (1, 0, true)], [], [])",
            steps);
    }

    /// <summary>
    /// A restart whose DATA and seal no answer showed names the requests in flight whose records reached the disk before
    /// the crash: the writer write and the recovery write of each position it holds, and the recovery's seal, which stays
    /// in flight when the node serves it again.
    /// </summary>
    [Fact]
    public void ARestartWhoseDataAndSealNoAnswerShowedNamesTheRequestsWhichLanded()
    {
        var steps = Steps(
            new LedgerEvent(0, LedgerEventKind.RecoveryBegan, "recovery", Ledger) { Call = 9, ContiguousTail = 0 },
            Called(1, "recovery", LedgerOperation.Seal, address: null),
            Called(2, "recovery", LedgerOperation.RecoveryWrite, 0),
            Called(3, "writer", LedgerOperation.WriterWrite, 1),
            Restarted(isSealed: true, data: [0, 1], faulty: []),
            Served(1, "recovery", LedgerOperation.Seal, address: null, LedgerTrace.TailAnswer, new LedgerReplicaTail(Slot.Zero, IsSealed: true)));

        Assert.Contains(
            "stepRestart(0, true, true, Set(0, 1), [], [(1, 0, false)], [(false, 0, 0, 0, false)], [(0, true)])",
            steps);
    }

    /// <summary>
    /// A process which a crash ended may answer from a record whose own answer it never logged: the read which returned
    /// the DATA of a write it never answered, and the writer write it answered Sealed from a seal it never answered, name
    /// those requests, so the restart which follows names nothing more.
    /// </summary>
    [Fact]
    public void AnAnswerOfACrashedProcessNamesTheRequestsWhoseRecordsItShowed()
    {
        var steps = Steps(
            new LedgerEvent(0, LedgerEventKind.RecoveryBegan, "recovery", Ledger) { Call = 9, ContiguousTail = 0 },
            Called(1, "writer", LedgerOperation.WriterWrite, 0),
            Called(2, "recovery", LedgerOperation.Seal, address: null),
            Called(3, "writer", LedgerOperation.WriterWrite, 1),
            Served(4, "reader", LedgerOperation.Read, 0, nameof(ReplicaReadCase.Batch)),
            Served(3, "writer", LedgerOperation.WriterWrite, 1, nameof(ReplicaAnswers.Sealed)),
            Restarted(isSealed: true, data: [0], faulty: []));

        Assert.EndsWith(", [(0, 0, false)], [], [])", steps[IndexOf(steps, "stepServePeek(")], StringComparison.Ordinal);
        Assert.EndsWith(", [], [], [(0, false)])", steps[IndexOf(steps, "stepServeWrite(")], StringComparison.Ordinal);
        Assert.Contains(
            "stepRestart(0, true, true, Set(0), [], [], [], [])",
            steps);
    }

    /// <summary>
    /// A restart which recovers the replica sealed, from a seal which landed without an answer, is the replica's seal: a
    /// later answer which reports it sealed stays ahead of another recovery's seal the node serves after it.
    /// </summary>
    [Fact]
    public void ARestartWhichRecoversTheSealPrecedesTheSealedAnswersAfterIt()
    {
        var steps = Steps(
            new LedgerEvent(0, LedgerEventKind.RecoveryBegan, "recovery-1", Ledger) { Call = 8, ContiguousTail = 0 },
            Called(1, "recovery-1", LedgerOperation.Seal, address: null),
            Called(2, "writer", LedgerOperation.WriterWrite, 0),
            Restarted(isSealed: true, data: [], faulty: []),
            Served(2, "writer", LedgerOperation.WriterWrite, 0, nameof(ReplicaAnswers.Sealed)),
            new LedgerEvent(0, LedgerEventKind.RecoveryBegan, "recovery-2", Ledger) { Call = 9, ContiguousTail = 0 },
            Called(3, "recovery-2", LedgerOperation.Seal, address: null),
            Served(3, "recovery-2", LedgerOperation.Seal, address: null, LedgerTrace.TailAnswer, new LedgerReplicaTail(Slot.Zero, IsSealed: true)));

        Assert.EndsWith(", [], [], [(0, false)])", steps[IndexOf(steps, "stepRestart(")], StringComparison.Ordinal);
        Assert.True(IndexOf(steps, "stepServeWrite(") < IndexOf(steps, "stepServeSeal("));
    }

    /// <summary>
    /// A read which returned DATA before its node crashed stays before the restart, ahead of the write's answer the
    /// restarted node gave: the restart ended the process whose completion order the renderer corrects, so the model
    /// checks that the DATA was durable when it was read.
    /// </summary>
    [Fact]
    public void AReadOfDataIsNotReorderedAfterAWriteAnswerAcrossARestart()
    {
        var steps = Steps(
            Called(1, "writer", LedgerOperation.WriterWrite, 0),
            Served(2, "reader", LedgerOperation.Read, 0, nameof(ReplicaReadCase.Batch)),
            Restarted(isSealed: false, data: [], faulty: []),
            Served(1, "writer", LedgerOperation.WriterWrite, 0, nameof(ReplicaAnswers.Written)));

        Assert.True(IndexOf(steps, "stepServePeek(") < IndexOf(steps, "stepRestart("));
        Assert.True(IndexOf(steps, "stepRestart(") < IndexOf(steps, "stepServeWrite("));
    }

    /// <summary>
    /// A write answered Sealed before its node crashed stays before the restart, ahead of the seal the restarted node
    /// served: the model checks that the seal it reports was durable when the write met it.
    /// </summary>
    [Fact]
    public void ASealedAnswerIsNotReorderedAfterASealAcrossARestart()
    {
        var steps = Steps(
            new LedgerEvent(0, LedgerEventKind.RecoveryBegan, "recovery", Ledger) { Call = 9, ContiguousTail = 0 },
            Called(1, "recovery", LedgerOperation.Seal, address: null),
            Called(2, "writer", LedgerOperation.WriterWrite, 0),
            Served(2, "writer", LedgerOperation.WriterWrite, 0, nameof(ReplicaAnswers.Sealed)),
            Restarted(isSealed: false, data: [], faulty: []),
            Served(1, "recovery", LedgerOperation.Seal, address: null, LedgerTrace.TailAnswer, new LedgerReplicaTail(Slot.Zero, IsSealed: true)));

        Assert.True(IndexOf(steps, "stepServeWrite(") < IndexOf(steps, "stepRestart("));
        Assert.True(IndexOf(steps, "stepRestart(") < IndexOf(steps, "stepServeSeal("));
    }

    /// <summary>
    /// The first delivery whose answer the client logged reaches it, and a delivery the node serves again leaves its
    /// request in flight.
    /// </summary>
    [Fact]
    public void ARequestServedTwiceStaysInFlightAndTheAnswerTheClientLoggedDelivers()
    {
        var steps = Steps(
            Called(1, "writer", LedgerOperation.WriterWrite, 0),
            Served(1, "writer", LedgerOperation.WriterWrite, 0, nameof(ReplicaAnswers.Written)),
            Served(1, "writer", LedgerOperation.WriterWrite, 0, nameof(ReplicaAnswers.Written)),
            Answered(1, "writer", LedgerOperation.WriterWrite, nameof(ReplicaAnswers.Written)));

        var serves = steps.Where(static step => step.StartsWith("stepServe", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, serves.Count);
        Assert.Equal("stepServeWrite(0, 0, 0, \"Written\", true, true, [], [], [])", serves[0]);
        Assert.Equal("stepServeWrite(0, 0, 0, \"Written\", false, false, [], [], [])", serves[1]);
    }

    /// <summary>
    /// A contiguous-tail read a reader sends while its seal check is open answers that check, which arrives and decides.
    /// </summary>
    [Fact]
    public void ATailReadWithinASealCheckAnswersTheCheck()
    {
        var tail = new LedgerReplicaTail(new Slot(2), IsSealed: false);
        var steps = Steps(
            new LedgerEvent(0, LedgerEventKind.TailReadBegan, "reader", Ledger) { Call = 7, ContiguousTail = 1 },
            Called(8, "reader", LedgerOperation.TailRead, address: null, contiguousTail: 1),
            Served(8, "reader", LedgerOperation.TailRead, address: null, LedgerTrace.TailAnswer, tail, contiguousTail: 1),
            Answered(8, "reader", LedgerOperation.TailRead, LedgerTrace.TailAnswer, tail),
            new LedgerEvent(0, LedgerEventKind.TailReadAnswered, "reader", Ledger) { Call = 7, Tail = tail });

        Assert.Equal(
            [
                "stepCheck(0, 1, false)",
                "stepServeCheck(0, 0, \"Tail\", 2, false, true, [], [], [])",
                "stepArrive(0, 0)",
                "stepChecked(0, 2, false, false, -1)",
            ],
            steps);
    }

    /// <summary>
    /// A recovery's conditional write of the map renders as a close at the recovery's end, with the prefix the writer had
    /// acknowledged by then: the writes a quorum acknowledged with every lower one. A recovery which begins after the
    /// close read the map before it, so its begin renders before the close.
    /// </summary>
    [Fact]
    public void ARecoverysCloseRendersWithTheAcknowledgedPrefixAndALaterBeginComesFirst()
    {
        var steps = Steps(
            Appended(1),
            Appended(0),
            Appended(2, nameof(ReplicaAnswers.Sealed)),
            new LedgerEvent(0, LedgerEventKind.RecoveryBegan, "recovery-1", Ledger) { Call = 8, ContiguousTail = 0 },
            new LedgerEvent(0, LedgerEventKind.RecoveryEnded, "recovery-1", Ledger) { Call = 8, ContiguousTail = 0, End = 3 },
            new LedgerEvent(0, LedgerEventKind.Closed, "recovery-1", Ledger) { End = 3 },
            new LedgerEvent(0, LedgerEventKind.RecoveryBegan, "recovery-2", Ledger) { Call = 9, ContiguousTail = 0 },
            new LedgerEvent(0, LedgerEventKind.Superseded, "recovery-2", Ledger));

        Assert.Equal(
            [
                "stepRecovered(0, 0, 3)",
                "stepRecover(1, 0)",
                "stepClose(0, 3, 2)",
                "stepSuperseded(1)",
            ],
            steps[^4..]);
    }

    /// <summary>
    /// A close the trace observes no recovery's conditional write for names no recovery the model can close with.
    /// </summary>
    [Fact]
    public void ACloseByNoObservedRecoveryIsNotMapped() =>
        Assert.Throws<NotSupportedException>(() => Steps(new LedgerEvent(0, LedgerEventKind.Closed, "map", Ledger) { End = 0 }));

    /// <summary>
    /// A <see cref="LedgerWriter"/>'s completions are its acknowledgements, which a close renders as its acknowledged
    /// prefix, while a write which reached <c>Q</c> written answers completes nothing yet; its stop stutters.
    /// </summary>
    [Fact]
    public void ALedgerWritersCompletionsAndStopsRenderAsItsSteps()
    {
        var steps = Steps(
            new LedgerEvent(0, LedgerEventKind.Acquired, "primary", Ledger) { Address = 0 },
            new LedgerEvent(0, LedgerEventKind.Acquired, "primary", Ledger) { Address = 1 },
            Appended(0, client: "primary"),
            new LedgerEvent(0, LedgerEventKind.Completed, "primary", Ledger) { Address = 0 },
            Appended(1, client: "primary"),
            new LedgerEvent(0, LedgerEventKind.Stopped, "primary", Ledger) { Answer = nameof(SequencerStopReason.Sealing) },
            new LedgerEvent(0, LedgerEventKind.RecoveryBegan, "executor", Ledger) { Call = 8, ContiguousTail = 0 },
            new LedgerEvent(0, LedgerEventKind.RecoveryEnded, "executor", Ledger) { Call = 8, ContiguousTail = 0, End = 2 },
            new LedgerEvent(0, LedgerEventKind.Closed, "executor", Ledger) { End = 2 });

        Assert.Equal(
            [
                "stepAppended(0, \"Written\")",
                "stepCompleted(0)",
                "stepAppended(1, \"Written\")",
                "stepStopped",
                "stepRecover(0, 0)",
                "stepRecovered(0, 0, 2)",
                "stepClose(0, 2, 1)",
            ],
            steps);
    }

    /// <summary>
    /// A client which learns the recorded <c>T</c> reads the map through its map view, and each replica a request carrying
    /// <c>T</c> reaches retains it as it admits the request; the writer's own checks report its known contiguous tail.
    /// </summary>
    [Fact]
    public void ALearnedTailIsRetainedByEachReplicaARequestCarryingItReaches()
    {
        var tail = new LedgerReplicaTail(new Slot(2), IsSealed: true);
        var trace = LedgerTraceExport.Translate(
            [
                Called(3, "primary", LedgerOperation.WriterWrite, 1),
                new LedgerEvent(0, LedgerEventKind.Stopped, "primary", Ledger) { Answer = nameof(SequencerStopReason.Sealing) },
                new LedgerEvent(0, LedgerEventKind.RecordedTailObserved, "primary", Ledger) { End = 2 },
                Called(4, "primary", LedgerOperation.WriterWrite, 1, contiguousTail: 2),
                new LedgerEvent(0, LedgerEventKind.TailReadBegan, "primary", Ledger) { Call = 5 },
                Called(6, "primary", LedgerOperation.TailRead, address: null, contiguousTail: 2),
                Served(6, "primary", LedgerOperation.TailRead, address: null, LedgerTrace.TailAnswer, tail, contiguousTail: 2),
                new LedgerEvent(0, LedgerEventKind.TailReadAnswered, "primary", Ledger) { Call = 5, Tail = tail },
            ],
            Ledger);

        Assert.Equal(["primary/map"], trace.FetcherIds);
        Assert.Equal(
            [
                "stepAcquire(1, 0)",
                "stepStopped",
                "stepLearn(0, 2)",
                "stepCallWrite(0, 1, 2, true)",
                "stepCheck(0, 2, true)",
                "stepRetain(0, 0)",
                "stepServeCheck(0, 0, \"Tail\", 2, true, false, [], [], [])",
                "stepChecked(0, 2, true, true, 0)",
            ],
            trace.Steps);
    }

    /// <summary>
    /// Media damage names its node and no ledger, so it renders as a damage of the node in the ledger's trace, in its place
    /// among the ledger's events, while another ledger's events stay out.
    /// </summary>
    [Fact]
    public void MediaDamageRendersAsADamageOfItsNodeInTheLedgersTrace()
    {
        var steps = Steps(
            Served(1, "reader", LedgerOperation.Read, 0, nameof(ReplicaReadCase.Batch)),
            new LedgerEvent(0, LedgerEventKind.Damaged, Node, default) { Node = Node, Incarnation = 1 },
            new LedgerEvent(0, LedgerEventKind.Dropped, Node, new LedgerId(Ledger.Value + 1)) { Node = Node },
            Served(2, "reader", LedgerOperation.Read, 0, Corrupt));

        Assert.Equal(
            [
                "stepServePeek(0, 0, 0, \"Batch\", -1, [], [], [])",
                "stepDamage(0)",
                "stepServePeek(0, 0, 0, \"LedgerReplicaCorruptException\", -1, [], [], [])",
            ],
            steps);
    }

    private static List<string> Steps(params LedgerEvent[] events) => [.. LedgerTraceExport.Translate(events, Ledger).Steps];

    private static int IndexOf(List<string> steps, string fragment)
    {
        var index = steps.FindIndex(step => step.Contains(fragment, StringComparison.Ordinal));
        Assert.True(index >= 0, $"No step contains {fragment}.");
        return index;
    }

    private static LedgerEvent Appended(long address, string answer = nameof(ReplicaAnswers.Written), string client = "writer") =>
        new(0, LedgerEventKind.Appended, client, Ledger) { Address = address, Answer = answer };

    private static LedgerEvent Restarted(bool isSealed, long[] data, long[] faulty) =>
        new(0, LedgerEventKind.Restarted, string.Empty, Ledger) { Node = Node, Recovered = new RecoveredReplica(Held: true, isSealed, data, faulty) };

    private static LedgerEvent Called(long call, string client, LedgerOperation operation, long? address, long contiguousTail = 0) =>
        new(0, LedgerEventKind.Called, client, Ledger)
        {
            Node = Node,
            Call = call,
            Operation = operation,
            Address = address,
            ContiguousTail = contiguousTail,
        };

    private static LedgerEvent Served(
        long call, string client, LedgerOperation operation, long? address, string answer, LedgerReplicaTail? tail = null, long contiguousTail = 0) =>
        new(0, LedgerEventKind.Served, client, Ledger)
        {
            Node = Node,
            Call = call,
            Operation = operation,
            Address = address,
            ContiguousTail = contiguousTail,
            Answer = answer,
            Tail = tail,
        };

    private static LedgerEvent Answered(long call, string client, LedgerOperation operation, string answer, LedgerReplicaTail? tail = null) =>
        new(0, LedgerEventKind.Answered, client, Ledger)
        {
            Node = Node,
            Call = call,
            Operation = operation,
            Answer = answer,
            Tail = tail,
        };
}
