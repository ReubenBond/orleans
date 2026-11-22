namespace Orleans.Durable.Storage.Engine.Ledgers;

/// <summary>
/// The writer's explicit contiguous-tail writes while idle (ledger-protocol.md §8.2): each write carries the contiguous
/// tail of the writes before it, so an idle ledger's replicas lack the completions of its last writes. Once a completion
/// raised the known contiguous tail past what the writes carried and the writer has not written for
/// <see cref="ContiguousTailInterval"/>, it writes the tail explicitly, as BookKeeper's explicit last add confirmed does,
/// so readers in other processes read the ledger's last batches from one replica (LP-I10, LP-I11).
/// </summary>
public sealed partial class LedgerWriter
{
    private readonly Lock _contiguousTailLock = new();
    private ITimer? _contiguousTailTimer;
    private bool _contiguousTailClosed;

    // 1 while the timer is armed for a completion whose tail no write has carried yet.
    private int _contiguousTailArmed;

    // The timestamp at which the writer last began a write attempt.
    private long _lastWrite;

    /// <summary>
    /// Gets the idle time after which the writer writes its contiguous tail explicitly (ledger-protocol.md §8.4).
    /// </summary>
    internal static TimeSpan ContiguousTailInterval { get; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Records that a write attempt begins, which carries the known contiguous tail.</summary>
    private void NoteWrite() => Volatile.Write(ref _lastWrite, _timeProvider.GetTimestamp());

    /// <summary>
    /// Arms the explicit contiguous-tail write once a completion raised the known contiguous tail: it fires once the writer
    /// has been idle for <see cref="ContiguousTailInterval"/> since its last write attempt.
    /// </summary>
    private void ScheduleContiguousTail()
    {
        if (Interlocked.CompareExchange(ref _contiguousTailArmed, 1, 0) != 0)
        {
            return;
        }

        var due = IdleRemaining();
        lock (_contiguousTailLock)
        {
            if (_contiguousTailClosed)
            {
                return;
            }

            if (_contiguousTailTimer is { } timer)
            {
                _ = timer.Change(due, Timeout.InfiniteTimeSpan);
                return;
            }

            // The timer serves the ledger, so it runs in no caller's execution context (conventions.md §12).
            using (ExecutionContext.SuppressFlow())
            {
                _contiguousTailTimer = _timeProvider.CreateTimer(
                    static state => ((LedgerWriter)state!).OnContiguousTailDue(),
                    this,
                    due,
                    Timeout.InfiniteTimeSpan);
            }
        }
    }

    /// <summary>
    /// Writes the known contiguous tail explicitly once the writer has been idle for <see cref="ContiguousTailInterval"/>,
    /// or waits out the rest of the interval when a write began since the timer was armed. A stopped lifetime or a
    /// disposed writer writes nothing.
    /// </summary>
    private void OnContiguousTailDue()
    {
        var remaining = IdleRemaining();
        if (remaining > TimeSpan.Zero)
        {
            lock (_contiguousTailLock)
            {
                if (!_contiguousTailClosed)
                {
                    _ = _contiguousTailTimer!.Change(remaining, Timeout.InfiniteTimeSpan);
                }
            }

            return;
        }

        // Disarmed with a full fence before the tail is read, so a completion which raises the tail afterwards arms again.
        _ = Interlocked.Exchange(ref _contiguousTailArmed, 0);

        // Checked and sent under the lock its disposal closes, so a writer whose disposal returned writes no tail.
        lock (_contiguousTailLock)
        {
            if (_contiguousTailClosed || Sequencer.IsStopped || Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            _ = Ledger.WriteContiguousTail();
        }
    }

    /// <summary>Stops the explicit contiguous-tail writes: the writer's disposal calls it.</summary>
    private void CloseContiguousTail()
    {
        lock (_contiguousTailLock)
        {
            _contiguousTailClosed = true;
            _contiguousTailTimer?.Dispose();
            _contiguousTailTimer = null;
        }
    }

    private TimeSpan IdleRemaining()
    {
        var remaining = ContiguousTailInterval - _timeProvider.GetElapsedTime(Volatile.Read(ref _lastWrite));
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }
}
