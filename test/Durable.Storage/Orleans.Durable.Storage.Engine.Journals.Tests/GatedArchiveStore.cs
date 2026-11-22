using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Engine.Journals.Tests;

/// <summary>
/// An archive store which passes each call to another, and holds a read at the store while a test acts, so a load or a
/// read can be caught mid-read: <see cref="HoldNextRead"/> makes the next read wait, <see cref="ReadHeld"/> completes once
/// it has arrived, and <see cref="Release"/> lets it through. It counts its reads, can refuse one key
/// (<see cref="RefusedKey"/>), and can fail its next read as corrupt (<see cref="NextReadFailure"/>).
/// </summary>
internal sealed class GatedArchiveStore(IArchiveStore inner) : IArchiveStore
{
    private TaskCompletionSource? _gate;
    private bool _answerFirst;
    private TaskCompletionSource? _release;
    private TaskCompletionSource _arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _reads;
    private int _arrivals;
    private TaskCompletionSource? _everyRead;
    private Exception? _nextReadFailure;

    public ArchiveStoreKind Kind => inner.Kind;

    public int? MaxAppendBytes => inner.MaxAppendBytes;

    /// <summary>Gets the number of reads the store answered or failed.</summary>
    public int Reads => Volatile.Read(ref _reads);

    /// <summary>Gets or sets a key the store cannot store (<c>AdmitsKey</c>).</summary>
    public byte[]? RefusedKey { get; set; }

    /// <summary>Gets or sets an exception the next read fails with, as a store whose bytes are damaged.</summary>
    public Exception? NextReadFailure
    {
        get => Volatile.Read(ref _nextReadFailure);
        set => Volatile.Write(ref _nextReadFailure, value);
    }

    /// <summary>Gets a task which completes once a held read has arrived at the store.</summary>
    public Task ReadHeld => _arrived.Task;

    /// <summary>Gets a task which completes once a held read has ended: released, or cancelled by its reader.</summary>
    public Task HeldReadEnded => _ended.Task;

    /// <summary>
    /// Makes the next read wait at the store until <see cref="Release"/>: before the store answers it, or, with
    /// <paramref name="afterAnswer"/>, once the store has answered it, so it returns the store's state when it arrived.
    /// </summary>
    public void HoldNextRead(bool afterAnswer = false)
    {
        _answerFirst = afterAnswer;
        _arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _gate = _release;
    }

    /// <summary>Lets the held read through.</summary>
    public void Release() => _release?.TrySetResult();

    /// <summary>Withdraws a hold <see cref="HoldNextRead"/> made which no read has met, so the next read passes.</summary>
    public void Withdraw() => Volatile.Write(ref _gate, null);

    /// <summary>Gets the number of reads which arrived at the store while <see cref="HoldEveryRead"/> held them.</summary>
    public int HeldArrivals => Volatile.Read(ref _arrivals);

    /// <summary>Makes every read wait at the store, before the store answers it, until <see cref="ReleaseEveryRead"/>.</summary>
    public void HoldEveryRead() => Volatile.Write(ref _everyRead, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

    /// <summary>Lets every read <see cref="HoldEveryRead"/> held through, and holds no more.</summary>
    public void ReleaseEveryRead() => Interlocked.Exchange(ref _everyRead, null)?.TrySetResult();

    public async ValueTask<ArchiveStoreRead> ReadAsync(ReadOnlyMemory<byte> key, LedgerMapValue map, CancellationToken cancellationToken)
    {
        _ = Interlocked.Increment(ref _reads);
        if (Interlocked.Exchange(ref _nextReadFailure, null) is { } failure)
        {
            throw failure;
        }

        if (Volatile.Read(ref _everyRead) is { } everyRead)
        {
            _ = Interlocked.Increment(ref _arrivals);
            await everyRead.Task.WaitAsync(cancellationToken);
        }

        if (Interlocked.Exchange(ref _gate, null) is not { } gate)
        {
            return await inner.ReadAsync(key, map, cancellationToken);
        }

        ArchiveStoreRead? answer = _answerFirst ? await inner.ReadAsync(key, map, cancellationToken) : default(ArchiveStoreRead?);
        _ = _arrived.TrySetResult();
        try
        {
            await gate.Task.WaitAsync(cancellationToken);
        }
        catch
        {
            (answer?.Value as IDisposable)?.Dispose();
            throw;
        }
        finally
        {
            _ = _ended.TrySetResult();
        }

        return answer ?? await inner.ReadAsync(key, map, cancellationToken);
    }

    public ValueTask<ArchiveStoreRecord> StoreRecordAsync(IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken) =>
        inner.StoreRecordAsync(parameters, cancellationToken);

    public ArchivePlan Begin(RowToken token) => inner.Begin(token);

    public bool Holds(ArchivePrefix prefix, ArchiveWrite write, int count) => inner.Holds(prefix, write, count);

    public bool IsReplaceRequested(RowToken token) => inner.IsReplaceRequested(token);

    public bool AdmitsKey(ReadOnlySpan<byte> key) => !key.SequenceEqual(RefusedKey) && inner.AdmitsKey(key);
}
