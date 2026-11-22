using System.Runtime.CompilerServices;
using Orleans.Durable.Storage.Engine.Buffers;

namespace Orleans.Durable.Storage.Engine.Ledgers;

/// <summary>
/// Shared point-wise <see cref="ILedgerStorage.ReadRangeAsync"/> implementation for storages whose slots live in separate
/// requests (<see cref="LedgerQuorum"/> and per-slot <see cref="Objects.ObjectLedgerStorage"/>): pipelines up to
/// <see cref="ILedgerStorage.WindowSize"/> reads in slot order within <c>maxReadAheadBytes</c> and an optional shared
/// in-flight window, and releases every unread buffer when enumeration ends early or fails.
/// </summary>
internal static class LedgerRangeReads
{
    /// <summary>
    /// Reads slots <c>[from, below)</c> through <see cref="ILedgerStorage.ReadAsync(Slot, Slot, CancellationToken)"/> in
    /// slot order, pipelining up to <see cref="ILedgerStorage.WindowSize"/> reads within
    /// <paramref name="maxReadAheadBytes"/> and <paramref name="window"/>.
    /// </summary>
    public static IAsyncEnumerable<ReplicaRead> ReadPointWiseAsync(
        ILedgerStorage storage,
        Slot from,
        Slot below,
        Slot acknowledged,
        long maxReadAheadBytes,
        SemaphoreSlim? window,
        Action<long>? onReadIssued,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentOutOfRangeException.ThrowIfLessThan(below, from);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxReadAheadBytes);
        return ReadCoreAsync(storage, from, below, acknowledged, maxReadAheadBytes, window, onReadIssued, cancellationToken);
    }

    private static async IAsyncEnumerable<ReplicaRead> ReadCoreAsync(
        ILedgerStorage storage,
        Slot from,
        Slot below,
        Slot acknowledged,
        long maxReadAheadBytes,
        SemaphoreSlim? window,
        Action<long>? onReadIssued,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (from == below)
        {
            yield break;
        }

        using var readAhead = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var readToken = readAhead.Token;
        var reads = new Queue<PendingRead>(Math.Min(storage.WindowSize, 64));
        var next = from;
        long syncHeldBytes = 0;
        var asyncInFlight = 0;
        try
        {
            await IssueAsync().ConfigureAwait(false);
            while (reads.TryDequeue(out var pending))
            {
                ReplicaRead read;
                try
                {
                    if (pending.Task is { } task)
                    {
                        asyncInFlight--;
                        read = await task.ConfigureAwait(false);
                    }
                    else
                    {
                        syncHeldBytes -= pending.HeldBytes;
                        read = pending.Completed;
                    }
                }
                finally
                {
                    _ = window?.Release();
                }

                if (!read.TryGetValue(out ArcBuffer batch))
                {
                    yield return read;
                    yield break;
                }

                try
                {
                    await IssueAsync().ConfigureAwait(false);
                }
                catch
                {
                    batch.Dispose();
                    throw;
                }

                yield return read;
            }
        }
        finally
        {
            if (asyncInFlight > 0)
            {
                await readAhead.CancelAsync().ConfigureAwait(false);
            }

            while (reads.TryDequeue(out var pending))
            {
                try
                {
                    ReplicaRead read;
                    if (pending.Task is { } task)
                    {
                        asyncInFlight--;
                        read = await task.ConfigureAwait(false);
                    }
                    else
                    {
                        syncHeldBytes -= pending.HeldBytes;
                        read = pending.Completed;
                    }

                    if (read.TryGetValue(out ArcBuffer unread))
                    {
                        unread.Dispose();
                    }
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    // A read-ahead the caller no longer needs failed; the caller already has its answer or exception.
                }
                finally
                {
                    _ = window?.Release();
                }
            }
        }

        async ValueTask IssueAsync()
        {
            await IssueAllAsync().ConfigureAwait(false);
            if (reads.Count == 0 && next < below && window is not null)
            {
                await window.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await EnqueueAsync().ConfigureAwait(false);
                }
                catch
                {
                    _ = window.Release();
                    throw;
                }

                await IssueAllAsync().ConfigureAwait(false);
            }
        }

        async ValueTask IssueAllAsync()
        {
            while (reads.Count < storage.WindowSize && next < below && HeldBytes() < maxReadAheadBytes && (window is null || window.Wait(0, CancellationToken.None)))
            {
                try
                {
                    await EnqueueAsync().ConfigureAwait(false);
                }
                catch
                {
                    _ = window?.Release();
                    throw;
                }
            }
        }

        async ValueTask EnqueueAsync()
        {
            onReadIssued?.Invoke(HeldBytes());
            var readTask = storage.ReadAsync(next, acknowledged, readToken);
            next += 1;
            if (readTask.IsCompletedSuccessfully)
            {
                var completed = await readTask.ConfigureAwait(false);
                var heldBytes = completed.TryGetValue(out ArcBuffer data) ? data.Length : 0;
                syncHeldBytes += heldBytes;
                reads.Enqueue(PendingRead.FromResult(completed, heldBytes));
            }
            else
            {
                asyncInFlight++;
                reads.Enqueue(PendingRead.FromTask(readTask.AsTask()));
            }
        }

        long HeldBytes()
        {
            var bytes = syncHeldBytes;
            if (asyncInFlight == 0)
            {
                return bytes;
            }

            foreach (var pending in reads)
            {
                if (pending.Task is { IsCompletedSuccessfully: true } task && task.Result.TryGetValue(out ArcBuffer data))
                {
                    bytes += data.Length;
                }
            }

            return bytes;
        }
    }

    private readonly struct PendingRead
    {
        private PendingRead(Task<ReplicaRead>? task, ReplicaRead completed, int heldBytes)
        {
            Task = task;
            Completed = completed;
            HeldBytes = heldBytes;
        }

        public Task<ReplicaRead>? Task { get; }

        public ReplicaRead Completed { get; }

        public int HeldBytes { get; }

        public static PendingRead FromResult(ReplicaRead completed, int heldBytes) => new(null, completed, heldBytes);

        public static PendingRead FromTask(Task<ReplicaRead> task) => new(task, default, 0);
    }
}
