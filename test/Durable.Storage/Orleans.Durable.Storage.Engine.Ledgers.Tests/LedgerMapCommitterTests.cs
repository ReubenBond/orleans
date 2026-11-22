using System.Collections.Immutable;
using Orleans.Durable.Storage.Engine.GroupCommit;
using Orleans.Durable.Storage.Engine.Ledgers.Control;
using Orleans.Durable.Storage.Testing;
using static Orleans.Durable.Storage.Engine.Ledgers.Tests.LedgerMapRulesTests;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests;

/// <summary>
/// <see cref="LedgerMapCommitter"/> over a scripted register (ledgers.md §2.6, §10): group commit, the versions it names
/// and the reads it makes, mismatches after an applied attempt, writes whose outcome is unknown landing before the
/// re-read, after it, or never, a second committer on the register, the value limit, defects, cancellation, disposal,
/// and the register's failures. Every answer is judged by <see cref="LedgerMapHistoryOracle"/> against the register's
/// values since its submission, and the leak check sees every entry the committer reads.
/// </summary>
public sealed class LedgerMapCommitterTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();
    private readonly ScriptedConditionalRegister _register = new();
    private readonly List<LedgerMapSubmission> _submissions = [];
    private readonly Dictionary<LedgerMapCommitter, int> _writers = [];

    public void Dispose()
    {
        var (history, dispatches, writers) = History();
        Assert.Empty(LedgerMapHistoryOracle.Violations(history, dispatches, writers, _submissions));
        _leaks.Dispose();
    }

    /// <summary>
    /// A committer answers from the value its write produced, and names the version its last applied write returned,
    /// so consecutive batches read the register only once, at first.
    /// </summary>
    [Fact]
    public async Task ACommitterNamesTheVersionItsLastWriteReturnedAndReadsOnlyAtFirst()
    {
        await using var committer = Committer();
        Assert.Equal(new LedgerMapCommitOutcome.Applied(1), (await SubmitAsync(committer, Install([], [KeyRange.Root]))).Value);
        Assert.Equal(new LedgerMapCommitOutcome.Applied(2), (await SubmitAsync(committer, Replace(1))).Value);
        Assert.Equal(new LedgerMapCommitOutcome.Applied(3), (await SubmitAsync(committer, new LedgerMapChange.RaiseFloor(4))).Value);
        Assert.Equal(1, _register.Reads);
        Assert.Equal(3, _register.Writes);
    }

    /// <summary>
    /// While a write is held, the next batch accumulates: its changes fold in submission order into one write, of two
    /// replacements of one ledger one applies, and a resubmission merges with its original, both callers answered alike.
    /// </summary>
    [Fact]
    public async Task ABatchFoldsInSubmissionOrderAndMergesIdenticalChanges()
    {
        await using var committer = Committer();
        _ = await SubmitAsync(committer, Install([], [KeyRange.Root]));
        var held = _register.HoldNextWrite();
        var raise = SubmitAsync(committer, new LedgerMapChange.RaiseFloor(1));
        await held.Arrived;
        var first = Replace(1, chosenAt: 1);
        var replacements = new[] { SubmitAsync(committer, first), SubmitAsync(committer, Replace(1, chosenAt: 1)), SubmitAsync(committer, first) };
        held.Answer();
        Assert.Equal(new LedgerMapCommitOutcome.Applied(2), (await raise).Value);
        var answers = await Task.WhenAll(replacements);
        Assert.Equal(new LedgerMapCommitOutcome.Applied(3), answers[0].Value);
        Assert.Equal(new LedgerMapCommitOutcome.Superseded(3), answers[1].Value);
        Assert.Equal(answers[0].Value, answers[2].Value);
        Assert.Equal(3, _register.Writes);
    }

    /// <summary>
    /// A second committer's writes make the first one's cached value lag: a change it would apply mismatches and is
    /// decided again from a read, and a batch which applies nothing against a value from before its submission re-reads.
    /// </summary>
    [Fact]
    public async Task ACommitterWhoseValueLagsDecidesFromARead()
    {
        await using var first = Committer();
        await using var second = Committer(writer: 1);
        _ = await SubmitAsync(first, Install([], [KeyRange.Root]));
        Assert.Equal(new LedgerMapCommitOutcome.Applied(2), (await SubmitAsync(second, Replace(1))).Value);
        Assert.Equal(new LedgerMapCommitOutcome.Superseded(2), (await SubmitAsync(first, Replace(1))).Value);

        Assert.Equal(new LedgerMapCommitOutcome.Applied(3), (await SubmitAsync(first, new LedgerMapChange.RaiseFloor(5))).Value);
        Assert.Equal(new LedgerMapCommitOutcome.Applied(4), (await SubmitAsync(second, new LedgerMapChange.RaiseFloor(6))).Value);
        var reads = _register.Reads;
        Assert.Equal(new LedgerMapCommitOutcome.Applied(4), (await SubmitAsync(first, new LedgerMapChange.RaiseFloor(5))).Value);
        Assert.Equal(reads + 1, _register.Reads);
    }

    /// <summary>
    /// A stale controller's install, chosen below a floor raised since, is superseded; a repeated floor raise finds its
    /// goal; and a stale bootstrap is superseded once the keyspace is bootstrapped.
    /// </summary>
    [Fact]
    public async Task StaleChoicesAreSupersededAndRepeatedRaisesFindTheirGoal()
    {
        await using var committer = Committer();
        var staleBootstrap = Install([], [KeyRange.Root]);
        _ = await SubmitAsync(committer, Install([], [KeyRange.Root]));
        Assert.Equal(new LedgerMapCommitOutcome.Superseded(1), (await SubmitAsync(committer, staleBootstrap)).Value);
        Assert.Equal(new LedgerMapCommitOutcome.Applied(2), (await SubmitAsync(committer, new LedgerMapChange.RaiseFloor(3))).Value);
        Assert.Equal(new LedgerMapCommitOutcome.Superseded(2), (await SubmitAsync(committer, Replace(1, chosenAt: 2))).Value);
        Assert.Equal(new LedgerMapCommitOutcome.Applied(2), (await SubmitAsync(committer, new LedgerMapChange.RaiseFloor(3))).Value);
    }

    /// <summary>A write which applied and answered a mismatch, as after an SDK retry, is decided from a read: its goals hold.</summary>
    [Fact]
    public async Task AMismatchAfterAnAppliedAttemptIsDecidedFromARead()
    {
        await using var committer = Committer();
        var held = _register.HoldNextWrite();
        var bootstrap = SubmitAsync(committer, Install([], [KeyRange.Root]));
        await held.Arrived;
        held.ApplyAndAnswerMismatch();
        Assert.Equal(new LedgerMapCommitOutcome.Applied(1), (await bootstrap).Value);
        Assert.Equal(1, _register.Writes);
    }

    /// <summary>
    /// A write whose outcome is unknown and which landed before the re-read is decided by it: its changes applied.
    /// </summary>
    [Fact]
    public async Task AnUnknownWriteWhichLandedBeforeTheReadIsDecidedByIt()
    {
        await using var committer = Committer();
        var held = _register.HoldNextWrite();
        var bootstrap = SubmitAsync(committer, Install([], [KeyRange.Root]));
        await held.Arrived;
        held.Fail(apply: true);
        Assert.Equal(new LedgerMapCommitOutcome.Applied(1), (await bootstrap).Value);
    }

    /// <summary>
    /// A write whose outcome is unknown and which never lands ends its batch with one read: the changes it carried fail
    /// with its exception, the changes it did not carry are superseded when the read shows their rule failed, or fail
    /// when the read decides nothing; and the fold's refusals and defects fail too, since a write at the version the read
    /// shows may still land, making room or applying the same drop after the close which makes it valid. No write
    /// follows.
    /// </summary>
    [Fact]
    public async Task AnUnknownWriteWhichNeverLandsFailsOnlyTheChangesItCarried()
    {
        var committer = Committer(Limit(2));
        await using (committer)
        {
            _ = await SubmitAsync(committer, Install([], [KeyRange.Root]));
            var held = _register.HoldNextWrite();
            var replace = Replace(1, chosenAt: 10);
            var blocker = SubmitAsync(committer, new LedgerMapChange.RaiseFloor(1));
            await held.Arrived;
            var batch = new[]
            {
                SubmitAsync(committer, Install([new(new LedgerId(1), Slot.Zero)], [KeyRange.Root.Children.Lower, KeyRange.Root.Children.Upper], chosenAt: 10)),
                SubmitAsync(committer, new LedgerMapChange.Drop(new LedgerId(1))),
                SubmitAsync(committer, replace),
                SubmitAsync(committer, Replace(1, chosenAt: 10)),
                SubmitAsync(committer, Replace(1, chosenAt: 0)),
            };
            var unknown = _register.HoldNextWrite();
            held.Answer();
            _ = await blocker;
            await unknown.Arrived;
            var writes = _register.Writes;
            unknown.Fail(apply: false);
            var answers = await Task.WhenAll(batch);
            _ = Assert.IsType<IOException>(answers[0].Failure);
            _ = Assert.IsType<IOException>(answers[1].Failure);
            _ = Assert.IsType<IOException>(answers[2].Failure);

            // Its rule failed only against the fold's result, so the read decides it no more than the write did.
            _ = Assert.IsType<IOException>(answers[3].Failure);
            Assert.Equal(new LedgerMapCommitOutcome.Superseded(2), answers[4].Value);
            Assert.Equal(writes, _register.Writes);

            // Submitted again, the change which failed applies.
            Assert.Equal(new LedgerMapCommitOutcome.Applied(3), (await SubmitAsync(committer, replace)).Value);
        }
    }

    /// <summary>
    /// A write whose outcome is unknown and which lands after the re-read fails the changes it carried; a lineage pair's
    /// drops among them stay unanswered rather than rejected, and once the write lands, submitting them again finds their
    /// goals.
    /// </summary>
    [Fact]
    public async Task AnUnknownWriteWhichLandsAfterTheReadLeavesItsChangesToTheirResubmission()
    {
        await using var committer = Committer();
        _ = await SubmitAsync(committer, Install([], [KeyRange.Root]));
        _ = await SubmitAsync(committer, Replace(1));

        // The drops queue behind the held replacement, so the next batch takes both into one write (#183).
        var blocker = _register.HoldNextWrite();
        var replace = SubmitAsync(committer, Replace(2));
        await blocker.Arrived;
        var drops = new[] { SubmitAsync(committer, new LedgerMapChange.Drop(new LedgerId(1))), SubmitAsync(committer, new LedgerMapChange.Drop(new LedgerId(2))) };
        var held = _register.HoldNextWrite();
        blocker.Answer();
        _ = await replace;
        await held.Arrived;
        var land = held.FailAndLandLater();
        Assert.All(await Task.WhenAll(drops), static answer => Assert.IsType<IOException>(answer.Failure));
        Assert.True(land());
        Assert.Equal(new LedgerMapCommitOutcome.Applied(4), (await SubmitAsync(committer, new LedgerMapChange.Drop(new LedgerId(1)))).Value);
        Assert.Equal(new LedgerMapCommitOutcome.Applied(4), (await SubmitAsync(committer, new LedgerMapChange.Drop(new LedgerId(2)))).Value);
    }

    /// <summary>
    /// An install which would exceed the value limit is refused; a drop frees room, after which it applies; and a floor
    /// raise into a full map applies.
    /// </summary>
    [Fact]
    public async Task TheValueLimitRefusesAnInstallUntilADropFreesRoom()
    {
        await using var committer = Committer(Limit(2));
        _ = await SubmitAsync(committer, Install([], [KeyRange.Root]));
        _ = await SubmitAsync(committer, Replace(1));
        var refused = Replace(2, chosenAt: 10);
        Assert.Equal(LedgerMapCommitOutcome.Refused.Instance, (await SubmitAsync(committer, refused)).Value);
        Assert.Equal(new LedgerMapCommitOutcome.Applied(3), (await SubmitAsync(committer, new LedgerMapChange.RaiseFloor(1))).Value);
        _ = await SubmitAsync(committer, new LedgerMapChange.Drop(new LedgerId(1)));

        Assert.Equal(new LedgerMapCommitOutcome.Applied(5), (await SubmitAsync(committer, refused)).Value);
    }

    /// <summary>
    /// Regression test, #260 (ledgers.md §2.6): a batch folds its changes in submission order, so an install the batch's
    /// earlier install leaves too large is refused, though the batch's later floor raise leaves the written value one where
    /// its rule fails, and no value the register holds shows it too large. Three changes queue behind a held write, so one
    /// batch takes them all; the oracle accepts the refusal at its own place in the batch's fold, and flags it when the
    /// install is placed before the change which leaves it too large.
    /// </summary>
    [Fact]
    public async Task AnInstallTheBatchsEarlierInstallLeavesTooLargeIsRefusedThoughALaterFloorRaiseFailsItsRule()
    {
        var limit = Limit(2);
        await using var committer = Committer(limit);
        var bootstrap = await SubmitAsync(committer, Install([], [KeyRange.Root]));
        var blocker = _register.HoldNextWrite();
        var floor = SubmitAsync(committer, new LedgerMapChange.RaiseFloor(1));
        await blocker.Arrived;
        var batch = new[]
        {
            SubmitAsync(committer, Replace(1, chosenAt: 1)),
            SubmitAsync(committer, Replace(2, chosenAt: 1)),
            SubmitAsync(committer, new LedgerMapChange.RaiseFloor(2)),
        };
        blocker.Answer();
        LedgerMapSubmission[] submitted = [bootstrap, await floor, .. await Task.WhenAll(batch)];

        Assert.Equal(new LedgerMapCommitOutcome.Applied(3), submitted[2].Value);
        Assert.Equal(LedgerMapCommitOutcome.Refused.Instance, submitted[3].Value);
        Assert.Equal(new LedgerMapCommitOutcome.Applied(3), submitted[4].Value);
        var (history, dispatches, writers) = History();
        Assert.All(history, value => Assert.NotEqual("too-large", LedgerMapReference.Decide(value, submitted[3].Change, limit).Kind));
        Assert.Empty(LedgerMapHistoryOracle.Violations(history, dispatches, writers, submitted, limit));

        // Submitted before the install which leaves it too large, the refused install's own place in the batch is the
        // write's basis, where it closes a ledger never installed: the oracle judges it there alone, and flags the refusal.
        LedgerMapSubmission[] refusedFirst = [submitted[0], submitted[1], submitted[3], submitted[2], submitted[4]];
        Assert.Contains(
            LedgerMapHistoryOracle.Violations(history, dispatches, writers, refusedFirst, limit),
            static violation => violation.Contains("was refused", StringComparison.Ordinal));
    }

    /// <summary>Each defective drop is rejected for its rule; the same drop submitted once its rule holds applies.</summary>
    [Fact]
    public async Task DefectiveDropsAreRejectedForTheirRule()
    {
        await using var committer = Committer();
        _ = await SubmitAsync(committer, Install([], [KeyRange.Root]));
        Assert.Equal(new LedgerMapCommitOutcome.Rejected(LedgerMapRejection.DropNotClosed), (await SubmitAsync(committer, new LedgerMapChange.Drop(new LedgerId(1)))).Value);
        Assert.Equal(new LedgerMapCommitOutcome.Rejected(LedgerMapRejection.DropNeverInstalled), (await SubmitAsync(committer, new LedgerMapChange.Drop(new LedgerId(2)))).Value);
        _ = await SubmitAsync(committer, Replace(1));
        _ = await SubmitAsync(committer, Replace(2));
        Assert.Equal(new LedgerMapCommitOutcome.Rejected(LedgerMapRejection.DropBlockedByLineage), (await SubmitAsync(committer, new LedgerMapChange.Drop(new LedgerId(2)))).Value);
        _ = await SubmitAsync(committer, new LedgerMapChange.Drop(new LedgerId(1)));
        Assert.Equal(new LedgerMapCommitOutcome.Applied(5), (await SubmitAsync(committer, new LedgerMapChange.Drop(new LedgerId(2)))).Value);
    }

    /// <summary>A caller's cancellation ends only its wait: its change stays in its batch and applies.</summary>
    [Fact]
    public async Task ACancelledCallersChangeStillApplies()
    {
        await using var committer = Committer();
        var held = _register.HoldNextWrite();
        using var cancellation = new CancellationTokenSource();
        var bootstrap = committer.CommitAsync(Install([], [KeyRange.Root]), cancellation.Token);
        await held.Arrived;
        await cancellation.CancelAsync();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => bootstrap);
        held.Answer();
        Assert.Equal(new LedgerMapCommitOutcome.Applied(1), (await SubmitAsync(committer, new LedgerMapChange.RaiseFloor(0))).Value);
        Assert.Single(History().History[1].Ledgers);
    }

    /// <summary>
    /// Disposal answers queued changes and the change whose write is in flight with <see cref="ObjectDisposedException"/>,
    /// and refuses later submissions.
    /// </summary>
    [Fact]
    public async Task DisposalAnswersQueuedAndInFlightChangesAndRefusesLaterOnes()
    {
        var committer = Committer();
        var held = _register.HoldNextWrite();
        var inFlight = SubmitAsync(committer, Install([], [KeyRange.Root]));
        await held.Arrived;
        var queued = SubmitAsync(committer, new LedgerMapChange.RaiseFloor(1));
        await committer.DisposeAsync();
        _ = Assert.IsType<ObjectDisposedException>((await inFlight).Failure);
        _ = Assert.IsType<ObjectDisposedException>((await queued).Failure);
        _ = await Assert.ThrowsAsync<ObjectDisposedException>(() => committer.CommitAsync(new LedgerMapChange.RaiseFloor(1), TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// A value which is not a ledger map fails the batch with the rejection naming its reason; a failed read fails the
    /// batch; and the next batch reads again.
    /// </summary>
    [Fact]
    public async Task AnUndecodableValueOrAFailedReadFailsTheBatchAndTheNextReadsAgain()
    {
        await using var committer = Committer();
        _register.Put([1, 2, 3]);
        var rejection = Assert.IsType<InvalidDataException>((await SubmitAsync(committer, new LedgerMapChange.RaiseFloor(1))).Failure);
        Assert.Equal(LedgerMapValue.ReasonTruncated, DecodeRejection.ReasonOf(rejection));
        _register.Put(LedgerMapSamples.Encoded(new LedgerMapValue(2, new LedgerId(1), 0, [])));
        _register.ReadFailure = new IOException("The scripted read failed.");
        _ = Assert.IsType<IOException>((await SubmitAsync(committer, new LedgerMapChange.RaiseFloor(1))).Failure);
        _register.ReadFailure = null;
        Assert.Equal(new LedgerMapCommitOutcome.Applied(3), (await SubmitAsync(committer, new LedgerMapChange.RaiseFloor(1))).Value);
        _submissions.Clear();
    }

    /// <summary>
    /// An install past the last ledger ID fails alone with <see cref="OverflowException"/>, and a value the committer would
    /// write which breaks an invariant fails its batch with <see cref="InvalidOperationException"/> and is not written.
    /// </summary>
    [Fact]
    public async Task AnOverflowFailsItsChangeAndABrokenInvariantFailsTheBatch()
    {
        await using var committer = Committer();
        _register.Put(LedgerMapSamples.Encoded(new LedgerMapValue(1, new LedgerId(ulong.MaxValue), 0, [])));
        _ = Assert.IsType<OverflowException>((await SubmitAsync(committer, Install([], [KeyRange.Root]))).Failure);

        // A register which lists two open ledgers over one range breaks LG-I1, which no write may carry forward.
        var shape = (ImmutableArray.Create(LedgerMapSamples.Node(1)), 1, Opening(KeyRange.Root).Primary);
        _register.Put(LedgerMapSamples.Encoded(new LedgerMapValue(2, new LedgerId(3), 0,
            [LedgerMapSamples.Ledger(1, KeyRange.Root, shape, LedgerStatus.Open.Instance), LedgerMapSamples.Ledger(2, KeyRange.Root, shape, LedgerStatus.Open.Instance)])));
        var applied = _register.AppliedWrites.Count;

        // A committer which knows the value from before the change would first write and meet a mismatch, after which a
        // defect leaves the change's outcome unknown; one which reads first writes nothing for it.
        await using var reading = Committer();
        var broken = await SubmitAsync(reading, new LedgerMapChange.RaiseFloor(1));
        Assert.StartsWith("The ledger map committer would write epoch 3, which breaks LG-I1", Assert.IsType<InvalidOperationException>(broken.Failure).Message, StringComparison.Ordinal);
        Assert.Equal(applied, _register.AppliedWrites.Count);
        _submissions.Clear();
    }

    /// <summary>The oracle rejects the answers of a committer which answers from a cached value, or before its write lands.</summary>
    [Fact]
    public void TheOracleRejectsAnswersFromAStaleCacheOrAWriteWhichNeverLanded()
    {
        var bootstrap = Install([], [KeyRange.Root]);
        var root = LedgerMapRulesTests.Applies(LedgerMapValue.Empty, bootstrap);
        LedgerMapValue[] history = [LedgerMapValue.Empty, new(1, root.NextLedgerId, 0, root.Ledgers), new(2, root.NextLedgerId, 7, root.Ledgers)];
        long[] dispatches = [-1, 0, 1];
        int[] writers = [-1, 0, 0];
        Assert.Empty(LedgerMapHistoryOracle.Violations(history, dispatches, writers, [new(bootstrap, 0, 0, new LedgerMapCommitOutcome.Applied(1)), new(bootstrap, 0, 2, new LedgerMapCommitOutcome.Applied(2))]));
        Assert.NotEmpty(LedgerMapHistoryOracle.Violations(history, dispatches, writers, [new(new LedgerMapChange.RaiseFloor(7), 0, 2, new LedgerMapCommitOutcome.Applied(1))]));
        Assert.NotEmpty(LedgerMapHistoryOracle.Violations(history, dispatches, writers, [new(Install([], [KeyRange.Root]), 0, 2, new LedgerMapCommitOutcome.Applied(3))]));
        Assert.NotEmpty(LedgerMapHistoryOracle.Violations(history, dispatches, writers, [new(Install([], [KeyRange.Root]), 0, 0, new LedgerMapCommitOutcome.Applied(1))]));
        Assert.NotEmpty(LedgerMapHistoryOracle.Violations(history, dispatches, writers, [new(bootstrap, 0, 0, new LedgerMapCommitOutcome.Superseded(1))]));
    }

    /// <summary>
    /// The oracle rejects a negative answer which a write the answering committer dispatched before it revokes, and
    /// accepts one which only a write dispatched after it, such as a resubmission's, or another committer's write
    /// overtakes. A rejection holds when a value since the submission shows the change a defect for its rule or one
    /// before it in its chain, since the fold decides it against a later value which the register never held.
    /// </summary>
    [Fact]
    public void TheOracleRejectsANegativeAnswerWhichAnEarlierWriteRevokes()
    {
        var bootstrap = Install([], [KeyRange.Root]);
        var root = LedgerMapRulesTests.Applies(LedgerMapValue.Empty, bootstrap);
        LedgerMapValue[] history = [LedgerMapValue.Empty, new(1, root.NextLedgerId, 0, root.Ledgers)];
        Assert.NotEmpty(LedgerMapHistoryOracle.Violations(history, [-1, 3], [-1, 0], [new(bootstrap, 0, 0, LedgerMapCommitOutcome.Refused.Instance, AnsweredAt: 4)]));
        Assert.Empty(LedgerMapHistoryOracle.Violations(history, [-1, 5], [-1, 0], [new(bootstrap, 0, 0, LedgerMapCommitOutcome.Refused.Instance, AnsweredAt: 4)]));
        Assert.Empty(LedgerMapHistoryOracle.Violations(history, [-1, 3], [-1, 1], [new(bootstrap, 0, 0, LedgerMapCommitOutcome.Refused.Instance, AnsweredAt: 4)]));
        var drop = new LedgerMapChange.Drop(new LedgerId(1));
        LedgerMapValue[] dropped = [new(1, new LedgerId(2), 0, [root.Ledgers[0].Close(Slot.Zero)]), new(2, new LedgerId(2), 0, [])];
        Assert.NotEmpty(LedgerMapHistoryOracle.Violations([LedgerMapValue.Empty, .. dropped], [-1, 0, 1], [-1, 0, 0], [new(drop, 0, 1, new LedgerMapCommitOutcome.Rejected(LedgerMapRejection.DropNotClosed), AnsweredAt: 2)]));
        Assert.Empty(LedgerMapHistoryOracle.Violations(history, [-1, 0], [-1, 0], [new(drop, 0, 1, new LedgerMapCommitOutcome.Rejected(LedgerMapRejection.DropBlockedByLineage), AnsweredAt: 1)]));
        Assert.NotEmpty(LedgerMapHistoryOracle.Violations(history, [-1, 0], [-1, 0], [new(drop, 0, 1, new LedgerMapCommitOutcome.Rejected(LedgerMapRejection.DropNeverInstalled), AnsweredAt: 1)]));
        Assert.NotEmpty(LedgerMapHistoryOracle.Violations(history, [-1, 0], [-1, 0], [new(bootstrap, 0, 1, new LedgerMapCommitOutcome.Rejected(LedgerMapRejection.OpensLeaveGap), AnsweredAt: 1)]));
        var closesUnknown = Install([new(new LedgerId(5), Slot.Zero)], [KeyRange.Root]);
        Assert.Empty(LedgerMapHistoryOracle.Violations(history, [-1, 0], [-1, 0], [new(closesUnknown, 0, 1, new LedgerMapCommitOutcome.Rejected(LedgerMapRejection.OpensLeaveGap), AnsweredAt: 1)]));
    }

    /// <summary>
    /// A refusal is not answered while a held write may still land, since a drop it carries can make room: the held
    /// write's batch drops one ledger to replace another within the limit, its outcome is unknown and the read shows the
    /// register unchanged, and the replacement, submitted again, would not fit beside the ledger the drop removes. The
    /// committer fences the register before answering it <c>Refused</c>, after which the held write can never land.
    /// </summary>
    [Fact]
    public async Task ARefusalWaitsForAFenceWhileAHeldWriteMayStillLand()
    {
        await using var committer = Committer(Limit(2));
        _ = await SubmitAsync(committer, Install([], [KeyRange.Root]));
        _ = await SubmitAsync(committer, Replace(1, chosenAt: 10));
        var blocker = _register.HoldNextWrite();
        var raise = SubmitAsync(committer, new LedgerMapChange.RaiseFloor(1));
        await blocker.Arrived;
        var replace = Replace(2, chosenAt: 10);
        var batch = new[] { SubmitAsync(committer, new LedgerMapChange.Drop(new LedgerId(1))), SubmitAsync(committer, replace) };
        var held = _register.HoldNextWrite();
        blocker.Answer();
        _ = await raise;
        await held.Arrived;
        var land = held.FailAndLandLater();
        Assert.All(await Task.WhenAll(batch), static answer => Assert.IsType<IOException>(answer.Failure));

        var writes = _register.Writes;
        Assert.Equal(LedgerMapCommitOutcome.Refused.Instance, (await SubmitAsync(committer, replace)).Value);
        Assert.Equal(writes + 1, _register.Writes);
        Assert.False(land(), "The held write landed after its refusal was answered.");
        var (history, _, _) = History();
        Assert.Equal(history[^2].Ledgers, history[^1].Ledgers);
    }

    /// <summary>
    /// A rejection is not answered while a held write may still land, since the held write may carry the same drop after
    /// the close which makes it valid: the held write's batch closes a ledger and drops it, its outcome is unknown and the
    /// read shows the ledger still open, and the drop, submitted again, breaks its rule against that value. While the
    /// fence's own outcome is unknown, the drop stays unanswered; once a fence applies, the committer answers it
    /// <c>Rejected</c>, and the held write can never land (#187).
    /// </summary>
    [Fact]
    public async Task ARejectionWaitsForAFenceWhileAHeldWriteMayStillLand()
    {
        await using var committer = Committer();
        _ = await SubmitAsync(committer, Install([], [KeyRange.Root]));
        var blocker = _register.HoldNextWrite();
        var raise = SubmitAsync(committer, new LedgerMapChange.RaiseFloor(1));
        await blocker.Arrived;
        var drop = new LedgerMapChange.Drop(new LedgerId(1));
        var batch = new[] { SubmitAsync(committer, Replace(1, chosenAt: 10)), SubmitAsync(committer, drop) };
        var held = _register.HoldNextWrite();
        blocker.Answer();
        _ = await raise;
        await held.Arrived;
        var land = held.FailAndLandLater();
        Assert.All(await Task.WhenAll(batch), static answer => Assert.IsType<IOException>(answer.Failure));

        var fence = _register.HoldNextWrite();
        var unfenced = SubmitAsync(committer, drop);

        // Without the fence, the drop is answered at once and no write arrives.
        if (await Task.WhenAny(fence.Arrived, unfenced) == unfenced)
        {
            Assert.Fail($"The drop was answered {(await unfenced).Value} before a fence.");
        }

        fence.Fail(apply: false);
        _ = Assert.IsType<IOException>((await unfenced).Failure);

        var writes = _register.Writes;
        Assert.Equal(new LedgerMapCommitOutcome.Rejected(LedgerMapRejection.DropNotClosed), (await SubmitAsync(committer, drop)).Value);
        Assert.Equal(writes + 1, _register.Writes);
        Assert.False(land(), "The held write landed after its drop was rejected.");
    }

    /// <summary>
    /// Each fence mutant answers what a held write can revoke, and the history oracle flags it: the refusal schedule of
    /// <see cref="ARefusalWaitsForAFenceWhileAHeldWriteMayStillLand"/>, in which <c>NoFence</c> refuses the replacement
    /// without a fence, and <c>AnswerWhileHeld</c> refuses it from the read after its fence's unknown outcome; either way
    /// the held write then lands, carrying the replacement.
    /// </summary>
    [Theory]
    [InlineData(nameof(LedgerMapCommitter.FenceMutant.NoFence))]
    [InlineData(nameof(LedgerMapCommitter.FenceMutant.AnswerWhileHeld))]
    public async Task TheOracleFlagsEachFenceMutant(string mutant)
    {
        await using var committer = Committer(Limit(2));
        committer.Mutant = Enum.Parse<LedgerMapCommitter.FenceMutant>(mutant);
        _ = await SubmitAsync(committer, Install([], [KeyRange.Root]));
        _ = await SubmitAsync(committer, Replace(1, chosenAt: 10));
        var blocker = _register.HoldNextWrite();
        var raise = SubmitAsync(committer, new LedgerMapChange.RaiseFloor(1));
        await blocker.Arrived;
        var replace = Replace(2, chosenAt: 10);
        var batch = new[] { SubmitAsync(committer, new LedgerMapChange.Drop(new LedgerId(1))), SubmitAsync(committer, replace) };
        var held = _register.HoldNextWrite();
        blocker.Answer();
        _ = await raise;
        await held.Arrived;
        var land = held.FailAndLandLater();
        _ = await Task.WhenAll(batch);

        var fence = _register.HoldNextWrite();
        var refusal = SubmitAsync(committer, replace);
        if (await Task.WhenAny(fence.Arrived, refusal) != refusal)
        {
            fence.Fail(apply: false);
        }

        Assert.Equal(LedgerMapCommitOutcome.Refused.Instance, (await refusal).Value);
        Assert.True(land(), "The held write could not land after the mutant's refusal.");
        var (history, dispatches, writers) = History();
        Assert.NotEmpty(LedgerMapHistoryOracle.Violations(history, dispatches, writers, _submissions));

        // The oracle flagged the mutant's answer; the teardown judges no more.
        _submissions.Clear();
    }

    /// <summary>
    /// A refusal from a write whose outcome is unknown is decided against the value read, not the value the fold
    /// refused it against: one held write drops a ledger, the next batch's write refuses a replacement against the full
    /// map and its outcome is unknown too, the first write lands and the second never can. The read shows room for the
    /// replacement, which therefore fails as unknown rather than refused, and applies when submitted again.
    /// </summary>
    [Fact]
    public async Task ARefusalAfterAnUnknownOutcomeIsDecidedAgainstTheValueRead()
    {
        await using var committer = Committer(Limit(2));
        _ = await SubmitAsync(committer, Install([], [KeyRange.Root]));
        _ = await SubmitAsync(committer, Replace(1));
        var first = _register.HoldNextWrite();
        var drop = SubmitAsync(committer, new LedgerMapChange.Drop(new LedgerId(1)));
        await first.Arrived;
        var replace = Replace(2, chosenAt: 10);
        var batch = new[] { SubmitAsync(committer, replace), SubmitAsync(committer, new LedgerMapChange.RaiseFloor(1)) };
        var second = _register.HoldNextWrite();
        var land = first.FailAndLandLater();
        _ = Assert.IsType<IOException>((await drop).Failure);
        await second.Arrived;
        Assert.True(land());
        second.Fail(apply: false);
        Assert.All(await Task.WhenAll(batch), static answer => Assert.IsType<IOException>(answer.Failure));

        Assert.Equal(new LedgerMapCommitOutcome.Applied(4), (await SubmitAsync(committer, replace)).Value);
    }

    /// <summary>
    /// A failed read after a write whose outcome is unknown fails every change of the batch with the write's exception,
    /// and the next batch reads afresh: the change, submitted again, finds the write applied.
    /// </summary>
    [Fact]
    public async Task AFailedReadAfterAnUnknownOutcomeFailsTheBatchWithTheWritesException()
    {
        await using var committer = Committer();
        var blocker = _register.HoldNextWrite();
        var bootstrap = SubmitAsync(committer, Install([], [KeyRange.Root]));
        await blocker.Arrived;
        var batch = new[] { SubmitAsync(committer, new LedgerMapChange.RaiseFloor(1)), SubmitAsync(committer, new LedgerMapChange.RaiseFloor(2)) };
        var held = _register.HoldNextWrite();
        blocker.Answer();
        _ = await bootstrap;
        await held.Arrived;
        _register.ReadFailure = new TimeoutException("The scripted read failed.");
        held.Fail(apply: true);
        Assert.All(await Task.WhenAll(batch), static answer => Assert.IsType<IOException>(answer.Failure));

        _register.ReadFailure = null;
        var reads = _register.Reads;
        Assert.Equal(new LedgerMapCommitOutcome.Applied(2), (await SubmitAsync(committer, new LedgerMapChange.RaiseFloor(2))).Value);
        Assert.Equal(reads + 1, _register.Reads);
    }

    /// <summary>
    /// A read after an unknown outcome which finds the register holding a value which is not a ledger map leaves the change
    /// the write carried unknown, the rejection inside, since the write may have landed first; it is never answered as a
    /// change which never applies (conventions.md §6). The batch ends with that one read, and writes nothing more.
    /// </summary>
    [Fact]
    public async Task ACorruptRegisterAfterAnUnknownOutcomeLeavesTheCarriedChangeUnknown()
    {
        await using var committer = Committer();
        var held = _register.HoldNextWrite();
        var bootstrap = SubmitAsync(committer, Install([], [KeyRange.Root]));
        await held.Arrived;
        _register.Put([1, 2, 3]);
        var (reads, writes) = (_register.Reads, _register.Writes);
        held.Fail(apply: false);

        var unknown = Assert.IsType<GroupCommitOutcomeUnknownException>((await bootstrap).Failure);
        Assert.Equal(LedgerMapValue.ReasonTruncated, DecodeRejection.ReasonOf(Assert.IsType<InvalidDataException>(unknown.InnerException)));
        Assert.Equal((reads + 1, writes), (_register.Reads, _register.Writes));

        // The register holds no ledger map, which the history oracle cannot judge.
        _submissions.Clear();
    }

    /// <summary>
    /// A committer given a view raises it with each value its writes land, before the commit answers, and with each value
    /// it reads, so a controller chooses from the map it committed (orleans.md §5.6).
    /// </summary>
    [Fact]
    public async Task ACommitterRaisesItsViewWithEachValueItWritesOrReads()
    {
        var view = await LedgerMapView.OpenAsync(_register.Writer(2), TestContext.Current.CancellationToken);
        await using var committer = Committer(view: view);
        await using var other = Committer(writer: 1);
        Assert.Equal(new LedgerMapCommitOutcome.Applied(1), (await SubmitAsync(committer, Install([], [KeyRange.Root]))).Value);
        Assert.Equal(1UL, view.Current.Epoch);

        Assert.Equal(new LedgerMapCommitOutcome.Applied(2), (await SubmitAsync(other, Replace(1))).Value);
        Assert.Equal(1UL, view.Current.Epoch);
        Assert.Equal(new LedgerMapCommitOutcome.Superseded(2), (await SubmitAsync(committer, Replace(1))).Value);
        Assert.Equal(2UL, view.Current.Epoch);
    }

    private static LedgerMapChange Replace(ulong id, ulong chosenAt = 0) => Install([new(new LedgerId(id), new Slot((long)id))], [KeyRange.Root], chosenAt);

    /// <summary>Returns the length of a map listing <paramref name="ledgers"/> ledgers of <c>N = 3</c> over the whole keyspace, one closed.</summary>
    private static int Limit(int ledgers) => LedgerMapValue.HeaderLength + LedgerMapValue.CrcLength + ledgers * 98 + 8;

    /// <summary>Creates a committer whose writes the register records as <paramref name="writer"/>'s, raising <paramref name="view"/> if given.</summary>
    private LedgerMapCommitter Committer(int maxValueLength = int.MaxValue, int writer = 0, LedgerMapView? view = null)
    {
        var committer = new LedgerMapCommitter(_register.Writer(writer), maxValueLength, view);
        _writers.Add(committer, writer);
        return committer;
    }

    /// <summary>
    /// Returns the register's values, from the absent register's, with the dispatch order and the writer of the write
    /// which produced each.
    /// </summary>
    private (List<LedgerMapValue> History, List<long> Dispatches, List<int> Writers) History()
    {
        List<LedgerMapValue> history = [LedgerMapValue.Empty];
        List<long> dispatches = [-1];
        List<int> writers = [-1];
        var applied = _register.AppliedWrites;
        var order = _register.AppliedDispatches;
        var writtenBy = _register.AppliedWriters;
        for (var index = 0; index < applied.Count; index++)
        {
            if (LedgerMapValue.Decode(applied[index].AsSpan()).Value is LedgerMapValue value)
            {
                history.Add(value);
                dispatches.Add(order[index]);
                writers.Add(writtenBy[index]);
            }
        }

        return (history, dispatches, writers);
    }

    /// <summary>Submits a change, recording the register's epoch at submission and the answer, for the oracle.</summary>
    private async Task<LedgerMapSubmission> SubmitAsync(LedgerMapCommitter committer, LedgerMapChange change)
    {
        var submittedAt = (ulong)_register.AppliedWrites.Count;
        LedgerMapSubmission submission;
        try
        {
            var outcome = await committer.CommitAsync(change, TestContext.Current.CancellationToken);
            submission = new(change, _writers[committer], submittedAt, outcome, AnsweredAt: _register.Dispatches);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            submission = new(change, _writers[committer], submittedAt, null, exception, _register.Dispatches);
        }

        lock (_submissions)
        {
            _submissions.Add(submission);
        }

        return submission;
    }
}
