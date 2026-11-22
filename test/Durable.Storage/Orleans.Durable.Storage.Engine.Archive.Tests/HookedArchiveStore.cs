using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Engine.Archive.Tests;

/// <summary>
/// An archive store which passes each call to another, records the map value of each read, and lets a test answer reads
/// <c>Stale</c>, fail them, or act before or after a read reaches the store.
/// </summary>
internal sealed class HookedArchiveStore(IArchiveStore inner) : IArchiveStore
{
    private readonly List<ulong> _readEpochs = [];

    /// <summary>Gets or sets the number of reads still to answer <c>Stale</c>, before they reach the store.</summary>
    public int StaleAnswers { get; set; }

    /// <summary>Gets or sets the exception the next read throws, before it reaches the store.</summary>
    public Exception? Failure { get; set; }

    /// <summary>Gets or sets an action which runs once, before the next read reaches the store.</summary>
    public Func<CancellationToken, Task>? BeforeNextRead { get; set; }

    /// <summary>Gets or sets an action which runs once, after the next read the store answers, before the caller hears.</summary>
    public Func<Task>? AfterNextRead { get; set; }

    /// <summary>Gets or sets a store which answers the next read in the inner store's place, once.</summary>
    public IArchiveStore? NextReadFrom { get; set; }

    /// <summary>Gets the epoch of each read's map value, in call order.</summary>
    public IReadOnlyList<ulong> ReadEpochs
    {
        get
        {
            lock (_readEpochs)
            {
                return [.. _readEpochs];
            }
        }
    }

    public async ValueTask<ArchiveStoreRead> ReadAsync(ReadOnlyMemory<byte> key, LedgerMapValue map, CancellationToken cancellationToken)
    {
        lock (_readEpochs)
        {
            _readEpochs.Add(map.Epoch);
        }

        if (Failure is { } failure)
        {
            Failure = null;
            throw failure;
        }

        if (StaleAnswers > 0)
        {
            StaleAnswers--;
            return ArchiveStoreRead.Stale.Instance;
        }

        if (BeforeNextRead is { } before)
        {
            BeforeNextRead = null;
            await before(cancellationToken);
        }

        var from = NextReadFrom ?? inner;
        NextReadFrom = null;
        var answer = await from.ReadAsync(key, map, cancellationToken);
        if (AfterNextRead is { } after)
        {
            AfterNextRead = null;
            await after();
        }

        return answer;
    }

    public ArchiveStoreKind Kind => inner.Kind;

    public int? MaxAppendBytes => inner.MaxAppendBytes;

    public ValueTask<ArchiveStoreRecord> StoreRecordAsync(IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken) =>
        inner.StoreRecordAsync(parameters, cancellationToken);

    public ArchivePlan Begin(RowToken token) => inner.Begin(token);

    public bool Holds(ArchivePrefix prefix, ArchiveWrite write, int count) => inner.Holds(prefix, write, count);

    public bool IsReplaceRequested(RowToken token) => inner.IsReplaceRequested(token);

    public bool AdmitsKey(ReadOnlySpan<byte> key) => inner.AdmitsKey(key);
}
