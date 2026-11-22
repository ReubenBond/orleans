using Microsoft.Extensions.Time.Testing;
using Orleans.Durable.Storage.Diagnostics;
using Orleans.Durable.Storage.Engine.Archive.Lsm.Control;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.GroupCommit;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Durable.Storage.Engine.ObjectStore.Memory;
using Orleans.Durable.Storage.Testing;
using static Orleans.Durable.Storage.Testing.ManifestChanges;

namespace Orleans.Durable.Storage.Engine.Archive.Lsm.Tests;

/// <summary>
/// Examples of <see cref="ManifestCommitter"/> (archive §2.9): the commit rules and goals of drains and compactions,
/// group commit, mismatches, unknown outcomes, the interval between replaces, and the one object it touches.
/// </summary>
public sealed class ManifestCommitterTests : IAsyncDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();
    private readonly InMemoryObjectStore _store = new();
    private readonly ProbeObjectStore _probe;
    private readonly ManifestView _view;
    private readonly ManifestCommitter _committer;

    public ManifestCommitterTests()
    {
        _probe = new ProbeObjectStore(_store);
        _view = new ManifestView(_probe);
        _committer = new ManifestCommitter(_probe, _view, TimeProvider.System, TimeSpan.Zero);
    }

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        await _committer.DisposeAsync();
        _leaks.Dispose();
    }

    /// <summary>
    /// A replace the store refuses because the encoding changed after its CRC-32C was taken reports the defect, which
    /// stops the process (archive §9), and fails the batch with the refusal rather than as an unknown outcome: no read
    /// follows it.
    /// </summary>
    [Fact]
    public async Task AReplaceRefusedForChangedContentStopsTheProcess()
    {
        var since = FatalErrors.ReportedCount;
        _probe.ChangeContent = true;

        _ = await Assert.ThrowsAsync<ObjectContentChangedException>(() => CommitAsync(Drain(1, 0, 0, complete: true)));

        var report = Assert.Single(FatalErrors.TakeMine(since));
        Assert.Equal(FatalErrorSite.Defect, report.Site);
        _ = Assert.IsType<ObjectContentChangedException>(report.Failure.Exception);
        Assert.Equal(1, _probe.Calls.Count(static call => call == "ReadVersioned(manifest)"));
        Assert.Equal(ManifestValue.Absent, await ReadAsync());
    }

    [Fact]
    public async Task ADrainOfNoRowsCompletesItsLedger()
    {
        Assert.Equal(Applied(1), await CommitAsync(Drain(1, 0, 0, complete: true)));

        var manifest = await ReadAsync();
        Assert.Equal(Ledger(2), manifest.DrainedBelow);
        Assert.Empty(manifest.Watermarks);
        Assert.True(manifest.IsComplete(Ledger(1)));
    }

    /// <summary>
    /// A ledger completed above <c>DrainedBelow</c> keeps a complete entry until every ledger below it completes, and
    /// then <c>DrainedBelow</c> advances past all of them at once.
    /// </summary>
    [Fact]
    public async Task DrainedBelowAdvancesPastEveryCompleteLedgerContiguousFromIt()
    {
        Assert.Equal(Applied(1), await CommitAsync(Drain(3, 0, 7, complete: true, Section("d/3/0/a"))));
        var manifest = await ReadAsync();
        Assert.True(manifest.IsComplete(Ledger(3)));
        Assert.Equal(Ledger(1), manifest.DrainedBelow);

        Assert.Equal(Applied(2), await CommitAsync(Drain(2, 0, 4, complete: true)));
        Assert.Equal(Applied(3), await CommitAsync(Drain(1, 0, 0, complete: true)));

        manifest = await ReadAsync();
        Assert.Equal(Ledger(4), manifest.DrainedBelow);
        Assert.Empty(manifest.Watermarks);
        Assert.Equal<SectionRef>([Section("d/3/0/a")], manifest.Sections);
    }

    /// <summary>
    /// A drain of more than 10,000 sections commits a manifest whose encoding spans several buffer pages, which a view
    /// reads back.
    /// </summary>
    [Fact]
    public async Task AManifestOfManyPagesCommitsAndReadsBack()
    {
        var sections = ManySections(12_000);

        Assert.Equal(Applied(1), await CommitAsync(Drain(1, 0, 10, false, sections)));
        Assert.Equal<SectionRef>(sections.OrderBy(static section => section.Object), (await ReadAsync()).Sections);
    }

    [Fact]
    public async Task ADrainFromAWatermarkWhichMovedIsSupersededAndAResubmissionIsApplied()
    {
        Assert.Equal(Applied(1), await CommitAsync(Drain(1, 0, 10, sections: Section("d/1/0/a"))));

        Assert.Equal(Superseded(1), await CommitAsync(Drain(1, 0, 20, sections: Section("d/1/0/b"))));
        Assert.Equal(Applied(1), await CommitAsync(Drain(1, 0, 10, sections: Section("d/1/0/a"))));
        Assert.Equal(Slot(10), (await ReadAsync()).RecordedWatermark(Ledger(1)));
    }

    /// <summary>
    /// A drain of a complete ledger, from the watermark its completion recorded, is superseded, and the ledger stays
    /// complete (LSM-I1).
    /// </summary>
    [Fact]
    public async Task ADrainOfACompleteLedgerIsSuperseded()
    {
        Assert.Equal(Applied(1), await CommitAsync(Drain(2, 0, 7, complete: true, Section("d/2/0/a"))));

        Assert.Equal(Superseded(1), await CommitAsync(Drain(2, 7, 9, false, Section("d/2/7/b"))));
        var manifest = await ReadAsync();
        Assert.True(manifest.IsComplete(Ledger(2)));
        Assert.Equal<SectionRef>([Section("d/2/0/a")], manifest.Sections);
    }

    [Fact]
    public async Task ACompactionWithNoOutputsRemovesItsInputs()
    {
        _ = await CommitAsync(Drain(1, 0, 10, sections: Section("d/1/0/a")));

        Assert.Equal(Applied(2), await CommitAsync(Compaction([Section("d/1/0/a")])));
        Assert.Empty((await ReadAsync()).Sections);
        Assert.Equal(Applied(2), await CommitAsync(Compaction([Section("d/1/0/a")])));
    }

    [Fact]
    public async Task ACompactionWhoseInputAnotherConsumedIsSuperseded()
    {
        _ = await CommitAsync(Drain(1, 0, 10, false, Section("d/1/0/a"), Section("d/1/0/b")));
        Assert.Equal(Applied(2), await CommitAsync(Compaction([Section("d/1/0/a")], Section("c/1/c"))));

        Assert.Equal(Superseded(2), await CommitAsync(Compaction([Section("d/1/0/a"), Section("d/1/0/b")], Section("c/1/d"))));
        Assert.Equal<SectionRef>([Section("c/1/c"), Section("d/1/0/b")], (await ReadAsync()).Sections);
    }

    [Fact]
    public async Task IdenticalSubmissionsOfOneBatchAreAnsweredAlike()
    {
        var (warmUp, batch) = await SubmitAsOneBatchAsync(Drain(1, 0, 10, sections: Section("d/1/0/a")), Drain(1, 0, 10, sections: Section("d/1/0/a")));

        _ = await warmUp;
        Assert.Equal<ManifestCommitOutcome>([Applied(2), Applied(2)], await Task.WhenAll(batch));
        Assert.Equal(2, Replaces());
    }

    /// <summary>
    /// A competing committer's replace makes this committer's replace mismatch; it reads the manifest and decides the
    /// batch again against it.
    /// </summary>
    [Fact]
    public async Task AMismatchReadsTheManifestAndDecidesTheBatchAgain()
    {
        _ = await CommitAsync(Drain(1, 0, 10));
        var current = _view.Current!;
        _ = await WriteAsync(_store, Next(current.Value, Drain(2, 0, 5)), current.Version, TestContext.Current.CancellationToken);

        Assert.Equal(Applied(3), await CommitAsync(Drain(1, 10, 20)));

        var manifest = await ReadAsync();
        Assert.Equal(Slot(20), manifest.RecordedWatermark(Ledger(1)));
        Assert.Equal(Slot(5), manifest.RecordedWatermark(Ledger(2)));
    }

    /// <summary>
    /// A change derived from a value a competing committer wrote is decided against a value read after it was
    /// submitted, never against the older value this committer last wrote.
    /// </summary>
    [Fact]
    public async Task AChangeDerivedFromACompetingCommittersValueIsDecidedAgainstALaterValue()
    {
        _ = await CommitAsync(Drain(1, 0, 10));
        var current = _view.Current!;
        _ = await WriteAsync(_store, Next(current.Value, Drain(1, 10, 20)), current.Version, TestContext.Current.CancellationToken);

        Assert.Equal(Applied(3), await CommitAsync(Drain(1, 20, 30)));
    }

    [Fact]
    public async Task AReplaceWhoseOutcomeIsUnknownButWhichLandedIsAnsweredFromTheReadWhichFollows()
    {
        _probe.FailReplaces(ProbeObjectStore.ReplaceFault.After);

        Assert.Equal(Applied(1), await CommitAsync(Drain(1, 0, 10)));
        Assert.Equal(1, Replaces());
    }

    [Fact]
    public async Task AReplaceWhoseOutcomeIsUnknownFailsTheChangesTheReadLeavesUndecided()
    {
        _ = await WriteAsync(_store, Next(ManifestValue.Absent, Drain(2, 0, 5)), null, TestContext.Current.CancellationToken);
        _probe.FailReplaces(ProbeObjectStore.ReplaceFault.None, ProbeObjectStore.ReplaceFault.Before);

        // The second drain derives from the absent manifest, whose watermark of ledger 2 has since moved to 5.
        var (warmUp, batch) = await SubmitAsOneBatchAsync(Drain(1, 0, 10), Drain(2, 0, 9));

        Assert.Equal(Applied(2), await warmUp);
        _ = await Assert.ThrowsAsync<IOException>(() => batch[0]);
        Assert.Equal(Superseded(2), await batch[1]);
        Assert.Equal(Applied(3), await CommitAsync(Drain(1, 0, 10)));
    }

    /// <summary>
    /// A change the replace of unknown outcome carried is answered only <c>Applied</c>, or fails (conventions.md §6): a
    /// compaction whose replace landed, and whose output a competing committer compacted away before the read which
    /// follows, fails, though the value read neither lists its output nor its input.
    /// </summary>
    [Fact]
    public async Task AChangeAReplaceOfUnknownOutcomeCarriedIsNeverAnsweredSuperseded()
    {
        _ = await CommitAsync(Drain(1, 0, 10, sections: Section("d/1/0/a")));
        _probe.FailReplaces(ProbeObjectStore.ReplaceFault.After);
        _probe.AfterLanding = async () =>
        {
            var landed = await new ManifestView(_store).ReadAsync(Cancellation);
            _ = await WriteAsync(_store, Next(landed.Value, Compaction([Section("c/1/c")], Section("c/1/d"))), landed.Version, Cancellation);
        };

        _ = await Assert.ThrowsAsync<IOException>(() => CommitAsync(Compaction([Section("d/1/0/a")], Section("c/1/c"))));

        var manifest = await ReadAsync();
        Assert.Equal(3ul, manifest.Epoch);
        Assert.Equal<SectionRef>([Section("c/1/d")], manifest.Sections);
    }

    /// <summary>
    /// A read which fails after a replace of unknown outcome fails the batch with the replace's exception
    /// (conventions.md §6), so its submitters act on the unknown outcome; the next commit reads the manifest first, so it
    /// replaces the value the held replace wrote rather than mismatching against it.
    /// </summary>
    [Fact]
    public async Task AFailedReadAfterAnUnknownOutcomeFailsTheBatchWithTheReplacesException()
    {
        _probe.FailReplaces(ProbeObjectStore.ReplaceFault.After);
        _probe.AfterLanding = () =>
        {
            _probe.FailReads = true;
            return Task.CompletedTask;
        };

        var failure = await Assert.ThrowsAsync<IOException>(() => CommitAsync(Drain(1, 0, 10)));

        Assert.Equal("A scripted replace failure after the store applied it.", failure.Message);
        _probe.FailReads = false;
        Assert.Equal(Applied(2), await CommitAsync(Drain(2, 0, 5)));
        Assert.Equal(2, Replaces());
    }

    /// <summary>
    /// A read after a replace whose outcome is unknown which finds the manifest corrupt leaves the change the replace
    /// carried unknown, the corruption inside, since the replace may have landed first (conventions.md §6); a later change,
    /// which no replace carried, fails with the corruption itself, which every read meets.
    /// </summary>
    [Fact]
    public async Task ACorruptManifestAfterAnUnknownOutcomeLeavesTheCarriedChangeUnknown()
    {
        Assert.Equal(Applied(1), await CommitAsync(Drain(1, 0, 10)));
        using (var garbage = ArcBuffer.Wrap("XLAM not a manifest"u8.ToArray()))
        {
            _ = Assert.IsType<ObjectReplaceOutcome.Applied>((await _store.ReplaceAsync(ObjectName.Manifest, _view.Current!.Version, garbage, null, Cancellation)).Value);
        }

        _probe.FailReplaces(ProbeObjectStore.ReplaceFault.Before);

        var unknown = await Assert.ThrowsAsync<GroupCommitOutcomeUnknownException>(() => CommitAsync(Drain(1, 10, 20)));
        _ = Assert.IsType<ArchiveCorruptException>(unknown.InnerException);
        _ = await Assert.ThrowsAsync<ArchiveCorruptException>(() => CommitAsync(Drain(2, 0, 5)));
        Assert.Equal(2, Replaces());
    }
    [Fact]
    public async Task AFailedReadFailsTheBatch()
    {
        _probe.FailReads = true;

        _ = await Assert.ThrowsAsync<IOException>(() => CommitAsync(Drain(1, 0, 10)));
        _probe.FailReads = false;

        Assert.Equal(Applied(1), await CommitAsync(Drain(1, 0, 10)));
    }

    /// <summary>
    /// An exception of the fold is a defect before any replace: the batch fails with
    /// <see cref="InvalidOperationException"/>, nothing is replaced, and the next batch reads the manifest first.
    /// </summary>
    [Fact]
    public async Task AnExceptionOfTheFoldFailsTheBatchAsADefect()
    {
        _ = await WriteAsync(_store, ManifestValue.Absent.WithEpoch(ulong.MaxValue), null, Cancellation);

        var defect = await Assert.ThrowsAsync<InvalidOperationException>(() => CommitAsync(Drain(1, 0, 10)));

        _ = Assert.IsType<OverflowException>(defect.InnerException);
        Assert.Equal(0, Replaces());
        var reads = _probe.VersionedReads;
        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => CommitAsync(Drain(1, 0, 10)));
        Assert.Equal(reads + 1, _probe.VersionedReads);
    }

    /// <summary>
    /// Disposal answers a queued change and one whose replace is in flight with <see cref="ObjectDisposedException"/>, and
    /// refuses later ones.
    /// </summary>
    [Fact]
    public async Task DisposalAnswersQueuedAndInFlightChangesAndRefusesLaterOnes()
    {
        var committer = new ManifestCommitter(_probe, new ManifestView(_probe), TimeProvider.System, TimeSpan.Zero);
        _probe.HoldReplaces();
        var inFlight = committer.CommitAsync(Drain(1, 0, 10), Cancellation);
        while (Replaces() == 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1), TimeProvider.System, Cancellation);
        }

        var queued = committer.CommitAsync(Drain(2, 0, 5), Cancellation);
        await committer.DisposeAsync();

        _ = await Assert.ThrowsAsync<ObjectDisposedException>(() => inFlight);
        _ = await Assert.ThrowsAsync<ObjectDisposedException>(() => queued);
        _ = await Assert.ThrowsAsync<ObjectDisposedException>(() => committer.CommitAsync(Drain(3, 0, 5), Cancellation));
        _probe.ReleaseReplaces();
    }

    /// <summary>The committer reads and replaces in no caller's execution context (conventions.md §12).</summary>
    [Fact]
    public async Task TheCommitterReadsAndReplacesInNoCallersContext()
    {
        ProbeObjectStore.Context.Value = "caller";
        Task<ManifestCommitOutcome> commit;
        try
        {
            commit = CommitAsync(Drain(1, 0, 10));
        }
        finally
        {
            ProbeObjectStore.Context.Value = null;
        }

        Assert.Equal(Applied(1), await commit);
        Assert.NotEmpty(_probe.Contexts);
        Assert.All(_probe.Contexts, static context => Assert.Null(context));
    }
    [Fact]
    public async Task ACancelledCommitStopsTheWaitWhileTheChangeMayStillApply()
    {
        using var cancellation = new CancellationTokenSource();
        _probe.HoldReads();
        var cancelled = _committer.CommitAsync(Drain(1, 0, 10), cancellation.Token);

        await cancellation.CancelAsync();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        _probe.ReleaseReads();

        Assert.Equal(Applied(1), await CommitAsync(Drain(1, 0, 10)));
    }

    /// <summary>
    /// Each replace starts at least the interval after the previous one completed, on the injected clock: the interval
    /// runs from a slow replace's completion, not from its start. The test moves the clock only once the committer has
    /// armed the interval's timer, so the timer's due time is what remains of the interval when the batch loop measured
    /// it (issue #226: a clock moved between the measurement and the arming armed the timer past the test's last advance).
    /// </summary>
    [Fact]
    public async Task EachReplaceStartsAtLeastTheIntervalAfterThePreviousOneCompleted()
    {
        var time = new ArmedTimeProvider();
        var interval = TimeSpan.FromSeconds(5);
        var probe = new ProbeObjectStore(_store, time);
        await using var committer = new ManifestCommitter(probe, new ManifestView(probe), time, interval);
        probe.HoldReplaces();
        var first = committer.CommitAsync(Drain(1, 0, 10), Cancellation);
        while (probe.ReplaceStarts.Count == 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1), TimeProvider.System, Cancellation);
        }

        time.Advance(TimeSpan.FromSeconds(3));
        var armed = time.Timers;
        probe.ReleaseReplaces();
        Assert.Equal(Applied(1), await first);

        // The batch loop arms the interval's timer once the first replace completes, before or after the next submission;
        // the clock has not moved since, so the interval's whole length remains.
        var next = committer.CommitAsync(Drain(1, 10, 20), Cancellation);
        Assert.Equal(interval, await time.NextDueTimeAsync(armed, Cancellation));
        time.Advance(interval - TimeSpan.FromMilliseconds(1));
        await Task.Delay(TimeSpan.FromMilliseconds(10), TimeProvider.System, Cancellation);
        Assert.False(next.IsCompleted);
        Assert.Single(probe.ReplaceStarts);
        time.Advance(TimeSpan.FromMilliseconds(1));

        Assert.Equal(Applied(2), await next);
        Assert.True(time.GetElapsedTime(probe.ReplaceCompletions.First(), probe.ReplaceStarts.Last()) >= interval);
    }

    [Fact]
    public async Task TheCommitterReadsAndReplacesOnlyTheManifest()
    {
        var random = new Random(7);
        for (var index = 0; index < 40; index++)
        {
            _ = await CommitAsync(ManifestWorkload.Derive(await ReadAsync(), random));
        }

        Assert.All(_probe.Calls, static call => Assert.Contains(call, (string[])["ReadVersioned(manifest)", "Replace(manifest)"]));
    }

    private static ManifestCommitOutcome Applied(ulong epoch) => new ManifestCommitOutcome.Applied(epoch);

    private static ManifestCommitOutcome Superseded(ulong epoch) => new ManifestCommitOutcome.Superseded(epoch);

    private Task<ManifestCommitOutcome> CommitAsync(ManifestChange change) => _committer.CommitAsync(change, Cancellation);

    private async Task<ManifestValue> ReadAsync() => (await new ManifestView(_store).ReadAsync(Cancellation)).Value;

    private int Replaces() => _probe.Calls.Count(static call => call == "Replace(manifest)");

    /// <summary>
    /// Submits, to a committer which has not read the manifest yet, a warm-up drain of ledger 9, whose read the probe
    /// holds, then <paramref name="changes"/>, which join the batch after it.
    /// </summary>
    private async Task<(Task<ManifestCommitOutcome> WarmUp, Task<ManifestCommitOutcome>[] Batch)> SubmitAsOneBatchAsync(params ManifestChange[] changes)
    {
        var reads = _probe.VersionedReads;
        _probe.HoldReads();
        var warmUp = CommitAsync(Drain(9, 0, 1));
        while (_probe.VersionedReads == reads)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1), TimeProvider.System, Cancellation);
        }

        var batch = changes.Select(CommitAsync).ToArray();
        _probe.ReleaseReads();
        return (warmUp, batch);
    }

    /// <summary>A fake clock which signals each timer its users arm, with the timer's due time.</summary>
    private sealed class ArmedTimeProvider : FakeTimeProvider
    {
        private readonly Lock _lock = new();
        private readonly List<TimeSpan> _dueTimes = [];
        private TaskCompletionSource _armed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Gets the number of timers armed so far.</summary>
        public int Timers
        {
            get
            {
                lock (_lock)
                {
                    return _dueTimes.Count;
                }
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            TaskCompletionSource armed;
            lock (_lock)
            {
                _dueTimes.Add(dueTime);
                (armed, _armed) = (_armed, new(TaskCreationOptions.RunContinuationsAsynchronously));
            }

            armed.SetResult();
            return timer;
        }

        /// <summary>Waits for the timer armed after the first <paramref name="armedBefore"/>, and returns its due time.</summary>
        public async Task<TimeSpan> NextDueTimeAsync(int armedBefore, CancellationToken cancellationToken)
        {
            while (true)
            {
                Task armed;
                lock (_lock)
                {
                    if (_dueTimes.Count > armedBefore)
                    {
                        return _dueTimes[armedBefore];
                    }

                    armed = _armed.Task;
                }

                await armed.WaitAsync(cancellationToken);
            }
        }
    }
}
