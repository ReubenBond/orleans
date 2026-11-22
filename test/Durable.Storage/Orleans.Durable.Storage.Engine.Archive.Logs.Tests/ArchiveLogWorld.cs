using System.Buffers;
using System.Collections.Immutable;
using System.Text;
using Orleans.Durable.Storage.Engine.Archive.Logs.Control;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.MetaStore;
using Orleans.Durable.Storage.Engine.ObjectStore.Memory;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Archive.Logs.Tests;

/// <summary>
/// Archive logs over live ledgers for the drain and sweep tests: one node's process over in-memory replicas and a ledger
/// map register, bootstrapped, whose rows store the token chains of a <see cref="LogArchiveStore"/>; a memory archive-log
/// store behind a probe, which records every request and every write in flight and injects faults; and an in-memory object
/// store of durable storage's objects under <c>durable-storage/</c>.
/// </summary>
internal sealed class ArchiveLogWorld : IAsyncDisposable
{
    private readonly List<ArchiveLogRequest> _requests = [];
    private readonly Dictionary<string, List<(RowKind Kind, byte[] Body)>> _proposed = [with(StringComparer.Ordinal)];
    private LiveLedgers? _ledgers;
    private ArchiveLogGate? _gate;

    private ArchiveLogWorld(ArchiveLogLayout? layout, ArchiveLogNames? names)
    {
        Memory = new MemoryArchiveLogStore(layout, request =>
        {
            lock (_requests)
            {
                _requests.Add(request);
            }

            return Faults?.Invoke(request) ?? ArchiveLogFault.None;
        });
        Logs = new ProbeArchiveLogStore(Memory);
        Store = new LogArchiveStore(Logs, Objects, names ?? ArchiveLogNames.Hexadecimal);
    }

    /// <summary>Gets the memory store beneath the probe.</summary>
    public MemoryArchiveLogStore Memory { get; }

    /// <summary>Gets the probe over the memory store, which the drains and the sweeper use.</summary>
    public ProbeArchiveLogStore Logs { get; }

    public InMemoryObjectStore Objects { get; } = new();

    /// <summary>Gets the archive gate over <see cref="Store"/>, which the world's drains and sweeps take.</summary>
    public ArchiveLogGate Gate => _gate ?? throw new InvalidOperationException("The world has not started.");

    public LogArchiveStore Store { get; }

    public LiveLedgers Ledgers => _ledgers ?? throw new InvalidOperationException("The world has not started.");

    /// <summary>Gets or sets the faults the memory store injects, by request.</summary>
    public Func<ArchiveLogRequest, ArchiveLogFault>? Faults { get; set; }

    /// <summary>Gets the number of requests the memory store has seen, a mark from which <see cref="WritesSince"/> counts.</summary>
    public int RequestMark
    {
        get
        {
            lock (_requests)
            {
                return _requests.Count;
            }
        }
    }

    public static async Task<ArchiveLogWorld> StartAsync(CancellationToken cancellationToken, ArchiveLogLayout? layout = null, ArchiveLogNames? names = null)
    {
        var world = new ArchiveLogWorld(layout, names);
        try
        {
            world._gate = await PassGateAsync(world.Store, cancellationToken);
            world._ledgers = await LiveLedgers.StartAsync(new InMemoryConditionalRegister(), TimeProvider.System, cancellationToken, chains: new ArchiveChains(world.Store));
            await world._ledgers.BootstrapAsync(cancellationToken);
            return world;
        }
        catch
        {
            await world.DisposeAsync();
            throw;
        }
    }

    /// <summary>Returns the write requests (creates, appends, bases, and generations) the memory store has seen since <paramref name="mark"/>.</summary>
    public ArchiveLogRequest[] WritesSince(int mark)
    {
        lock (_requests)
        {
            return [.. _requests.Skip(mark).Where(static request => request.Kind is ArchiveLogRequestKind.CreateLog or ArchiveLogRequestKind.AppendUnit or ArchiveLogRequestKind.CreateBase or ArchiveLogRequestKind.OpenGeneration)];
        }
    }

    /// <summary>Returns a drain worker of its own process, with its own map view unless the node's is asked for.</summary>
    public ArchiveLogDrainWorker Worker(ArchiveLogOptions? options = null, TimeProvider? timeProvider = null, bool ignoresLineage = false, bool marksUnheld = false, ArchiveLogGate? gate = null) =>
        new(gate ?? Gate, Ledgers.Map, Ledgers.Reader, options ?? new ArchiveLogOptions(), timeProvider ?? TimeProvider.System, new ArchiveLogWriterOptions { Delay = TimeSpan.FromMilliseconds(1) })
        {
            IgnoresLineageMutant = ignoresLineage,
            MarksUnheldMutant = marksUnheld,
            BackpressureDelay = TimeSpan.FromMilliseconds(1),
        };

    /// <summary>
    /// Passes the archive gate of <paramref name="store"/> with no caller parameters, as a silo does once the node registry
    /// records the store: its identity is the one the record names.
    /// </summary>
    public static async Task<ArchiveLogGate> PassGateAsync(LogArchiveStore store, CancellationToken cancellationToken)
    {
        var record = await store.StoreRecordAsync(ImmutableDictionary<string, string>.Empty, cancellationToken);
        return await ArchiveLogGate.PassAsync(store, new ArchiveStoreIdentity(ArchiveStoreKind.Logs, new ArchiveStorePrefix("t"), record.Id), ImmutableDictionary<string, string>.Empty, cancellationToken);
    }

    /// <summary>Proposes one batch of rows to the open ledger <paramref name="ledger"/>, recording each key's commands for the fold.</summary>
    public async Task ProposeAsync(LedgerId ledger, params (string Key, RowKind Kind, string Body)[] rows)
    {
        _ = await Ledgers.ProposeAsync(ledger, [.. rows.Select(static row => LiveRow.Of(row.Key, row.Kind, row.Body))], CancellationToken.None);
        foreach (var (key, kind, body) in rows)
        {
            if (!_proposed.TryGetValue(key, out var commands))
            {
                _proposed.Add(key, commands = []);
            }

            commands.Add((kind, Encoding.UTF8.GetBytes(body)));
        }
    }

    /// <summary>Returns the keys proposed so far.</summary>
    public IEnumerable<string> Keys => _proposed.Keys;

    /// <summary>Returns the fold of every command proposed of <paramref name="key"/>.</summary>
    public ArchiveFold Fold(string key) => ArchiveOracle.Fold(_proposed[key], static row => row.Kind, static row => row.Body);

    /// <summary>Returns the state the store's read of <paramref name="key"/> with the node's current map value gives.</summary>
    public async Task<ArchiveFold> ReadAsync(string key, CancellationToken cancellationToken)
    {
        switch ((await Store.ReadAsync(Encoding.UTF8.GetBytes(key), Ledgers.Map.Current, cancellationToken)).Value)
        {
            case ArchiveStoreRead.Read read:
                using (read)
                {
                    return ArchiveFold.Of(read.Prefix);
                }

            case ArchiveStoreRead.Absent:
                return ArchiveFold.Nothing;
            default:
                throw new InvalidOperationException("The memory store answered a read Stale.");
        }
    }

    /// <summary>Returns the name of <paramref name="key"/>'s log.</summary>
    public static ArchiveLogName NameOf(string key) => ArchiveLogNames.Hexadecimal.Of(Encoding.UTF8.GetBytes(key));

    /// <summary>Returns the drain markers under <c>durable-storage/d/</c>, each as its ledger, start, and content's end.</summary>
    public async Task<(LedgerId Ledger, Slot Start, Slot End)[]> MarkersAsync(CancellationToken cancellationToken)
    {
        var markers = new List<(LedgerId, Slot, Slot)>();
        await foreach (var listed in Objects.ListAsync(ArchiveLogNames.DrainMarkerPrefix, cancellationToken: cancellationToken))
        {
            var (ledger, start) = ArchiveLogNames.ParseDrainMarker(listed.Name.Value) ?? throw new InvalidDataException($"'{listed.Name}' is no drain marker.");
            var stored = (await Objects.ReadVersionedAsync(listed.Name, cancellationToken))!.Value;
            using var content = stored.Content;
            markers.Add((ledger, start, ArchiveLogNames.ParseDrainMarkerContent(content.ToArray())!.Value));
        }

        return [.. markers];
    }

    /// <summary>Drains <paramref name="ledger"/>, the map view's, until the drain answers other than progress past its start.</summary>
    public async Task<ArchiveLogDrainOutcome> DrainAsync(ArchiveLogDrainWorker worker, LedgerId ledger, Slot below, CancellationToken cancellationToken) =>
        await worker.DrainAsync(Ledgers.Ledger(ledger), below, cancellationToken);

    /// <summary>Closes the open ledger <paramref name="ledger"/> by a replace, and returns its <c>T</c>.</summary>
    public async Task<Slot> CloseAsync(LedgerId ledger, CancellationToken cancellationToken)
    {
        await Ledgers.ReplaceAsync(ledger, cancellationToken);
        return Assert.IsType<LedgerStatus.Closed>(Ledgers.Ledger(ledger).Status.Value).T;
    }

    public async ValueTask DisposeAsync()
    {
        if (_ledgers is { } ledgers)
        {
            await ledgers.DisposeAsync();
        }
    }
}

/// <summary>
/// A probe over an archive-log store: it counts each key write in flight, and the most at once, and refuses the writes of
/// the logs a test names, as a store which no retry can make accept them.
/// </summary>
internal sealed class ProbeArchiveLogStore(IArchiveLogStore inner) : IArchiveLogStore
{
    private int _inFlight;

    /// <summary>Gets the most appends and openers in flight at once.</summary>
    public int MostInFlight { get; private set; }

    /// <summary>Gets or sets how long the probe holds each write in flight before running it, so concurrent writes overlap.</summary>
    public TimeSpan WriteDelay { get; set; }

    /// <summary>Gets the logs whose writes the store refuses.</summary>
    public HashSet<string> Refused { get; } = [with(StringComparer.Ordinal)];

    public ArchiveLogLayout Layout => inner.Layout;

    public bool AdmitsName(string name) => inner.AdmitsName(name);

    public ValueTask<ArchiveLogRead> ReadAsync(ArchiveLogName name, CancellationToken cancellationToken) => inner.ReadAsync(name, cancellationToken);

    public ValueTask<ArchiveLogProperties?> GetPropertiesAsync(ArchiveLogName name, CancellationToken cancellationToken) => inner.GetPropertiesAsync(name, cancellationToken);

    public async ValueTask<ArchiveLogWriteOutcome> AppendAsync(ArchiveLogName name, ArchiveLogPosition from, IReadOnlyList<ReadOnlySequence<byte>> run, CancellationToken cancellationToken)
    {
        ThrowIfRefused(name);
        await EnterAsync();
        try
        {
            return await inner.AppendAsync(name, from, run, cancellationToken);
        }
        finally
        {
            _ = Interlocked.Decrement(ref _inFlight);
        }
    }

    public async ValueTask<ArchiveLogWriteOutcome> OpenAsync(ArchiveLogName name, ArchiveLogPosition from, ArchiveLogOpener opener, ReadOnlySequence<byte> content, CancellationToken cancellationToken)
    {
        ThrowIfRefused(name);
        await EnterAsync();
        try
        {
            return await inner.OpenAsync(name, from, opener, content, cancellationToken);
        }
        finally
        {
            _ = Interlocked.Decrement(ref _inFlight);
        }
    }

    public IAsyncEnumerable<ArchiveLogGenerationPage> ListGenerationsAsync(string? continuation, CancellationToken cancellationToken) => inner.ListGenerationsAsync(continuation, cancellationToken);

    public ValueTask DeleteGenerationAsync(string log, ulong generation, CancellationToken cancellationToken) => inner.DeleteGenerationAsync(log, generation, cancellationToken);

    private void ThrowIfRefused(ArchiveLogName name)
    {
        if (Refused.Contains(name.Value))
        {
            throw new ArchiveLogRefusedException($"The probe refuses the log '{name}'.");
        }
    }

    private async Task EnterAsync()
    {
        var now = Interlocked.Increment(ref _inFlight);
        lock (Refused)
        {
            MostInFlight = Math.Max(MostInFlight, now);
        }

        if (WriteDelay > TimeSpan.Zero)
        {
            await Task.Delay(WriteDelay, TimeProvider.System, CancellationToken.None);
        }
    }
}
