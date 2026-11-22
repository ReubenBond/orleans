using Orleans.Durable.Storage.Engine.Archive.Lsm.Control;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.MetaStore;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Durable.Storage.Engine.ObjectStore.Memory;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Archive.Lsm.Tests;

/// <summary>
/// The LSM over live ledgers for the drain, store, and drop tests: one node's process over in-memory replicas and a
/// ledger map register, bootstrapped; an in-memory object store behind a probe; and the manifest's committer with its
/// view.
/// </summary>
internal sealed class ArchiveWorld : IAsyncDisposable
{
    private LiveLedgers? _ledgers;

    private ArchiveWorld()
    {
        Store = new ProbeObjectStore(Objects);
        Manifest = new ManifestView(Store);
        Committer = new ManifestCommitter(Store, Manifest, TimeProvider.System, TimeSpan.Zero);
    }

    public InMemoryObjectStore Objects { get; } = new();

    public ProbeObjectStore Store { get; }

    /// <summary>Gets the committer's manifest view, which holds every value it applied.</summary>
    public ManifestView Manifest { get; }

    public ManifestCommitter Committer { get; }

    public LiveLedgers Ledgers => _ledgers ?? throw new InvalidOperationException("The world has not started.");

    /// <summary>Gets the number of versioned reads of the manifest begun.</summary>
    public int ManifestReads => Store.Calls.Count(static call => call == "ReadVersioned(manifest)");

    /// <summary>Gets the number of calls made of the store, a mark from which <see cref="CreatesSince"/> counts.</summary>
    public int CallMark => Store.Calls.Count;

    /// <summary>Returns the number of creates begun since <paramref name="mark"/>.</summary>
    public int CreatesSince(int mark) => Store.Calls.Skip(mark).Count(static call => call.StartsWith("Create(", StringComparison.Ordinal));

    /// <summary>Returns the number of the manifest's replaces begun since <paramref name="mark"/>.</summary>
    public int CommitsSince(int mark) => Store.Calls.Skip(mark).Count(static call => call == "Replace(manifest)");

    /// <summary>Returns the number of ranged reads of <paramref name="name"/> begun: its tail's and its blocks'.</summary>
    public int RangedReads(ObjectName name) => Store.Calls.Count(call => call == "Read(" + name.Value + ")");

    /// <summary>Returns the number of ranged reads of drain outputs begun.</summary>
    public int DrainOutputReads() => Store.Calls.Count(static call => call.StartsWith("Read(d/", StringComparison.Ordinal));

    public static async Task<ArchiveWorld> StartAsync(CancellationToken cancellationToken)
    {
        var world = new ArchiveWorld();
        try
        {
            world._ledgers = await LiveLedgers.StartAsync(new InMemoryConditionalRegister(), TimeProvider.System, cancellationToken);
            await world._ledgers.BootstrapAsync(cancellationToken);
            return world;
        }
        catch
        {
            await world.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Returns a drain worker of its own process, with a manifest view of its own unless one is given, as the
    /// <c>lsm-mid-segment-watermark</c> mutant when <paramref name="insideSegments"/>, and with
    /// <paramref name="backpressureRetries"/> resumes after backpressure, a millisecond apart.
    /// </summary>
    public DrainWorker Worker(ManifestView? view = null, ArchiveOptions? options = null, bool insideSegments = false, int backpressureRetries = 8, BlockCache? blocks = null) =>
        new(Store, view ?? new ManifestView(Store), Ledgers.Reader, options ?? new ArchiveOptions(), TimeProvider.System, blocks)
        {
            DrainsInsideSegmentsMutant = insideSegments,
            BackpressureRetries = backpressureRetries,
            BackpressureDelay = TimeSpan.FromMilliseconds(1),
        };

    /// <summary>Returns a archive store of its own process, with a manifest view of its own unless one is given.</summary>
    public LsmArchiveStore ArchiveStore(ManifestView? view = null, ArchiveOptions? options = null, TimeProvider? timeProvider = null, BlockCache? blocks = null)
    {
        options ??= new ArchiveOptions();
        return new(Store, view ?? new ManifestView(Store), new TailCache(Store, options), options, timeProvider ?? TimeProvider.System, blocks);
    }

    /// <summary>Submits a change to the manifest's committer, as a drain worker's caller does.</summary>
    public Task<ManifestCommitOutcome> CommitAsync(ManifestChange change, CancellationToken cancellationToken) =>
        Committer.CommitAsync(change, cancellationToken);

    /// <summary>Drains <paramref name="ledger"/> once toward <paramref name="below"/> through the committer.</summary>
    public Task<DrainOutcome> DrainOnceAsync(DrainWorker worker, LedgerMetadata ledger, Slot below, CancellationToken cancellationToken) =>
        worker.DrainAsync(ledger, below, CommitAsync, cancellationToken);

    /// <summary>Drains <paramref name="ledger"/> once below <paramref name="below"/>, checks the change applied, and returns it.</summary>
    public async Task<ManifestChange.Drain> DrainAndCommitAsync(DrainWorker worker, LedgerMetadata ledger, Slot below, CancellationToken cancellationToken)
    {
        var drained = Assert.IsType<DrainOutcome.Drained>((await DrainOnceAsync(worker, ledger, below, cancellationToken)).Value);
        _ = Assert.IsType<ManifestCommitOutcome.Applied>(drained.Outcome.Value);
        return drained.Change;
    }

    /// <summary>
    /// Ends the open segment of the open ledger <paramref name="ledger"/> with a marker and drains its closed segments to
    /// the marker's slot, as a failure-triggered drain does, with the bound the marker's durability shows, checks each
    /// drain applied, and returns the last change, which ends at the marker.
    /// </summary>
    public async Task<ManifestChange.Drain> DrainOpenAsync(DrainWorker worker, LedgerId ledger, CancellationToken cancellationToken)
    {
        var marker = await Ledgers.EndSegmentAsync(ledger, cancellationToken);
        while (true)
        {
            var change = await DrainAndCommitAsync(worker, Ledgers.Ledger(ledger), marker + 1, cancellationToken);
            if (change.Below == marker)
            {
                return change;
            }
        }
    }

    /// <summary>Drains a closed ledger's segments to its <c>T</c>, completing it, and returns the last change.</summary>
    public async Task<ManifestChange.Drain> CompleteAsync(DrainWorker worker, LedgerId ledger, CancellationToken cancellationToken)
    {
        var metadata = Ledgers.Ledger(ledger);
        while (true)
        {
            var change = await DrainAndCommitAsync(worker, metadata, Assert.IsType<LedgerStatus.Closed>(metadata.Status.Value).T, cancellationToken);
            if (change.Complete)
            {
                return change;
            }
        }
    }

    /// <summary>
    /// Closes <paramref name="ledger"/>, drains it to its <c>T</c>, completing it, and drops it, so a store read with the
    /// map after holds its rows in its prefix (archive-lsm.md §2.8).
    /// </summary>
    public async Task CloseCompleteAndDropAsync(LedgerId ledger, CancellationToken cancellationToken)
    {
        await Ledgers.ReplaceAsync(ledger, cancellationToken);
        _ = await CompleteAsync(Worker(), ledger, cancellationToken);
        var dropper = new LedgerDropper(Ledgers.Map, Manifest, Microsoft.Extensions.Logging.Abstractions.NullLogger<LedgerDropper>.Instance);
        Assert.Equal([ledger], await dropper.DropCompleteAsync(Ledgers.CommitAsync, cancellationToken));
    }

    public async ValueTask DisposeAsync()
    {
        await Committer.DisposeAsync();
        if (_ledgers is { } ledgers)
        {
            await ledgers.DisposeAsync();
        }
    }
}
