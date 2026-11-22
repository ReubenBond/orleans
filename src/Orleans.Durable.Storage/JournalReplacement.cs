using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Journaling;
using Orleans.Metadata;
using Orleans.Runtime;

namespace Orleans.Durable.Storage;

/// <summary>
/// Re-places the silo's journaled grains on their journals' primaries (orleans.md §5.4): it tracks each activation whose
/// journal storage the silo's <see cref="DurableJournalStorageProvider"/> creates for the activation's own journal, when its
/// grain type is placed by <see cref="JournalPlacement"/>, from that creation until the activation is deactivated; and in
/// each pass it asks every tracked activation which <see cref="JournalPlacementDirector"/>, on this silo's views, would
/// place on another silo to migrate (<see cref="IGrainContext.Migrate"/>), at most <see cref="MaxMigrationsPerPass"/> a
/// pass. The director places each migration, on this silo, with the same views.
/// </summary>
/// <remarks>
/// A pass runs when the node's map view changes the primary of an open ledger, when membership changes, and at least every
/// <c>MapRefreshInterval</c>, which brings registry changes, a target silo which became active, and what a pass's bound
/// left. The rule is the director's own, so the activations it would place elsewhere are asked until they move, and no
/// other is: one whose type the primary's silo does not host, or whose primary has no active registered silo, stays. A
/// repeated ask is harmless: <see cref="IGrainContext.Migrate"/> does nothing for an activation still being created or
/// already deactivating, and the next pass asks again. Migration carries none of the journaled state: the new activation
/// recovers it by reading its journal, on the primary a local read. The loop runs in no caller's execution context
/// (conventions.md §12).
/// </remarks>
internal sealed partial class JournalReplacement : ILifecycleParticipant<ISiloLifecycle>, IDisposable
{
    /// <summary>The most migrations a pass asks for; the next pass asks for the rest.</summary>
    internal const int MaxMigrationsPerPass = 100;

    private readonly ConcurrentDictionary<IGrainContext, bool> _tracked = new();
    private readonly LocalRouting _routing;
    private readonly JournalPlacementDirector _director;
    private readonly IClusterMembershipService _membership;
    private readonly IClusterManifestProvider _manifests;
    private readonly SiloAddress _silo;
    private readonly IGrainContextAccessor _contexts;
    private readonly Func<GrainType, bool> _placedByJournal;
    private readonly TimeSpan _sweepInterval;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly Meter _meter = new(DurableStorageMeter.Name);
    private readonly Counter<long> _migrationCounter;
    private readonly CancellationTokenSource _stopping = new();
    private Task _loop = Task.CompletedTask;
    private long _migrations;

    /// <summary>Initializes a new instance of the <see cref="JournalReplacement"/> class.</summary>
    /// <param name="routing">The node's routing, whose map view the director reads.</param>
    /// <param name="director">The placement director of <see cref="JournalPlacement"/>.</param>
    /// <param name="membership">The silo's membership view, whose changes start a pass.</param>
    /// <param name="manifests">The cluster manifest, which names the silos hosting each grain type.</param>
    /// <param name="silo">The silo.</param>
    /// <param name="contexts">Names the activation whose journal storage is being created.</param>
    /// <param name="placedByJournal">Whether a grain type is placed by <see cref="JournalPlacement"/>.</param>
    /// <param name="sweepInterval">The longest time between passes, the cluster's <c>MapRefreshInterval</c>.</param>
    /// <param name="timeProvider">Times the sweeps.</param>
    /// <param name="loggers">The service's logger.</param>
    public JournalReplacement(
        LocalRouting routing,
        JournalPlacementDirector director,
        IClusterMembershipService membership,
        IClusterManifestProvider manifests,
        ILocalSiloDetails silo,
        IGrainContextAccessor contexts,
        Func<GrainType, bool> placedByJournal,
        TimeSpan sweepInterval,
        TimeProvider timeProvider,
        ILoggerFactory loggers)
    {
        ArgumentNullException.ThrowIfNull(silo);
        ArgumentNullException.ThrowIfNull(loggers);
        _routing = routing;
        _director = director;
        _membership = membership;
        _manifests = manifests;
        _silo = silo.SiloAddress;
        _contexts = contexts;
        _placedByJournal = placedByJournal;
        _sweepInterval = sweepInterval;
        _timeProvider = timeProvider;
        _logger = loggers.CreateLogger<JournalReplacement>();
        _migrationCounter = _meter.CreateCounter<long>("orleans.durable.storage.journal.migrations", "{activation}", "The migrations the silo asked of journaled activations which the journal placement director would place on another silo.");
    }

    /// <summary>Gets how many activations the service tracks; for tests.</summary>
    internal int TrackedCount => _tracked.Count;

    /// <summary>Gets how many migrations the service has asked for.</summary>
    internal long Migrations => Interlocked.Read(ref _migrations);

    /// <summary>
    /// Tracks the activation the storage of <paramref name="journal"/> is created for, when it is the activation's own
    /// journal and its grain type is placed by <see cref="JournalPlacement"/>, until the activation is deactivated.
    /// </summary>
    public void Track(JournalId journal)
    {
        var context = _contexts.GrainContext;
        if (JournalId.FromGrainId(context.GrainId).Value != journal.Value || !_placedByJournal(context.GrainId.Type) || !_tracked.TryAdd(context, true))
        {
            return;
        }

        _ = context.Deactivated.ContinueWith(
            static (_, state) =>
            {
                var (owner, deactivated) = ((JournalReplacement, IGrainContext))state!;
                owner.Untrack(deactivated);
            },
            (this, context),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>
    /// Runs a pass: asks each tracked activation which the director would place on another silo to migrate, at most
    /// <see cref="MaxMigrationsPerPass"/>; returns how many it asked.
    /// </summary>
    internal int Replace()
    {
        var manifest = _manifests.Current;
        var compatible = new Dictionary<GrainType, SiloAddress[]>();
        var asked = 0;
        foreach (var context in _tracked.Keys)
        {
            if (asked == MaxMigrationsPerPass)
            {
                break;
            }

            var grain = context.GrainId;
            if (!compatible.TryGetValue(grain.Type, out var silos))
            {
                compatible[grain.Type] = silos = Hosting(manifest, grain.Type);
            }

            if (_director.PrimarySilo(grain, silos) is { } target && target != _silo)
            {
                LogMigrating(_logger, grain, target);
                context.Migrate(requestContext: null);
                _ = Interlocked.Increment(ref _migrations);
                _migrationCounter.Add(1);
                asked++;
            }
        }

        return asked;
    }

    /// <inheritdoc/>
    public void Participate(ISiloLifecycle observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        _ = observer.Subscribe(nameof(JournalReplacement), ServiceLifecycleStage.Active, _ => Start(), _ => StopAsync());
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _meter.Dispose();
        _stopping.Dispose();
    }

    /// <summary>Returns the silos whose grain manifests in <paramref name="manifest"/> hold <paramref name="type"/>.</summary>
    private static SiloAddress[] Hosting(ClusterManifest manifest, GrainType type)
    {
        var declared = GenericGrainType.TryParse(type, out var generic) && generic.IsConstructed ? generic.GetUnconstructedGrainType().GrainType : type;
        return [.. manifest.Silos.Where(silo => silo.Value.Grains.ContainsKey(declared)).Select(static silo => silo.Key)];
    }

    /// <summary>Returns the open ledgers' ranges and primaries in <paramref name="map"/>, which decide every placement.</summary>
    private static ImmutableArray<(KeyRange Range, NodeId Primary)> Primaries(LedgerMapValue map) =>
        [.. map.Ledgers.Where(static ledger => ledger.IsOpen).Select(static ledger => (ledger.Range, ledger.Primary.Node))];

    private void Untrack(IGrainContext context) => _ = _tracked.TryRemove(context, out _);

    private Task Start()
    {
        using (ExecutionContext.SuppressFlow())
        {
            _loop = Task.Run(() => RunAsync(_stopping.Token), CancellationToken.None);
        }

        return Task.CompletedTask;
    }

    private async Task StopAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        await _loop.ConfigureAwait(false);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var members = _membership.MembershipUpdates.GetAsyncEnumerator(cancellationToken);
        Task<bool>? membershipChanged = null;
        var ended = new TaskCompletionSource<bool>().Task;
        try
        {
            var current = await _routing.ReadyAsync(cancellationToken).ConfigureAwait(false);
            var epoch = current.View.Current.Epoch;
            var primaries = Primaries(current.View.Current);
            Task<LedgerMapValue>? mapChanged = null;
            Task? sweep = null;
            while (true)
            {
                mapChanged ??= current.View.WaitForChangeAsync(epoch, cancellationToken);
                membershipChanged ??= members.MoveNextAsync().AsTask();
                sweep ??= Task.Delay(_sweepInterval, _timeProvider, cancellationToken);
                var woke = await Task.WhenAny(mapChanged, membershipChanged, sweep).ConfigureAwait(false);
                var pass = true;
                if (woke == mapChanged)
                {
                    var map = await mapChanged.ConfigureAwait(false);
                    (mapChanged, epoch) = (null, map.Epoch);
                    var next = Primaries(map);
                    pass = !next.SequenceEqual(primaries);
                    primaries = next;
                }
                else if (woke == membershipChanged)
                {
                    // A membership stream which ends starts no more passes; the sweeps go on.
                    membershipChanged = await membershipChanged.ConfigureAwait(false) ? null : ended;
                }
                else
                {
                    await sweep.ConfigureAwait(false);
                    sweep = null;
                }

                if (pass)
                {
                    _ = Replace();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception failure)
        {
            LogLoopFailed(_logger, failure);
            throw;
        }
        finally
        {
            // An async enumerator is disposed only once its pending move has ended, which the cancellation ends.
            if (membershipChanged is not null && membershipChanged != ended)
            {
                await ((Task)membershipChanged).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }

            await members.DisposeAsync().ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Asking grain {Grain} to migrate to silo {Silo}, its journal's primary's.")]
    private static partial void LogMigrating(ILogger logger, GrainId grain, SiloAddress silo);

    [LoggerMessage(Level = LogLevel.Error, Message = "The re-placement of journaled grains failed; the silo re-places no grain until it restarts.")]
    private static partial void LogLoopFailed(ILogger logger, Exception exception);
}
