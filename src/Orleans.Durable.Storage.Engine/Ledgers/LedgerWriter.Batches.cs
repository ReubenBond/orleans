using System.Diagnostics;
using System.Globalization;

namespace Orleans.Durable.Storage.Engine.Ledgers;

/// <summary>
/// The writer's batches (ledgers.md §2.4, §8.2): slots at creation, one creation at a time, the proposal budget, and the
/// segments its batches form.
/// </summary>
public sealed partial class LedgerWriter
{
    // The creations waiting for their turn, in call order: each waits until the batch created before it is proposed and
    // its maxLength fits the proposal budget beside the batches created and not yet decided.
    private readonly LinkedList<Creation> _creations = [];

    // The slots AcquireAsync acquired and AppendAsync has not yet appended at (ledger-protocol.md §2.3).
    private readonly Dictionary<long, SlotGrant> _acquired = [];

    // The writer's dictionary of its open segment (ledgers.md §8.2).
    private readonly LedgerSegmentDictionary _dictionary = new();

    // A creation which holds its turn: it is acquiring its slot. Under the lock, as is every field below.
    private bool _creating;

    // The creation number of the batch created last while it holds a slot and is not yet proposed or disposed, or 0; and
    // the last creation number taken.
    private long _unproposed;
    private long _created;

    // The bytes of the batches created and not yet decided: each created batch's maxLength, shrunk to its length once it
    // is proposed.
    private long _reservedBytes;

    // The bytes of every batch the writer proposed at a slot, its markers included.
    private long _proposedBytes;

    // Whether created batches take slots: until StopAdmittingAsync, or until a created batch was disposed unproposed,
    // which abandons the writer.
    private bool _admittingBatches = true;
    private bool _abandoning;

    // The open segment: its first slot, its batches' bytes, whether it holds a batch with rows and since when, and
    // whether its age ended it, so the next batch starts a segment and, while the writer is idle, a marker is owed.
    private Slot _segmentStart;
    private long _segmentBytes;
    private long? _firstRowsAt;
    private bool _segmentEnded;
    private bool _segmentReported;
    private ITimer? _ageTimer;

    // The segments the writer ended which start at or above the last watermark ForgetDrainedBelow was given, oldest first.
    private readonly Queue<EndedSegment> _endedSegments = new();

    // The markers the writer proposes itself for aged segments, which its disposal awaits.
    private Task? _agedMarkers;

    /// <summary>
    /// Gets the age at which the writer ends its segments, counted from each segment's first batch with rows: drawn once,
    /// when the writer is created, between nine tenths of <see cref="LedgerWriterOptions.MaxSegmentAge"/> and it.
    /// </summary>
    internal TimeSpan DrawnSegmentAge { get; private set; }

    /// <summary>Gets the bytes of the batches created and not yet decided.</summary>
    internal long ReservedBytes
    {
        get
        {
            lock (_lock)
            {
                return _reservedBytes;
            }
        }
    }

    /// <summary>
    /// Gets the bytes of every batch the writer has proposed at a slot, its markers included: the ledger's load, whose
    /// rate over a window its node reports (orleans.md §5.6). It only grows.
    /// </summary>
    public long ProposedBytes
    {
        get
        {
            lock (_lock)
            {
                return _proposedBytes;
            }
        }
    }

    /// <summary>
    /// Gets the first slot of the writer's open segment: every batch below it belongs to a closed segment (ledgers.md §2.4),
    /// ended by its size, its keys, or its age. A drain is due once a closed segment lies above the ledger's watermark and
    /// below <see cref="KnownContiguousTail"/>; both are known here, with no I/O.
    /// </summary>
    public Slot OpenSegmentStart
    {
        get
        {
            lock (_lock)
            {
                return _segmentStart;
            }
        }
    }

    /// <summary>Gets the contiguous tail the writer knows, every slot below it decided (LP-I11), with no I/O.</summary>
    public Slot KnownContiguousTail => Ledger.KnownContiguousTail;

    /// <summary>
    /// Answers what the writer proposed in the segments which start at or above <paramref name="watermark"/>: the bytes of
    /// those batches and the age of the oldest of them with rows (orleans.md §5.9, §8). It changes nothing.
    /// </summary>
    /// <param name="watermark">
    /// The ledger's drain watermark as its caller knows it, at or below <see cref="OpenSegmentStart"/>: every segment which
    /// starts below it is drained.
    /// </param>
    /// <returns>The undrained bytes and the age of the oldest undrained batch with rows, measured now.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="watermark"/> is above <see cref="OpenSegmentStart"/>.</exception>
    public LedgerUndrained ObserveUndrained(Slot watermark)
    {
        lock (_lock)
        {
            ThrowIfAboveOpenSegmentLocked(watermark);
            long? proposedBefore = null;
            long? firstRowsAt = null;
            foreach (var segment in _endedSegments)
            {
                if (segment.Start < watermark)
                {
                    continue;
                }

                proposedBefore ??= segment.ProposedBefore;
                if (segment.FirstRowsAt is { } since)
                {
                    firstRowsAt = since;
                    break;
                }
            }

            // Every batch of the ended segments is proposed, so the bytes proposed outside the open segment precede it.
            proposedBefore ??= _proposedBytes - _segmentBytes;
            firstRowsAt ??= _firstRowsAt;
            return new LedgerUndrained(_proposedBytes - proposedBefore.Value, firstRowsAt is { } oldest ? _timeProvider.GetElapsedTime(oldest) : null);
        }
    }

    /// <summary>
    /// Forgets the ended segments which start below <paramref name="watermark"/>, which a drain passed, so the writer holds
    /// one small entry per segment it ended until its drain completes (orleans.md §5.9). A forgotten segment stays
    /// forgotten: a later, lower watermark restores nothing.
    /// </summary>
    /// <param name="watermark">The ledger's drain watermark, at or below <see cref="OpenSegmentStart"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="watermark"/> is above <see cref="OpenSegmentStart"/>.</exception>
    public void ForgetDrainedBelow(Slot watermark)
    {
        lock (_lock)
        {
            ThrowIfAboveOpenSegmentLocked(watermark);
            while (_endedSegments.TryPeek(out var drained) && drained.Start < watermark)
            {
                _ = _endedSegments.Dequeue();
            }

            if (_endedSegments.Count == 0)
            {
                _endedSegments.TrimExcess();
            }
        }
    }

    /// <summary>Gets the number of ended segments the writer holds, which tests read.</summary>
    internal int EndedSegments
    {
        get
        {
            lock (_lock)
            {
                return _endedSegments.Count;
            }
        }
    }

    /// <summary>Gets the number of keys the writer's open segment defined, which its dictionary holds; for tests.</summary>
    internal int DictionaryKeys => _dictionary.Count;

    /// <summary>Gets the number of creations waiting for their turn.</summary>
    internal int WaitingCreations
    {
        get
        {
            lock (_lock)
            {
                return _creations.Count;
            }
        }
    }


    /// <summary>
    /// Gets or sets a value indicating whether an append accepts a slot it already appended at: the mutant of
    /// ledger-protocol.md §2.3's precondition, which the conformance tests set to show the oracle catches it.
    /// </summary>
    internal bool AcceptsSlotTwiceMutant { get; set; }

    /// <summary>Gets a value indicating whether a batch created now takes a slot. Under the lock.</summary>
    private bool AdmitsBatchesLocked =>
        _admittingBatches && !_abandoning && !Sequencer.IsStopped && Volatile.Read(ref _disposed) == 0;

    /// <summary>
    /// Creates a batch of <paramref name="rowCount"/> rows at the writer's next slot, with its segment state (ledgers.md
    /// §2.4, §8.2). It waits until the batch the writer created before is proposed, <paramref name="maxLength"/> fits
    /// <see cref="LedgerWriterOptions.MaxProposalBytes"/> beside the batches created and not yet decided, and the window
    /// has room; creations take their turns in call order. The caller writes the batch's rows and proposes it
    /// (<see cref="ProposeAsync"/>), or disposes it, which abandons the writer: every created batch is decided in slot
    /// order, or no later batch is (LG-I12).
    /// </summary>
    /// <param name="rowCount">The rows the batch holds, at least 1 and at most <see cref="LedgerWriterOptions.MaxSegmentKeys"/>.</param>
    /// <param name="maxLength">
    /// The most bytes the batch will hold, which the caller bounds by its rows (<see cref="LedgerBatch.MaxRowLength"/>,
    /// <see cref="LedgerBatch.MaxHeaderLength"/>): the creation reserves it from the proposal budget, it chooses the
    /// batch's pages, and the batch refuses a row which would pass it.
    /// </param>
    /// <param name="cancellationToken">
    /// While the call waits, withdraws it: it creates nothing and takes no slot. A caller holds no lock of its own across
    /// the call, which may wait for the caller's own earlier proposals to be decided.
    /// </param>
    /// <returns>
    /// The batch, which the caller disposes. When the lifetime has stopped, which the call checks before each wait and
    /// which ends every wait, the batch takes no slot (<see cref="LedgerBatch.Slot"/> is <see langword="null"/>), and its
    /// proposal completes <see cref="ProposalOutcome.Fenced"/> at once.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="rowCount"/> is below 1 or above <see cref="LedgerWriterOptions.MaxSegmentKeys"/>, or
    /// <paramref name="maxLength"/> is below <see cref="LedgerBatch.MaxHeaderLength"/> of <paramref name="rowCount"/> or
    /// above the replica's payload limit or <see cref="LedgerWriterOptions.MaxProposalBytes"/>. Nothing was created.
    /// </exception>
    /// <exception cref="InvalidOperationException">The writer only appends.</exception>
    /// <exception cref="ObjectDisposedException">The writer was disposed.</exception>
    public ValueTask<LedgerBatch> CreateBatchAsync(int rowCount, int maxLength, CancellationToken cancellationToken = default)
    {
        var serving = Proposing;
        var host = serving.Host;
        ArgumentOutOfRangeException.ThrowIfLessThan(rowCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rowCount, host.Options.MaxSegmentKeys);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, LedgerBatch.MaxHeaderLength(rowCount));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxLength, host.MaxBatchLength);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxLength, host.Options.MaxProposalBytes);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<LedgerBatch>(cancellationToken);
        }

        // Fast path: when idle and within MaxProposalBytes, claim the creation turn directly without allocating a
        // queued Creation TCS, LinkedListNode, or cancellation registration.
        var claimed = false;
        var slotless = false;
        Creation? queued = null;
        lock (_lock)
        {
            if (!AdmitsBatchesLocked && _creations.Count == 0)
            {
                slotless = true;
            }
            else if (AdmitsBatchesLocked
                && !_segmentEnded
                && !_creating
                && _unproposed == 0
                && _creations.Count == 0
                && _reservedBytes + maxLength <= host.Options.MaxProposalBytes)
            {
                _creating = true;
                _reservedBytes += maxLength;
                Debug.Assert(_reservedBytes <= host.Options.MaxProposalBytes, "The batches created and not yet decided stay within MaxProposalBytes (ledgers.md §8.7).");
                claimed = true;
            }
            else
            {
                queued = new Creation(this, rowCount, maxLength, isMarker: false);
                queued.Node = _creations.AddLast(queued);
                PumpCreations();
            }
        }

        if (slotless)
        {
#pragma warning disable CA2000 // Ownership of the batch transfers to the caller.
            return ValueTask.FromResult(Slotless(serving, rowCount, maxLength));
#pragma warning restore CA2000
        }

        if (claimed)
        {
            return CreateClaimedAsync(serving, rowCount, maxLength, cancellationToken);
        }

        return CreateAsync(queued!, cancellationToken);
    }

    /// <summary>
    /// Ends the open segment with a marker batch the call creates, in turn with its callers' creations, and proposes
    /// (ledgers.md §2.4): once the marker is <see cref="ProposalOutcome.Durable"/> at <c>Slot</c>, the segment before it is
    /// closed and ends at <c>Slot</c>, so a caller which needs the open segment's rows complete now takes them below it.
    /// </summary>
    /// <param name="cancellationToken">
    /// Before the marker is created, withdraws the call; afterwards it stops only the caller's wait.
    /// </param>
    /// <returns>The marker's outcome.</returns>
    /// <exception cref="InvalidOperationException">The writer only appends.</exception>
    /// <exception cref="ObjectDisposedException">The writer was disposed.</exception>
    public ValueTask<ProposalOutcome> EndSegmentAsync(CancellationToken cancellationToken = default)
    {
        _ = Proposing;
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return cancellationToken.IsCancellationRequested
            ? ValueTask.FromCanceled<ProposalOutcome>(cancellationToken)
            : ProposeMarkerAsync(Enqueue(rowCount: 0, LedgerBatch.MaxHeaderLength(0), isMarker: true), cancellationToken);
    }

    /// <summary>
    /// Acquires the next slot (ledger-protocol.md §2.3), granted in request order, waiting while <c>K</c> slots are
    /// acquired and not completed (LP-I2), and raises the known contiguous tail to <c>max(0, p + 1 − K)</c> (LP-I11). The
    /// caller appends at it (<see cref="AppendAsync"/>) or stops the lifetime (LP-I13).
    /// </summary>
    /// <param name="cancellationToken">While the call waits, withdraws it, consuming no slot.</param>
    /// <returns>The slot, or <see langword="null"/> when the lifetime has stopped, which acquires nothing.</returns>
    /// <exception cref="InvalidOperationException">
    /// The writer proposes batches, whose slots its creations take: a payload appended here would lie in a segment.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The writer was disposed.</exception>
    internal async ValueTask<Slot?> AcquireAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfProposing();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (await AcquireGrantAsync(cancellationToken).ConfigureAwait(false) is not { } grant)
        {
            return null;
        }

        lock (_lock)
        {
            _acquired.Add(grant.Slot.Value, grant);
        }

        return grant.Slot;
    }

    /// <summary>
    /// Called when <paramref name="batch"/>, which this writer created, is disposed without being proposed: a batch which
    /// holds a slot abandons the writer, since no later batch can then be decided (LG-I12), and releases its reservation
    /// and its turn. The writer stops giving later creations slots before it lets them in, so none takes the slot after
    /// the abandoned one.
    /// </summary>
    internal void OnUnproposed(LedgerBatch batch)
    {
        bool abandon;
        lock (_lock)
        {
            if (_unproposed == 0 || _unproposed != batch.Creation)
            {
                return;
            }

            _unproposed = 0;
            _reservedBytes -= batch.MaxLength;
            abandon = Volatile.Read(ref _disposed) == 0;
            _abandoning |= abandon;
            PumpCreations();
        }

        if (abandon)
        {
            Abandon();
        }
    }

    /// <summary>
    /// Takes a proposed batch out of its creation's turn: its reservation shrinks to its length, and it joins its
    /// segment's bytes. Returns the bytes its resolution releases once it ends.
    /// </summary>
    private long OnProposed(LedgerBatch batch)
    {
        lock (_lock)
        {
            if (_unproposed == 0 || _unproposed != batch.Creation)
            {
                return 0;
            }

            _unproposed = 0;
            _reservedBytes -= batch.MaxLength - batch.Length;
            _proposedBytes += batch.Length;
            if (batch.Segment?.Start == _segmentStart)
            {
                _segmentBytes += batch.Length;
            }

            PumpCreations();
            return batch.Length;
        }
    }

    /// <summary>Releases the reservation of a proposal whose resolution ended, which frees room for waiting creations.</summary>
    private void ReleaseReservation(long bytes)
    {
        if (bytes == 0)
        {
            return;
        }

        lock (_lock)
        {
            _reservedBytes -= bytes;
            Debug.Assert(_reservedBytes >= 0, "A writer releases no more proposal bytes than it reserved.");
            PumpCreations();
        }
    }

    /// <summary>Draws the writer's segment age once, from its process's seeded random source (ledgers.md §8.2).</summary>
    private void InitializeBatches(LedgerWriterHost host) => DrawnSegmentAge = host.DrawSegmentAge();

    /// <summary>Refuses the append path on a writer which proposes batches.</summary>
    private void ThrowIfProposing()
    {
        if (_serving is not null)
        {
            throw new InvalidOperationException(
                "A writer which proposes batches takes each slot at a batch's creation (ledgers.md §2.4); its append path would write a payload inside a segment.");
        }
    }

    /// <summary>Acquires a slot, or returns null once the lifetime stopped.</summary>
    private async ValueTask<SlotGrant?> AcquireGrantAsync(CancellationToken cancellationToken)
    {
        var acquireStarted = _timeProvider.GetTimestamp();
        SlotGrant grant;
        try
        {
            grant = await Sequencer.AcquireSlotAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SequencerStoppedException)
        {
            // The lifetime is stopped; nothing was acquired, and a slot the stopped lifetime granted is filled or passed by
            // the recovery (LP-I13).
            return null;
        }

        LedgerWriterMetrics.RecordAcquire(_timeProvider.GetElapsedTime(acquireStarted));

        // LP-I11: at most K slots are in flight (LP-I2), so every slot below p + 1 - K has completed.
        _ = Ledger.RaiseKnownContiguousTail(new Slot(Math.Max(0, checked(grant.Slot.Value + 1 - Sequencer.WindowSize))));
        return grant;
    }

    /// <summary>Takes the acquired slot <paramref name="slot"/> for its one append.</summary>
    /// <exception cref="ArgumentException">This writer did not acquire the slot, or it was already appended.</exception>
    private SlotGrant TakeAcquired(Slot slot)
    {
        lock (_lock)
        {
            if (AcceptsSlotTwiceMutant ? _acquired.TryGetValue(slot.Value, out var grant) : _acquired.Remove(slot.Value, out grant))
            {
                return grant;
            }
        }

        throw new ArgumentException(
            string.Create(CultureInfo.InvariantCulture, $"Slot {slot} was not acquired from this writer, or was already appended."),
            nameof(slot));
    }

    /// <summary>Queues a creation in call order.</summary>
    private Creation Enqueue(int rowCount, int maxLength, bool isMarker)
    {
        lock (_lock)
        {
            return EnqueueLocked(rowCount, maxLength, isMarker);
        }
    }

    private Creation EnqueueLocked(int rowCount, int maxLength, bool isMarker)
    {
        var creation = new Creation(this, rowCount, maxLength, isMarker);
        creation.Node = _creations.AddLast(creation);
        PumpCreations();
        return creation;
    }

    /// <summary>Creates a marker in its turn and proposes it.</summary>
    private async ValueTask<ProposalOutcome> ProposeMarkerAsync(Creation creation, CancellationToken cancellationToken)
    {
        using var marker = await CreateAsync(creation, cancellationToken).ConfigureAwait(false);
        return await ProposeAsync(marker, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Proposes the marker an aged segment owes. Its outcome decides nothing the writer waits for; a failure other than the
    /// writer's disposal is a defect, which the writer's disposal surfaces.
    /// </summary>
    private async Task ProposeAgedMarkerAsync(Creation creation)
    {
        try
        {
            _ = await ProposeMarkerAsync(creation, CancellationToken.None).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // The writer's disposal ended the marker; its lifetime is stopped, and the ledger's close ends the segment.
        }
        catch (Exception exception) when (!IsProcessCritical(exception))
        {
            Debug.Fail($"The marker of ledger {Ledger.Id}'s aged segment failed unexpectedly: {exception}");
            throw;
        }
    }

    /// <summary>Creates a batch whose turn and byte reservation were already claimed without queuing.</summary>
    private async ValueTask<LedgerBatch> CreateClaimedAsync(Serving serving, int rowCount, int maxLength, CancellationToken cancellationToken)
    {
        SlotGrant? grant;
        try
        {
            grant = await AcquireGrantAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            EndTurn(maxLength);
            throw;
        }

        if (grant is not { } granted)
        {
            EndTurn(maxLength);
            return Slotless(serving, rowCount, maxLength);
        }

        return Create(serving, rowCount, maxLength, isMarker: false, granted);
    }

    /// <summary>Creates a queued batch, or marker, in its turn (ledgers.md §8.2).</summary>
    private async ValueTask<LedgerBatch> CreateAsync(Creation creation, CancellationToken cancellationToken)
    {
        var serving = Proposing;
        bool slotted;
        using (cancellationToken.UnsafeRegister(static (state, token) => ((Creation)state!).Withdraw(token), creation))
        {
            slotted = await creation.Task.ConfigureAwait(false);
        }

        if (!slotted)
        {
            return Slotless(serving, creation.RowCount, creation.MaxLength);
        }

        // The creation holds its turn and its reservation while it acquires its slot, which waits while the window is full.
        SlotGrant? grant;
        try
        {
            grant = await AcquireGrantAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            EndTurn(creation.MaxLength);
            throw;
        }

        if (grant is not { } granted)
        {
            EndTurn(creation.MaxLength);
            return Slotless(serving, creation.RowCount, creation.MaxLength);
        }

        // A slot granted once the writer stopped admitting, or once its disposal began, stays the batch's: its proposal
        // is Fenced in slot order, or throws ObjectDisposedException, and the stop which follows ends the ledger at or below
        // it.
        return Create(serving, creation.RowCount, creation.MaxLength, creation.IsMarker, granted);
    }

    /// <summary>
    /// Builds the batch at its granted slot, and only then commits the writer's segment state, so a failure leaves the
    /// state as it was; a failure after the slot was granted abandons the writer, since the slot is never written.
    /// </summary>
    private LedgerBatch Create(Serving serving, int rowCount, int maxLength, bool isMarker, SlotGrant grant)
    {
        LedgerBatch? batch = null;
        try
        {
            lock (_lock)
            {
                _creating = false;
                var start = StartsSegmentLocked(grant.Slot, rowCount, isMarker);
                var segment = start
                    ? new LedgerBatchSegment(IsStart: true, Offset: 0, OrdinalBase: 0, _dictionary)
                    : new LedgerBatchSegment(IsStart: false, (ulong)(grant.Slot - _segmentStart), (ulong)_dictionary.Count, _dictionary);
                batch = new LedgerBatch(this, serving.Range, maxLength, rowCount, segment, grant, new LedgerSegmentId(LedgerId, start ? grant.Slot : _segmentStart))
                {
                    Creation = _created + 1,
                };
                _unproposed = ++_created;
                CommitSegmentLocked(grant.Slot, start, rowCount, isMarker);
                return batch;
            }
        }
        catch
        {
            lock (_lock)
            {
                _creating = false;
                _unproposed = 0;
                _reservedBytes -= maxLength;
                _abandoning = true;
                PumpCreations();
            }

            batch?.Dispose();
            Abandon();
            throw;
        }
    }

    /// <summary>
    /// Returns whether the batch at <paramref name="slot"/> starts a segment: at slot 0, for a marker, once the open
    /// segment's age ended it, once its batches reached <see cref="LedgerWriterOptions.MaxSegmentBytes"/> beside a batch
    /// with rows, or when the batch's rows could take its dictionary past <see cref="LedgerWriterOptions.MaxSegmentKeys"/>.
    /// Under the lock.
    /// </summary>
    private bool StartsSegmentLocked(Slot slot, int rowCount, bool isMarker)
    {
        var options = Proposing.Host.Options;
        return slot == Slot.Zero
            || isMarker
            || _segmentEnded
            || _firstRowsAt is not null && _segmentBytes >= options.MaxSegmentBytes
            || _dictionary.Count > 0 && (long)_dictionary.Count + rowCount > options.MaxSegmentKeys
            || _firstRowsAt is { } since && _timeProvider.GetElapsedTime(since) >= DrawnSegmentAge;
    }

    /// <summary>
    /// Gives the open segment, with the keys it defined, to the host's <see cref="LedgerWriterHost.OnSegmentEnded"/> sink
    /// (ledgers.md §2.4), once: when the next segment starts, or when the writer stops. A segment which defined no key, a
    /// marker's alone, is not given. Under the lock.
    /// </summary>
    private void ReportSegmentEndedLocked()
    {
        if (_dictionary.Count > 0 && !_segmentReported && _serving?.Host.OnSegmentEnded is { } sink)
        {
            _segmentReported = true;
            sink(new LedgerSegmentKeys(new LedgerSegmentId(LedgerId, _segmentStart), _dictionary.Count));
        }
    }

    /// <summary>Commits the segment state of the batch created at <paramref name="slot"/>. Under the lock.</summary>
    private void CommitSegmentLocked(Slot slot, bool start, int rowCount, bool isMarker)
    {
        if (start)
        {
            // The batch created before this one, if any, belongs to the segment this one ends: its keys are final, since a
            // creation waits until the batch created before it is proposed.
            if (_created > 1)
            {
                ReportSegmentEndedLocked();

                // Every batch the ending segment holds is proposed, so the bytes proposed outside it precede it.
                _endedSegments.Enqueue(new EndedSegment(_segmentStart, _proposedBytes - _segmentBytes, _firstRowsAt));
            }

            // A segment ended by its size or its keys is followed by one as busy, which reuses the dictionary's capacity;
            // one ended by its age or a marker shrinks it, as its ledger is quiet.
            _dictionary.Release(keepCapacity: !isMarker && !_segmentEnded && _firstRowsAt is not null);
            _segmentStart = slot;
            _segmentReported = false;
            _segmentBytes = 0;
            _firstRowsAt = null;
            _segmentEnded = false;
            _ageTimer?.Dispose();
            _ageTimer = null;
        }

        if (rowCount == 0 || _firstRowsAt is not null)
        {
            return;
        }

        // The segment's age counts from its first batch with rows, so a segment which holds only its marker never ends by
        // age, and an idle ledger writes one marker and then nothing until its next batch with rows.
        _firstRowsAt = _timeProvider.GetTimestamp();

        // The timer serves the ledger, so it runs in no caller's execution context (conventions.md §12).
        using (ExecutionContext.SuppressFlow())
        {
            _ageTimer = _timeProvider.CreateTimer(
                static state =>
                {
                    var (writer, segment) = ((LedgerWriter, Slot))state!;
                    writer.OnSegmentAged(segment);
                },
                (this, _segmentStart),
                DrawnSegmentAge,
                Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Ends the segment which starts at <paramref name="segment"/> once its first batch with rows reached the writer's
    /// segment age: the next batch created starts a segment, and while the writer is idle it owes a marker, which
    /// <see cref="PumpCreations"/> creates (ledgers.md §8.2).
    /// </summary>
    private void OnSegmentAged(Slot segment)
    {
        lock (_lock)
        {
            if (segment != _segmentStart || _firstRowsAt is null || _segmentEnded)
            {
                return;
            }

            _segmentEnded = true;
            PumpCreations();
        }
    }

    /// <summary>Creates a batch which takes no slot: its proposal completes Fenced at once.</summary>
    private LedgerBatch Slotless(Serving serving, int rowCount, int maxLength) =>
        new(this, serving.Range, maxLength, rowCount, new LedgerBatchSegment(IsStart: true, Offset: 0, OrdinalBase: 0, new LedgerSegmentDictionary()), grant: null, segmentId: null);

    /// <summary>Ends a creation's turn which created nothing, releasing its reservation.</summary>
    private void EndTurn(int maxLength)
    {
        lock (_lock)
        {
            _creating = false;
            _reservedBytes -= maxLength;
            PumpCreations();
        }
    }

    /// <summary>
    /// Gives the creation at the head of the queue its turn once the batch created before it is proposed and its
    /// maxLength fits the proposal budget; once the writer admits no batch, gives every waiting creation its answer at
    /// once, a batch which takes no slot. While the writer is idle and its open segment aged, queues the marker the segment
    /// owes, so no segment outlives its age by more than the writer's own turn. Under the lock.
    /// </summary>
    private void PumpCreations()
    {
        if (!AdmitsBatchesLocked)
        {
            while (_creations.First is { } stopped)
            {
                _creations.RemoveFirst();
                stopped.Value.Node = null;
                stopped.Value.Grant(slotted: false);
            }

            return;
        }

        if (_segmentEnded && !_creating && _unproposed == 0 && _creations.Count == 0)
        {
            // The marker is queued here, under the lock, so no caller's creation comes between the segment's end and it,
            // and a segment ends once.
            var marker = new Creation(this, rowCount: 0, LedgerBatch.MaxHeaderLength(0), isMarker: true);
            marker.Node = _creations.AddLast(marker);

            // The marker serves the ledger, so it runs in no caller's execution context (conventions.md §12).
            using (ExecutionContext.SuppressFlow())
            {
                var proposing = Task.Run(() => ProposeAgedMarkerAsync(marker), CancellationToken.None);
                _agedMarkers = _agedMarkers is null or { IsCompleted: true } ? proposing : Task.WhenAll(_agedMarkers, proposing);
            }
        }

        if (_creating || _unproposed != 0 || _creations.First is not { } head)
        {
            return;
        }

        var creation = head.Value;
        if (_reservedBytes + creation.MaxLength > Proposing.Host.Options.MaxProposalBytes)
        {
            return;
        }

        _creations.RemoveFirst();
        creation.Node = null;
        _creating = true;
        _reservedBytes += creation.MaxLength;
        Debug.Assert(_reservedBytes <= Proposing.Host.Options.MaxProposalBytes, "The batches created and not yet decided stay within MaxProposalBytes (ledgers.md §8.7).");
        creation.Grant(slotted: true);
    }

    /// <summary>
    /// Wakes every waiting creation once the lifetime stops or the writer stops admitting, which ends every wait.
    /// </summary>
    private void OnStoppedCreations()
    {
        lock (_lock)
        {
            _ageTimer?.Dispose();
            _ageTimer = null;
            PumpCreations();
        }
    }

    /// <summary>One creation waiting for its turn, completed with whether it takes a slot.</summary>
    private sealed class Creation(LedgerWriter writer, int rowCount, int maxLength, bool isMarker) : TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
    {
        public int RowCount { get; } = rowCount;

        public int MaxLength { get; } = maxLength;

        public bool IsMarker { get; } = isMarker;

        /// <summary>Gets or sets its place in the queue, which it leaves once it has its turn or is withdrawn.</summary>
        public LinkedListNode<Creation>? Node { get; set; }

        public void Grant(bool slotted) => _ = TrySetResult(slotted);

        /// <summary>Withdraws the creation while it waits for its turn; one which has its turn keeps it.</summary>
        public void Withdraw(CancellationToken cancellationToken)
        {
            lock (writer._lock)
            {
                if (Node?.List is null)
                {
                    return;
                }

                writer._creations.Remove(Node);
                Node = null;
                writer.PumpCreations();
            }

            _ = TrySetCanceled(cancellationToken);
        }
    }

    /// <summary>Throws when <paramref name="watermark"/> is above the open segment's start, which no drain passes. Under the lock.</summary>
    private void ThrowIfAboveOpenSegmentLocked(Slot watermark)
    {
        if (watermark > _segmentStart)
        {
            throw new ArgumentOutOfRangeException(nameof(watermark), watermark, string.Create(CultureInfo.InvariantCulture, $"A drain watermark is at or below the open segment's start, {_segmentStart}."));
        }
    }

    /// <summary>An ended segment no forgotten watermark has passed: its first slot, the bytes proposed before it, and when its first batch with rows was created.</summary>
    private readonly record struct EndedSegment(Slot Start, long ProposedBefore, long? FirstRowsAt);
}

/// <summary>
/// What a ledger's primary proposed at or above the ledger's drain watermark (orleans.md §5.9, §8): its batches' bytes, and
/// the age of the oldest of them with rows, a marker's alone needing no drain.
/// </summary>
/// <param name="Bytes">The bytes of the batches proposed at or above the watermark, markers included.</param>
/// <param name="Age">The age of the oldest undrained batch with rows, or <see langword="null"/> when none is.</param>
public readonly record struct LedgerUndrained(long Bytes, TimeSpan? Age);
