using System.Diagnostics;
using System.Globalization;
using Orleans.Durable.Storage.Engine.MetaStore;

namespace Orleans.Durable.Storage.Engine.Ledgers;

/// <summary>
/// A process's cached, read-only view of the <c>ledger-map</c> register (ledgers.md §2.3): the newest map value it has
/// read, by epoch, and reads of the register shared between concurrent callers.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Current"/> holds only values read from the register, by the view or by the map's committer, or written by
/// that committer's writes which landed, so it is durable and may be stale: a hint about currency (conventions.md §13).
/// Reads are linearizable and <see cref="Current"/> keeps the newest by epoch, so the view only advances.
/// </para>
/// <para>
/// One read is in flight at a time. A caller which arrives while a read is in flight shares the next read, which begins
/// once that one ends, so every caller's read begins after its call. The shared read runs without any caller's token
/// or execution context (conventions.md §3, §12): one caller's cancellation ends only its own wait. Each read disposes
/// the entry it reads (conventions.md §1). A value which fails to decode fails the read with its rejection, the map's
/// corruption, and leaves <see cref="Current"/> as it was. The view has no background work beyond reads callers wait
/// for, and needs no disposal.
/// </para>
/// </remarks>
public sealed class LedgerMapView
{
    private readonly IConditionalRegister _register;
    private readonly Lock _lock = new();
    private LedgerMapValue? _current;
    private TaskCompletionSource _changed = NewSignal();
    private TaskCompletionSource<LedgerMapValue>? _runningRead;
    private TaskCompletionSource<LedgerMapValue>? _queuedRead;

    private LedgerMapView(IConditionalRegister register) => _register = register;

    /// <summary>
    /// Gets the newest map value this process has read, by epoch.
    /// </summary>
    public LedgerMapValue Current
    {
        get
        {
            var current = Volatile.Read(ref _current);
            Debug.Assert(current is not null, "A view is returned only after its first read.");
            return current;
        }
    }

    /// <summary>
    /// Opens a view of <paramref name="register"/>, returning once its first read has returned, so that
    /// <see cref="Current"/> always holds a value the register held.
    /// </summary>
    /// <exception cref="InvalidDataException">The register holds a value which is not a ledger map (ledgers.md §7).</exception>
    public static async Task<LedgerMapView> OpenAsync(IConditionalRegister register, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(register);
        var view = new LedgerMapView(register);
        _ = await view.ReadAsync(cancellationToken).ConfigureAwait(false);
        return view;
    }

    /// <summary>
    /// Reads the register linearizably, in a read begun after the call, and raises <see cref="Current"/> when the value
    /// read is newer. Calls made while a read is in flight share the next read.
    /// </summary>
    /// <returns>The value read; an absent register reads as <see cref="LedgerMapValue.Empty"/>.</returns>
    /// <exception cref="InvalidDataException">The register holds a value which is not a ledger map.</exception>
    public Task<LedgerMapValue> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TaskCompletionSource<LedgerMapValue> read;
        var start = false;
        lock (_lock)
        {
            if (_runningRead is null)
            {
                read = _runningRead = NewRead();
                start = true;
            }
            else
            {
                read = _queuedRead ??= NewRead();
            }
        }

        if (start)
        {
            // The reads serve every caller, so they run in none's execution context (conventions.md §12).
            using (ExecutionContext.SuppressFlow())
            {
                _ = Task.Run(() => RunReadsAsync(read), CancellationToken.None);
            }
        }

        return read.Task.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Completes once <see cref="Current"/> has at least <paramref name="epoch"/>, reading the register only while it is
    /// older; concurrent calls share reads.
    /// </summary>
    /// <param name="epoch">The epoch of a value read from the register or written by a write which landed.</param>
    /// <param name="cancellationToken">Ends this caller's wait.</param>
    /// <returns><see cref="Current"/>, at <paramref name="epoch"/> or later.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A read begun after the call found an older epoch, so <paramref name="epoch"/> names no value the register held.
    /// </exception>
    /// <exception cref="InvalidDataException">The register holds a value which is not a ledger map.</exception>
    public async Task<LedgerMapValue> RefreshAsync(ulong epoch, CancellationToken cancellationToken = default)
    {
        var current = Current;
        if (current.Epoch >= epoch)
        {
            return current;
        }

        var read = await ReadAsync(cancellationToken).ConfigureAwait(false);
        if (read.Epoch < epoch)
        {
            throw new ArgumentOutOfRangeException(
                nameof(epoch),
                epoch,
                string.Create(CultureInfo.InvariantCulture, $"The register held epoch {read.Epoch} after the call, so epoch {epoch} names no value it held."));
        }

        return Current;
    }

    /// <summary>
    /// Completes with <see cref="Current"/> once its epoch passes <paramref name="afterEpoch"/>. It reads nothing: a read
    /// some caller makes raises <see cref="Current"/>.
    /// </summary>
    public async Task<LedgerMapValue> WaitForChangeAsync(ulong afterEpoch, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task changed;
            lock (_lock)
            {
                Debug.Assert(_current is not null, "A view is returned only after its first read.");
                if (_current.Epoch > afterEpoch)
                {
                    return _current;
                }

                changed = _changed.Task;
            }

            await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Returns, in <see cref="Current"/>, the open ledger whose range contains <paramref name="key"/> and every closed
    /// ledger whose range contains it, in ID order; <see langword="null"/> before the keyspace is bootstrapped.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="key"/> is empty or longer than <see cref="KeyRange.MaxKeyLength"/> bytes.</exception>
    public LedgerLookup? Find(ReadOnlySpan<byte> key) => Current.Find(key);

    /// <summary>
    /// Returns, in <see cref="Current"/>, the open ledger whose range contains <paramref name="key"/>, allocating nothing;
    /// <see langword="null"/> before the keyspace is bootstrapped.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="key"/> is empty or longer than <see cref="KeyRange.MaxKeyLength"/> bytes.</exception>
    public LedgerMetadata? FindOpen(ReadOnlySpan<byte> key) => Current.FindOpen(key);

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource<LedgerMapValue> NewRead() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Runs <paramref name="read"/>, then each read queued meanwhile, one at a time, until none is queued.
    /// </summary>
    private async Task RunReadsAsync(TaskCompletionSource<LedgerMapValue> read)
    {
        while (true)
        {
            try
            {
                var (value, _) = await LedgerMapValue.ReadAsync(_register, CancellationToken.None).ConfigureAwait(false);

                // Current is never published without its lookup index.
                _ = value.BuildIndex();
                Raise(value);
                read.SetResult(value);
            }
            catch (Exception exception)
            {
                read.SetException(exception);
            }

            lock (_lock)
            {
                _runningRead = _queuedRead;
                _queuedRead = null;
                if (_runningRead is null)
                {
                    return;
                }

                read = _runningRead;
            }
        }
    }

    /// <summary>
    /// Raises <see cref="Current"/> to <paramref name="value"/> when it is newer: a value the map's committer read, or wrote
    /// with a write which landed, so durable as every value the view holds (ledgers.md §2.6).
    /// </summary>
    internal void Observe(LedgerMapValue value)
    {
        // Current is never published without its lookup index.
        _ = value.BuildIndex();
        Raise(value);
    }

    /// <summary>Raises <see cref="Current"/> to <paramref name="value"/> when it is newer.</summary>
    private void Raise(LedgerMapValue value)
    {
        lock (_lock)
        {
            if (_current is null || value.Epoch > _current.Epoch)
            {
                Volatile.Write(ref _current, value);
                _ = _changed.TrySetResult();
                _changed = NewSignal();
            }
        }
    }
}
