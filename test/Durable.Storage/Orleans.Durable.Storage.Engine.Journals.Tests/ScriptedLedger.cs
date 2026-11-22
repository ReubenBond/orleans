using System.Buffers;
using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Archive.Memory;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Engine.Journals.Tests;

/// <summary>
/// A scripted ledger for admission tests. The admission runs inline (<c>runInline</c>), and each proposal's outcome
/// completes with synchronous continuations, so a write, a load's completion, or a test's decision of a proposal returns
/// only once the admission has done all it does in response: the tests check what is pending without waiting.
/// </summary>
/// <remarks>
/// Each batch the admission proposes starts a segment of its own, unless the test continues the segment
/// (<see cref="StartEachSegment"/>), and is decoded and recorded as the next slot's proposal, whose outcome the test
/// decides. <see cref="DrainDurable"/> drains every <c>Durable</c> row into the ledger's memory store, which checks each
/// key's token chain and the store's limits (HI-I4), the oracle of the tokens admission plans, and
/// <see cref="CheckVersions"/> checks each key's rows follow one another. A key's state and planning state load, by
/// default, as a history read would give them (JS-I9): from the rows the test scripted for it and then its rows of every
/// proposal decided <c>Durable</c>. Failures of the checks a proposal runs are recorded, thrown to the admission, and
/// rethrown when the ledger is disposed, so they fail the test rather than become an outcome; disposal also checks that
/// every entry the admission accepted left its queued bytes. The admission's clock is a fake one the test advances, from
/// <see cref="Epoch"/>.
/// </remarks>
internal sealed class ScriptedLedger : IAsyncDisposable
{
    public static readonly LedgerId Id = new(7);

    /// <summary>The time the admission's clock starts at.</summary>
    public static readonly DateTimeOffset Epoch = new(2026, 11, 13, 0, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly Lock _lock = new();
    private readonly List<ScriptedProposal> _proposals = [];
    private readonly Dictionary<JournalRowKey, Func<CancellationToken, ValueTask<JournalLoadOutcome>>> _loads = [];
    private readonly Dictionary<JournalRowKey, ArchiveRow[]> _heldRows = [];
    private readonly ConcurrentQueue<Exception> _failures = new();
    private readonly long _largestBatch;
    private int _loadsMade;
    private int _abandoned;
    private readonly LedgerSegmentView _view = new(Id);
    private int _batchesCreated;
    private LedgerSegmentDictionary? _dictionary;
    private long _segmentStart;
    private bool _endSegment;

    public ScriptedLedger(int window = 4, int maxBatchLength = int.MaxValue, JournalStoreOptions? options = null, KeyRange? range = null, IArchiveStore? store = null, InMemoryArchiveStore? memory = null)
    {
        Range = range ?? KeyRange.Root;
        MaxBatchLength = maxBatchLength;
        options ??= new JournalStoreOptions();
        _largestBatch = LedgerAdmission.LargestBatch(options);
        Memory = memory ?? new InMemoryArchiveStore();
        Store = store ?? Memory;
        Admission = new LedgerAdmission(Id, Range, window, maxBatchLength, options, Store, Clock, LoadAsync, CreateBatch, Propose, Abandon, runInline: true);
    }

    /// <summary>Gets the memory store <see cref="DrainDurable"/> drains into, which the ledger disposes.</summary>
    public InMemoryArchiveStore Memory { get; }

    /// <summary>Gets the archive store whose pure members admission uses: <see cref="Memory"/> unless the test gave another.</summary>
    public IArchiveStore Store { get; }

    /// <summary>
    /// Gets or sets a value indicating whether each batch starts a segment of its own; otherwise a batch continues the
    /// segment the last began, until <see cref="EndSegment"/>.
    /// </summary>
    public bool StartEachSegment { get; set; } = true;

    public LedgerAdmission Admission { get; }

    public KeyRange Range { get; }

    public int MaxBatchLength { get; }

    /// <summary>Gets the admission's clock, which starts at <see cref="Epoch"/>.</summary>
    public FakeTimeProvider Clock { get; } = new(Epoch);

    /// <summary>Gets the number of loads made.</summary>
    public int Loads => Volatile.Read(ref _loadsMade);

    /// <summary>Gets the number of times the admission abandoned the writer.</summary>
    public int Abandoned => Volatile.Read(ref _abandoned);

    /// <summary>Gets the number of batches the admission created.</summary>
    public int BatchesCreated => Volatile.Read(ref _batchesCreated);

    /// <summary>Gets or sets an exception the next batch's creation throws.</summary>
    public Exception? NextCreateBatchFailure { get; set; }

    /// <summary>Gets or sets an exception the next proposal throws synchronously, before it is recorded.</summary>
    public Exception? NextProposeFailure { get; set; }

    /// <summary>Gets or sets an exception the next proposal's outcome faults with; the proposal is recorded.</summary>
    public Exception? NextOutcomeFailure { get; set; }

    /// <summary>Gets the proposals made, in order.</summary>
    public IReadOnlyList<ScriptedProposal> Proposals
    {
        get
        {
            lock (_lock)
            {
                return [.. _proposals];
            }
        }
    }

    /// <summary>Gets proposal <paramref name="index"/>, from 0, which the admission has made.</summary>
    public ScriptedProposal Proposal(int index)
    {
        var proposals = Proposals;
        Assert.True(index < proposals.Count, $"The admission made {proposals.Count} proposals, not proposal {index}.");
        return proposals[index];
    }

    /// <summary>
    /// Scripts the state of the records' key, which every record shares, as the fold of the rows of
    /// <paramref name="records"/>, applied in order from <c>Origin</c>, one row per slot of ledger 1 from slot 100. The
    /// ledger disposes the records.
    /// </summary>
    public void Holds(params JournalEntryRecord[] records)
    {
        var key = records[0].Key;
        var rows = new ArchiveRow[records.Length];
        Span<byte> rowBodyHead = stackalloc byte[JournalEntry.TombstoneLength];
        for (var index = 0; index < records.Length; index++)
        {
            using var record = records[index];
            Assert.Equal(key, record.Key);
            var tag = JournalSamples.At(100 + index);
            using var body = ArcBuffer.Wrap((byte[])[.. record.WriteRowBodyHead(rowBodyHead), .. record.Payload.ToArray()]);
            rows[index] = ArchiveRow.Live(tag, record.Entry.RowKind, record.Entry.ExpectedVersion.Next().ToRowVersion(), new LedgerSegmentId(tag.Ledger, tag.Slot), default, body);
        }

        _heldRows[key] = rows;
    }

    /// <summary>Makes the next batch start a segment, as a writer's segment end does.</summary>
    public void EndSegment() => _endSegment = true;

    /// <summary>
    /// Drains every <c>Durable</c> row of each key into <see cref="Memory"/>, one key's write per segment, in slot order:
    /// the store checks each write's stored token against the key's chain and its commands against its limits, and
    /// throws <see cref="ArchiveCorruptException"/> on a break (HI-I4).
    /// </summary>
    public void DrainDurable()
    {
        var rows = new List<(string Key, ArchiveRow Row)>();
        try
        {
            foreach (var (proposal, slot) in Proposals.Select(static (proposal, slot) => (proposal, slot)))
            {
                if (proposal.Outcome is not { IsCompletedSuccessfully: true, Result.Value: ProposalOutcome.Durable })
                {
                    continue;
                }

                for (var index = 0; index < proposal.Rows.Count; index++)
                {
                    var row = proposal.Rows[index];
                    rows.Add((row.Key, JournalSamples.Live(new Tag(Id, new Slot(slot), index), row.Kind, row.Version, row.Body, row.Token, row.Segment)));
                }
            }

            foreach (var key in rows.Select(static row => row.Key).Distinct(StringComparer.Ordinal))
            {
                var keyRows = rows.Where(row => row.Key == key).Select(static row => row.Row).ToArray();
                for (var start = 0; start < keyRows.Length;)
                {
                    var end = start + 1;
                    while (end < keyRows.Length && keyRows[end].Segment == keyRows[start].Segment)
                    {
                        end++;
                    }

                    Memory.Drain(JournalRowKey.Encode(key).Memory, keyRows.AsSpan(start, end - start));
                    start = end;
                }
            }
        }
        finally
        {
            JournalSamples.Release(rows.Select(static row => row.Row));
        }
    }

    /// <summary>Scripts <paramref name="key"/>'s loads to call <paramref name="load"/>.</summary>
    public void LoadsFrom(JournalRowKey key, Func<CancellationToken, ValueTask<JournalLoad>> load) =>
        _loads[key] = async disposal => new JournalLoadOutcome(new JournalLoadOutcome.Loaded(await load(disposal)));

    /// <summary>
    /// Scripts <paramref name="key"/>'s loads to call <paramref name="load"/> for the state, with a planning state of no
    /// recorded segment from <c>Origin</c>, as for a key no live row records.
    /// </summary>
    public void LoadsFrom(JournalRowKey key, Func<CancellationToken, ValueTask<JournalState?>> load) =>
        LoadsFrom(key, async disposal => new JournalLoad(await load(disposal), JournalPlanning.Unrecorded(Store, default)));

    /// <summary>Scripts <paramref name="key"/>'s loads to answer <paramref name="load"/>'s outcome, a <c>Moved</c> among them.</summary>
    public void LoadsOutcomeFrom(JournalRowKey key, Func<CancellationToken, ValueTask<JournalLoadOutcome>> load) => _loads[key] = load;

    /// <summary>
    /// Checks that each key's rows of every proposal, whatever its outcome, follow one another from <c>Origin</c>: each
    /// row's version is the next of the one before it.
    /// </summary>
    public void CheckVersions()
    {
        var versions = new Dictionary<string, JournalVersion>(StringComparer.Ordinal);
        var proposals = Proposals;
        for (var slot = 0; slot < proposals.Count; slot++)
        {
            foreach (var row in proposals[slot].Rows)
            {
                var before = versions.GetValueOrDefault(row.Key, JournalVersion.Origin);
                if (row.Version != before.Next())
                {
                    throw new InvalidOperationException($"The row of {row.Key} at slot {slot} has version {row.Version}, which does not follow {before}.");
                }

                versions[row.Key] = row.Version;
            }
        }
    }

    /// <summary>
    /// Admits <paramref name="record"/>, disposing the caller's record. The returned task completes when the admission
    /// answers the write.
    /// </summary>
    public Task<JournalWriteOutcome> WriteAsync(JournalEntryRecord record) => WriteCancellablyAsync(record, CancellationToken.None);

    /// <summary>Admits <paramref name="record"/>, disposing the caller's record, until <paramref name="cancellationToken"/> stops the wait.</summary>
    public Task<JournalWriteOutcome> WriteCancellablyAsync(JournalEntryRecord record, CancellationToken cancellationToken)
    {
        using (record)
        {
            return Admission.AdmitAsync(record, cancellationToken);
        }
    }

    /// <summary>Admits <paramref name="record"/>, which the caller keeps and disposes.</summary>
    public Task<JournalWriteOutcome> Admit(JournalEntryRecord record) => Admission.AdmitAsync(record, CancellationToken.None);

    /// <summary>Waits until every <c>Durable</c> batch below <paramref name="below"/> is applied, or the admission fences.</summary>
    public Task<bool> WaitApplied(Slot below) => Admission.WaitAppliedAsync(below, CancellationToken.None);

    /// <summary>Admits an <c>Expired</c> of <paramref name="key"/>'s deleted state at <paramref name="expected"/>, as the expiry scan does.</summary>
    public Task<JournalWriteOutcome> ExpireAsync(JournalVersion expected, JournalRowKey? key = null) =>
        Admission.ExpireAsync(key ?? JournalSamples.Key, expected);

    /// <summary>Decides proposal <paramref name="index"/> <c>Durable</c> at its slot.</summary>
    public void Durable(int index) => Proposal(index).Decide(new ProposalOutcome(new ProposalOutcome.Durable(new Slot(index))));

    public void Fenced(int index) => Proposal(index).Decide(new ProposalOutcome(default(ProposalOutcome.Fenced)));

    public void Ambiguous(int index) => Proposal(index).Decide(new ProposalOutcome(default(ProposalOutcome.Ambiguous)));

    /// <summary>
    /// Disposes the admission, deciding <c>Fenced</c> every proposal the test left undecided, as when an assertion
    /// failed; releases the scripted rows; and fails with any check a proposal failed.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        var disposal = Admission.DisposeAsync();
        foreach (var proposal in Proposals)
        {
            proposal.Abandon();
        }

        await disposal.AsTask().WaitAsync(Timeout, TimeProvider.System, CancellationToken.None);
        Assert.Equal(0, Admission.QueuedBytes);
        foreach (var rows in _heldRows.Values)
        {
            JournalSamples.Release(rows);
        }

        _view.Dispose();
        Memory.Dispose();

        if (!_failures.IsEmpty)
        {
            throw new AggregateException("A proposal's checks failed.", _failures);
        }
    }

    /// <summary>Returns the journal key of a row key.</summary>
    private static string JournalKeyOf(ReadOnlySpan<byte> rowKey) => Encoding.UTF8.GetString(rowKey[JournalRowKey.PrefixLength..]);

    /// <summary>
    /// Takes the batch's bytes, as the next slot's, and decodes its rows through the ledger's segment view: their keys,
    /// kinds, versions, tokens, segments, and bodies.
    /// </summary>
    private ScriptedProposal Record(LedgerBatch batch, Slot slot)
    {
        Assert.Equal(batch.DeclaredRowCount, batch.RowsBegun);
        var length = batch.Length;
        Assert.True(length <= _largestBatch, $"A batch of {length} bytes passes the largest batch the options allow, {_largestBatch} bytes (journals.md §8.4).");
        using var bytes = batch.Complete();
        Assert.Equal(length, bytes.Length);
        Assert.Null(BatchView.Decode(bytes, out var view));
        Assert.Null(_view.Advance(slot, view));
        Assert.Equal(batch.Segment, _view.Segment);
        var rows = new List<ScriptedRow>();
        foreach (var row in _view.Rows)
        {
            rows.Add(new ScriptedRow(JournalKeyOf(row.Key), row.Kind, JournalVersion.FromRowVersion(row.Version), row.Token, _view.Segment, row.Body.ToArray()));
        }

        Assert.Equal(batch.DeclaredRowCount, rows.Count);
        return new ScriptedProposal(rows);
    }

    private void Abandon() => Interlocked.Increment(ref _abandoned);

    private ValueTask<JournalLoadOutcome> LoadAsync(JournalRowKey key, CancellationToken disposal)
    {
        _ = Interlocked.Increment(ref _loadsMade);
        return _loads.TryGetValue(key, out var load) ? load(disposal) : ValueTask.FromResult(new JournalLoadOutcome(new JournalLoadOutcome.Loaded(Fold(key))));
    }

    /// <summary>Loads <paramref name="key"/> from its scripted rows and then its rows of every <c>Durable</c> proposal, in tag order.</summary>
    private JournalLoad Fold(JournalRowKey key)
    {
        var rows = new List<ArchiveRow>();
        try
        {
            foreach (var held in _heldRows.TryGetValue(key, out var scripted) ? scripted : [])
            {
                rows.Add(held.Retain());
            }

            foreach (var proposal in Proposals)
            {
                if (proposal.Outcome is not { IsCompletedSuccessfully: true, Result.Value: ProposalOutcome.Durable durable })
                {
                    continue;
                }

                for (var index = 0; index < proposal.Rows.Count; index++)
                {
                    var row = proposal.Rows[index];
                    if (row.Key == key.ToString())
                    {
                        rows.Add(JournalSamples.Live(new Tag(Id, durable.Slot, index), row.Kind, row.Version, row.Body, row.Token, row.Segment));
                    }
                }
            }

            return JournalSamples.Load(null, [.. rows], Store);
        }
        finally
        {
            JournalSamples.Release(rows);
        }
    }

    /// <summary>Creates a batch which starts a segment of its own, as the writer's creation would at a segment's end.</summary>
    private ValueTask<LedgerBatch> CreateBatch(int rowCount, int maxLength, CancellationToken cancellationToken)
    {
        _ = Interlocked.Increment(ref _batchesCreated);
        if (NextCreateBatchFailure is { } failure)
        {
            NextCreateBatchFailure = null;
            throw failure;
        }

        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxLength, MaxBatchLength);
        long slot;
        lock (_lock)
        {
            slot = _proposals.Count;
        }

        LedgerBatchSegment segment;
        if (StartEachSegment || _endSegment || _dictionary is null)
        {
            _endSegment = false;
            _dictionary = new LedgerSegmentDictionary();
            _segmentStart = slot;
            segment = new LedgerBatchSegment(IsStart: true, Offset: 0, OrdinalBase: 0, _dictionary);
        }
        else
        {
            segment = new LedgerBatchSegment(IsStart: false, Offset: (ulong)(slot - _segmentStart), OrdinalBase: (ulong)_dictionary.Count, _dictionary);
        }

#pragma warning disable CA2000 // The admission, which receives the batch, disposes it.
        var batch = new LedgerBatch(Range, maxLength, rowCount, segment, segmentId: new LedgerSegmentId(Id, new Slot(_segmentStart)), slot: new Slot(slot));
#pragma warning restore CA2000
        return ValueTask.FromResult(batch);
    }

    private ValueTask<ProposalOutcome> Propose(LedgerBatch batch, CancellationToken cancellationToken)
    {
        if (NextProposeFailure is { } failure)
        {
            NextProposeFailure = null;
            throw failure;
        }

        ScriptedProposal proposal;
        try
        {
            lock (_lock)
            {
                proposal = Record(batch, new Slot(_proposals.Count));
            }
        }
        catch (Exception exception)
        {
            _failures.Enqueue(exception);
            throw;
        }

        lock (_lock)
        {
            _proposals.Add(proposal);
        }

        if (NextOutcomeFailure is { } outcomeFailure)
        {
            NextOutcomeFailure = null;
            proposal.Fault(outcomeFailure);
        }

        return new ValueTask<ProposalOutcome>(proposal.Outcome);
    }
}

/// <summary>One row of a proposed batch: its journal key, row kind, version after it, stored token, segment, and native body.</summary>
internal sealed record ScriptedRow(string Key, RowKind Kind, JournalVersion Version, RowToken Token, LedgerSegmentId Segment, byte[] Body);

/// <summary>One proposal: its rows, and the outcome the test decides, whose continuations run synchronously.</summary>
internal sealed class ScriptedProposal(IReadOnlyList<ScriptedRow> rows)
{
    private readonly TaskCompletionSource<ProposalOutcome> _outcome = new();

    public IReadOnlyList<ScriptedRow> Rows { get; } = rows;

    public Task<ProposalOutcome> Outcome => _outcome.Task;

    /// <summary>Gets the count of each row's version, the version after it.</summary>
    public IEnumerable<ulong> Counts => Rows.Select(static row => row.Version.Count);

    public void Decide(ProposalOutcome outcome) => _outcome.SetResult(outcome);

    public void Fault(Exception exception) => _outcome.SetException(exception);

    /// <summary>Decides the proposal <c>Fenced</c> unless the test decided it.</summary>
    public void Abandon() => _ = _outcome.TrySetResult(new ProposalOutcome(default(ProposalOutcome.Fenced)));
}
