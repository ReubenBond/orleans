using Microsoft.Extensions.Time.Testing;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.GroupCommit.Tests;

/// <summary>
/// The group committer's policy (conventions.md §6), over fake rules and a scripted store: group commit and merging, the
/// answers from values after each submission, unknown outcomes, mismatches, corruption, defects by phase, the fence and
/// its guard, disposal at each await, the execution context, and the write interval.
/// </summary>
public sealed class GroupCommitterTests : IAsyncDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();
    private readonly FakeStore _store = new();
    private readonly FakeRules _rules;
    private readonly GroupCommitter<FakeValue, FakeVersion, string, string, GroupCommitWrite<FakeVersion>> _committer;

    public GroupCommitterTests()
    {
        _rules = new FakeRules(_store);
        _committer = new(_rules, "FakeCommitter");
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        await _committer.DisposeAsync();
        _leaks.Dispose();
    }

    /// <summary>Changes submitted while a write is in flight share the next write, and an identical change is folded once.</summary>
    [Fact]
    public async Task ConcurrentChangesShareOneWriteAndIdenticalOnesAreFoldedOnce()
    {
        var held = _store.HoldNextWrite();
        var first = _committer.CommitAsync("a", Token);
        await held.Arrived;
        Task<string>[] batch = [_committer.CommitAsync("b", Token), _committer.CommitAsync("c", Token), _committer.CommitAsync("b", Token)];
        held.Decide(FakeStore.WriteDecision.Answer);

        Assert.Equal("applied@1", await first);
        Assert.Equal(["applied@2", "applied@2", "applied@2"], await Task.WhenAll(batch));
        Assert.Equal(2, _store.Writes);
        Assert.Equal(["b", "c"], _rules.Folds[^1].Changes);
    }

    /// <summary>
    /// A change which joins an identical pending submission is answered only from a value from after it joined: here the
    /// value a write dispatched between the two submissions wrote holds its goal, and the committer reads before answering.
    /// </summary>
    [Fact]
    public async Task AJoinerIsAnsweredFromAValueFromAfterItJoined()
    {
        var read = _store.HoldNextRead();
        var write = _store.HoldNextWrite();
        var first = _committer.CommitAsync("a", Token);
        await WaitUntilAsync(() => _store.Reads == 1);
        var early = _committer.CommitAsync("a", Token);
        read.SetResult();
        await write.Arrived;
        var joiner = _committer.CommitAsync("a", Token);
        var reads = _store.Reads;
        write.Decide(FakeStore.WriteDecision.Answer);

        Assert.Equal("applied@1", await first);
        Assert.Equal(["applied@1", "applied@1"], await Task.WhenAll(early, joiner));
        Assert.Equal(reads + 1, _store.Reads);
        Assert.Equal(1, _store.Writes);
    }

    /// <summary>An exception of the fold is a defect before any write: no write carried the change, and the next batch reads first.</summary>
    [Fact]
    public async Task AnExceptionOfTheFoldFailsTheBatchBeforeAnyWrite()
    {
        _rules.Fails = static member => member == nameof(FakeRules.Fold);
        var defect = await Assert.ThrowsAsync<InvalidOperationException>(() => _committer.CommitAsync("a", Token));
        _ = Assert.IsType<FormatException>(defect.InnerException);
        Assert.Equal(0, _store.Writes);

        _rules.Fails = null;
        var reads = _store.Reads;
        Assert.Equal("applied@1", await _committer.CommitAsync("a", Token));
        Assert.Equal(reads + 1, _store.Reads);
    }

    /// <summary>A value which breaks the rules' invariants, or does not decode as itself, is never written.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AValueWhichBreaksTheRulesOrDoesNotDecodeIsNeverWritten(bool undecodable)
    {
        (_rules.Violation, _rules.Undecodable) = undecodable ? (null, true) : ("The fake write breaks FAKE-I1.", false);

        var defect = await Assert.ThrowsAsync<InvalidOperationException>(() => _committer.CommitAsync("a", Token));

        Assert.Contains(undecodable ? "does not decode as itself" : "FAKE-I1", defect.Message, StringComparison.Ordinal);
        Assert.Equal(0, _store.Writes);
    }

    /// <summary>
    /// A defect after the batch's write applied, in an answer or in observing the write, leaves the carried change's outcome
    /// unknown: the write landed, so the committer never says nothing was written.
    /// </summary>
    [Theory]
    [InlineData(nameof(IGroupCommitBatch<,,>.Settle))]
    [InlineData(nameof(IGroupCommitBatch<,,>.OnApplied))]
    [InlineData(nameof(IGroupCommitRules<,,,,>.WriteOutcome))]
    public async Task ADefectAfterAnAppliedWriteLeavesTheCarriedChangeUnknown(string member)
    {
        _rules.Fails = failing => failing == member;

        var unknown = await Assert.ThrowsAsync<GroupCommitOutcomeUnknownException>(() => _committer.CommitAsync("a", Token));

        _ = Assert.IsType<FormatException>(Assert.IsType<InvalidOperationException>(unknown.InnerException).InnerException);
        Assert.Equal(1, _store.Applied);
    }

    /// <summary>A defect in the answers after an unknown outcome leaves the change the write carried unknown.</summary>
    [Fact]
    public async Task ADefectAfterAnUnknownOutcomeLeavesTheCarriedChangeUnknown()
    {
        _rules.Fails = static member => member == nameof(IGroupCommitBatch<,,>.Applied);
        var held = _store.HoldNextWrite();
        var change = _committer.CommitAsync("a", Token);
        await held.Arrived;
        held.Decide(FakeStore.WriteDecision.FailApplied);

        _ = await Assert.ThrowsAsync<GroupCommitOutcomeUnknownException>(() => change);
    }

    /// <summary>
    /// A defect after a mismatch, deciding the batch again, leaves the changes the mismatched write carried unknown: a
    /// backend retry may have applied an earlier attempt of it.
    /// </summary>
    [Fact]
    public async Task ADefectAfterAMismatchLeavesTheMismatchedWritesChangesUnknown()
    {
        _store.AppliedMismatches = 1;
        _rules.Fails = member => member == nameof(FakeRules.Fold) && _rules.Folds.Count > 1;

        _ = await Assert.ThrowsAsync<GroupCommitOutcomeUnknownException>(() => _committer.CommitAsync("a", Token));
        Assert.Equal(1, _store.Writes);
    }

    /// <summary>
    /// After a write whose outcome is unknown, the read which ends the batch answers a change the write carried applied
    /// when it shows its goal, and fails it with the write's exception otherwise.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnUnknownOutcomeIsAnsweredFromOneRead(bool landed)
    {
        var held = _store.HoldNextWrite();
        var change = _committer.CommitAsync("a", Token);
        await held.Arrived;
        var reads = _store.Reads;
        held.Decide(landed ? FakeStore.WriteDecision.FailApplied : FakeStore.WriteDecision.Fail);

        if (landed)
        {
            Assert.Equal("applied@1", await change);
        }
        else
        {
            _ = await Assert.ThrowsAsync<IOException>(() => change);
        }

        Assert.Equal(reads + 1, _store.Reads);
        Assert.Equal(1, _store.Writes);
    }

    /// <summary>
    /// A read after an unknown outcome which finds the value corrupt fails a change the write carried with an unknown
    /// outcome around the corruption, since the write may have landed, and a change no write carried with the corruption.
    /// </summary>
    [Fact]
    public async Task ACorruptReadAfterAnUnknownOutcomeLeavesCarriedChangesUnknown()
    {
        var first = _store.HoldNextWrite();
        var setup = _committer.CommitAsync("x", Token);
        await first.Arrived;
        Task<string>[] batch = [_committer.CommitAsync("a", Token), _committer.CommitAsync("refuse:r", Token)];
        var second = _store.HoldNextWrite();
        first.Decide(FakeStore.WriteDecision.Answer);
        Assert.Equal("applied@1", await setup);
        await second.Arrived;
        _store.Corrupt = true;
        second.Decide(FakeStore.WriteDecision.FailApplied);

        var unknown = await Assert.ThrowsAsync<GroupCommitOutcomeUnknownException>(() => batch[0]);
        _ = Assert.IsType<InvalidDataException>(unknown.InnerException);
        _ = await Assert.ThrowsAsync<InvalidDataException>(() => batch[1]);
    }

    /// <summary>The read after a mismatch which finds the value corrupt leaves the changes the mismatched write carried unknown.</summary>
    [Fact]
    public async Task ACorruptReadAfterAMismatchLeavesCarriedChangesUnknown()
    {
        var held = _store.HoldNextWrite();
        var change = _committer.CommitAsync("a", Token);
        await held.Arrived;
        _store.Corrupt = true;
        held.Decide(FakeStore.WriteDecision.Mismatch);

        var unknown = await Assert.ThrowsAsync<GroupCommitOutcomeUnknownException>(() => change);
        _ = Assert.IsType<InvalidDataException>(unknown.InnerException);
    }

    /// <summary>
    /// A read after a mismatch which fails, as a register read meeting a malformed entry throws, leaves the changes the
    /// mismatched write carried unknown, the read's failure inside, since that write may have landed: never answered as
    /// nothing written (conventions.md §6).
    /// </summary>
    [Fact]
    public async Task AFailedReadAfterAMismatchLeavesCarriedChangesUnknown()
    {
        var held = _store.HoldNextWrite();
        var change = _committer.CommitAsync("a", Token);
        await held.Arrived;
        _store.ReadFailure = new InvalidDataException("The register's entry is malformed.");
        held.Decide(FakeStore.WriteDecision.Mismatch);

        var unknown = await Assert.ThrowsAsync<GroupCommitOutcomeUnknownException>(() => change);
        _ = Assert.IsType<InvalidDataException>(unknown.InnerException);

        _store.ReadFailure = null;
        Assert.Equal("applied@1", await _committer.CommitAsync("b", Token));
    }

    /// <summary>
    /// A write the store refuses wrote nothing: its batch fails with the refusal, no read follows, and the next batch is
    /// decided against the value the refused write was.
    /// </summary>
    [Fact]
    public async Task ARefusedWriteFailsTheBatchWithTheRefusalAndNoReadFollows()
    {
        var held = _store.HoldNextWrite();
        var change = _committer.CommitAsync("a", Token);
        await held.Arrived;
        held.Decide(FakeStore.WriteDecision.Refuse);

        _ = await Assert.ThrowsAsync<FakeRefusalException>(() => change);
        Assert.Equal(1, _store.Reads);
        Assert.Equal("applied@1", await _committer.CommitAsync("b", Token));
        Assert.Equal(1, _store.Reads);
    }

    /// <summary>
    /// A refusal asserts only that its own attempt wrote nothing: a change an earlier, mismatched attempt of the batch
    /// carried is answered as an unknown outcome around the refusal.
    /// </summary>
    [Fact]
    public async Task ARefusalAfterAMismatchLeavesEarlierCarriedChangesUnknown()
    {
        var first = _store.HoldNextWrite();
        var change = _committer.CommitAsync("a", Token);
        await first.Arrived;
        var retry = _store.HoldNextWrite();
        first.Decide(FakeStore.WriteDecision.Mismatch);
        await retry.Arrived;
        retry.Decide(FakeStore.WriteDecision.Refuse);

        var unknown = await Assert.ThrowsAsync<GroupCommitOutcomeUnknownException>(() => change);
        _ = Assert.IsType<FakeRefusalException>(unknown.InnerException);
    }

    /// <summary>A corrupt value met before any write fails the batch with the corruption, and the next batch reads again.</summary>
    [Fact]
    public async Task ACorruptValueBeforeAnyWriteFailsTheBatchWithTheCorruption()
    {
        _store.Corrupt = true;
        _ = await Assert.ThrowsAsync<InvalidDataException>(() => _committer.CommitAsync("a", Token));

        _store.Corrupt = false;
        Assert.Equal("applied@1", await _committer.CommitAsync("a", Token));
        Assert.Equal(2, _store.Reads);
    }

    /// <summary>
    /// While a write whose outcome is unknown may still land, the fold sees it, and its fence decides the batch again;
    /// once the store moves past the held write's version, the fold sees it never lands.
    /// </summary>
    [Fact]
    public async Task TheFoldLearnsWhetherAHeldWriteMayLand()
    {
        var lost = _store.HoldNextWrite();
        var first = _committer.CommitAsync("a", Token);
        await lost.Arrived;
        lost.Decide(FakeStore.WriteDecision.Fail);
        _ = await Assert.ThrowsAsync<IOException>(() => first);

        Assert.Equal("refused", await _committer.CommitAsync("refuse:r", Token));
        Assert.Equal(1, _store.Applied);

        var landed = _store.HoldNextWrite();
        var second = _committer.CommitAsync("b", Token);
        await landed.Arrived;
        landed.Decide(FakeStore.WriteDecision.FailApplied);
        Assert.Equal("applied@2", await second);
        Assert.Equal("applied@3", await _committer.CommitAsync("c", Token));

        Assert.Equal([false, true, true, false, false, false], _rules.Folds.Select(static fold => fold.HeldWriteMayLand));
    }

    /// <summary>A fold which writes without carrying a change while no held write may land is a defect, and writes nothing.</summary>
    [Fact]
    public async Task AWriteWhichCarriesNothingWithoutAHeldWriteIsADefect()
    {
        Assert.Equal("applied@1", await _committer.CommitAsync("a", Token));
        _rules.AlwaysFence = true;

        var defect = await Assert.ThrowsAsync<InvalidOperationException>(() => _committer.CommitAsync("a", Token));

        Assert.Contains("fence", defect.Message, StringComparison.Ordinal);
        Assert.Equal(1, _store.Writes);
    }

    /// <summary>An answer which holds no case fails the batch as a defect, and the committer serves the next one.</summary>
    [Fact]
    public async Task AnAnswerWhichHoldsNoCaseIsADefectAndTheCommitterGoesOn()
    {
        _rules.Malformed = true;
        _ = await Assert.ThrowsAsync<GroupCommitOutcomeUnknownException>(() => _committer.CommitAsync("a", Token));

        _rules.Malformed = false;
        Assert.Equal("applied@2", await _committer.CommitAsync("b", Token));
    }

    /// <summary>
    /// An answer which fails without an exception is a defect, as one which holds no case is: the change, which the applied
    /// write carried, is unknown, and the committer serves the next batch rather than hanging it.
    /// </summary>
    [Fact]
    public async Task AFailureWithoutItsExceptionIsADefectAndTheCommitterGoesOn()
    {
        _rules.FailureWithoutException = true;
        _ = await Assert.ThrowsAsync<GroupCommitOutcomeUnknownException>(() => _committer.CommitAsync("a", Token));

        _rules.FailureWithoutException = false;
        Assert.Equal("applied@2", await _committer.CommitAsync("b", Token));
    }

    /// <summary>
    /// The rules learn of an applied write before any submission of its batch is answered, so a value its notice publishes
    /// precedes every answer the write gives.
    /// </summary>
    [Fact]
    public async Task OnAppliedRunsBeforeAnySubmissionIsReleased()
    {
        var held = _store.HoldNextWrite();
        var first = _committer.CommitAsync("a", Token);
        await held.Arrived;
        var second = _committer.CommitAsync("b", Token);
        var released = new List<bool>();
        _rules.OnAppliedProbe = () => released.Add((released.Count == 0 ? first : second).IsCompleted);
        held.Decide(FakeStore.WriteDecision.Answer);

        Assert.Equal(("applied@1", "applied@2"), (await first, await second));
        Assert.Equal([false, false], released);
    }

    /// <summary>
    /// Disposal during a read or a write answers every admitted change <see cref="ObjectDisposedException"/>, ends the
    /// batch loop, and refuses later changes.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DisposalDuringAReadOrAWriteAnswersEveryAdmittedChange(bool duringRead)
    {
        var committer = new GroupCommitter<FakeValue, FakeVersion, string, string, GroupCommitWrite<FakeVersion>>(_rules, "FakeCommitter");
        Task<string> change;
        if (duringRead)
        {
            _ = _store.HoldNextRead();
            change = committer.CommitAsync("a", Token);
            await WaitUntilAsync(() => _store.Reads == 1);
        }
        else
        {
            var held = _store.HoldNextWrite();
            change = committer.CommitAsync("a", Token);
            await held.Arrived;
        }

        var queued = committer.CommitAsync("b", Token);
        await committer.DisposeAsync();

        _ = await Assert.ThrowsAsync<ObjectDisposedException>(() => change);
        _ = await Assert.ThrowsAsync<ObjectDisposedException>(() => queued);
        _ = await Assert.ThrowsAsync<ObjectDisposedException>(() => committer.CommitAsync("c", Token));
        await committer.DisposeAsync();
    }

    /// <summary>Disposal while the batch loop waits out the write interval answers the waiting changes.</summary>
    [Fact]
    public async Task DisposalDuringTheIntervalAnswersTheWaitingChanges()
    {
        var time = new ArmedTimeProvider(new FakeTimeProvider());
        var committer = new GroupCommitter<FakeValue, FakeVersion, string, string, GroupCommitWrite<FakeVersion>>(_rules, "FakeCommitter", time, TimeSpan.FromSeconds(5));
        Assert.Equal("applied@1", await committer.CommitAsync("a", Token));
        var waiting = committer.CommitAsync("b", Token);
        await time.Armed.WaitAsync(TimeSpan.FromSeconds(10), Token);

        await committer.DisposeAsync();

        _ = await Assert.ThrowsAsync<ObjectDisposedException>(() => waiting);
        Assert.Equal(1, _store.Writes);
    }

    /// <summary>
    /// The batch loop runs in its owner's execution context, which the committer captured when the owner created it, and
    /// in no submitter's (conventions.md §12).
    /// </summary>
    [Fact]
    public async Task TheBatchLoopRunsInItsOwnersContext()
    {
        FakeStore.Context.Value = "owner";
        GroupCommitter<FakeValue, FakeVersion, string, string, GroupCommitWrite<FakeVersion>> committer;
        try
        {
            committer = new(_rules, "OwnedCommitter");
        }
        finally
        {
            FakeStore.Context.Value = null;
        }

        await using (committer)
        {
            FakeStore.Context.Value = "submitter";
            Task<string> change;
            try
            {
                change = committer.CommitAsync("a", Token);
            }
            finally
            {
                FakeStore.Context.Value = null;
            }

            Assert.Equal("applied@1", await change);
        }

        Assert.NotEmpty(_store.Contexts);
        Assert.All(_store.Contexts, static context => Assert.Equal("owner", context));
    }

    /// <summary>The batch loop runs in no caller's execution context, and needs no caller's synchronization context.</summary>
    [Fact]
    public async Task TheBatchLoopRunsInNoCallersContext()
    {
        FakeStore.Context.Value = "caller";
        var previous = SynchronizationContext.Current;
        Task<string> change;
        SynchronizationContext.SetSynchronizationContext(new NeverPumps());
        try
        {
            change = _committer.CommitAsync("a", Token);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
            FakeStore.Context.Value = null;
        }

        Assert.Equal("applied@1", await change);
        Assert.All(_store.Contexts, static context => Assert.Null(context));
    }

    /// <summary>
    /// A write starts at least the interval after the previous one completed: the wait rounds up the whole milliseconds a
    /// delay waits, so it never spins on a remainder below one, and measures again after each delay, so a timer which
    /// fires early starts no write.
    /// </summary>
    [Fact]
    public async Task EachWriteStartsAtLeastTheIntervalAfterThePreviousOneCompleted()
    {
        var fake = new FakeTimeProvider();
        var time = new ArmedTimeProvider(fake);
        _store.Clock = fake;
        var interval = TimeSpan.FromSeconds(5);
        await using var committer = new GroupCommitter<FakeValue, FakeVersion, string, string, GroupCommitWrite<FakeVersion>>(_rules, "FakeCommitter", time, interval);
        var write = _store.HoldNextWrite();
        var change = committer.CommitAsync("a", Token);
        await write.Arrived;
        var read = _store.HoldNextRead();
        write.Decide(FakeStore.WriteDecision.Mismatch);
        var completed = fake.GetTimestamp();
        await WaitUntilAsync(() => _store.Reads == 2);

        // The retry measures the interval at a fractional millisecond, and its delay rounds the remainder up.
        fake.Advance(TimeSpan.FromMilliseconds(0.5));
        read.SetResult();
        await time.Armed.WaitAsync(TimeSpan.FromSeconds(10), Token);
        fake.Advance(interval - TimeSpan.FromMilliseconds(0.8));
        var measured = time.Timestamps;
        await Task.Delay(TimeSpan.FromMilliseconds(20), TimeProvider.System, Token);
        Assert.Equal(1, _store.Writes);
        Assert.True(time.Timestamps - measured < 20, $"The wait measured the clock {time.Timestamps - measured} times while it waited.");

        fake.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal("applied@1", await change);
        Assert.Equal(2, _store.Writes);
        Assert.True(fake.GetElapsedTime(completed, _store.WriteStarts[^1]) >= interval);
    }

    /// <summary>Changes submitted while the batch loop waits out the interval join the batch its next write carries.</summary>
    [Fact]
    public async Task ChangesSubmittedDuringTheIntervalShareTheNextWrite()
    {
        var fake = new FakeTimeProvider();
        var time = new ArmedTimeProvider(fake);
        await using var committer = new GroupCommitter<FakeValue, FakeVersion, string, string, GroupCommitWrite<FakeVersion>>(_rules, "FakeCommitter", time, TimeSpan.FromSeconds(5));
        Assert.Equal("applied@1", await committer.CommitAsync("a", Token));
        await time.Armed.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Task<string>[] waiting = [committer.CommitAsync("b", Token), committer.CommitAsync("c", Token)];

        fake.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(["applied@2", "applied@2"], await Task.WhenAll(waiting));
        Assert.Equal(2, _store.Writes);
    }

    /// <summary>
    /// Type-enforced durability barriers (conventions.md §6, §13): default proof tokens are rejected, no proof type
    /// exposes a public constructor, and a read-only freshness proof requires a valid post-IO proof whose sequence
    /// predates no submission in the batch.
    /// </summary>
    [Fact]
    public void DefaultDurabilityProofTokensAreRejectedAndHaveNoPublicConstructors()
    {
        Type[] proofTypes =
        [
            typeof(GroupCommitter<FakeValue, FakeVersion, string, string, GroupCommitWrite<FakeVersion>>.DurableCommit),
            typeof(GroupCommitter<FakeValue, FakeVersion, string, string, GroupCommitWrite<FakeVersion>>.DurableRead),
            typeof(GroupCommitter<FakeValue, FakeVersion, string, string, GroupCommitWrite<FakeVersion>>.FreshDurableState),
        ];

        foreach (var proofType in proofTypes)
        {
            Assert.Empty(proofType.GetConstructors(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance));
        }

        var defaultCommit = default(GroupCommitter<FakeValue, FakeVersion, string, string, GroupCommitWrite<FakeVersion>>.DurableCommit);
        Assert.False(defaultCommit.IsValid);
        _ = Assert.Throws<InvalidOperationException>(() => defaultCommit.EnsureDurable());
        _ = Assert.Throws<InvalidOperationException>(() => defaultCommit.Value);
        _ = Assert.Throws<InvalidOperationException>(() => defaultCommit.Version);
        _ = Assert.Throws<InvalidOperationException>(() =>
            GroupCommitter<FakeValue, FakeVersion, string, string, GroupCommitWrite<FakeVersion>>.DurableCommit.FromApplied(
                FakeValue.Empty,
                new GroupCommitWrite<FakeVersion>.Applied(null!)));

        var defaultRead = default(GroupCommitter<FakeValue, FakeVersion, string, string, GroupCommitWrite<FakeVersion>>.DurableRead);
        Assert.False(defaultRead.IsValid);
        _ = Assert.Throws<InvalidOperationException>(() => defaultRead.EnsureDurable());
        _ = Assert.Throws<InvalidOperationException>(() => defaultRead.Value);
        _ = Assert.Throws<InvalidOperationException>(() => defaultRead.Version);
        _ = Assert.Throws<InvalidOperationException>(() => defaultRead.HeldWriteMayLand);

        var defaultFresh = default(GroupCommitter<FakeValue, FakeVersion, string, string, GroupCommitWrite<FakeVersion>>.FreshDurableState);
        Assert.False(defaultFresh.IsValid);
        _ = Assert.Throws<InvalidOperationException>(() => defaultFresh.EnsureValid());
        _ = Assert.Throws<InvalidOperationException>(() => defaultFresh.Value);

        _ = Assert.Throws<InvalidOperationException>(() =>
            GroupCommitter<FakeValue, FakeVersion, string, string, GroupCommitWrite<FakeVersion>>.FreshDurableState.TryFromCommit(
                in defaultCommit,
                sequenceBefore: 1,
                latestSubmissionSequence: 1,
                out _));
        _ = Assert.Throws<InvalidOperationException>(() =>
            GroupCommitter<FakeValue, FakeVersion, string, string, GroupCommitWrite<FakeVersion>>.FreshDurableState.TryFromRead(
                in defaultRead,
                sequenceBefore: 1,
                latestSubmissionSequence: 1,
                out _));

        var validCommit = GroupCommitter<FakeValue, FakeVersion, string, string, GroupCommitWrite<FakeVersion>>.DurableCommit.FromApplied(
            FakeValue.Empty,
            new GroupCommitWrite<FakeVersion>.Applied(new FakeVersion(1)));
        Assert.False(GroupCommitter<FakeValue, FakeVersion, string, string, GroupCommitWrite<FakeVersion>>.FreshDurableState.TryFromCommit(
            in validCommit,
            sequenceBefore: 1,
            latestSubmissionSequence: 2,
            out var stale));
        Assert.False(stale.IsValid);

        Assert.True(GroupCommitter<FakeValue, FakeVersion, string, string, GroupCommitWrite<FakeVersion>>.FreshDurableState.TryFromCommit(
            in validCommit,
            sequenceBefore: 2,
            latestSubmissionSequence: 2,
            out var fresh));
        Assert.True(fresh.IsValid);
        Assert.Same(FakeValue.Empty, fresh.Value);
    }

    /// <summary>
    /// A write whose CAS fails without landing (refused or failed write followed by a failed read) never mints a
    /// <see cref="GroupCommitter{TValue, TVersion, TChange, TOutcome, TReply}.DurableCommit"/>, never calls
    /// <see cref="IGroupCommitBatch{TValue, TVersion, TOutcome}.OnApplied"/>, and never answers applied.
    /// </summary>
    [Fact]
    public async Task RefusedOrFailedWriteNeverAnswersAppliedOrInvokesOnApplied()
    {
        var appliedNotifications = 0;
        _rules.OnAppliedProbe = () => appliedNotifications++;

        var held = _store.HoldNextWrite();
        var refused = _committer.CommitAsync("a", Token);
        await held.Arrived;
        held.Decide(FakeStore.WriteDecision.Refuse);

        _ = await Assert.ThrowsAsync<FakeRefusalException>(() => refused);
        Assert.Equal(0, appliedNotifications);

        var heldFail = _store.HoldNextWrite();
        var failed = _committer.CommitAsync("b", Token);
        await heldFail.Arrived;
        _store.ReadFailure = new IOException("injected read failure");
        heldFail.Decide(FakeStore.WriteDecision.Fail);

        _ = await Assert.ThrowsAsync<IOException>(() => failed);
        Assert.Equal(0, appliedNotifications);
        _store.ReadFailure = null;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        while (!condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1), TimeProvider.System, Token);
        }
    }

    /// <summary>A time provider which signals each timer it creates.</summary>
    private sealed class ArmedTimeProvider(TimeProvider inner) : TimeProvider
    {
        private readonly TaskCompletionSource _armed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Armed => _armed.Task;

        public override long TimestampFrequency => inner.TimestampFrequency;

        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();

        public long Timestamps { get; private set; }

        public override long GetTimestamp()
        {
            Timestamps++;
            return inner.GetTimestamp();
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = inner.CreateTimer(callback, state, dueTime, period);
            _ = _armed.TrySetResult();
            return timer;
        }
    }

    /// <summary>A synchronization context which never runs what is posted to it.</summary>
    private sealed class NeverPumps : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
        }
    }
}
