using System.Globalization;
using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Archive.Memory;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Testing.Simulation;

namespace Orleans.Durable.Storage.Simulation;

/// <summary>
/// A simulated archive store over the memory store (archive.md §3, §9; docs/spec/verification.md §7.2): each read
/// reaches the store after a random latency, reads it at one instant, and returns after another, or answers
/// <see cref="ArchiveStoreRead.Stale"/>, as a store whose own snapshot proved out of date does, at
/// <see cref="StaleRate"/>. Drains give it each key's rows of closed segments through <see cref="Drain"/>, which applies at once; its pure
/// members are the memory store's.
/// </summary>
/// <remarks>
/// It counts its reads and its <c>Stale</c> answers, so a scenario sees how often its readers restarted. Disposal
/// releases every payload the store holds.
/// </remarks>
public sealed class SimulatedArchiveStore : IArchiveStore, IDisposable
{
    private readonly InMemoryArchiveStore _store;
    private readonly SimulationEnvironment _environment;
    private readonly SimulationRandom _random;

    /// <summary>Initializes a new instance of the <see cref="SimulatedArchiveStore"/> class, empty.</summary>
    /// <param name="environment">The simulation.</param>
    /// <param name="name">The store's name in the trace, which also selects its random source.</param>
    /// <param name="maxAppends">The appends a generation holds, or none for no limit, as the memory store takes it.</param>
    /// <param name="replaceRequestAppends">The appends at which the store asks for a replace, or none.</param>
    public SimulatedArchiveStore(SimulationEnvironment environment, string name, int? maxAppends = null, int? replaceRequestAppends = null)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _environment = environment;
        _random = environment.CreateRandom(name);
        var ids = environment.CreateRandom(name + "/ids");
        _store = new InMemoryArchiveStore(() => NextId(ids), maxAppends: maxAppends, replaceRequestAppends: replaceRequestAppends);
        Name = name;
    }

    /// <summary>Gets the store's name in the trace.</summary>
    public string Name { get; }

    /// <summary>Gets or sets the probability that a read answers <see cref="ArchiveStoreRead.Stale"/>.</summary>
    public double StaleRate { get; set; }

    /// <summary>Gets or sets the longest latency of a read, and separately of its answer.</summary>
    public TimeSpan MaxLatency { get; set; } = TimeSpan.FromMilliseconds(2);

    /// <summary>Gets the number of reads.</summary>
    public int Reads { get; private set; }

    /// <summary>Gets the number of reads answered <see cref="ArchiveStoreRead.Stale"/>.</summary>
    public int StaleAnswers { get; private set; }

    /// <summary>
    /// Gets or sets an action which runs once, after the store answers the next read which is not <c>Stale</c> and
    /// before its caller hears the answer: a scenario's drain and drop between a reader's store read and its scans.
    /// </summary>
    public Func<Task>? AfterNextRead { get; set; }

    /// <summary>
    /// Gets or sets the key whose read alone runs <see cref="AfterNextRead"/>, so a scenario's step runs it within its own
    /// read rather than another's; empty, the default, lets the next read of any key run it.
    /// </summary>
    public ReadOnlyMemory<byte> AfterNextReadOf { get; set; }

    /// <inheritdoc/>
    public ArchiveStoreKind Kind => _store.Kind;

    /// <inheritdoc/>
    public int? MaxAppendBytes => _store.MaxAppendBytes;

    /// <summary>Takes one key's rows of a closed segment a drain read (<see cref="InMemoryArchiveStore.Drain"/>).</summary>
    public void Drain(ReadOnlyMemory<byte> key, ReadOnlySpan<ArchiveRow> rows)
    {
        _store.Drain(key, rows);
        _environment.Trace.Record(string.Create(CultureInfo.InvariantCulture, $"archive store {Name}: key {Convert.ToHexStringLower(key.Span)} of segment ({rows[0].Segment.Ledger}, {rows[0].Segment.Start}) drained"));
    }

    /// <inheritdoc/>
    public async ValueTask<ArchiveStoreRead> ReadAsync(ReadOnlyMemory<byte> key, LedgerMapValue map, CancellationToken cancellationToken)
    {
        Reads++;
        await DelayAsync(cancellationToken);
        if (_random.Chance(StaleRate))
        {
            StaleAnswers++;
            await DelayAsync(cancellationToken);
            return ArchiveStoreRead.Stale.Instance;
        }

        var answer = await _store.ReadAsync(key, map, cancellationToken);
        try
        {
            if (AfterNextRead is { } after && (AfterNextReadOf.IsEmpty || AfterNextReadOf.Span.SequenceEqual(key.Span)))
            {
                AfterNextRead = null;
                AfterNextReadOf = default;
                await after();
            }

            await DelayAsync(cancellationToken);
            return answer;
        }
        catch
        {
            if (answer.Value is ArchiveStoreRead.Read read)
            {
                read.Dispose();
            }

            throw;
        }
    }

    /// <inheritdoc/>
    public ValueTask<ArchiveStoreRecord> StoreRecordAsync(IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken) =>
        _store.StoreRecordAsync(parameters, cancellationToken);

    /// <inheritdoc/>
    public ArchivePlan Begin(RowToken token) => _store.Begin(token);

    /// <inheritdoc/>
    public bool Holds(ArchivePrefix prefix, ArchiveWrite write, int count) => _store.Holds(prefix, write, count);

    /// <inheritdoc/>
    public bool IsReplaceRequested(RowToken token) => _store.IsReplaceRequested(token);

    /// <inheritdoc/>
    public bool AdmitsKey(ReadOnlySpan<byte> key) => _store.AdmitsKey(key);

    /// <summary>Releases every payload the store holds.</summary>
    public void Dispose() => _store.Dispose();

    /// <summary>Draws a non-zero ID from the seeded <paramref name="random"/>.</summary>
    private static UInt128 NextId(SimulationRandom random)
    {
        Span<byte> bytes = stackalloc byte[16];
        var value = UInt128.Zero;
        while (value == UInt128.Zero)
        {
            random.NextBytes(bytes);
            value = System.Buffers.Binary.BinaryPrimitives.ReadUInt128LittleEndian(bytes);
        }

        return value;
    }

    private Task DelayAsync(CancellationToken cancellationToken) =>
        MaxLatency <= TimeSpan.Zero
            ? Task.CompletedTask
            : Task.Delay(SimulationEnvironment.WholeMilliseconds(_random.NextTimeSpan(MaxLatency)), SimulationEnvironment.Clock, cancellationToken);
}
