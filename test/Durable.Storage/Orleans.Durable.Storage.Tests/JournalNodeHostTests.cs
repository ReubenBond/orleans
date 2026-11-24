using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Orleans.Durable.Storage.Engine.Archive.Memory;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Journals;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Control;
using Orleans.Durable.Storage.Engine.MetaStore;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Tests;

/// <summary>
/// The host behind <see cref="IJournalStoreGrain"/> (orleans.md §5.5, §7.2), one row per path, each checked before the
/// Journals and so without effect: another node's key answers <c>NotThisNode</c>; a stopped node answers a write and a
/// read <c>Moved</c> naming no owner and a page <c>Expired</c>; a write to a throttled ledger the node is primary of is
/// answered <c>Throttled</c> (OR-I10), and one to a ledger it only replicates reaches the Journals; a key which is no
/// journal key is refused as an argument; a record which fails its verification against the key's row key is refused as
/// invalid (JS-I5); a caller's cancellation while the call waits for the registration propagates. The host releases the
/// forwarded record on every path (OR-I18), which the test's ledger of Orleans buffers checks.
/// </summary>
public sealed class JournalNodeHostTests : IAsyncLifetime
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();
    private readonly OrleansBufferLedger _wire = new();
    private readonly InMemoryArchiveStore _archive = new();
    private readonly NodePressure _pressure = new(TimeSpan.FromSeconds(1), new FakeTimeProvider(), NullLogger<NodePressure>.Instance);
    private TransportScenario? _scenario;
    private LedgerReader? _reader;
    private LedgerWriterRegistry? _writers;
    private JournalStore? _store;
    private ForwardedReads? _reads;
    private LedgerMapCommitter? _committer;
    private int _journalCalls;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private TransportScenario Scenario => _scenario!;

    private NodeId Self => Scenario.Store.NodeId;

    public async ValueTask InitializeAsync()
    {
        _scenario = await TransportScenario.CreateAsync();
        var register = await new InMemoryMetaStore().GetRegisterAsync(Guid.NewGuid(), "ledger-map", Token);
        _reader = new LedgerReader(await LedgerMapView.OpenAsync(register, Token), Scenario.Transport, TimeProvider.System, Self);
        _writers = new LedgerWriterRegistry(_reader, 32 << 20, LedgerWriterOptions.Default, TimeProvider.System, NullLoggerFactory.Instance);
        _store = new JournalStore(_writers, _archive, new JournalStoreOptions(), TimeProvider.System);
        _reads = new ForwardedReads(TestReplicaLimits.Options(), TestReplicaLimits.Of(), TimeProvider.System);
    }

    public async ValueTask DisposeAsync()
    {
        _reads!.Dispose();
        if (_committer is not null)
        {
            await _committer.DisposeAsync();
        }

        await _store!.DisposeAsync();
        await _writers!.DisposeAsync();
        await _reader!.DisposeAsync();
        await _scenario!.DisposeAsync();
        _archive.Dispose();
        _wire.Dispose();
        _leaks.Dispose();
    }

    /// <summary>OR-I14: a call keyed by another node is answered <c>NotThisNode</c> before the Journals, its record released.</summary>
    [Fact]
    public async Task ACallKeyedByAnotherNodeAnswersNotThisNode()
    {
        var host = await HostAsync(reachable: false);
        var other = new NodeId(Guid.NewGuid());

        Assert.Same(NotThisNode.Instance, (await host.WriteAsync(other, Entry("k"), Token)).Value);
        Assert.Same(NotThisNode.Instance, (await host.ReadAsync(other, "k", Token)).Value);
        Assert.Same(NotThisNode.Instance, (await host.ReadPageAsync(other, Guid.NewGuid(), 0, Token)).Value);
        Assert.Same(NotThisNode.Instance, await host.ReleaseSnapshotAsync(other, Guid.NewGuid(), Token));
    }

    /// <summary>
    /// orleans.md §7.2: once the node stops, a write and a read answer <c>Moved</c> naming no owner and a page
    /// <c>Expired</c>, before the Journals, the write's record released.
    /// </summary>
    [Fact]
    public async Task AStoppedNodeAnswersMovedNamingNoOwnerAndExpired()
    {
        var host = await HostAsync(reachable: false);
        Scenario.Local.Stop();

        Assert.Equal(new JournalMoved(Owner: null), (await host.WriteAsync(Self, Entry("k"), Token)).Value);
        Assert.Equal(new JournalMoved(Owner: null), (await host.ReadAsync(Self, "k", Token)).Value);
        Assert.Same(JournalForwardedPage.Expired.Instance, (await host.ReadPageAsync(Self, Guid.NewGuid(), 0, Token)).Value);
        Assert.Null(await host.ReleaseSnapshotAsync(Self, Guid.NewGuid(), Token));
    }

    /// <summary>
    /// OR-I10: while the node is under disk pressure, a write to a key whose open ledger the node is primary of is answered
    /// <c>Throttled</c> before the Journals; a write to a key whose ledger it only replicates reaches them.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OnlyAThrottledPrimarysWriteIsAnsweredThrottled(bool primary)
    {
        var host = await HostAsync(reachable: !primary, primary: primary ? Self : new NodeId(Guid.NewGuid()));
        _pressure.SetUnderPressure(true);

        var outcome = await host.WriteAsync(Self, Entry("k"), Token);

        Assert.Equal(primary ? new JournalForwardedWriteOutcome(LocalJournalWriteOutcome.Throttled.Instance) : new JournalMoved(Owner: null), outcome);
        Assert.Equal(primary ? 0 : 1, _journalCalls);
    }

    /// <summary>
    /// A key which is no journal key is refused as an argument before the Journals, for a write, its record released, and
    /// for a read.
    /// </summary>
    [Theory]
    [InlineData("empty")]
    [InlineData("lone surrogate")]
    [InlineData("too long")]
    public async Task AKeyWhichIsNoJournalKeyIsRefusedAsAnArgument(string defect)
    {
        var host = await HostAsync(reachable: false);
        var key = defect switch
        {
            "empty" => string.Empty,
            "lone surrogate" => "k\ud800",
            "too long" => new string('k', JournalRowKey.MaxKeyBytes + 1),
            _ => throw new ArgumentOutOfRangeException(nameof(defect), defect, "No such defect."),
        };

        Assert.Equal(JournalRefusal.Argument, Assert.IsType<JournalCallRefused>((await host.WriteAsync(Self, Entry("k") with { Key = key }, Token)).Value).Kind);
        Assert.Equal(JournalRefusal.Argument, Assert.IsType<JournalCallRefused>((await host.ReadAsync(Self, key, Token)).Value).Kind);
    }

    /// <summary>
    /// JS-I5: a record which fails its verification against the key's row key, one encoded for another key or one with a
    /// flipped byte, is refused as invalid before the Journals, naming its rejection, its record released.
    /// </summary>
    [Theory]
    [InlineData("another key")]
    [InlineData("flipped")]
    [InlineData("truncated")]
    public async Task ARecordWhichFailsItsVerificationIsRefusedAsInvalid(string defect)
    {
        var host = await HostAsync(reachable: false);
        var bytes = RecordBytes(defect == "another key" ? "another" : "k");
        if (defect == "flipped")
        {
            bytes[^1] ^= 0x01;
        }

        var entry = new JournalForwardedEntry { Key = "k", Record = _wire.Track(WireBytes.Of(defect == "truncated" ? bytes[..^1] : bytes)) };

        var refused = Assert.IsType<JournalCallRefused>((await host.WriteAsync(Self, entry, Token)).Value);

        Assert.Equal(JournalRefusal.Invalid, refused.Kind);
        Assert.False(string.IsNullOrEmpty(refused.Message));
    }

    /// <summary>A valid record reaches the Journals, which answer it; the host releases the record it received.</summary>
    [Fact]
    public async Task AValidRecordReachesTheJournals()
    {
        var host = await HostAsync(reachable: true);

        Assert.Equal(new JournalMoved(Owner: null), (await host.WriteAsync(Self, Entry("k"), Token)).Value);
        Assert.Equal(new JournalMoved(Owner: null), (await host.ReadAsync(Self, "k", Token)).Value);
        Assert.Equal(2, _journalCalls);
    }

    /// <summary>
    /// OR-I2: a call waits for the node's registration; its caller's cancellation meanwhile propagates, reaching no
    /// Journals, and the write's record is released.
    /// </summary>
    [Fact]
    public async Task ACancellationWhileTheCallWaitsForTheRegistrationPropagates()
    {
        await using var unregistered = await TransportScenario.CreateAsync(registered: false);
        var journals = new LocalJournals(_pressure) { BeforeCall = static () => throw new InvalidOperationException("A cancelled call reaches no Journals.") };
        var host = new JournalNodeHost(unregistered.Local, journals, _reads!);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);

        var write = host.WriteAsync(unregistered.Store.NodeId, Entry("k"), cancel.Token);
        var read = host.ReadAsync(unregistered.Store.NodeId, "k", cancel.Token);
        Assert.False(write.IsCompleted || read.IsCompleted);
        await cancel.CancelAsync();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write.WaitAsync(Bound, Token));
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(Bound, Token));
    }

    /// <summary>
    /// orleans.md §5.5: a serving node's release of a snapshot its reader abandoned releases it at once, its reservation
    /// returned, and its next page answers <c>Expired</c>.
    /// </summary>
    [Fact]
    public async Task AServingNodesReleaseReleasesTheSnapshotAtOnce()
    {
        var host = await HostAsync(reachable: false);
        var chunks = new[] { ArcBuffer.Wrap(new byte[JournalForwardedRead.PageBytes]), ArcBuffer.Wrap(new byte[JournalForwardedRead.PageBytes]) };
        var found = Assert.IsType<JournalForwardedRead.Found>((await _reads!.ReadAsync(
            _ => Task.FromResult<ForwardedReads.Source>(new ForwardedReads.Content(JournalVersion.Origin, Exists: true, 2L * JournalForwardedRead.PageBytes, ReplaceRequested: false, chunks)),
            _reads.DeadlineFromNow(),
            Token)).Value);
        found.FirstPage.Dispose();
        var snapshot = Assert.NotNull(found.Snapshot);

        Assert.Null(await host.ReleaseSnapshotAsync(Self, snapshot, Token).WaitAsync(Bound, Token));

        Assert.Equal((0, 0L), (_reads.Pinned, _reads.ReservedBytes));
        Assert.Same(JournalForwardedPage.Expired.Instance, (await host.ReadPageAsync(Self, snapshot, JournalForwardedRead.PageBytes, Token).WaitAsync(Bound, Token)).Value);
    }

    /// <summary>
    /// orleans.md §5.5: a forwarded read's deadline runs from the owner's receipt, before the wait for the registration: a
    /// read which waited out its whole deadline for the registration, and then finds its one slot held, answers <c>Busy</c>
    /// at once, without reading the journal.
    /// </summary>
    [Fact]
    public async Task AReadsDeadlineRunsFromItsReceiptBeforeTheRegistration()
    {
        var time = new FakeTimeProvider();
        await using var unregistered = await TransportScenario.CreateAsync(registered: false);
        using var reads = new ForwardedReads(TestReplicaLimits.Options(static options => options.Journals.MaxConcurrentHistoryReads = 1), TestReplicaLimits.Of(deadline: Bound), time);
        var history = new TaskCompletionSource<ForwardedReads.Source>(TaskCreationOptions.RunContinuationsAsynchronously);
        var holding = reads.ReadAsync(_ => history.Task, reads.DeadlineFromNow(), Token);
        var journals = new LocalJournals(_pressure) { BeforeCall = static () => throw new InvalidOperationException("A read past its deadline reaches no Journals.") };
        var host = new JournalNodeHost(unregistered.Local, journals, reads);

        var read = host.ReadAsync(unregistered.Store.NodeId, "k", Token);
        time.Advance(Bound);
        unregistered.Register();

        Assert.Same(JournalForwardedRead.Busy.Instance, (await read.WaitAsync(Bound, Token)).Value);
        history.SetResult(new JournalMoved(Owner: null));
        Assert.Equal(new JournalMoved(Owner: null), (await holding.WaitAsync(Bound, Token)).Value);
    }

    /// <summary>
    /// orleans.md §5.5, journals.md §8.1: a pre-admission <see cref="ArgumentException"/> from <c>JournalStore.WriteAsync</c>
    /// (such as an unregistered local-append stream) returns <see cref="JournalCallRefused"/> with
    /// <see cref="JournalRefusal.Argument"/> rather than settling as <c>Ambiguous</c>.
    /// </summary>
    [Fact]
    public async Task PreAdmissionArgumentExceptionReturnsRefusedArgument()
    {
        var host = await HostAsync(reachable: true);
        var stream = LocalStreamName.Parse("unregistered.stream");
        using var payload = ArcBuffer.Wrap("x"u8.ToArray());
        using var record = JournalEntryRecord.Append(
            JournalRowKey.Encode("k"),
            JournalVersion.Origin,
            payload,
            [new LocalAppendRecord(stream, "wk"u8.ToArray())]);
        using var raw = record.ToArcBuffer();
        var forwarded = new JournalForwardedEntry { Key = "k", Record = _wire.Track(WireBytes.Of(raw.ToArray())) };

        var outcome = await host.WriteAsync(Self, forwarded, Token);

        var refused = Assert.IsType<JournalCallRefused>(outcome.Value);
        Assert.Equal(JournalRefusal.Argument, refused.Kind);
        Assert.Contains("unregistered.stream", refused.Message, StringComparison.Ordinal);
    }

    private static byte[] RecordBytes(string key)
    {
        using var record = JournalEntryRecord.Delete(JournalRowKey.Encode(key), JournalVersion.Origin, DateTimeOffset.UnixEpoch);
        using var bytes = record.ToArcBuffer();
        return bytes.ToArray();
    }

    /// <summary>Returns a forwarded <c>Delete</c> of <paramref name="key"/>, whose record the test's ledger tracks.</summary>
    private JournalForwardedEntry Entry(string key) => new() { Key = key, Record = _wire.Track(WireBytes.Of(RecordBytes(key))) };

    /// <summary>
    /// Returns a host over the node's Journals, serving over a map view of their own which lists one open ledger over every
    /// key whose primary is <paramref name="primary"/>, or none; the Journals throw for any call unless
    /// <paramref name="reachable"/>, and count each call which reaches them.
    /// </summary>
    private async Task<JournalNodeHost> HostAsync(bool reachable, NodeId? primary = null)
    {
        var journals = new LocalJournals(_pressure);
        var register = await new InMemoryMetaStore().GetRegisterAsync(Guid.NewGuid(), "ledger-map", Token);
        var view = await LedgerMapView.OpenAsync(register, Token);
        if (primary is { } node)
        {
            _committer = new LedgerMapCommitter(register, 1 << 20, view);
            _ = Assert.IsType<LedgerMapCommitOutcome.Applied>((await _committer.CommitAsync(
                new LedgerMapChange.Install([], [new LedgerOpening(KeyRange.Root, [Self, new NodeId(Guid.NewGuid()), new NodeId(Guid.NewGuid())], 4, new PrimaryClaim(node, SequencerInstanceId.New()))], 1),
                Token)).Value);
        }

        Assert.True(journals.Serve(_store!, view, Self));
        journals.BeforeCall = () =>
        {
            _journalCalls++;
            return reachable ? Task.CompletedTask : throw new InvalidOperationException("A refused call reaches no Journals.");
        };
        return new JournalNodeHost(Scenario.Local, journals, _reads!);
    }
}
