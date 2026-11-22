using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests.Writer;

public sealed class SequencerTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    [Fact]
    public async Task AcquireSlot_BlocksWhenWindowIsFullUntilContiguousTailAdvances()
    {
        var sequencer = new Sequencer(windowSize: 2);
        var first = await sequencer.AcquireSlotAsync(TestContext.Current.CancellationToken);
        var second = await sequencer.AcquireSlotAsync(TestContext.Current.CancellationToken);

        var blockedAcquire = AcquireAsync(sequencer, TestContext.Current.CancellationToken);
        var outOfOrderCompletion = CompleteAsync(
            sequencer,
            second,
            TestContext.Current.CancellationToken);

        Assert.False(blockedAcquire.IsCompleted);
        Assert.False(outOfOrderCompletion.IsCompleted);
        Assert.Equal(Slot.Zero, sequencer.CompletedTail);

        await sequencer.CompleteSlotAsync(first, TestContext.Current.CancellationToken);

        Assert.Equal(new Slot(2), sequencer.CompletedTail);
        Assert.Equal(new Slot(2), (await blockedAcquire).Slot);
        await outOfOrderCompletion;
    }

    [Fact]
    public async Task CompleteSlot_WaitsForEveryPriorPosition()
    {
        var sequencer = new Sequencer(windowSize: 3);
        var first = await sequencer.AcquireSlotAsync(TestContext.Current.CancellationToken);
        var second = await sequencer.AcquireSlotAsync(TestContext.Current.CancellationToken);
        var third = await sequencer.AcquireSlotAsync(TestContext.Current.CancellationToken);

        var thirdCompletion = CompleteAsync(
            sequencer,
            third,
            TestContext.Current.CancellationToken);
        var secondCompletion = CompleteAsync(
            sequencer,
            second,
            TestContext.Current.CancellationToken);

        Assert.False(thirdCompletion.IsCompleted);
        Assert.False(secondCompletion.IsCompleted);

        await sequencer.CompleteSlotAsync(first, TestContext.Current.CancellationToken);

        await Task.WhenAll(secondCompletion, thirdCompletion);
        Assert.Equal(new Slot(3), sequencer.CompletedTail);
    }

    [Fact]
    public async Task CancelledAcquire_DoesNotConsumeAPosition()
    {
        var sequencer = new Sequencer(windowSize: 1);
        var first = await sequencer.AcquireSlotAsync(TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await sequencer.AcquireSlotAsync(cancellation.Token));

        await sequencer.CompleteSlotAsync(first, TestContext.Current.CancellationToken);
        Assert.Equal(
            new Slot(1),
            (await sequencer.AcquireSlotAsync(TestContext.Current.CancellationToken)).Slot);
    }

    /// <summary>
    /// ledger-protocol.md §2.6: acquisitions which wait for a full window are granted in the order of their requests, each
    /// as a completion opens the window, and one cancelled while it waits leaves its place and consumes no slot.
    /// </summary>
    [Fact]
    public async Task WaitingAcquisitionsAreGrantedInRequestOrder()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var sequencer = new Sequencer(windowSize: 1);
        var held = await sequencer.AcquireSlotAsync(cancellationToken);
        using var cancellation = new CancellationTokenSource();
        var waiting = new[]
        {
            AcquireAsync(sequencer, cancellationToken),
            AcquireAsync(sequencer, cancellation.Token),
            AcquireAsync(sequencer, cancellationToken),
            AcquireAsync(sequencer, cancellationToken),
        };
        await cancellation.CancelAsync();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting[1]);

        var expected = 1L;
        foreach (var acquisition in waiting.Where((_, index) => index != 1))
        {
            await sequencer.CompleteSlotAsync(held, cancellationToken);
            held = await acquisition;
            Assert.Equal(new Slot(expected++), held.Slot);
        }
    }

    /// <summary>
    /// LP-I13: a stop fails every acquisition waiting for the window.
    /// </summary>
    [Fact]
    public async Task AStopFailsEveryWaitingAcquisition()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var sequencer = new Sequencer(windowSize: 1);
        _ = await sequencer.AcquireSlotAsync(cancellationToken);
        var waiting = new[] { AcquireAsync(sequencer, cancellationToken), AcquireAsync(sequencer, cancellationToken) };

        sequencer.Stop(SequencerStopReason.Sealing);

        foreach (var acquisition in waiting)
        {
            Assert.Equal(SequencerStopReason.Sealing, (await Assert.ThrowsAsync<SequencerStoppedException>(() => acquisition)).Reason);
        }
    }

    [Fact]
    public async Task DuplicateCompletion_IsIdempotent()
    {
        var sequencer = new Sequencer(windowSize: 1);
        var position = await sequencer.AcquireSlotAsync(TestContext.Current.CancellationToken);

        await sequencer.CompleteSlotAsync(position, TestContext.Current.CancellationToken);
        await sequencer.CompleteSlotAsync(position, TestContext.Current.CancellationToken);

        Assert.Equal(new Slot(1), sequencer.CompletedTail);
        Assert.Equal(new Slot(1), (await sequencer.AcquireSlotAsync(TestContext.Current.CancellationToken)).Slot);
    }

    /// <summary>
    /// A position completed again while it waits for an earlier one waits with its first completion.
    /// </summary>
    [Fact]
    public async Task RepeatedCompletionBehindAHole_WaitsForThePriorPositions()
    {
        var sequencer = new Sequencer(windowSize: 2);
        var first = await sequencer.AcquireSlotAsync(TestContext.Current.CancellationToken);
        var second = await sequencer.AcquireSlotAsync(TestContext.Current.CancellationToken);

        var completion = CompleteAsync(sequencer, second, TestContext.Current.CancellationToken);
        var repeated = CompleteAsync(sequencer, second, TestContext.Current.CancellationToken);

        Assert.False(completion.IsCompleted);
        Assert.False(repeated.IsCompleted);

        await sequencer.CompleteSlotAsync(first, TestContext.Current.CancellationToken);

        await Task.WhenAll(completion, repeated);
        Assert.Equal(new Slot(2), sequencer.CompletedTail);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_RejectsANonPositiveWindow(int windowSize)
    {
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new Sequencer(windowSize));
    }

    [Fact]
    public async Task CancelledCompletionBehindHole_RemainsCompleteAndReleasesTheFullWindow()
    {
        var sequencer = new Sequencer(windowSize: 2);
        var first = await sequencer.AcquireSlotAsync(TestContext.Current.CancellationToken);
        var second = await sequencer.AcquireSlotAsync(TestContext.Current.CancellationToken);
        using var completionCancellation = new CancellationTokenSource();

        var secondCompletion = CompleteAsync(sequencer, second, completionCancellation.Token);
        var firstBlockedAcquire = AcquireAsync(sequencer, TestContext.Current.CancellationToken);
        var secondBlockedAcquire = AcquireAsync(sequencer, TestContext.Current.CancellationToken);

        Assert.False(secondCompletion.IsCompleted);
        Assert.False(firstBlockedAcquire.IsCompleted);
        Assert.False(secondBlockedAcquire.IsCompleted);

        await completionCancellation.CancelAsync();
        var cancellation = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await secondCompletion);
        Assert.Equal(completionCancellation.Token, cancellation.CancellationToken);

        Assert.Equal(Slot.Zero, sequencer.CompletedTail);
        Assert.False(firstBlockedAcquire.IsCompleted);
        Assert.False(secondBlockedAcquire.IsCompleted);

        await sequencer.CompleteSlotAsync(first, TestContext.Current.CancellationToken);
        var releasedSlots = await Task.WhenAll(firstBlockedAcquire, secondBlockedAcquire);

        Assert.Equal(new Slot(2), sequencer.CompletedTail);
        Assert.Equal(
            [new Slot(2), new Slot(3)],
            releasedSlots.Select(static slot => slot.Slot).Order());
        await sequencer.CompleteSlotAsync(second, TestContext.Current.CancellationToken);
        Assert.Equal(new Slot(2), sequencer.CompletedTail);
    }

    [Fact]
    public async Task DefaultSlot_CannotCompleteAnAcquiredPosition()
    {
        var sequencer = new Sequencer(windowSize: 1);
        _ = await sequencer.AcquireSlotAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await sequencer.CompleteSlotAsync(
                default,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SlotFromAnotherSequencer_CannotCompleteAnAcquiredPosition()
    {
        var sequencer = new Sequencer(windowSize: 1);
        var other = new Sequencer(windowSize: 1);
        _ = await sequencer.AcquireSlotAsync(TestContext.Current.CancellationToken);
        var foreignSlot = await other.AcquireSlotAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await sequencer.CompleteSlotAsync(
                foreignSlot,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Stop_ReleasesCurrentWaitersAndRejectsSubsequentOperations()
    {
        var sequencer = new Sequencer(windowSize: 2);
        var first = await sequencer.AcquireSlotAsync(TestContext.Current.CancellationToken);
        var second = await sequencer.AcquireSlotAsync(TestContext.Current.CancellationToken);
        var orderedCompletion = CompleteAsync(
            sequencer,
            second,
            TestContext.Current.CancellationToken);
        var blockedAcquire = AcquireAsync(sequencer, TestContext.Current.CancellationToken);

        sequencer.Stop(SequencerStopReason.PayloadWriteFailed);
        sequencer.Stop(SequencerStopReason.Requested);

        await Assert.ThrowsAsync<SequencerStoppedException>(async () => await orderedCompletion);
        await Assert.ThrowsAsync<SequencerStoppedException>(async () => await blockedAcquire);
        var exception = await Assert.ThrowsAsync<SequencerStoppedException>(
            async () => await sequencer.AcquireSlotAsync(TestContext.Current.CancellationToken));
        Assert.Contains(
            nameof(SequencerStopReason.PayloadWriteFailed),
            exception.Message,
            StringComparison.Ordinal);
        await Assert.ThrowsAsync<SequencerStoppedException>(
            async () => await sequencer.CompleteSlotAsync(first, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Stop_AcceptsTheSealingReasonAndRejectsSubsequentOperations()
    {
        var sequencer = new Sequencer(windowSize: 1);
        _ = await sequencer.AcquireSlotAsync(TestContext.Current.CancellationToken);

        sequencer.Stop(SequencerStopReason.Sealing);

        var exception = await Assert.ThrowsAsync<SequencerStoppedException>(
            async () => await sequencer.AcquireSlotAsync(TestContext.Current.CancellationToken));
        Assert.Contains(
            nameof(SequencerStopReason.Sealing),
            exception.Message,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData((int)SequencerStopReason.None)]
    [InlineData(int.MaxValue)]
    public async Task Stop_RejectsInvalidReasonsWithoutStopping(int reason)
    {
        var sequencer = new Sequencer(windowSize: 1);

        _ = Assert.Throws<ArgumentOutOfRangeException>(() => sequencer.Stop((SequencerStopReason)reason));

        Assert.Equal(
            Slot.Zero,
            (await sequencer.AcquireSlotAsync(TestContext.Current.CancellationToken)).Slot);
    }

    /// <summary>
    /// LP-I15: each lifetime draws a fresh instance ID, which names no other, and grants from slot 0; a stopped lifetime's
    /// stop reaches no other.
    /// </summary>
    [Fact]
    public async Task EachLifetimeDrawsAFreshInstanceIdAndGrantsFromSlotZero()
    {
        var lifetimes = Enumerable.Range(0, 8).Select(static _ => new Sequencer(windowSize: 2)).ToArray();

        Assert.Equal(lifetimes.Length, lifetimes.Select(static lifetime => lifetime.InstanceId).Distinct().Count());
        lifetimes[0].Stop(SequencerStopReason.Requested);
        foreach (var lifetime in lifetimes.Skip(1))
        {
            Assert.Equal(Slot.Zero, (await lifetime.AcquireSlotAsync(TestContext.Current.CancellationToken)).Slot);
        }
    }

    /// <summary>
    /// The observer receives each step the lifetime takes outside its lock, in order: an acquisition, a completion called,
    /// and every stop request, a repeated one included. The writer reports a completion which returned and a write which
    /// met a seal (<see cref="LedgerWriterTests.TheObserverReceivesTheWritersSteps"/>).
    /// </summary>
    [Fact]
    public async Task TheObserverReceivesEveryStep()
    {
        var steps = new List<SequencerStep>();
        var sequencer = new Sequencer(windowSize: 1) { Observer = steps.Add };
        var slot = await sequencer.AcquireSlotAsync(TestContext.Current.CancellationToken);
        await sequencer.CompleteSlotAsync(slot, TestContext.Current.CancellationToken);
        sequencer.Stop(SequencerStopReason.Sealing);
        sequencer.Stop(SequencerStopReason.Requested);

        Assert.Equal(
            [
                SequencerStep.Acquired(Slot.Zero),
                SequencerStep.Completing(Slot.Zero),
                SequencerStep.Stopped(SequencerStopReason.Sealing),
                SequencerStep.Stopped(SequencerStopReason.Requested),
            ],
            steps);
        Assert.True(sequencer.IsStopped);
    }

    private static async Task<SlotGrant> AcquireAsync(
        Sequencer sequencer,
        CancellationToken cancellationToken) =>
        await sequencer.AcquireSlotAsync(cancellationToken);

    private static async Task CompleteAsync(
        Sequencer sequencer,
        SlotGrant grant,
        CancellationToken cancellationToken) =>
        await sequencer.CompleteSlotAsync(grant, cancellationToken);
}
