using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Journals;
using Orleans.Durable.Storage.Engine.Threading;

namespace Orleans.Durable.Storage;

/// <summary>
/// The read snapshots a node pins for forwarded reads (orleans.md §5.5, §5.12, OR-I17): the owner answers a read whose
/// content fits one page whole, and pins the content of a longer one, reserving its length within
/// <c>MaxForwardedReadBytes</c>, or the whole budget for a snapshot longer than it, which then waits until it runs alone,
/// before it answers the first page; it serves the later pages from that one snapshot, so a read never restarts, and
/// releases the snapshot at its last page, its deadline, its reader's release, or the node's stop.
/// </summary>
/// <remarks>
/// <para>
/// A read's deadline is Orleans' response timeout from when the owner received it (<see cref="DeadlineFromNow"/>), taken
/// before the node's serving gate, so every wait and hold the read makes on the owner counts against it: the serving gate,
/// a forwarded-read slot, the history read, which the deadline cancels, and its reservation. A read which reaches its
/// deadline in any of them, or whose content of more than one page arrives after it, is answered
/// <see cref="JournalForwardedRead.Busy"/>, having pinned nothing; content of at most one page pins nothing, so it is
/// answered whole whenever it arrives. The owner bounds every hold it makes by these deadlines; the composition does not
/// rely on Orleans cancelling a call whose caller timed out.
/// </para>
/// <para>
/// A read holds one of <c>MaxConcurrentHistoryReads</c> forwarded-read slots from before its history read, whose length
/// it cannot know before, until its reservation, and a read of at most one page until its page is answered, so at most that
/// many assembled snapshots wait for the budget at once. The slots are apart from the Journals' history-read slots, which a
/// history read releases once it returns, so key loads and the writes which wait for them never wait behind a snapshot
/// waiting for budget; a small forwarded read does wait for a slot behind large ones waiting for budget.
/// </para>
/// <para>
/// A snapshot's first window is what remains of its read's deadline, and each later page renews it by a full response
/// timeout, so a reader which asks for each page once the previous one has arrived keeps it however long the journal; one
/// which stops asking loses it once the window passes, and its next page answers <see cref="JournalForwardedPage.Expired"/>,
/// after which it reads again from the start. Each page is a copy of the snapshot's bytes in an Orleans buffer the reply
/// carries, which the owner never releases (OR-I18).
/// </para>
/// </remarks>
internal sealed class ForwardedReads : IDisposable
{
    private readonly CallBudget _slots;
    private readonly CallBudget _reserved;
    private readonly TimeSpan _callTimeout;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<Guid, Snapshot> _snapshots = new();
    private int _stopped;

    /// <summary>Initializes a new instance of the <see cref="ForwardedReads"/> class.</summary>
    /// <remarks>
    /// The forwarded-read slots are as many as the Journals' history-read slots, <c>Journals.MaxConcurrentHistoryReads</c>:
    /// a large read keeps its slot until it is reserved, so the slots bound the assembled snapshots waiting for budget as the
    /// history-read slots bound those being assembled (orleans.md §5.5).
    /// </remarks>
    public ForwardedReads(DurableStorageOptions options, ReplicaCallLimits limits, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _slots = CallBudget.ForCalls(options.Journals.MaxConcurrentHistoryReads, timeProvider, BudgetTimedOut);
        _reserved = new CallBudget(options.MaxForwardedReadBytes, int.MaxValue, timeProvider, BudgetTimedOut);
        _callTimeout = limits.Deadline;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Gets the bytes the pinned snapshots reserve: each snapshot's length, or the whole budget for one longer than it, which
    /// pins its whole length.
    /// </summary>
    public long ReservedBytes => _reserved.OutstandingBytes;

    /// <summary>Gets the snapshots pinned.</summary>
    public int Pinned => _snapshots.Count;

    /// <summary>Gets the reads which wait to reserve their snapshot.</summary>
    public int Reserving => _reserved.Waiting;

    /// <summary>Gets the reads which wait for a forwarded-read slot.</summary>
    public int WaitingForSlot => _slots.Waiting;

    /// <summary>Gets the forwarded-read slots held.</summary>
    public int SlotsHeld => _slots.OutstandingCalls;

    /// <summary>
    /// Takes <paramref name="result"/>'s answer and content, which the returned content owns; the caller keeps and disposes
    /// the result.
    /// </summary>
    public static async Task<Content> TakeAsync(JournalReadResult result, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        var chunks = new List<ArcBuffer>();
        try
        {
            await foreach (var chunk in result.ReadPayloadsAsync(cancellationToken).ConfigureAwait(false))
            {
                chunks.Add(chunk);
            }

            Debug.Assert(chunks.Sum(static chunk => (long)chunk.Length) == result.ContentLength, "A read's chunks sum to its content's length.");
            return new Content(result.CurrentVersion, result.Exists, result.ContentLength, result.ReplaceRequested, [.. chunks]);
        }
        catch
        {
            Release(CollectionsMarshal.AsSpan(chunks));
            throw;
        }
    }

    /// <summary>Returns the deadline of a forwarded read the owner receives now: Orleans' response timeout from now.</summary>
    public long DeadlineFromNow() => _reserved.DeadlineAfter(_callTimeout);

    /// <summary>
    /// Runs <paramref name="read"/>, the Journals' read of the journal, which returns its content, and answers its first
    /// page, pinning the content for the later pages when more follow; answers the read's <c>Moved</c> as it is, and
    /// <see cref="JournalForwardedRead.Busy"/>, having pinned nothing, once <paramref name="deadline"/> passes while it
    /// waits for a slot, reads its history, which the deadline cancels, or waits for its reservation, or before its content
    /// of more than one page arrives.
    /// </summary>
    /// <param name="read">The Journals' read.</param>
    /// <param name="deadline">The read's deadline, from <see cref="DeadlineFromNow"/> as the owner received it.</param>
    /// <param name="cancellationToken">The caller's cancellation.</param>
    public async Task<JournalForwardedRead> ReadAsync(Func<CancellationToken, Task<Source>> read, long deadline, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(read);
        CallBudget.Lease slot;
        try
        {
            slot = await _slots.EnterLeaseAsync(0, deadline, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return JournalForwardedRead.Busy.Instance;
        }

        using (slot)
        {
            Source source;
            using (var expiry = new CancellationTokenSource(_reserved.Remaining(deadline), _timeProvider))
            using (var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, expiry.Token))
            {
                try
                {
                    source = await read(bounded.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (expiry.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    // The history read had no effect; the read's deadline ended it.
                    return JournalForwardedRead.Busy.Instance;
                }
            }

            return source switch
            {
                JournalMoved moved => moved,
                Content content => await AnswerAsync(content, deadline, cancellationToken).ConfigureAwait(false),
                null => throw new UnreachableException(),
            };
        }
    }

    /// <summary>
    /// Answers the page of the pinned <paramref name="snapshot"/> at <paramref name="offset"/>, at most
    /// <see cref="JournalForwardedRead.PageBytes"/>; the last releases the snapshot, and any other renews its deadline.
    /// Answers <see cref="JournalForwardedPage.Expired"/> once the snapshot was released, and refuses an offset outside its
    /// content with <see cref="JournalRefusal.Argument"/>.
    /// </summary>
    public JournalForwardedPage Page(Guid snapshot, long offset) =>
        _snapshots.TryGetValue(snapshot, out var pinned) ? pinned.Page(offset) : JournalForwardedPage.Expired.Instance;

    /// <summary>Releases the pinned <paramref name="snapshot"/> of a read its reader abandoned; one already released is left as it is.</summary>
    public void Release(Guid snapshot)
    {
        if (_snapshots.TryGetValue(snapshot, out var pinned))
        {
            pinned.Release();
        }
    }

    /// <summary>
    /// Releases every pinned snapshot, once the node's Journals stop, and every one a read pins afterwards; a later page
    /// answers <see cref="JournalForwardedPage.Expired"/>.
    /// </summary>
    public void Stop()
    {
        Volatile.Write(ref _stopped, 1);
        foreach (var pinned in _snapshots.Values)
        {
            pinned.Release();
        }
    }

    /// <summary>Stops the snapshots (<see cref="Stop"/>).</summary>
    public void Dispose() => Stop();

    private static TimeoutException BudgetTimedOut(TimeSpan waited) =>
        new(string.Create(CultureInfo.InvariantCulture, $"A forwarded read waited {waited} for the owner's forwarded-read slots or budget, past its deadline; it had no effect."));

    private static JournalForwardedRead.Found Found(Content content, OrleansBuffer firstPage, Guid? snapshot)
    {
        // The owner sends only what the reading silo accepts (orleans.md §7.2).
        Debug.Assert(content.Exists || content.ContentLength == 0, "An absent journal has no content (journals.md §2.3).");
        Debug.Assert(snapshot is null == content.ContentLength <= JournalForwardedRead.PageBytes, "A read names a snapshot exactly when more pages follow.");
        return new(content.Version, content.Exists, content.ContentLength, content.ReplaceRequested, firstPage, snapshot);
    }

    private static void Release(ReadOnlySpan<ArcBuffer> chunks)
    {
        foreach (var chunk in chunks)
        {
            chunk.Dispose();
        }
    }

    /// <summary>
    /// Answers <paramref name="content"/>: whole when it fits one page, otherwise its first page from a snapshot it pins once
    /// reserved; <see cref="JournalForwardedRead.Busy"/> when the deadline passed before it arrived or while it waits for
    /// its reservation, its chunks released.
    /// </summary>
    private async Task<JournalForwardedRead> AnswerAsync(Content content, long deadline, CancellationToken cancellationToken)
    {
        if (content.ContentLength <= JournalForwardedRead.PageBytes)
        {
            try
            {
                return Found(content, WireBuffers.CopyToOrleans(content.Chunks, 0, (int)content.ContentLength), snapshot: null);
            }
            finally
            {
                Release(content.Chunks);
            }
        }

        if (_reserved.Remaining(deadline) == TimeSpan.Zero)
        {
            // The history arrived past the read's deadline: its caller has stopped waiting, so pin nothing.
            Release(content.Chunks);
            return JournalForwardedRead.Busy.Instance;
        }

        CallBudget.Grant grant;
        try
        {
            grant = await _reserved.EnterAsync(Math.Min(content.ContentLength, _reserved.MaxBytes), deadline, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Release(content.Chunks);
            return JournalForwardedRead.Busy.Instance;
        }
        catch
        {
            Release(content.Chunks);
            throw;
        }

        var pinned = new Snapshot(this, Guid.NewGuid(), content.Chunks, content.ContentLength, grant);
        OrleansBuffer first;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            first = pinned.FirstPage();
        }
        catch
        {
            pinned.Release();
            throw;
        }

        _snapshots[pinned.Id] = pinned;
        pinned.Arm(_reserved.Remaining(deadline));

        // A stop which ran before the snapshot was listed released none of it.
        if (Volatile.Read(ref _stopped) != 0)
        {
            pinned.Release();
        }

        return Found(content, first, pinned.Id);
    }

    /// <summary>Removes <paramref name="snapshot"/>, which was released, from the pinned snapshots.</summary>
    private void Forget(Snapshot snapshot) => _ = _snapshots.TryRemove(new KeyValuePair<Guid, Snapshot>(snapshot.Id, snapshot));

    /// <summary>
    /// A journal's read on its owner, as a forwarded read answers it (journals.md §2.3): the version of the content it
    /// returns, whether the journal exists, its content's length, the replace request the read gives, and the content in
    /// order, in chunks which it owns until <see cref="ReadAsync"/> takes them.
    /// </summary>
    /// <param name="Version">The version of the content.</param>
    /// <param name="Exists">Whether the journal exists.</param>
    /// <param name="ContentLength">The content's length, which the chunks sum to.</param>
    /// <param name="ReplaceRequested">Whether the read asks the journal's writer for a <c>Replace</c>.</param>
    /// <param name="Chunks">The content, in order.</param>
    public sealed record Content(JournalVersion Version, bool Exists, long ContentLength, bool ReplaceRequested, ArcBuffer[] Chunks);

    /// <summary>The Journals' answer to a forwarded read: its content, or <c>Moved</c>.</summary>
    public readonly union Source(Content, JournalMoved);

    /// <summary>
    /// A pinned snapshot: its content's chunks, its reservation, and its window's timer, which releases it only once the time
    /// its last renewal set has passed, so a renewal never loses to a timer which fired as it ran.
    /// </summary>
    private sealed class Snapshot(ForwardedReads reads, Guid id, ArcBuffer[] chunks, long length, CallBudget.Grant grant)
    {
        private readonly Lock _lock = new();
        private readonly TimeProvider _timeProvider = reads._timeProvider;
        private readonly TimeSpan _renewal = reads._callTimeout;
        private ITimer? _timer;
        private long _expiresAt;
        private bool _released;

        public Guid Id { get; } = id;

        /// <summary>Returns the first page, which the reply to the read carries.</summary>
        public OrleansBuffer FirstPage() => WireBuffers.CopyToOrleans(chunks, 0, JournalForwardedRead.PageBytes);

        /// <summary>Starts the snapshot's first window, <paramref name="window"/>, what remains of its read's deadline.</summary>
        public void Arm(TimeSpan window)
        {
            lock (_lock)
            {
                if (_released)
                {
                    return;
                }

                _expiresAt = _timeProvider.GetTimestamp() + (long)(window.TotalSeconds * _timeProvider.TimestampFrequency);
                using (ExecutionContext.SuppressFlow())
                {
                    // Created disarmed and then armed, so a callback which runs at once finds its timer assigned.
                    _timer = _timeProvider.CreateTimer(static state => ((Snapshot)state!).Expire(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                }

                _ = _timer.Change(window, Timeout.InfiniteTimeSpan);
            }
        }

        public JournalForwardedPage Page(long offset)
        {
            lock (_lock)
            {
                if (_released)
                {
                    return JournalForwardedPage.Expired.Instance;
                }

                if (offset < 0 || offset >= length)
                {
                    return new JournalCallRefused(JournalRefusal.Argument, string.Create(CultureInfo.InvariantCulture, $"A page's offset, {offset}, is not within the snapshot's {length} bytes."));
                }

                var count = (int)Math.Min(JournalForwardedRead.PageBytes, length - offset);
                var page = new JournalForwardedPage.Page(WireBuffers.CopyToOrleans(chunks, offset, count));
                if (offset + count == length)
                {
                    ReleaseLocked();
                }
                else
                {
                    _expiresAt = _timeProvider.GetTimestamp() + (long)(_renewal.TotalSeconds * _timeProvider.TimestampFrequency);
                    _ = _timer?.Change(_renewal, Timeout.InfiniteTimeSpan);
                }

                return page;
            }
        }

        public void Release()
        {
            lock (_lock)
            {
                ReleaseLocked();
            }
        }

        /// <summary>Releases the snapshot once its window has passed, or arms the timer again for what a renewal left.</summary>
        private void Expire()
        {
            lock (_lock)
            {
                var left = _timeProvider.GetElapsedTime(_timeProvider.GetTimestamp(), _expiresAt);
                if (left > TimeSpan.Zero && !_released)
                {
                    _ = _timer?.Change(left, Timeout.InfiniteTimeSpan);
                    return;
                }

                ReleaseLocked();
            }
        }

        private void ReleaseLocked()
        {
            if (_released)
            {
                return;
            }

            _released = true;
            reads.Forget(this);
            _timer?.Dispose();
            ForwardedReads.Release(chunks);
            grant.Dispose();
        }
    }
}
