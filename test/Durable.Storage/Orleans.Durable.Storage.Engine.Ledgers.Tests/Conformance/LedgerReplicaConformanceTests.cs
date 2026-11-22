using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using CsCheck;
using Microsoft.Accordant;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers.Memory;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests.Conformance;

/// <summary>
/// Every <see cref="ILedgerReplica"/> conforms to <see cref="LedgerReplicaSpec"/> (ledger-replicas.md §2.1, §10): the memory provider's
/// and a LedgerStore's replicas of two ledgers on one node, the store across restarts, and the composition of
/// any <c>(N, Q)</c>, over faulty and slow replicas. Accordant generates every sequence, and every concurrent group, up
/// to a small depth; CsCheck generates long random sequences and concurrent groups, with faults and restarts, and shrinks
/// a failure. Each case runs in its own page leak scope, so a case which leaks a page fails, is shrunk, and reports its
/// seed.
/// </summary>
public sealed class LedgerReplicaConformanceTests : IDisposable
{
    /// <summary>
    /// The window of Accordant's exhaustive generation, small enough for its depth to reach past it.
    /// </summary>
    private const int ExhaustiveWindow = 2;

    /// <summary>
    /// The window of CsCheck's random sequences, over addresses 0 to 5.
    /// </summary>
    private const int RandomWindow = 3;

    private static readonly Gen<long> Addresses = Gen.Frequency((24, Gen.Long[0, 5]), (1, Gen.Const(long.MaxValue)));

    /// <summary>
    /// The known contiguous tail a request carries: mostly none, and otherwise a small one (LR-I6).
    /// </summary>
    private static readonly Gen<long> Tails = Gen.Frequency((3, Gen.Const(0L)), (1, Gen.Long[1, 4]));

    private static readonly Gen<bool> Rarely = Gen.Frequency((7, Gen.Const(false)), (1, Gen.Const(true)));

    private static readonly Gen<bool> Sometimes = Gen.Frequency((3, Gen.Const(false)), (1, Gen.Const(true)));

    /// <summary>
    /// The ledger of a replica's request: mostly the replica under test's, and otherwise the other ledger on its node.
    /// </summary>
    private static readonly Gen<int> ReplicaLedgers = Gen.Frequency((3, Gen.Const(0)), (1, Gen.Const(1)));

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    [Fact(Timeout = 120_000)]
    public async Task TheMemoryProviderConforms()
    {
        await CheckExhaustivelyAsync(Replica(ExhaustiveWindow), (window, _) => Task.FromResult(new MemoryTarget(window)), TestContext.Current.CancellationToken);
        var kit = RandomKit(Replica(RandomWindow), static (window, _) => Task.FromResult(new MemoryTarget(window)), TestContext.Current.CancellationToken);
        await kit.SampleAsync(kit.Sequences(40), iterations: 400);
        await kit.SampleAsync(kit.ConcurrentSequences(20), iterations: 400);
    }

    /// <summary>
    /// A LedgerStore replica conforms, its restarts included: the spec's restart keeps every durable answer, the seal
    /// among them, and rebuilds each retained contiguous tail from the replica's highest DATA and its window (LR-I6,
    /// LS-I11).
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task ALedgerStoreReplicaConformsAcrossRestarts()
    {
        await CheckExhaustivelyAsync(
            Replica(ExhaustiveWindow), static (window, _) => LedgerStoreTarget.CreateAsync(window), TestContext.Current.CancellationToken, restarts: true);
        var kit = RandomKit(Replica(RandomWindow), static (window, _) => LedgerStoreTarget.CreateAsync(window), TestContext.Current.CancellationToken)
            .Operation("Restart", Gen.Bool.Select(static operatingSystem => new LedgerReplicaRestart(operatingSystem)), Execute<LedgerStoreTarget, LedgerReplicaRestart>, weight: 2);
        await kit.SampleAsync(kit.Sequences(40), iterations: 150);
        await kit.SampleAsync(kit.ConcurrentSequences(20), iterations: 150);
    }

    /// <summary>
    /// Regression test, LP-I5: the composition reports a writer write which meets dropped replicas before its quorum as
    /// sealed, counting the dropped answers as sealed ones, while a node the ledger's removal has not yet reached holds
    /// its value, which a concurrent read returns. The spec took a dropped answer for one without effect, so it refused
    /// this history; it still refuses the sealed write when no removal overlapped it.
    /// </summary>
    [Fact]
    public void AReadMayReturnTheValueOfAConcurrentWriteTheCompositionReportsSealedAsTheLedgerIsRemoved() =>
        AssertAllowedOnlyWhenOverlapping(
            Composition(ExhaustiveWindow, 3, 2) with { Ambiguous = true },
            new LedgerReplicaState(),
            [
                ("Write", new LedgerReplicaWrite(0), new LedgerReplicaOutcome(LedgerReplicaOutcomeKind.Sealed)),
                ("Remove", LedgerReplicaRemove.Instance, new LedgerReplicaOutcome(LedgerReplicaOutcomeKind.Done)),
                ("ReadRange", new LedgerReplicaReadRange(0, 1, 1), new LedgerReplicaOutcome(LedgerReplicaOutcomeKind.Data, Bytes: LedgerReplicaSpec.Payload(0)[1..2])),
            ]);

    /// <summary>
    /// LP-I5, ledger-protocol.md §6.4: the composition answers <c>Dropped</c> only once the ledger left the map, so a read
    /// may answer it beside an overlapping removal and not without one, and a writer write and a seal check, which count
    /// a dropped answer as a sealed one, never answer it.
    /// </summary>
    [Fact]
    public void TheCompositionAnswersDroppedOnlyOnceTheLedgerLeftTheMap()
    {
        var spec = LedgerReplicaSpec.Create(Composition(ExhaustiveWindow, 3, 2) with { Ambiguous = true });
        var dropped = new LedgerReplicaOutcome(LedgerReplicaOutcomeKind.Dropped);
        var removed = (spec.GetOperation<LedgerReplicaRemove, LedgerReplicaOutcome>("Remove"), (object)LedgerReplicaRemove.Instance, new LedgerReplicaOutcome(LedgerReplicaOutcomeKind.Done));
        var read = (spec.GetOperation<LedgerReplicaRead, LedgerReplicaOutcome>("Read"), (object)new LedgerReplicaRead(0), dropped);

        Assert.True(spec.AllowsConcurrent(new StateProfile(new LedgerReplicaState()), [read, removed]).Item1);
        Assert.False(spec.AllowsConcurrent(new StateProfile(new LedgerReplicaState()), [read]).Item1);
        Assert.False(spec.AllowsConcurrent(
            new StateProfile(new LedgerReplicaState()),
            [(spec.GetOperation<LedgerReplicaWrite, LedgerReplicaOutcome>("Write"), new LedgerReplicaWrite(0), dropped), removed]).Item1);
        Assert.False(spec.AllowsConcurrent(
            new StateProfile(new LedgerReplicaState()),
            [(spec.GetOperation<LedgerReplicaReadTail, LedgerReplicaOutcome>("ReadTail"), new LedgerReplicaReadTail(0), dropped), removed]).Item1);
    }

    /// <summary>
    /// Regression test, LR-I6: a LedgerStore replica retains a call's contiguous tail when the call reaches it, so a
    /// seal reported the tail of a writer write which then met the seal. The spec retained a tail only when its call took
    /// effect, so it refused this history; it still refuses the report from a call which overlapped none.
    /// </summary>
    [Fact]
    public void ATailReportMayHoldTheTailOfAnOverlappingCall() =>
        AssertAllowedOnlyWhenOverlapping(
            Replica(ExhaustiveWindow),
            new LedgerReplicaState(),
            [
                ("Write", new LedgerReplicaWrite(1, ContiguousTail: 1), new LedgerReplicaOutcome(LedgerReplicaOutcomeKind.Sealed)),
                ("ReadTail", new LedgerReplicaReadTail(0, Seal: true), new LedgerReplicaOutcome(LedgerReplicaOutcomeKind.Tail, 1, IsSealed: true)),
            ]);

    /// <summary>
    /// Regression test, LP-I4, LP-I7: a writer write of the composition left its DATA on one replica, met the seal of an
    /// overlapping recovery on the others and reported sealed, and the recovery found its DATA and ended above it. The
    /// spec ordered the write before the recovery only as written, so it refused this history; it still refuses a sealed
    /// write which no seal or recovery overlapped.
    /// </summary>
    [Fact]
    public void AWriterWriteMayMeetAnOverlappingRecoveryWhichFindsItsData() =>
        AssertAllowedOnlyWhenOverlapping(
            Composition(ExhaustiveWindow, 3, 2),
            new LedgerReplicaState { Ledgers = [new() { Data = [0], Exists = true }, new()] },
            [
                ("Write", new LedgerReplicaWrite(1), new LedgerReplicaOutcome(LedgerReplicaOutcomeKind.Sealed)),
                ("Recover", new LedgerReplicaRecover(0), new LedgerReplicaOutcome(LedgerReplicaOutcomeKind.End, 2)),
            ]);

    /// <summary>
    /// Regression test, LR-I7: a LedgerStore write created its replica, which an overlapping listing named, and then met
    /// the admission an overlapping drop closed and reported dropped. The spec created a replica only when its write took
    /// effect, so it refused this history; it still refuses a listing of a replica no overlapping call created.
    /// </summary>
    [Fact]
    public void AListingMayNameAReplicaAnOverlappingWriteCreatedBeforeADropRefusedIt() =>
        AssertAllowedOnlyWhenOverlapping(
            Replica(ExhaustiveWindow),
            new LedgerReplicaState(),
            [
                ("Write", new LedgerReplicaWrite(2), new LedgerReplicaOutcome(LedgerReplicaOutcomeKind.Dropped)),
                ("ListReplicas", LedgerReplicaListReplicas.Instance, new LedgerReplicaOutcome(LedgerReplicaOutcomeKind.Listed, Listing: 1)),
                ("DropReplica", new LedgerReplicaDropReplica(), new LedgerReplicaOutcome(LedgerReplicaOutcomeKind.Done)),
            ]);

    /// <summary>
    /// Regression test, LR-I7, LP-I5: a recovery which overlapped the ledger's removal sealed, and so created, the replicas
    /// of the nodes the removal had not yet reached, which an overlapping listing named, and then met a node's tombstone
    /// and answered dropped. The spec took only writes and seals as creating a replica, so it refused this history, which
    /// a seeded schedule of the simulated composition found (`SimulatedLedgerCompositionConformanceTests`); it still refuses
    /// the listing when no call overlapped it.
    /// </summary>
    [Fact]
    public void AListingMayNameALedgerAnOverlappingRecoveryCreatedAsTheLedgerIsRemoved() =>
        AssertAllowedOnlyWhenOverlapping(
            Composition(ExhaustiveWindow, 3, 2) with { Ambiguous = true },
            new LedgerReplicaState(),
            [
                ("Remove", LedgerReplicaRemove.Instance, new LedgerReplicaOutcome(LedgerReplicaOutcomeKind.Done)),
                ("Recover", new LedgerReplicaRecover(0), new LedgerReplicaOutcome(LedgerReplicaOutcomeKind.Dropped)),
                ("ListReplicas", LedgerReplicaListReplicas.Instance, new LedgerReplicaOutcome(LedgerReplicaOutcomeKind.Listed, Listing: 1)),
            ]);

    /// <summary>
    /// Regression test, #171 (LR-I6, LR-I7): a write publishes the replica it creates before its DATA, in the memory
    /// provider and in a LedgerStore, so an overlapping read of the address found the replica empty, answered missing, and
    /// left its contiguous tail retained, which a later contiguous-tail read reported. The spec created the replica only
    /// when its write took effect, so it refused this history; it still refuses the report after a read which overlapped
    /// no creation, or only a write of an invalid address, which creates nothing.
    /// </summary>
    [Fact]
    public void AReadMayLeaveItsTailOnTheReplicaAnOverlappingWriteCreated()
    {
        (string Name, object Request, LedgerReplicaOutcome Outcome) read =
            ("Read", new LedgerReplicaRead(1, ContiguousTail: 4), new LedgerReplicaOutcome(LedgerReplicaOutcomeKind.Missing));
        (string Name, object Request, LedgerReplicaOutcome Outcome) report =
            ("ReadTail", new LedgerReplicaReadTail(0), new LedgerReplicaOutcome(LedgerReplicaOutcomeKind.Tail, 4));
        AssertAllowedOnlyWhenOverlapping(
            Replica(ExhaustiveWindow),
            new LedgerReplicaState(),
            [("Write", new LedgerReplicaWrite(1), new LedgerReplicaOutcome(LedgerReplicaOutcomeKind.Written)), read],
            report);

        (string Name, object Request, LedgerReplicaOutcome Outcome) invalid =
            ("Write", new LedgerReplicaWrite(long.MaxValue), new LedgerReplicaOutcome(LedgerReplicaOutcomeKind.InvalidArgument) { Overlap = new([read.Request]) });
        Assert.False(
            Allows(
                LedgerReplicaSpec.Create(Replica(ExhaustiveWindow)),
                new LedgerReplicaState(),
                [invalid, read with { Outcome = read.Outcome with { Overlap = new([invalid.Request]) } }],
                ("Write", new LedgerReplicaWrite(1), new LedgerReplicaOutcome(LedgerReplicaOutcomeKind.Written)),
                report).IsValid,
            "The spec retained the tail of a read which overlapped only a write of an invalid address.");
    }

    /// <summary>
    /// LR-I6, LR-I7: a contiguous-tail read which overlaps a creating seal may likewise find the created replica, before
    /// the seal takes effect, empty and unsealed, and leave its argument retained there; one which overlapped no creation
    /// retains nothing, whatever the seal overlapped.
    /// </summary>
    [Fact]
    public void AContiguousTailReadMayLeaveItsTailOnTheReplicaAnOverlappingSealCreated()
    {
        (string Name, object Request, LedgerReplicaOutcome Outcome) seal =
            ("ReadTail", new LedgerReplicaReadTail(0, Seal: true), new LedgerReplicaOutcome(LedgerReplicaOutcomeKind.Tail, 4, IsSealed: true));
        (string Name, object Request, LedgerReplicaOutcome Outcome) read =
            ("ReadTail", new LedgerReplicaReadTail(4), new LedgerReplicaOutcome(LedgerReplicaOutcomeKind.Tail, 4));
        (string Name, object Request, LedgerReplicaOutcome Outcome) then =
            ("ReadTail", new LedgerReplicaReadTail(0), new LedgerReplicaOutcome(LedgerReplicaOutcomeKind.Tail, 4, IsSealed: true));
        AssertAllowedOnlyWhenOverlapping(Replica(ExhaustiveWindow), new LedgerReplicaState(), [seal, read], then);

        var sealOverlapsTheRead = seal with { Outcome = seal.Outcome with { Overlap = new([read.Request]) } };
        Assert.False(
            Allows(LedgerReplicaSpec.Create(Replica(ExhaustiveWindow)), new LedgerReplicaState(), [sealOverlapsTheRead, read], then).IsValid,
            "The spec retained the tail of a contiguous-tail read which overlapped no creation.");
    }

    /// <summary>
    /// Regression test, #296 (LR-I6, LR-I9): a contiguous-tail read or a read of a replica its node did not yet hold, which
    /// failed as unavailable, may still reach the node later, once a later write created the replica, and leave its
    /// contiguous tail retained there, which a later report holds. The spec bounded a later report only by the tails of
    /// failed calls on a replica which existed, so it refused this history, which the grain-call path over a faulty
    /// network produced whenever the delayed request landed before the report; it still refuses a report above the tail
    /// the failed call carried.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AFailedCallOnAnAbsentReplicaMayLeaveItsTailOnceALaterWriteCreatesIt(bool tailRead)
    {
        var spec = LedgerReplicaSpec.Create(Replica(ExhaustiveWindow) with { Ambiguous = true });
        (string Name, object Request, LedgerReplicaOutcome Outcome) failed = tailRead
            ? ("ReadTail", new LedgerReplicaReadTail(2, Ledger: 1), new LedgerReplicaOutcome(LedgerReplicaOutcomeKind.Unavailable))
            : ("Read", new LedgerReplicaRead(0, Ledger: 1, ContiguousTail: 2), new LedgerReplicaOutcome(LedgerReplicaOutcomeKind.Unavailable));
        (string Name, object Request, LedgerReplicaOutcome Outcome) creates =
            ("Write", new LedgerReplicaWrite(1, Ledger: 1, ContiguousTail: 1), new LedgerReplicaOutcome(LedgerReplicaOutcomeKind.Written));
        (string Name, object Request, LedgerReplicaOutcome Outcome) Report(long tail) =>
            ("ReadTail", new LedgerReplicaReadTail(0, Ledger: 1), new LedgerReplicaOutcome(LedgerReplicaOutcomeKind.Tail, tail));

        var allowed = Allows(spec, new LedgerReplicaState(), [failed], creates, Report(2));
        Assert.True(allowed.IsValid, allowed.Explanation);
        Assert.False(Allows(spec, new LedgerReplicaState(), [failed], creates, Report(3)).IsValid, "The spec allowed a tail no call carried.");
    }

    /// <summary>
    /// Asserts that the spec allows the concurrent <paramref name="calls"/> from <paramref name="state"/>, followed by the
    /// calls <paramref name="then"/> in turn, when each one's outcome holds the others as overlapping, and refuses them when
    /// none does.
    /// </summary>
    private static void AssertAllowedOnlyWhenOverlapping(
        LedgerReplicaSpecOptions options,
        LedgerReplicaState state,
        (string Name, object Request, LedgerReplicaOutcome Outcome)[] calls,
        params (string Name, object Request, LedgerReplicaOutcome Outcome)[] then)
    {
        var spec = LedgerReplicaSpec.Create(options);
        (string Name, object Request, LedgerReplicaOutcome Outcome)[] overlapping =
        [
            .. calls.Select((call, index) => call with
            {
                Outcome = call.Outcome with { Overlap = new([.. calls.Where((_, other) => other != index).Select(static other => other.Request)]) },
            }),
        ];
        var allowed = Allows(spec, state, overlapping, then);
        Assert.True(allowed.IsValid, allowed.Explanation);
        Assert.False(Allows(spec, state, calls, then).IsValid, "The spec allowed the history without the overlap which explains it.");
    }

    /// <summary>
    /// Returns whether the spec allows the concurrent <paramref name="calls"/> from <paramref name="state"/>, with the
    /// overlaps their outcomes hold, followed by the calls <paramref name="then"/> in turn.
    /// </summary>
    private static (bool IsValid, string Explanation) Allows(
        Microsoft.Accordant.Spec<LedgerReplicaState> spec,
        LedgerReplicaState state,
        (string Name, object Request, LedgerReplicaOutcome Outcome)[] calls,
        params (string Name, object Request, LedgerReplicaOutcome Outcome)[] then)
    {
        var (isValid, explanation, profile) = spec.AllowsConcurrent(
            new StateProfile(state),
            [.. calls.Select(call => (spec.GetOperation(call.Name), call.Request, (object)call.Outcome))]);
        foreach (var (name, request, outcome) in then)
        {
            if (!isValid)
            {
                break;
            }

            (isValid, explanation, profile) = spec.Allows(spec.GetOperation(name), request, outcome, profile);
        }

        return (isValid, explanation);
    }

    /// <summary>
    /// The composition, a Ledger of three replicas and quorum two, conforms over healthy replicas, strictly for sequences,
    /// and over replicas which fail before or after applying an operation, hold it, or answer it <c>Backpressure</c>, at
    /// random or on demand. A call fails as unavailable only when an injected fault fired during it or another call
    /// overlapped it, and every faulty case ends by clearing the faults and checking that a seal check, a seal, a recovery,
    /// and reads succeed. Over faulty replicas the ledger's process is on the first replica's node, so its reads below the
    /// tail ask that replica first, fall back past it when it missed a write or fails, and hedge it while it holds a read:
    /// a read alone which waited out one replica's hold fails. Two scripted cases then hedge a held first replica and fall
    /// back past a hole in it, so the runs do both by construction (ledger-protocol.md §8.3, §8.4).
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task TheQuorumCompositionConformsOverFaultyReplicas()
    {
        var coverage = new LedgerTargetCoverage();
        var strict = Composition(ExhaustiveWindow, 3, 2);
        var options = strict with { Ambiguous = true, Backpressure = true };
        await CheckExhaustivelyAsync(
            strict,
            static (window, _) => Task.FromResult(new LedgerTarget(window)),
            TestContext.Current.CancellationToken,
            concurrentOptions: strict with { Ambiguous = true });
        await CheckExhaustivelyAsync(
            options,
            (window, seed) => Task.FromResult(new LedgerTarget(window, seed: seed, chaos: true, preferred: 0, coverage: coverage)),
            TestContext.Current.CancellationToken,
            configure: Heals);

        var kit = Heals(RandomKit(options with { WindowSize = RandomWindow }, (window, _) => Task.FromResult(new LedgerTarget(window, preferred: 0, coverage: coverage)), TestContext.Current.CancellationToken))
            .Action(
                "Fault",
                Gen.Select(
                        Gen.Int[0, 2],
                        Gen.Int[1, 3],
                        Gen.OneOfConst(InjectedFault.BeforeApplying, InjectedFault.AfterApplying, InjectedFault.Held, InjectedFault.Backpressure),
                        Gen.Frequency((1, Gen.Const(default(string))), (1, Gen.OneOfConst<string?>("Write", "RecoveryWrite", "Read", "ReadTail", "Seal"))))
                    .Select(static (replica, count, fault, operation) => new ReplicaFault(replica, count, fault, operation)),
                static (target, fault) =>
                {
                    target.Inject(fault);
                    return Task.CompletedTask;
                },
                weight: 8)
            .Action("Release", Gen.Const(Release.Instance), static (target, _) => target.ReleaseAsync(), weight: 2);
        await kit.SampleAsync(kit.Sequences(40), iterations: 400);
        await kit.SampleAsync(kit.ConcurrentSequences(20), iterations: 400);

        LedgerTarget Covered(LedgerReplicaOptions window) => new(window, preferred: 0, coverage: coverage);
        await AssertTheCaseConformsAsync(
            new ConformanceCase(
            [
                new([new("Write", new LedgerReplicaWrite(0))]),
                new([new("Fault", new ReplicaFault(0, 1, InjectedFault.Held, "Read"))]),
                new([new("Read", new LedgerReplicaRead(0, ContiguousTail: 1))]),
            ]),
            TestContext.Current.CancellationToken,
            create: Covered);
        await AssertTheCaseConformsAsync(
            new ConformanceCase(
            [
                new([new("Fault", new ReplicaFault(0, 1, InjectedFault.BeforeApplying, "Write"))]),
                new([new("Write", new LedgerReplicaWrite(0))]),
                new([new("Read", new LedgerReplicaRead(0, ContiguousTail: 1))]),
            ]),
            TestContext.Current.CancellationToken,
            create: Covered);

        Assert.True(coverage.Hedges > 0, "No read below the tail hedged a held replica.");
        Assert.True(coverage.MissingFallbacks > 0, "No read below the tail fell back past a replica's hole.");
    }

    /// <summary>
    /// The composition conforms for any <c>1 ≤ Q ≤ N</c>, even <c>N</c> included, over healthy replicas: strictly
    /// for every sequence, and for every concurrent group (LP-I4, LP-I6, LP-I7, LP-I9).
    /// </summary>
    [Theory(Timeout = 180_000)]
    [InlineData(1, 1)]
    [InlineData(3, 1)]
    [InlineData(3, 3)]
    [InlineData(4, 2)]
    [InlineData(5, 3)]
    public Task TheQuorumCompositionConformsForAnyQuorum(int replicaCount, int quorum)
    {
        var strict = Composition(ExhaustiveWindow, replicaCount, quorum);
        return CheckExhaustivelyAsync(
            strict,
            (window, _) => Task.FromResult(new LedgerTarget(window, replicaCount, quorum)),
            TestContext.Current.CancellationToken,
            concurrentOptions: strict with { Ambiguous = true });
    }

    /// <summary>
    /// LP-I6, LP-I9: a recovery over healthy replicas seals <c>N − Q + 1</c> of them, so a seal check, which hears
    /// <c>Q</c>, reports it while any one replica fails. The replica fails its next three operations, so the check cannot
    /// recover by reading it again. The spec is strict: no failure is an allowed outcome.
    /// </summary>
    [Theory(Timeout = 120_000)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ASealCheckAfterARecoveryReportsSealedWhileAnyReplicaFails(int failing)
    {
        await AssertTheCaseConformsAsync(
            new ConformanceCase(
            [
                new([new("Write", new LedgerReplicaWrite(0))]),
                new([new("Recover", new LedgerReplicaRecover(0))]),
                new([new("Fault", new ReplicaFault(failing, 3, InjectedFault.BeforeApplying))]),
                new([new("ReadTail", new LedgerReplicaReadTail(0))]),
            ]),
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// LP-I6, LP-I9: a recovery whose seal never reached a held replica still sealed <c>N − Q + 1</c> others, so a seal
    /// check after it reports sealed while another replica fails, whatever the held replica answers.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AQuorumSealCheckAfterARecoveryWhichMissedAHeldReplicaReportsSealed()
    {
        await AssertTheCaseConformsAsync(
            new ConformanceCase(
            [
                new([new("Write", new LedgerReplicaWrite(0))]),
                new([new("Fault", new ReplicaFault(2, 1, InjectedFault.Held, "Seal"))]),
                new([new("Recover", new LedgerReplicaRecover(0))]),
                new([new("Fault", new ReplicaFault(0, 3, InjectedFault.BeforeApplying))]),
                new([new("ReadTail", new LedgerReplicaReadTail(0))]),
            ]),
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// LP-I7, LP-I4: after a recovery, a writer write reports sealed and is never written, even when the recovery's seal
    /// missed a held replica which takes the write: fewer than <c>Q</c> replicas can.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AWriterWriteAfterARecoveryIsSealedAndNeverWritten()
    {
        await AssertTheCaseConformsAsync(
            new ConformanceCase(
            [
                new([new("Write", new LedgerReplicaWrite(0))]),
                new([new("Fault", new ReplicaFault(2, 1, InjectedFault.Held, "Seal"))]),
                new([new("Recover", new LedgerReplicaRecover(0))]),
                new([new("Write", new LedgerReplicaWrite(1))]),
                new([new("Write", new LedgerReplicaWrite(2))]),
                new([new("Read", new LedgerReplicaRead(1))]),
            ]),
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// LP-I7: a recovery over a replica which missed two writes, while a replica holding them cannot read, ends above
    /// them, retrying the failed reads and copying the writes to the replica which missed them; a later read of each,
    /// with the replica which first held it failing, still returns its DATA.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ARecoveryOverAReplicaWhichMissedWritesEndsAboveThem()
    {
        await AssertTheCaseConformsAsync(
            new ConformanceCase(
            [
                new([new("Fault", new ReplicaFault(2, 2, InjectedFault.BeforeApplying, "Write"))]),
                new([new("Write", new LedgerReplicaWrite(0))]),
                new([new("Write", new LedgerReplicaWrite(1))]),
                new([new("Fault", new ReplicaFault(1, 3, InjectedFault.BeforeApplying, "Read"))]),
                new([new("Recover", new LedgerReplicaRecover(0))]),
                new([new("Fault", new ReplicaFault(0, 2, InjectedFault.BeforeApplying, "Read"))]),
                new([new("Read", new LedgerReplicaRead(0))]),
                new([new("Read", new LedgerReplicaRead(1))]),
            ]),
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// ledger-replicas.md §2.3, LR-I7: a write which every replica fails before applying it reaches no node, so the
    /// composition's listing may omit the ledger, until a write lands and creates it. The regression of #143, in which
    /// the spec took the failed write for a creation and required the listing to name the ledger. The same change to a
    /// failed recovery has no case: a recovery repeats its replica operations until they answer and fails only on a
    /// dropped answer, which the spec answers from the ledger's removal first, so reverting it is an equivalent mutant.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AWriteWhichEveryReplicaFailsBeforeApplyingItMayLeaveTheLedgerUnlisted()
    {
        ConformanceRound[] failEveryReplica =
        [
            new([new("Fault", new ReplicaFault(0, 1, InjectedFault.BeforeApplying))]),
            new([new("Fault", new ReplicaFault(1, 1, InjectedFault.BeforeApplying))]),
            new([new("Fault", new ReplicaFault(2, 1, InjectedFault.BeforeApplying))]),
        ];
        ConformanceRound listReplicas = new([new("ListReplicas", LedgerReplicaListReplicas.Instance)]);
        await AssertTheCaseConformsAsync(
            new ConformanceCase(
            [
                .. failEveryReplica,
                new([new("Write", new LedgerReplicaWrite(0))]),
                listReplicas,
                new([new("Write", new LedgerReplicaWrite(0))]),
                listReplicas,
            ]),
            TestContext.Current.CancellationToken,
            ambiguous: true);
    }

    /// <summary>
    /// ledger-replicas.md §2.1, outcomes.md §1: a memory replica whose operations answer <c>Backpressure</c> at random,
    /// before they retain or write anything, conforms to the replica role, whose backpressure has no effect.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AReplicaWhichAnswersBackpressureConforms()
    {
        var events = new ConcurrentQueue<string>();
        var kit = RandomKit(
            Replica(RandomWindow) with { Backpressure = true },
            (window, seed) => Task.FromResult(new MemoryTarget(
                window, replica => new FaultyLedgerReplica(0, replica, TimeProvider.System, events, new Random(seed), InjectedFault.Backpressure))),
            TestContext.Current.CancellationToken);
        await kit.SampleAsync(kit.Sequences(40), iterations: 200);
        await kit.SampleAsync(kit.ConcurrentSequences(20), iterations: 200);
        Assert.Contains(events, static entry => entry.EndsWith("meets Backpressure", StringComparison.Ordinal));
    }

    /// <summary>
    /// The kit catches a replica which answers a write <c>Backpressure</c> after applying it, whose backpressure then
    /// had an effect (ledger-replicas.md §2.1), and minimizes the case to the write and an operation which shows its DATA,
    /// by reading it or by answering a range past it with its length, its retained tail, or the replica it created (LR-I6,
    /// LR-I7). A tail the write did not carry takes one more round: an operation which carries it, which only the
    /// replica the write created retains, before the one which reports it (#185).
    /// </summary>
    [Fact(Timeout = 120_000)]
    public Task TheKitCatchesABackpressureAnswerAfterTheWriteApplied() =>
        CatchesAsync(
            RandomKit(
                Replica(RandomWindow) with { Backpressure = true },
                static (window, _) => Task.FromResult(new MemoryTarget(window, static replica => new BackpressureAfterApplyingLedgerReplica(replica))),
                TestContext.Current.CancellationToken),
            maxRounds: 3,
            @"^((S2\.)?(Read|ReadRange)\(.*\) → Data\[.*\]|(S2\.)?ReadRange\(.*\) → (InvalidArgument|OutsideBatch\(\d+\))|(S2\.)?(ReadTail|Seal)\(\d+\) → Tail\(\d+(, sealed)?\)|ListReplicas → Listed\[S\d\])$");

    /// <summary>
    /// The kit catches a replica whose writer write on a sealed replica stores and reports written (LR-I8), and minimizes
    /// the case to the seal and the write.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public Task TheKitCatchesAWriterWriteWhichIgnoresTheSeal() =>
        CatchesAsync(Mutation.WriteIgnoresSeal, maxRounds: 2, @"^(S2\.)?Write\(\d+(, tail \d+)?\) → Written$", TestContext.Current.CancellationToken);

    /// <summary>
    /// The kit catches a replica whose recovery write on a sealed replica reports sealed (LR-I5, LR-I8), and minimizes the
    /// case to the seal and the recovery write.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public Task TheKitCatchesARecoveryWriteRefusedWhenSealed() =>
        CatchesAsync(Mutation.RecoveryWriteRefusedWhenSealed, maxRounds: 2, @"^(S2\.)?RecoveryWrite\(\d+(, tail \d+)?\) → Sealed$", TestContext.Current.CancellationToken);

    /// <summary>
    /// The kit catches a replica which never forwards the contiguous tail an operation carries to its replica (LR-I6), and
    /// minimizes the case to an operation which carried a tail and a contiguous-tail read which reports less.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public Task TheKitCatchesATailWhichIsNotRetained() =>
        CatchesAsync(Mutation.TailNotRetained, maxRounds: 3, @"^(S2\.)?(ReadTail|Seal)\(\d+\) → Tail\(\d+(, sealed)?\)$", TestContext.Current.CancellationToken);

    /// <summary>
    /// The kit catches a node whose seal of a replica it does not hold answers sealed but creates nothing (LR-I7, LR-I8),
    /// and minimizes the case to the seal and an operation which shows the replica missing or unsealed, or, when the case
    /// shows it through a retained tail, to three calls (<see cref="ASealWhichDoesNotCreateMayBeCaughtInThreeCalls"/>).
    /// </summary>
    [Fact(Timeout = 120_000)]
    public Task TheKitCatchesASealWhichDoesNotCreate() =>
        CatchesAsync(Mutation.SealDoesNotCreate, maxRounds: 3, SealDoesNotCreateCall, TestContext.Current.CancellationToken);

    /// <summary>
    /// Regression test, #286: a seal which does not create is also caught by a call which retains a contiguous tail on the
    /// replica the seal should have created, and a later seal which reports a tail below it; no call of those three can be
    /// removed, so the kit, which removes one call at a time, minimizes such a case to three rounds, not two. The case is a
    /// random sample's shrunk case, run through the kit's minimizer.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public Task ASealWhichDoesNotCreateMayBeCaughtInThreeCalls()
    {
        static ConformanceRound Round(string name, object request) => new([new ConformanceCall(name, request)]);
        var shrunk = new ConformanceCase(
        [
            Round("ReadTail", new LedgerReplicaReadTail(1, Seal: true)),
            Round("Read", new LedgerReplicaRead(4, ContiguousTail: 3)),
            Round("WriteEmpty", new LedgerReplicaWriteEmpty(4)),
            Round("ReadTail", new LedgerReplicaReadTail(2, Seal: true)),
            Round("Write", new LedgerReplicaWrite(2)),
        ]);
        var kit = RandomKit(
            Replica(RandomWindow),
            (window, _) => Task.FromResult(new MutantTarget(window, Mutation.SealDoesNotCreate)),
            TestContext.Current.CancellationToken);
        return CatchesAsync(kit, maxRounds: 3, @"^Seal\(2\) → Tail\(2, sealed\)$", Gen.Const(shrunk), iterations: 1, minRounds: 3);
    }

    // A seal which does not create shows through the next write, contiguous-tail read, or listing, or a later seal below
    // the tail a call retained since.
    private const string SealDoesNotCreateCall = @"^((S2\.)?(Write|ReadTail|Seal)\(.*|ListReplicas) → .+$";

    /// <summary>
    /// The kit catches a node whose drop removes the replica but leaves no tombstone (LR-I3, LR-I7), and minimizes the case
    /// to the drop and an operation on the ledger which answers rather than refuse.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public Task TheKitCatchesADropWhichLeavesNoTombstone() =>
        CatchesAsync(
            Mutation.DropLeavesNoTombstone,
            maxRounds: 2,
            @"^(S2\.)?(Write|RecoveryWrite|Read|ReadRange|ReadTail|Seal)\(.* → (?!Dropped$).+$",
            TestContext.Current.CancellationToken);

    /// <summary>
    /// The kit catches a node whose read of a replica it does not hold creates it (LR-I7), and minimizes the case to the
    /// read and a listing which names the ledger, or a contiguous-tail read or seal which reports the tail the read carried,
    /// which the replica it created retained (LR-I6).
    /// </summary>
    [Fact(Timeout = 120_000)]
    public Task TheKitCatchesAReadWhichCreatesAReplica() =>
        CatchesAsync(Mutation.ReadCreates, maxRounds: 2, @"^(ListReplicas → Listed\[S\d\]|(S2\.)?(ReadTail|Seal)\(\d+\) → Tail\(\d+(, sealed)?\))$", TestContext.Current.CancellationToken);

    /// <summary>
    /// The kit catches a node whose listing names the ledgers it dropped (ledger-replicas.md §2.3), and minimizes the case to the
    /// drop and the listing.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public Task TheKitCatchesAListingWhichIncludesDroppedLedgers() =>
        CatchesAsync(Mutation.ListingIncludesDropped, maxRounds: 2, @"^ListReplicas → Listed\[S\d\]$", TestContext.Current.CancellationToken);

    /// <summary>
    /// The kit catches a composition which counts a replica's sealed answer as written (LP-I4), and minimizes the case to
    /// a recovery and a writer write which reports written.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public Task TheKitCatchesAQuorumWhichCountsASealedAnswerAsWritten() =>
        CatchesQuorumAsync(
            static replica => new SealedAsWrittenLedgerReplica(replica),
            @"^Write\(\d+\) → Written$",
            TestContext.Current.CancellationToken);

    /// <summary>
    /// The kit catches a quorum whose replicas answer a seal sealed before they seal (LR-I4, LR-I8, LP-I6), applying it
    /// only after their next writer write, which they take, and minimizes the case to a recovery and a writer
    /// write which reports written, or a listing which misses the replica the seal should have created (LR-I7).
    /// </summary>
    [Fact(Timeout = 120_000)]
    public Task TheKitCatchesAQuorumWhoseReplicasAnswerASealBeforeApplyingIt() =>
        CatchesQuorumAsync(
            static replica => new LateSealLedgerReplica(replica),
            @"^(Write\(\d+\) → Written|ListReplicas → Listed\[\])$",
            TestContext.Current.CancellationToken);

    /// <summary>
    /// The kit catches a quorum whose replicas answer missing for DATA they hold at or above the contiguous tail a read
    /// carries (LR-I2, LR-I4), so that a recovery ends below a written position (LP-I7), and minimizes the case to a
    /// write and a recovery which ends at it, or a read which reports it missing.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public Task TheKitCatchesAQuorumWhoseReplicasHideDataAtOrAboveTheTail() =>
        CatchesQuorumAsync(
            static replica => new HidesDataLedgerReplica(replica),
            @"^((Read|ReadRange)\([^)]*\) → Missing|Recover\(0\) → End\(\d+\))$",
            TestContext.Current.CancellationToken);

    /// <summary>
    /// LP-I10: the kit catches a composition whose read answers one replica's answer alone, and minimizes the case to that
    /// replica missing a write, the write, which <c>Q</c> others take, and a read which reports the slot missing.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public Task TheKitCatchesAReadWhichTrustsOneReplica() =>
        CatchesReadMutantAsync(LedgerReadMutant.TrustsOneReplica, maxRounds: 3, @"^(Read|ReadRange)\([^)]*\) → Missing$", TestContext.Current.CancellationToken);

    /// <summary>
    /// LP-I10, ledger-protocol.md §8.3: the kit catches a composition which reads a slot at or above the known contiguous
    /// tail as decided, one replica at a time, and minimizes the case to a read of a slot no replica holds, which fails
    /// rather than reporting it missing.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public Task TheKitCatchesAReadWhichDecidesASlotAtOrAboveTheTail() =>
        CatchesReadMutantAsync(LedgerReadMutant.DecidesEverySlot, maxRounds: 1, @"^(Read|ReadRange)\([^)]*\) → Unexpected\(", TestContext.Current.CancellationToken);

    private static LedgerReplicaSpecOptions Replica(int windowSize) => new() { WindowSize = windowSize, Role = LedgerReplicaRole.Replica };

    private static LedgerReplicaSpecOptions Composition(int windowSize, int replicaCount, int quorum) =>
        new() { WindowSize = windowSize, Role = LedgerReplicaRole.Composition, ReplicaCount = replicaCount, Quorum = quorum };

    /// <summary>
    /// Ends every case of <paramref name="kit"/> by healing: every fault clears and every held operation lands, and then a
    /// seal check, a recovery, and a read of each address must succeed, since no fault fires.
    /// </summary>
    private static ConformanceKit<LedgerReplicaState, LedgerTarget> Heals(ConformanceKit<LedgerReplicaState, LedgerTarget> kit) =>
        kit.Action("Heal", Gen.Const(Healing.Instance), static (target, _) => target.HealAsync(), weight: 0)
            .Finally(
            [
                new("Heal", Healing.Instance),
                new("ReadTail", new LedgerReplicaReadTail(0)),
                new("Recover", new LedgerReplicaRecover(0)),
                .. Enumerable.Range(0, 6).Select(static slot => new ConformanceCall("Read", new LedgerReplicaRead(slot))),
            ]);

    /// <summary>
    /// Registers random requests: a replica's on either of its node's ledgers, and the composition's on its one, each
    /// carrying a known contiguous tail; writes are sometimes recovery writes and contiguous-tail reads sometimes seals.
    /// </summary>
    private static ConformanceKit<LedgerReplicaState, TTarget> Register<TTarget>(ConformanceKit<LedgerReplicaState, TTarget> kit, LedgerReplicaRole role)
        where TTarget : LedgerReplicaTarget
    {
        var ledgers = role == LedgerReplicaRole.Replica ? ReplicaLedgers : Gen.Const(0);
        _ = kit
            .Operation(
                "Write",
                Gen.Select(Addresses, ledgers, Tails, Rarely).Select(static (slot, ledger, tail, recovery) => new LedgerReplicaWrite(slot, ledger, tail, recovery)),
                Execute<TTarget, LedgerReplicaWrite>,
                weight: 12)
            .Operation(
                "WriteEmpty",
                Gen.Select(Gen.Long[0, 5], ledgers).Select(static (slot, ledger) => new LedgerReplicaWriteEmpty(slot, ledger)),
                Execute<TTarget, LedgerReplicaWriteEmpty>)
            .Operation(
                "Read",
                Gen.Select(Addresses, ledgers, Tails).Select(static (slot, ledger, tail) => new LedgerReplicaRead(slot, ledger, tail)),
                Execute<TTarget, LedgerReplicaRead>,
                weight: 6)
            .Operation(
                "ReadRange",
                Gen.Select(Gen.Long[0, 5], Gen.Int[0, 4], Gen.Int[1, 4], ledgers, Tails)
                    .Select(static (slot, offset, length, ledger, tail) => new LedgerReplicaReadRange(slot, offset, length, ledger, tail)),
                Execute<TTarget, LedgerReplicaReadRange>,
                weight: 3)
            .Operation(
                "ReadTail",
                Gen.Select(Gen.Long[0, 4], Sometimes, ledgers).Select(static (tail, seal, ledger) => new LedgerReplicaReadTail(tail, seal, ledger)),
                Execute<TTarget, LedgerReplicaReadTail>,
                weight: 6)
            .Operation("ListReplicas", Gen.Const(LedgerReplicaListReplicas.Instance), Execute<TTarget, LedgerReplicaListReplicas>, weight: 2);
        if (role == LedgerReplicaRole.Composition)
        {
            return kit
                .Operation("Recover", Tails.Select(static tail => new LedgerReplicaRecover(tail)), Execute<TTarget, LedgerReplicaRecover>, weight: 2)
                .Operation("Remove", Gen.Const(LedgerReplicaRemove.Instance), Execute<TTarget, LedgerReplicaRemove>);
        }

        return kit.Operation("DropReplica", ledgers.Select(static ledger => new LedgerReplicaDropReplica(ledger)), Execute<TTarget, LedgerReplicaDropReplica>);
    }

    private static Task<LedgerReplicaOutcome> Execute<TTarget, TRequest>(TTarget target, TRequest request)
        where TTarget : LedgerReplicaTarget
        where TRequest : notnull =>
        target.RunAsync(request);

    /// <summary>
    /// Runs random sequences against a replica mutant and checks the report.
    /// </summary>
    private Task CatchesAsync(Mutation mutation, int maxRounds, string failingCall, CancellationToken cancellationToken) =>
        CatchesAsync(
            RandomKit(Replica(RandomWindow), (window, _) => Task.FromResult(new MutantTarget(window, mutation)), cancellationToken), maxRounds, failingCall);

    /// <summary>
    /// Runs random sequences against the three-replica, quorum-two composition over replicas which
    /// <paramref name="wrap"/> makes mutants, and checks that the report minimizes the case to two rounds. The composition's
    /// caller preconditions tie a call to the calls which raised the contiguous tail it needs, so the kit, which removes
    /// one call at a time, could stop at a larger case; these requests carry no contiguous tail and write within the
    /// first window, so every precondition holds in every state.
    /// </summary>
    private Task CatchesQuorumAsync(Func<ILedgerReplica, ILedgerReplica> wrap, string failingCall, CancellationToken cancellationToken)
    {
        var addresses = Gen.Long[0, RandomWindow - 1];
        var kit = Kit(Composition(RandomWindow, 3, 2), (window, _) => Task.FromResult(new LedgerTarget(window, wrap: wrap)), cancellationToken)
            .Operation(
                "Write",
                addresses.Select(static slot => new LedgerReplicaWrite(slot)),
                Execute<LedgerTarget, LedgerReplicaWrite>,
                weight: 12)
            .Operation("Read", addresses.Select(static slot => new LedgerReplicaRead(slot)), Execute<LedgerTarget, LedgerReplicaRead>, weight: 6)
            .Operation(
                "ReadRange",
                Gen.Select(addresses, Gen.Int[0, 4], Gen.Int[1, 4]).Select(static (slot, offset, length) => new LedgerReplicaReadRange(slot, offset, length)),
                Execute<LedgerTarget, LedgerReplicaReadRange>,
                weight: 3)
            .Operation("ReadTail", Gen.Const(new LedgerReplicaReadTail(0)), Execute<LedgerTarget, LedgerReplicaReadTail>, weight: 6)
            .Operation("Recover", Gen.Const(new LedgerReplicaRecover(0)), Execute<LedgerTarget, LedgerReplicaRecover>, weight: 2)
            .Operation("ListReplicas", Gen.Const(LedgerReplicaListReplicas.Instance), Execute<LedgerTarget, LedgerReplicaListReplicas>, weight: 2);
        return CatchesAsync(kit, maxRounds: 2, failingCall);
    }

    /// <summary>
    /// Runs random sequences against the three-replica, quorum-two composition whose reads carry <paramref name="mutant"/>,
    /// preferring its first replica, of writes, reads, and a fault which makes that replica miss its next write, and
    /// checks the report.
    /// </summary>
    private Task CatchesReadMutantAsync(LedgerReadMutant mutant, int maxRounds, string failingCall, CancellationToken cancellationToken)
    {
        var addresses = Gen.Long[0, RandomWindow - 1];
        var kit = Kit(Composition(RandomWindow, 3, 2), (window, _) => Task.FromResult(new LedgerTarget(window, preferred: 0, mutant: mutant)), cancellationToken)
            .Operation("Write", addresses.Select(static slot => new LedgerReplicaWrite(slot)), Execute<LedgerTarget, LedgerReplicaWrite>, weight: 6)
            .Operation("Read", addresses.Select(static slot => new LedgerReplicaRead(slot)), Execute<LedgerTarget, LedgerReplicaRead>, weight: 6)
            .Action(
                "Fault",
                Gen.Const(new ReplicaFault(0, 1, InjectedFault.BeforeApplying, "Write")),
                static (target, fault) =>
                {
                    target.Inject(fault);
                    return Task.CompletedTask;
                },
                weight: 4);
        return CatchesAsync(kit, maxRounds, failingCall);
    }

    /// <summary>
    /// Runs random sequences of <paramref name="kit"/> against a mutant, or <paramref name="cases"/>, and checks the report:
    /// its first case, the minimized one, fails at its last round, has from <paramref name="minRounds"/> to
    /// <paramref name="maxRounds"/> rounds, fails at a call matching <paramref name="failingCall"/>, and names the seed which
    /// replays it.
    /// </summary>
    private static async Task CatchesAsync<TTarget>(
        ConformanceKit<LedgerReplicaState, TTarget> kit,
        int maxRounds,
        string failingCall,
        Gen<ConformanceCase>? cases = null,
        long iterations = 200,
        int minRounds = 1)
        where TTarget : LedgerReplicaTarget
    {
        var timeout = TimeSpan.FromSeconds(5);

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => kit.SampleAsync(cases ?? kit.Sequences(40), iterations));

        var report = Regex.Match(
            failure.Message,
            @"The spec does not allow round (?<round>\d+) of (?<rounds>\d+), seed \d+:.*?^> \k<round>\. (?<call>[^\r\n]*)",
            RegexOptions.Singleline | RegexOptions.Multiline,
            timeout);
        Assert.True(report.Success, failure.Message);
        Assert.Equal(report.Groups["rounds"].Value, report.Groups["round"].Value);
        Assert.InRange(int.Parse(report.Groups["rounds"].Value, CultureInfo.InvariantCulture), minRounds, maxRounds);
        Assert.Matches(new Regex(failingCall, RegexOptions.None, timeout), report.Groups["call"].Value);
        var seed = Regex.Match(failure.Message, "CsCheck_Seed=(?<seed>[^\\s\"]+)", RegexOptions.None, timeout);
        Assert.True(seed.Success, failure.Message);
        Assert.Matches(
            new Regex("Replay the shrunk case with CLOG_TEST_SEED=" + Regex.Escape(seed.Groups["seed"].Value) + @"(\s|$)", RegexOptions.None, timeout),
            failure.Message);
    }

    private ConformanceKit<LedgerReplicaState, TTarget> Kit<TTarget>(
        LedgerReplicaSpecOptions options, Func<LedgerReplicaOptions, int, Task<TTarget>> create, CancellationToken cancellationToken)
        where TTarget : LedgerReplicaTarget
    {
        var window = new LedgerReplicaOptions { WindowSize = options.WindowSize };
        return new ConformanceKit<LedgerReplicaState, TTarget>(
            LedgerReplicaSpec.Create(options),
            static () => new LedgerReplicaState(),
            seed => create(window, seed),
            (request, state) => LedgerReplicaSpec.IsCallerAllowed(request, state, options),
            cancellationToken,
            _leaks.InScopeAsync);
    }

    /// <summary>
    /// Runs a hand-written case against the three-replica, quorum-two composition, which fails unless the spec allows every
    /// round: strict, or, when <paramref name="ambiguous"/>, allowing a failed operation to take effect later or never.
    /// </summary>
    private Task AssertTheCaseConformsAsync(
        ConformanceCase testCase, CancellationToken cancellationToken, bool ambiguous = false, Func<LedgerReplicaOptions, LedgerTarget>? create = null) =>
        CaseKit(Composition(RandomWindow, 3, 2) with { Ambiguous = ambiguous }, cancellationToken, create).RunAsync(testCase);

    /// <summary>
    /// Returns a kit over the three-replica, quorum-two composition, or the targets <paramref name="create"/> builds,
    /// with the operations and actions the hand-written cases name.
    /// </summary>
    private ConformanceKit<LedgerReplicaState, LedgerTarget> CaseKit(
        LedgerReplicaSpecOptions options, CancellationToken cancellationToken, Func<LedgerReplicaOptions, LedgerTarget>? create = null) =>
        Kit(options, (window, _) => Task.FromResult(create?.Invoke(window) ?? new LedgerTarget(window, options.ReplicaCount, options.Quorum)), cancellationToken)
            .Operation("Write", Gen.Const(new LedgerReplicaWrite(0)), Execute<LedgerTarget, LedgerReplicaWrite>)
            .Operation("Read", Gen.Const(new LedgerReplicaRead(0)), Execute<LedgerTarget, LedgerReplicaRead>)
            .Operation("ReadTail", Gen.Const(new LedgerReplicaReadTail(0)), Execute<LedgerTarget, LedgerReplicaReadTail>)
            .Operation("Recover", Gen.Const(new LedgerReplicaRecover(0)), Execute<LedgerTarget, LedgerReplicaRecover>)
            .Operation("ListReplicas", Gen.Const(LedgerReplicaListReplicas.Instance), Execute<LedgerTarget, LedgerReplicaListReplicas>)
            .Action("Fault", Gen.Const(new ReplicaFault(0, 1, InjectedFault.BeforeApplying)), static (target, fault) =>
            {
                target.Inject(fault);
                return Task.CompletedTask;
            })
            .Action("Release", Gen.Const(Release.Instance), static (target, _) => target.ReleaseAsync());

    /// <summary>
    /// Runs every sequence of up to <paramref name="depth"/> operations, and every concurrent group after a shorter
    /// sequence, which Accordant generates over addresses 0 to 2 with window <see cref="ExhaustiveWindow"/>, with the
    /// store's restart when <paramref name="restarts"/> is set. Generation explores the contract's outcomes without
    /// failures, whose branches add states but no requests; the kit judges the sequences with <paramref name="options"/>
    /// and the concurrent groups with <paramref name="concurrentOptions"/>, or with <paramref name="options"/> when it is
    /// <see langword="null"/>.
    /// </summary>
    private async Task CheckExhaustivelyAsync<TTarget>(
        LedgerReplicaSpecOptions options,
        Func<LedgerReplicaOptions, int, Task<TTarget>> create,
        CancellationToken cancellationToken,
        int depth = 4,
        LedgerReplicaSpecOptions? concurrentOptions = null,
        Func<ConformanceKit<LedgerReplicaState, TTarget>, ConformanceKit<LedgerReplicaState, TTarget>>? configure = null,
        bool restarts = false)
        where TTarget : LedgerReplicaTarget
    {
        configure ??= static kit => kit;
        ConformanceKit<LedgerReplicaState, TTarget> Configured(LedgerReplicaSpecOptions judged)
        {
            var registered = Register(Kit(judged, create, cancellationToken), options.Role);
            if (restarts)
            {
                _ = registered.Operation("Restart", Gen.Bool.Select(static operatingSystem => new LedgerReplicaRestart(operatingSystem)), Execute<TTarget, LedgerReplicaRestart>);
            }

            return configure(registered);
        }

        var kit = Configured(options);
        var concurrentKit = concurrentOptions is null ? kit : Configured(concurrentOptions);
        var spec = LedgerReplicaSpec.Create(options with { Ambiguous = false, Backpressure = false });
        var inputs = new InputSet();
        void Add<TRequest>(string operation, TRequest request)
            where TRequest : notnull =>
            inputs.Add(spec.GetOperation<TRequest, LedgerReplicaOutcome>(operation).With(request, request.ToString()));

        Add("Write", new LedgerReplicaWrite(0));
        Add("Write", new LedgerReplicaWrite(1));
        Add("Write", new LedgerReplicaWrite(2));
        Add("Write", new LedgerReplicaWrite(long.MaxValue));
        Add("Write", new LedgerReplicaWrite(1, ContiguousTail: 1));
        Add("WriteEmpty", new LedgerReplicaWriteEmpty(0));
        Add("Read", new LedgerReplicaRead(1));
        Add("ReadRange", new LedgerReplicaReadRange(0, 1, 1));
        Add("ReadTail", new LedgerReplicaReadTail(0));
        Add("ReadTail", new LedgerReplicaReadTail(1));
        Add("ListReplicas", LedgerReplicaListReplicas.Instance);
        if (options.Role == LedgerReplicaRole.Replica)
        {
            Add("Write", new LedgerReplicaWrite(0, Recovery: true));
            Add("ReadTail", new LedgerReplicaReadTail(0, Seal: true));
            Add("DropReplica", new LedgerReplicaDropReplica());
            Add("Write", new LedgerReplicaWrite(0, Ledger: 1));
            Add("ReadTail", new LedgerReplicaReadTail(0, Seal: true, Ledger: 1));
            Add("DropReplica", new LedgerReplicaDropReplica(Ledger: 1));
            if (restarts)
            {
                Add("Restart", new LedgerReplicaRestart(OperatingSystem: false));
            }
        }
        else
        {
            Add("Recover", new LedgerReplicaRecover(0));
            Add("Remove", LedgerReplicaRemove.Instance);
        }

        var initial = new LedgerReplicaState();
        var sequences = spec.GenerateTests(initial, inputs, kit.GenerationOptions(depth));
        var concurrent = spec.GenerateConcurrentTests(initial, inputs, concurrentKit.GenerationOptions(depth - 1));
        Assert.NotEmpty(sequences);
        Assert.NotEmpty(concurrent);
        await kit.RunAsync(sequences);
        await concurrentKit.RunAsync(concurrent);
    }

    /// <summary>
    /// Returns a kit which draws random requests over addresses 0 to 5, and rarely an invalid address, with the node's
    /// listings and drops, or the composition's recoveries and removal.
    /// </summary>
    private ConformanceKit<LedgerReplicaState, TTarget> RandomKit<TTarget>(
        LedgerReplicaSpecOptions options, Func<LedgerReplicaOptions, int, Task<TTarget>> create, CancellationToken cancellationToken)
        where TTarget : LedgerReplicaTarget =>
        Register(Kit(options, create, cancellationToken), options.Role);

    /// <summary>
    /// Every held replica operation is released and lands.
    /// </summary>
    private sealed record Release
    {
        internal static Release Instance { get; } = new();

        public override string ToString() => "the held operations land";
    }

    /// <summary>
    /// Every fault clears and every held replica operation lands.
    /// </summary>
    private sealed record Healing
    {
        internal static Healing Instance { get; } = new();

        public override string ToString() => "the faults clear and the held operations land";
    }

    /// <summary>
    /// A contract violation of <see cref="MutantTarget"/>.
    /// </summary>
    private enum Mutation
    {
        /// <summary>A writer write on a sealed replica stores and reports written.</summary>
        WriteIgnoresSeal,

        /// <summary>A recovery write on a sealed replica reports sealed and stores nothing.</summary>
        RecoveryWriteRefusedWhenSealed,

        /// <summary>A write or read never forwards the contiguous tail it carries to the replica.</summary>
        TailNotRetained,

        /// <summary>A seal of a replica the node does not hold reports sealed and creates nothing.</summary>
        SealDoesNotCreate,

        /// <summary>A drop removes the replica and leaves no tombstone.</summary>
        DropLeavesNoTombstone,

        /// <summary>A read of a replica the node does not hold creates it.</summary>
        ReadCreates,

        /// <summary>A listing names the ledgers the node dropped.</summary>
        ListingIncludesDropped,
    }

    /// <summary>
    /// A replica which reports every sealed write as written, so its quorum counts a sealed answer toward written.
    /// </summary>
    private sealed class SealedAsWrittenLedgerReplica(ILedgerReplica inner) : ILedgerReplica
    {
        public async ValueTask<ReplicaWriteOutcome> WriteAsync(
            Slot slot, ArcBuffer payload, Slot contiguousTail, bool recovery, CancellationToken cancellationToken = default)
        {
            _ = await inner.WriteAsync(slot, payload, contiguousTail, recovery, cancellationToken);
            return ReplicaAnswers.Written;
        }

        public ValueTask<ReplicaRead> ReadAsync(Slot slot, Slot contiguousTail, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(slot, contiguousTail, cancellationToken);

        public ValueTask<ReplicaRead> ReadAsync(
            Slot slot, int offset, int length, Slot contiguousTail, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(slot, offset, length, contiguousTail, cancellationToken);

        public ValueTask<ReplicaTailRead> ReadTailAsync(Slot contiguousTail, bool seal, CancellationToken cancellationToken = default) =>
            inner.ReadTailAsync(contiguousTail, seal, cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    /// <summary>
    /// A replica which answers every write it applied <c>Backpressure</c>, which a replica gives only before it retains or
    /// writes anything (ledger-replicas.md §2.1).
    /// </summary>
    private sealed class BackpressureAfterApplyingLedgerReplica(ILedgerReplica inner) : ILedgerReplica
    {
        public async ValueTask<ReplicaWriteOutcome> WriteAsync(
            Slot slot, ArcBuffer payload, Slot contiguousTail, bool recovery, CancellationToken cancellationToken = default)
        {
            var answer = await inner.WriteAsync(slot, payload, contiguousTail, recovery, cancellationToken);
            return answer is ReplicaWriteOutcome.Written ? ReplicaAnswers.Backpressure : answer;
        }

        public ValueTask<ReplicaRead> ReadAsync(Slot slot, Slot contiguousTail, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(slot, contiguousTail, cancellationToken);

        public ValueTask<ReplicaRead> ReadAsync(
            Slot slot, int offset, int length, Slot contiguousTail, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(slot, offset, length, contiguousTail, cancellationToken);

        public ValueTask<ReplicaTailRead> ReadTailAsync(Slot contiguousTail, bool seal, CancellationToken cancellationToken = default) =>
            inner.ReadTailAsync(contiguousTail, seal, cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    /// <summary>
    /// A replica which answers a seal sealed before it seals: it reports itself sealed from then on, but applies the seal
    /// only after its next writer write, which it takes, so the seal's answer reflects no durable state (LR-I4, LR-I8).
    /// </summary>
    private sealed class LateSealLedgerReplica(ILedgerReplica inner) : ILedgerReplica
    {
        private int _sealPending;

        public async ValueTask<ReplicaWriteOutcome> WriteAsync(
            Slot slot, ArcBuffer payload, Slot contiguousTail, bool recovery, CancellationToken cancellationToken = default)
        {
            var result = await inner.WriteAsync(slot, payload, contiguousTail, recovery, cancellationToken);
            if (!recovery && Interlocked.Exchange(ref _sealPending, 0) != 0)
            {
                _ = await inner.ReadTailAsync(contiguousTail, seal: true, cancellationToken);
            }

            return result;
        }

        public ValueTask<ReplicaRead> ReadAsync(Slot slot, Slot contiguousTail, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(slot, contiguousTail, cancellationToken);

        public ValueTask<ReplicaRead> ReadAsync(
            Slot slot, int offset, int length, Slot contiguousTail, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(slot, offset, length, contiguousTail, cancellationToken);

        public async ValueTask<ReplicaTailRead> ReadTailAsync(Slot contiguousTail, bool seal, CancellationToken cancellationToken = default)
        {
            var answer = await inner.ReadTailAsync(contiguousTail, seal: false, cancellationToken);
            if (!answer.TryGetValue(out LedgerReplicaTail tail))
            {
                return answer;
            }

            if (seal && !tail.IsSealed)
            {
                Volatile.Write(ref _sealPending, 1);
            }

            return Volatile.Read(ref _sealPending) != 0 ? tail with { IsSealed = true } : tail;
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    /// <summary>
    /// A replica which answers missing for DATA it holds at or above the contiguous tail a read carries, which it still
    /// retains, as though the DATA were not there (LR-I2).
    /// </summary>
    private sealed class HidesDataLedgerReplica(ILedgerReplica inner) : ILedgerReplica
    {
        public ValueTask<ReplicaWriteOutcome> WriteAsync(
            Slot slot, ArcBuffer payload, Slot contiguousTail, bool recovery, CancellationToken cancellationToken = default) =>
            inner.WriteAsync(slot, payload, contiguousTail, recovery, cancellationToken);

        public async ValueTask<ReplicaRead> ReadAsync(Slot slot, Slot contiguousTail, CancellationToken cancellationToken = default)
        {
            var result = await inner.ReadAsync(slot, contiguousTail, cancellationToken);
            if (slot < contiguousTail || result.Case != ReplicaReadCase.Batch)
            {
                return result;
            }

            result.Batch.Dispose();
            return ReplicaAnswers.Missing;
        }

        public ValueTask<ReplicaRead> ReadAsync(
            Slot slot, int offset, int length, Slot contiguousTail, CancellationToken cancellationToken = default) =>
            LedgerReplicaOperations.ReadRangeOfValueAsync(this, slot, offset, length, contiguousTail, cancellationToken);

        public ValueTask<ReplicaTailRead> ReadTailAsync(Slot contiguousTail, bool seal, CancellationToken cancellationToken = default) =>
            inner.ReadTailAsync(contiguousTail, seal, cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    /// <summary>
    /// A node of memory replicas, created by their first write, recovery write, or seal, with one contract violation.
    /// </summary>
    private sealed class MutantTarget : LedgerReplicaTarget, ILedgerStore
    {
        private readonly Lock _lock = new();
        private readonly LedgerReplicaOptions _window;
        private readonly Mutation _mutation;
        private readonly Dictionary<LedgerId, InMemoryLedgerReplica> _replicas = [];
        private readonly HashSet<LedgerId> _tombstones = [];
        private readonly MutantReplica[] _mutantReplicas;

        internal MutantTarget(LedgerReplicaOptions window, Mutation mutation)
        {
            _window = window;
            _mutation = mutation;
            _mutantReplicas = [.. Ledgers.Select(ledger => new MutantReplica(this, ledger))];
        }

        public NodeId NodeId => LedgerReplicaTestData.Node;

        protected override ILedgerStore Node => this;

        internal override ILedgerReplica Replica(int ledger) => _mutantReplicas[ledger];

        public ValueTask<IReadOnlyList<LedgerId>> ListReplicasAsync(CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                var listed = _mutation == Mutation.ListingIncludesDropped ? _replicas.Keys.Union(_tombstones) : _replicas.Keys;
                return ValueTask.FromResult<IReadOnlyList<LedgerId>>([.. listed.Order()]);
            }
        }

        public async ValueTask DropReplicaAsync(LedgerId ledger, CancellationToken cancellationToken = default)
        {
            InMemoryLedgerReplica? dropped;
            lock (_lock)
            {
                if (_mutation != Mutation.DropLeavesNoTombstone)
                {
                    _ = _tombstones.Add(ledger);
                }

                dropped = _replicas.GetValueOrDefault(ledger);
                _ = _replicas.Remove(ledger);
            }

            if (dropped is not null)
            {
                await dropped.DisposeAsync();
            }
        }

        public override async ValueTask DisposeAsync()
        {
            InMemoryLedgerReplica[] replicas;
            lock (_lock)
            {
                replicas = [.. _replicas.Values];
                _replicas.Clear();
            }

            foreach (var replica in replicas)
            {
                await replica.DisposeAsync();
            }
        }

        /// <summary>
        /// Finds the replica of <paramref name="ledger"/> for a mutation, creating it, or returns <see langword="null"/>
        /// when the node dropped the ledger.
        /// </summary>
        private InMemoryLedgerReplica? FindOrCreate(LedgerId ledger)
        {
            lock (_lock)
            {
                if (_tombstones.Contains(ledger))
                {
                    return null;
                }

                if (!_replicas.TryGetValue(ledger, out var replica))
                {
                    replica = new InMemoryLedgerReplica(_window);
                    _replicas.Add(ledger, replica);
                }

                return replica;
            }
        }

        /// <summary>
        /// Finds the replica of <paramref name="ledger"/> for a read, or returns <see langword="null"/> when the node does
        /// not hold it, with <paramref name="dropped"/> set when the node dropped the ledger.
        /// </summary>
        private InMemoryLedgerReplica? Find(LedgerId ledger, out bool dropped)
        {
            if (_mutation == Mutation.ReadCreates)
            {
                var created = FindOrCreate(ledger);
                dropped = created is null;
                return created;
            }

            lock (_lock)
            {
                dropped = _tombstones.Contains(ledger);
                return dropped ? null : _replicas.GetValueOrDefault(ledger);
            }
        }

        /// <summary>
        /// The mutant node's replica of one ledger.
        /// </summary>
        private sealed class MutantReplica(MutantTarget node, LedgerId ledger) : ILedgerReplica
        {
            public async ValueTask<ReplicaWriteOutcome> WriteAsync(
                Slot slot, ArcBuffer payload, Slot contiguousTail, bool recovery, CancellationToken cancellationToken = default)
            {
                // A request the replica rejects creates nothing (ledger-replicas.md §3.2, LR-I7).
                LedgerReplicaOperations.ValidateSlot(slot);
                LedgerReplicaOperations.ValidateBatch(payload);
                if (node.FindOrCreate(ledger) is not { } replica)
                {
                    return ReplicaAnswers.Dropped;
                }

                var forwarded = Forwarded(contiguousTail);
                if (node._mutation == Mutation.WriteIgnoresSeal && !recovery)
                {
                    return await replica.WriteAsync(slot, payload, forwarded, recovery: true, cancellationToken);
                }

                if (node._mutation == Mutation.RecoveryWriteRefusedWhenSealed
                    && recovery
                    && (await replica.ReadTailAsync(Slot.Zero, seal: false, cancellationToken)).Tail.IsSealed)
                {
                    return ReplicaAnswers.Sealed;
                }

                return await replica.WriteAsync(slot, payload, forwarded, recovery, cancellationToken);
            }

            public async ValueTask<ReplicaRead> ReadAsync(Slot slot, Slot contiguousTail, CancellationToken cancellationToken = default)
            {
                LedgerReplicaOperations.ValidateSlot(slot);
                var replica = node.Find(ledger, out var dropped);
                if (dropped)
                {
                    return ReplicaWriteOutcome.Dropped.Instance;
                }

                return replica is null ? ReplicaAnswers.Missing : await replica.ReadAsync(slot, Forwarded(contiguousTail), cancellationToken);
            }

            public ValueTask<ReplicaRead> ReadAsync(
                Slot slot, int offset, int length, Slot contiguousTail, CancellationToken cancellationToken = default) =>
                LedgerReplicaOperations.ReadRangeOfValueAsync(this, slot, offset, length, contiguousTail, cancellationToken);

            public async ValueTask<ReplicaTailRead> ReadTailAsync(Slot contiguousTail, bool seal, CancellationToken cancellationToken = default)
            {
                if (seal)
                {
                    if (node._mutation == Mutation.SealDoesNotCreate && node.Find(ledger, out var refused) is null && !refused)
                    {
                        return new LedgerReplicaTail(contiguousTail, IsSealed: true);
                    }

                    return node.FindOrCreate(ledger) is { } sealing
                        ? await sealing.ReadTailAsync(contiguousTail, seal: true, cancellationToken)
                        : ReplicaWriteOutcome.Dropped.Instance;
                }

                var replica = node.Find(ledger, out var dropped);
                if (dropped)
                {
                    return ReplicaWriteOutcome.Dropped.Instance;
                }

                return replica is null
                    ? new LedgerReplicaTail(contiguousTail, IsSealed: false)
                    : await replica.ReadTailAsync(contiguousTail, seal: false, cancellationToken);
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;

            private Slot Forwarded(Slot contiguousTail) =>
                node._mutation == Mutation.TailNotRetained ? Slot.Zero : contiguousTail;
        }
    }
}
