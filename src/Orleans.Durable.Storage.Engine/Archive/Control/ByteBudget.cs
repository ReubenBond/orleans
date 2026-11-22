using System.Diagnostics;

namespace Orleans.Durable.Storage.Engine.Archive.Control;

/// <summary>
/// A budget of bytes which holders acquire and release, granted in arrival order, so a large request waits only for the
/// holders before it and blocks those after it.
/// </summary>
internal sealed class ByteBudget
{
    private readonly Lock _sync = new();
    private readonly LinkedList<Waiter> _waiters = new();
    private long _available;

    /// <summary>
    /// Initializes a new instance of the <see cref="ByteBudget"/> class.
    /// </summary>
    /// <param name="capacity">The bytes the holders hold at most at once, at least 1.</param>
    public ByteBudget(long capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
        _available = capacity;
    }

    /// <summary>Gets the bytes the holders hold at most at once.</summary>
    public long Capacity { get; }

    /// <summary>Gets the bytes no holder holds; for tests.</summary>
    internal long Available
    {
        get
        {
            lock (_sync)
            {
                return _available;
            }
        }
    }

    /// <summary>
    /// Acquires <paramref name="bytes"/> once every earlier request has been granted and they are available.
    /// </summary>
    /// <returns>The lease, whose disposal releases the bytes.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bytes"/> lies outside 1 to the capacity.</exception>
    /// <exception cref="OperationCanceledException">The wait was cancelled before the bytes were granted.</exception>
    public async Task<Lease> AcquireAsync(long bytes, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bytes, Capacity);
        LinkedListNode<Waiter> node;
        lock (_sync)
        {
            if (_waiters.Count == 0 && _available >= bytes)
            {
                _available -= bytes;
                return new Lease(this, bytes);
            }

            node = _waiters.AddLast(new Waiter(bytes));
        }

        using (cancellationToken.Register(() => Cancel(node, cancellationToken)))
        {
            await node.Value.Granted.Task.ConfigureAwait(false);
        }

        return new Lease(this, bytes);
    }

    /// <summary>Takes <paramref name="bytes"/> at once when no request waits and they are available; never waits.</summary>
    private bool TryTake(long bytes)
    {
        lock (_sync)
        {
            if (_waiters.Count > 0 || _available < bytes)
            {
                return false;
            }

            _available -= bytes;
            return true;
        }
    }

    private void Release(long bytes)
    {
        lock (_sync)
        {
            _available += bytes;
            Debug.Assert(_available <= Capacity, "Holders release only the bytes they acquired.");
            GrantWaiters();
        }
    }

    private void Cancel(LinkedListNode<Waiter> node, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (node.List is null)
            {
                return;
            }

            _waiters.Remove(node);
            _ = node.Value.Granted.TrySetCanceled(cancellationToken);

            // The cancelled request may have blocked those after it.
            GrantWaiters();
        }
    }

    /// <summary>Grants the waiters, in arrival order, while the first one's bytes are available. Under the lock.</summary>
    private void GrantWaiters()
    {
        while (_waiters.First is { } first && first.Value.Bytes <= _available)
        {
            _waiters.RemoveFirst();
            _available -= first.Value.Bytes;
            _ = first.Value.Granted.TrySetResult();
        }
    }

    /// <summary>
    /// Bytes acquired from the budget, which may grow without waiting and which its disposal releases once. One holder
    /// uses a lease at a time.
    /// </summary>
    internal sealed class Lease(ByteBudget budget, long bytes) : IDisposable
    {
        private int _released;

        /// <summary>Gets the bytes the lease holds.</summary>
        public long Bytes { get; private set; } = bytes;

        /// <summary>
        /// Adds <paramref name="more"/> bytes to the lease when no request waits and they are available, without waiting:
        /// a holder which waited while it held bytes could deadlock with another doing the same.
        /// </summary>
        /// <returns>Whether the lease grew.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="more"/> is below 1.</exception>
        /// <exception cref="ObjectDisposedException">The lease was released.</exception>
        public bool TryGrow(long more)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(more, 1);
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _released) != 0, this);
            if (!budget.TryTake(more))
            {
                return false;
            }

            Bytes += more;
            return true;
        }

        /// <summary>Returns <paramref name="less"/> of the lease's bytes to the budget, keeping at least one.</summary>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="less"/> is below 0 or leaves the lease no byte.</exception>
        /// <exception cref="ObjectDisposedException">The lease was released.</exception>
        public void Shrink(long less)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(less);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(less, Bytes);
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _released) != 0, this);
            if (less > 0)
            {
                Bytes -= less;
                budget.Release(less);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                budget.Release(Bytes);
            }
        }
    }

    private sealed record Waiter(long Bytes)
    {
        public TaskCompletionSource Granted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
