using Orleans.Durable.Storage.Engine.Archive.Lsm.Control;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Durable.Storage.Engine.ObjectStore.Memory;
using Orleans.Durable.Storage.Testing;
using static Orleans.Durable.Storage.Testing.ManifestChanges;

namespace Orleans.Durable.Storage.Engine.Archive.Lsm.Tests;

/// <summary>
/// <see cref="ManifestView"/> (archive §2.2): reads linearizable after their call, which concurrent callers share;
/// <see cref="ManifestView.Current"/> only from values read or applied, newest by epoch; and a corrupt manifest as
/// <see cref="ArchiveCorruptException"/>.
/// </summary>
public sealed class ManifestViewTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();
    private readonly InMemoryObjectStore _store = new();

    public void Dispose() => _leaks.Dispose();

    [Fact]
    public async Task AnAbsentManifestReadsAsTheAbsentValue()
    {
        var view = new ManifestView(_store);
        Assert.Null(view.Current);

        var read = await view.ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ManifestValue.Absent, read.Value);
        Assert.Null(read.Version);
        Assert.Same(read, view.Current);
    }

    [Fact]
    public async Task AReadReturnsTheManifestAReplaceWrote()
    {
        var value = Next(ManifestValue.Absent, Drain(1, 0, 10, sections: Section("d/1/0/a")));
        var version = await WriteAsync(_store, value, null, TestContext.Current.CancellationToken);
        var view = new ManifestView(_store);

        var read = await view.ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(value, read.Value);
        Assert.Equal(version, read.Version);
    }

    /// <summary>
    /// A read which began before a replace may return the older value, but the calls made while it ran share one read
    /// which begins after it, so they see the replace.
    /// </summary>
    [Fact]
    public async Task CallsDuringAReadShareTheNextReadWhichBeginsAfterThem()
    {
        var probe = new ProbeObjectStore(_store);
        var view = new ManifestView(probe);
        probe.HoldReads();
        var first = view.ReadAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => probe.VersionedReads == 1);
        var value = Next(ManifestValue.Absent, Drain(1, 0, 4));
        _ = await WriteAsync(_store, value, null, TestContext.Current.CancellationToken);

        var second = view.ReadAsync(TestContext.Current.CancellationToken);
        var third = view.ReadAsync(TestContext.Current.CancellationToken);
        probe.ReleaseReads();

        Assert.Equal(ManifestValue.Absent, (await first).Value);
        Assert.Same(await second, await third);
        Assert.Equal(value, (await second).Value);
        Assert.Equal(2, probe.VersionedReads);
        Assert.Equal(value, view.Current?.Value);
    }

    /// <summary>
    /// A join shares the read in flight, which began before it, and reads only when none is; a refresh returns the value
    /// it reached, reading only while the view is older.
    /// </summary>
    [Fact]
    public async Task AJoinSharesTheReadInFlightAndARefreshReturnsTheValueItReached()
    {
        var probe = new ProbeObjectStore(_store);
        var view = new ManifestView(probe);
        probe.HoldReads();
        var first = view.ReadAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => probe.VersionedReads == 1);
        var value = Next(ManifestValue.Absent, Drain(1, 0, 4));
        _ = await WriteAsync(_store, value, null, TestContext.Current.CancellationToken);

        var joined = view.JoinAsync(TestContext.Current.CancellationToken);
        probe.ReleaseReads();

        Assert.Same(await first, await joined);
        Assert.Equal(ManifestValue.Absent, (await joined).Value);
        Assert.Equal(1, probe.VersionedReads);
        Assert.Equal(value, (await view.JoinAsync(TestContext.Current.CancellationToken)).Value);
        Assert.Equal(2, probe.VersionedReads);
        Assert.Equal(value, (await view.RefreshAsync(1, TestContext.Current.CancellationToken)).Value);
        Assert.Equal(2, probe.VersionedReads);
    }

    [Fact]
    public async Task ACancelledCallStopsWaitingWhileTheReadItSharesRunsOn()
    {
        var probe = new ProbeObjectStore(_store);
        var view = new ManifestView(probe);
        probe.HoldReads();
        using var cancellation = new CancellationTokenSource();
        var cancelled = view.ReadAsync(cancellation.Token);
        var waiting = view.ReadAsync(TestContext.Current.CancellationToken);

        await cancellation.CancelAsync();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        probe.ReleaseReads();

        Assert.Equal(ManifestValue.Absent, (await waiting).Value);
        Assert.Equal(2, probe.VersionedReads);
        Assert.NotNull(view.Current);
    }

    /// <summary>
    /// A replace whose outcome is unknown never updates <see cref="ManifestView.Current"/>; the read which follows it
    /// does.
    /// </summary>
    [Fact]
    public async Task CurrentHoldsOnlyValuesReadOrApplied()
    {
        var probe = new ProbeObjectStore(_store);
        var view = new ManifestView(probe);
        await using var committer = new ManifestCommitter(probe, view, TimeProvider.System, TimeSpan.Zero);
        probe.FailReplaces(ProbeObjectStore.ReplaceFault.Before);

        _ = await Assert.ThrowsAsync<IOException>(() => committer.CommitAsync(Drain(1, 0, 10), TestContext.Current.CancellationToken));

        Assert.Equal(ManifestValue.Absent, view.Current?.Value);
        var applied = Assert.IsType<ManifestCommitOutcome.Applied>((await committer.CommitAsync(Drain(1, 0, 10), TestContext.Current.CancellationToken)).Value);
        Assert.Equal(1UL, applied.Epoch);
        Assert.Equal(1UL, view.Current?.Value.Epoch);
    }

    [Fact]
    public async Task CurrentKeepsTheNewestEpoch()
    {
        var view = new ManifestView(_store);
        var value = Next(Next(ManifestValue.Absent, Drain(1, 0, 4)), Drain(1, 4, 8));
        _ = await WriteAsync(_store, value, null, TestContext.Current.CancellationToken);
        var read = await view.ReadAsync(TestContext.Current.CancellationToken);

        view.Observe(new ManifestSnapshot(Next(ManifestValue.Absent, Drain(1, 0, 4)), null));

        Assert.Same(read, view.Current);
    }

    [Fact]
    public async Task RefreshReadsOnlyWhileCurrentIsOlder()
    {
        var probe = new ProbeObjectStore(_store);
        var view = new ManifestView(probe);
        var version = await WriteAsync(_store, Next(ManifestValue.Absent, Drain(1, 0, 4)), null, TestContext.Current.CancellationToken);
        await view.RefreshAsync(1, TestContext.Current.CancellationToken);
        await view.RefreshAsync(1, TestContext.Current.CancellationToken);
        Assert.Equal(1, probe.VersionedReads);

        _ = await WriteAsync(_store, Next(view.Current!.Value, Drain(1, 4, 8)), version, TestContext.Current.CancellationToken);
        await view.RefreshAsync(2, TestContext.Current.CancellationToken);

        Assert.Equal(2, probe.VersionedReads);
        Assert.Equal(2UL, view.Current.Value.Epoch);
    }

    [Fact]
    public async Task ACorruptManifestIsArchiveCorruption()
    {
        using (var garbage = ArcBuffer.Wrap("XLAM not a manifest"u8.ToArray()))
        {
            _ = await _store.CreateAsync(ObjectName.Manifest, garbage, null, TestContext.Current.CancellationToken);
        }

        var view = new ManifestView(_store);

        var corrupt = await Assert.ThrowsAsync<ArchiveCorruptException>(() => view.ReadAsync(TestContext.Current.CancellationToken));

        var rejection = Assert.IsType<InvalidDataException>(corrupt.InnerException);
        Assert.Equal(ManifestFormat.BadMagic, DecodeRejection.ReasonOf(rejection));
        Assert.Null(view.Current);
    }

    [Fact]
    public async Task AFailedReadFailsItsCallersAndTheNextCallReadsAgain()
    {
        var probe = new ProbeObjectStore(_store) { FailReads = true };
        var view = new ManifestView(probe);

        _ = await Assert.ThrowsAsync<IOException>(() => view.ReadAsync(TestContext.Current.CancellationToken));
        probe.FailReads = false;

        Assert.Equal(ManifestValue.Absent, (await view.ReadAsync(TestContext.Current.CancellationToken)).Value);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        while (!condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1), TimeProvider.System, TestContext.Current.CancellationToken);
        }
    }
}
