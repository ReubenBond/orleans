using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Durable.Journaling;
using Orleans.Durable.Storage.Control;
using Orleans.Durable.Storage.Diagnostics;
using Orleans.Durable.Storage.Engine;
using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Archive.Control;
using Orleans.Durable.Storage.Engine.Archive.Logs;
using Orleans.Durable.Storage.Engine.Archive.Logs.Control;
using Orleans.Durable.Storage.Engine.Archive.Lsm;
using Orleans.Durable.Storage.Engine.Archive.Lsm.Control;
using Orleans.Durable.Storage.Engine.Cluster.Control;
using Orleans.Durable.Storage.Engine.Journals;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Control;
using Orleans.Durable.Storage.Engine.Ledgers.Objects;
using Orleans.Durable.Storage.Engine.Ledgers.Store;
using Orleans.Durable.Storage.Engine.MetaStore;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Runtime;
using Polly;
using Polly.Retry;

namespace Orleans.Durable.Storage.Hosting;

/// <summary>
/// The silo's Ledgers runtime (orleans.md §5.2, §5.6): once the node is registered it opens the node's view of
/// <c>ledger-map</c> and builds the <see cref="NodeRuntime"/>, the node's composition root, whose node controller it
/// publishes to the node control grain through <see cref="LocalNodeControl"/>, and the node's Journals, which it creates as
/// the silo's start check and serves through <see cref="LocalJournals"/> once the archive store's record is the registry's
/// store's and holds the silo's journal format and <c>MaxBodyBytes</c> (OR-I8, OR-I20), the check which passes or refuses
/// the silo's archive gate; it runs the node's control loop, and stops the runtime in one order.
/// </summary>
/// <remarks>
/// <para>
/// Its one loop, each <c>ControlInterval</c> or at once when a ledger becomes unserved: reads the map once
/// <c>MapRefreshInterval</c> has passed with no report reply confirming the view's epoch; reconciles the node's replicas
/// with the map, and sweeps its stray replicas each <c>ReplicaSweepInterval</c>; reports to the controller grain and
/// follows the reply's epoch (OR-I19), one report at a time beside the loop; and, while the node serves and its silo's
/// archive gate passed, starts the drains due which are not in flight, on the LSM or on archive logs, each of which ends on
/// its own beside the loop, within <c>MaxConcurrentDrains</c> in flight, the ledgers a dead replica exposes first: closed
/// ledgers naming a dead node, and the failure-triggered drain of each open ledger the node is primary of with only
/// <c>Q</c> replicas live in its membership view; and, while its Journals serve, starts an expiry scan each
/// <c>ExpiryInterval</c>, one at a time beside the loop, which proposes the <c>Expired</c>s of the tombstones whose
/// retention has passed (journals.md §8.6). A failed step
/// is logged, and the next interval runs.
/// </para>
/// <para>
/// Its governance loop, each <c>ControlInterval</c> on a timer of its own, so no other step's failure or stall holds it
/// (§5.9): measures the node's disk, records its pressure and its resources for the meter, observes its primaries' throttles, and, while the node is
/// under pressure, reports it to the primary of every open ledger the node replicates, one round at a time beside the
/// loop; and requests the WAL-release rolls its oldest replicas need (OR-I13), one request at a time beside the loop. Each step fails alone and is logged; a failed measurement which stopped the process ends the loop. The start
/// measures once before the node serves, so a restarted node under pressure throttles from its first write, and a
/// failed first measurement, or bounds which leave pressure no way to end, fail the silo's start.
/// </para>
/// <para>
/// It starts at the stage after the one at which the node registers, and stops before the node stops serving, in this
/// order (§5.2): (1) the node hands off: it executes no transition, and asks the controller grain to hand off each ledger it
/// is primary of, at once and each <c>ControlInterval</c> until the runtime stops, waiting for its map view to name it
/// primary of none, or for its registry and membership views to show fewer than <c>N</c> other active nodes, when no
/// hand-off can be placed;
/// (2) it closes the admission of grain calls and drain starts and waits, within <c>TransitionTimeout</c>, for those
/// admitted, and every drain the node started, to end; (3) it checks its view again, and hands off once more when a late install made it primary; (4) it
/// stops the Journals and disposes their store, which waits for the outcomes of its proposals, and cancels the runtime's
/// stopping token, which ends the loop and the work still running; and (5) it disposes the writer registry, then the
/// reader, so the writers decide the Journals' proposals from their view while they can, rather than leave them
/// <c>Ambiguous</c>, and then awaits the Journals' disposal. Steps 4 and 5 take at most <c>TransitionTimeout</c> from the
/// cancellation together, a disposal held past it by work which outlasted it completing without the stop.
/// </para>
/// </remarks>
internal sealed partial class NodeLedgers(
    DurableStorageOptions options,
    ControlOptions control,
    LocalNode local,
    LocalNodeControl nodeControl,
    LocalJournals journals,
    ForwardedReads reads,
    LocalRouting routing,
    IOptions<JournaledStateManagerOptions> journaling,
    NodePressure pressure,
    IMetaStore metaStore,
    ILedgerReplicaTransport transport,
    NodeRegistryResolver registry,
    IClusterMembershipService membership,
    ILocalSiloDetails silo,
    IGrainFactory grains,
    SiloArchive archive,
    SiloObjectLedgers objectLedgers,
    ReplicaCallLimits limits,
    TimeProvider timeProvider,
    ILoggerFactory loggers,
    IJournalIndex? journalIndex = null,
    LocalStreamRegistration? localStreams = null) : ILifecycleParticipant<ISiloLifecycle>, IAsyncDisposable
{
    /// <summary>The lifecycle stage of the runtime: after <see cref="ServiceLifecycleStage.Active"/>, at which the node registers.</summary>
    public const int Stage = ServiceLifecycleStage.Active + 1;

    /// <summary>Gets the optional local <see cref="IJournalIndex"/> resolved from DI for this node.</summary>
    internal IJournalIndex? JournalIndex => journalIndex;

    private static readonly TimeSpan FirstCheckRetryDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan LongestCheckRetryDelay = TimeSpan.FromSeconds(5);
    private readonly ILogger _logger = loggers.CreateLogger<NodeLedgers>();
    private NodeRuntime? _runtime;

    // A runtime disposal which outlasted its stop's bound, which the silo's disposal of this participant awaits.
    private Task? _disposal;

    // Whether that disposal began after step 4's work ended within the bound, so a whole bound of its own ends it.
    private bool _disposalOwesItsBound;
    private Task _loop = Task.CompletedTask;
    private Task _governance = Task.CompletedTask;
    private Task _archiveCheck = Task.CompletedTask;

    /// <summary>
    /// Gets the runtime disposal which outlasted its stop's bound, which the silo's disposal awaits, or
    /// <see langword="null"/>; for tests.
    /// </summary>
    internal Task? LateDisposal => Volatile.Read(ref _disposal);

    /// <summary>Gets the node's runtime while it runs, or <see langword="null"/>; for tests.</summary>
    internal NodeRuntime? Runtime => Volatile.Read(ref _runtime);

    private IClusterControllerGrain ClusterController => grains.GetGrain<IClusterControllerGrain>(IClusterControllerGrain.Key);

    /// <summary>
    /// Returns whether a retried call whose answer is still unknown logs its <paramref name="failures"/>th failure: the
    /// first, and then every <see cref="RetryLogInterval"/>th, so a long outage logs at a bounded rate.
    /// </summary>
    internal static bool LogsRetry(int failures) => failures == 1 || failures % RetryLogInterval == 0;

    /// <summary>The interval, in failures, at which a retried call logs again.</summary>
    internal const int RetryLogInterval = 10;

    /// <summary>
    /// Returns whether the background record check's call failed with <paramref name="failure"/> leaving the archive
    /// store's answer unknown, as <see cref="IsUnknownOutcome"/> does, and also when the store refused the node's credential
    /// or access (<see cref="ObjectStoreRefusedException.IsAuthorization"/>): a grant may still be propagating, and the
    /// controller grain no longer waits on a silo whose gate is pending (orleans.md §5.13), so the check waits for it. A
    /// first registration, which fails the silo's start instead, takes such a refusal as definite.
    /// </summary>
    internal static bool IsUnknownCheckOutcome(Exception failure, CancellationToken stopping) =>
        failure is ObjectStoreRefusedException { IsAuthorization: true } || IsUnknownOutcome(failure, stopping);

    /// <summary>
    /// Returns whether the record check's call failed with <paramref name="failure"/> leaving the archive store's answer
    /// unknown, so the call is made again (OR-I8). The object store leaves every failure of its calls but its definite
    /// refusals an unknown outcome, its service SDKs' among them, which this composition does not name, so the check
    /// classifies as the replicas' repetitions do (<see cref="LedgerReplicaFailures.IsTransient"/>): the store's definite
    /// answers, a record which breaks its format, and a defect (a refused argument, a disposed or misused component, an
    /// unsupported call, invalid data, a state the code holds unreachable, a bad cast, index, or key, or running out of
    /// memory) never change on repetition; a cancellation the runtime did not request, a timeout, an I/O error, and every
    /// other failure do.
    /// </summary>
    /// <param name="failure">The failure.</param>
    /// <param name="stopping">The runtime's stopping token, whose cancellation is the runtime's own and ends the check.</param>
    internal static bool IsUnknownOutcome(Exception failure, CancellationToken stopping) => failure switch
    {
        OperationCanceledException => !stopping.IsCancellationRequested,
        ArchiveStoreMismatchException
            or ArchiveCorruptException
            or ObjectLedgerStoreMismatchException
            or ObjectStoreRefusedException
            or ArchiveLogRefusedException
            or ArgumentException
            or InvalidDataException
            or ObjectDisposedException
            or InvalidOperationException
            or NotSupportedException
            or NullReferenceException
            or UnreachableException
            or InvalidCastException
            or IndexOutOfRangeException
            or KeyNotFoundException
            or OutOfMemoryException => false,
        _ => true,
    };

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        // A silo stopped through its lifecycle disposed the runtime already, or left its disposal here once it outlasted
        // the stop's bound.
        if (Interlocked.Exchange(ref _runtime, null) is { } runtime)
        {
            await DisposeRuntimeAsync(runtime).ConfigureAwait(false);
        }

        if (Interlocked.Exchange(ref _disposal, null) is not { } disposal)
        {
            return;
        }

        await disposal.WaitAsync(control.TransitionTimeout, timeProvider, CancellationToken.None).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (!disposal.IsCompleted)
        {
            if (Volatile.Read(ref _disposalOwesItsBound))
            {
                LogDisposalOutlasted(_logger, local.Node, control.TransitionTimeout);
                Debug.Fail($"Node {local.Node}'s runtime disposal outlasted a whole bound of {control.TransitionTimeout} of its own though its cancelled work and its Journals' disposal ended within the stop's bound.");
            }
            else
            {
                LogDisposalAbandoned(_logger, local.Node, control.TransitionTimeout);
            }
            _ = disposal.ContinueWith(
                static (failed, state) =>
                {
                    var (logger, node) = ((ILogger, NodeId))state!;
                    LogAbandonedDisposalFailed(logger, node, failed.Exception!);
                },
                (_logger, local.Node),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return;
        }

        await disposal.ConfigureAwait(false);
    }

    /// <summary>
    /// Stops the node's Journals, so every call gets its stopped answer, and releases the snapshots its forwarded reads pinned,
    /// which no page reads once the Journals stop (orleans.md §5.5).
    /// </summary>
    private void StopJournals()
    {
        journals.Stop();
        reads.Stop();
    }

    /// <summary>
    /// Disposes a runtime the lifecycle's stop never claimed, as the stop does (§5.2 steps 4 and 5): the Journals stop
    /// before the writer registry is disposed, and the wait for their disposal and the record check is bounded.
    /// </summary>
    private async Task DisposeRuntimeAsync(NodeRuntime runtime)
    {
        nodeControl.Stop();
        StopJournals();
        var stopped = runtime.StopJournalsAsync();
        await runtime.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(stopped, _archiveCheck).WaitAsync(control.TransitionTimeout, timeProvider, CancellationToken.None).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        await runtime.DisposeAsync().ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public void Participate(ISiloLifecycle observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        _ = observer.Subscribe(nameof(NodeLedgers), Stage, StartAsync, StopAsync);
    }

    private async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            _ = await local.Registered.WaitAsync(cancellationToken).ConfigureAwait(false);

            // The node controller checks its node active in the resolver's value before every transition (OR-I3), so the
            // runtime serves only once the resolver holds a value: before, it would decline its own takeovers.
            _ = await registry.GetAsync(cancellationToken).ConfigureAwait(false);
            var register = await metaStore.GetRegisterAsync(options.Cluster, LedgerMapRegister.Name, cancellationToken).ConfigureAwait(false);
            var view = await LedgerMapView.OpenAsync(register, cancellationToken).ConfigureAwait(false);
            var node = local.Node;
            var store = local.StoreIfOpen;
            NodeDisk? disk;
            ulong releaseDistance;
            if (store is not null)
            {
                var usage = await store.MeasureDiskAsync(cancellationToken).ConfigureAwait(false);
                disk = NodeDisk.Create(node, options, usage, loggers.CreateLogger<NodeDisk>());
                pressure.SetUnderPressure(disk.Judge(usage).UnderPressure);
                releaseDistance = WalRelease.Distance(WalRelease.Retention(options, disk.BudgetBytes), options.WalFileSize);
            }
            else
            {
                disk = null;
                pressure.SetUnderPressure(false);
                releaseDistance = 0;
            }

            // Starting the runtime constructs the node's Journals, the silo's start check, which throws
            // JournalStoreLimitException naming the bound which fails (journals.md §8.4, OR-I17), and validates the caller
            // parameters the check of the archive store's record compares (OR-I8).
            var runtime = await NodeRuntime.StartAsync(
                node,
                silo.SiloAddress.Generation,
                disk,
                pressure,
                releaseDistance,
                view,
                transport,
                store,
                archive,
                objectLedgers,
                () => registry.Current?.Find(node) is { IsRetired: false },
                DeadNodes,
                () => registry.Current?.ActiveNodeCount ?? 1,
                limits.PayloadLimit,
                options.CreateWriterOptions(),
                options.Lease,
                options.Journals,
                journaling.Value.JournalFormatKey,
                options.Lsm,
                options.CreateArchiveLogOptions(),
                control,
                options.BlockedDrainRetryInterval,
                timeProvider,
                loggers,
                journalIndex,
                localStreams,
                cancellationToken).ConfigureAwait(false);
            _runtime = runtime;
            nodeControl.Serve(runtime.Controller, runtime.Scheduler, runtime.Stopping);
            routing.Publish(node, runtime.View, runtime.Archive);
            using (ExecutionContext.SuppressFlow())
            {
                _loop = Task.Run(() => RunAsync(runtime), CancellationToken.None);
                _governance = Task.Run(() => GovernAsync(runtime), CancellationToken.None);
                _archiveCheck = Task.Run(() => CheckArchiveAsync(runtime), CancellationToken.None);
            }
        }
        catch
        {
            // A runtime which never served declines every call, and Journals which never served answer every call as
            // stopped; the silo's start fails, and its archive gate is never decided.
            nodeControl.Stop();
            StopJournals();
            archive.Gate.Cancel();
            if (objectLedgers.IsConfigured)
            {
                objectLedgers.Gate.Cancel();
            }

            throw;
        }
    }

    /// <summary>
    /// Checks the archive store's record in the background (OR-I20, OR-I8, orleans.md §5.13), outside any lifecycle stage and
    /// bounded only by the runtime's cancellation, against the identity the node registry records, which the node
    /// registered with, and opens the services which wait for it. First the store's kind and prefix, which need no
    /// request; then the record's ID and its store fields, which the store checks itself; then its caller parameters, the
    /// Journals' and the silo's journal format. On the LSM the check reads the record through <c>StoreRecordAsync</c>,
    /// passes the silo's archive gate once its identity and store fields hold, so the node drains and compacts, the
    /// controller grain on this silo commits the manifest, and the sweep host sweeps, and serves the Journals once its
    /// parameters hold too. On archive logs it passes <see cref="ArchiveLogGate"/>, which reads the record without creating
    /// it and checks the parameters too, since the Azure layout writes the format into the logs: the gate opens the
    /// drains, the sweeps, and the Journals at once. The check needs the archive store, so it runs beside the silo's start,
    /// which an archive store outage never holds (orleans.md §5.9): a call whose outcome is unknown
    /// (<see cref="IsUnknownOutcome"/>) is made again with bounded, jittered backoff until the runtime stops. Any other
    /// failure refuses for the runtime's life, logged as its critical event: another parameter refuses the Journals, and on
    /// archive logs the gate too; another store, other store fields, a record which breaks its format, or a defect met before
    /// the runtime's stop refuses the gate and the Journals. A failure the stop brings, such as its disposal of the archive
    /// store, ends the check, and cancels the gate. A refused node then asks the controller grain to hand its ledgers off,
    /// each control interval until the runtime stops, so it is pending and takes no work (orleans.md §5.6, #274's D2 and
    /// D3); it goes on serving its replicas and its control plane.
    /// </summary>
    private async Task CheckArchiveAsync(NodeRuntime runtime)
    {
        try
        {
            await CheckArchiveCoreAsync(runtime).ConfigureAwait(false);
        }
        finally
        {
            // The one place the gates are cancelled once the runtime started: a gate the check did not decide never is.
            archive.Gate.Cancel();
            if (objectLedgers.IsConfigured)
            {
                objectLedgers.Gate.Cancel();
            }
        }
    }

    private async Task CheckArchiveCoreAsync(NodeRuntime runtime)
    {
        // The runtime's source is disposed once it stops, after which its token property throws.
        var stopping = runtime.Stopping;
        var failures = 0;
        var retry = new ResiliencePipelineBuilder { TimeProvider = timeProvider }
            .AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = arguments => ValueTask.FromResult(arguments.Outcome.Exception is { } failure && IsUnknownCheckOutcome(failure, stopping)),
                MaxRetryAttempts = int.MaxValue,
                DelayGenerator = static arguments => ValueTask.FromResult<TimeSpan?>(Backoff.Delay(arguments.AttemptNumber, FirstCheckRetryDelay, LongestCheckRetryDelay, Random.Shared.NextDouble())),
                OnRetry = arguments =>
                {
                    if (++failures == 1)
                    {
                        LogJournalsCheckFailed(_logger, local.Node, arguments.Outcome.Exception!);
                    }
                    else if (LogsRetry(failures))
                    {
                        LogJournalsCheckFailedAgain(_logger, local.Node, failures, arguments.Outcome.Exception!);
                    }

                    return ValueTask.CompletedTask;
                },
            })
            .Build();
        Exception? refusal = null;
        var journalsParameterRefused = false;
        var checkingObjectLedgers = false;
        try
        {
            // The runtime starts only once the node registered, with the identity the node registry records (OR-I20).
            var registration = await local.Registered.ConfigureAwait(false);
            try
            {
                await archive.CheckAsync(registration.ArchiveStore, runtime.RecordParameters, retry, stopping).ConfigureAwait(false);
            }
            catch (ArchiveStoreMismatchException mismatch) when (mismatch.IsParameter && archive.Gate.IsPassed)
            {
                // On the LSM a parameter mismatch leaves archive.Gate passed so drains stay active (OR-I8); check
                // objectLedgers independently before refusing Journals and handing off.
                LogJournalsParameterRefused(_logger, local.Node, mismatch.Field ?? string.Empty, mismatch.StoredValue ?? "nothing", mismatch.CallerValue ?? "nothing", mismatch);
                journalsParameterRefused = true;
            }

            if (objectLedgers.IsConfigured)
            {
                checkingObjectLedgers = true;
                if (registration.ObjectLedgerStore is not { } expectedObjectStore)
                {
                    throw new ObjectLedgerStoreMismatchException(
                        "This silo configures an object-ledger store, but the node registry records no object-ledger store ID (OL-I1).");
                }

                await objectLedgers.CheckAsync(expectedObjectStore, retry, stopping).ConfigureAwait(false);
            }

            if (!journalsParameterRefused)
            {
                if (journals.Serve(runtime.Journals, runtime.View, local.Node))
                {
                    LogJournalsServe(_logger, local.Node, failures);
                }

                return;
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            // The runtime stopped before the store answered: the Journals never served.
            return;
        }
        catch (ArchiveStoreMismatchException mismatch) when (mismatch.IsParameter)
        {
            LogJournalsParameterRefused(_logger, local.Node, mismatch.Field ?? string.Empty, mismatch.StoredValue ?? "nothing", mismatch.CallerValue ?? "nothing", mismatch);
            refusal = mismatch;
        }
        catch (ArchiveStoreMismatchException mismatch)
        {
            LogArchiveStoreRefused(_logger, local.Node, mismatch.Field ?? string.Empty, mismatch.StoredValue ?? "nothing", mismatch.CallerValue ?? "nothing", mismatch);
            refusal = mismatch;
        }
        catch (ObjectLedgerStoreMismatchException mismatch)
        {
            LogObjectLedgerStoreRefused(_logger, local.Node, mismatch);
            refusal = mismatch;
        }
        catch (ArchiveCorruptException corrupt)
        {
            LogJournalsRecordCorrupt(_logger, local.Node, corrupt);
            refusal = corrupt;
        }
        catch (InvalidDataException corrupt) when (checkingObjectLedgers)
        {
            LogObjectLedgerRecordCorrupt(_logger, local.Node, corrupt);
            refusal = corrupt;
        }
        catch (Exception refused) when (refused is ObjectStoreRefusedException or ArchiveLogRefusedException && !stopping.IsCancellationRequested)
        {
            // The store refused the request definitely; a refusal of the silo's credential or access is read again.
            LogArchiveStoreRequestRefused(_logger, local.Node, refused);
            refusal = refused;
        }
        catch (Exception) when (stopping.IsCancellationRequested)
        {
            // The runtime stops, disposing what the check used: the Journals never served, and stop with it.
            return;
        }
        catch (Exception defect) when (defect is not OutOfMemoryException)
        {
            LogJournalsCheckDefect(_logger, local.Node, defect);
            refusal = defect;
        }

        // On the LSM a parameter's refusal comes after archive.Gate passed, and objectLedgers.Gate was checked
        // independently above; leave passed gates open (OR-I8, OL-I1) and refuse only a gate whose check failed.
        if (refusal is not null)
        {
            if (checkingObjectLedgers)
            {
                objectLedgers.Gate.Refuse(refusal);
            }
            else
            {
                archive.Gate.Refuse(refusal);
                if (objectLedgers.IsConfigured)
                {
                    objectLedgers.Gate.Refuse(refusal);
                }
            }
        }

        journals.Refuse();
        await HandOffRefusedAsync(stopping).ConfigureAwait(false);
    }

    /// <summary>
    /// Asks the controller grain to hand off the ledgers of a node whose Journals are refused, at once and each control
    /// interval until the runtime stops, so it is pending for as long as it runs (orleans.md §5.6); a restart at a newer
    /// generation serves again once its check passes.
    /// </summary>
    private async Task HandOffRefusedAsync(CancellationToken stopping)
    {
        try
        {
            await RequestHandOffAsync(stopping).ConfigureAwait(false);
            await RepeatHandOffAsync(stopping).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
        }
    }

    private async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Runtime is not { } runtime)
        {
            nodeControl.Stop();
            StopJournals();
            archive.Gate.Cancel();
            if (objectLedgers.IsConfigured)
            {
                objectLedgers.Gate.Cancel();
            }

            return;
        }

        // (1) Hand off, asking until the runtime stops. The first request is made before the view is checked, so a node
        // primary of nothing is pending too, and is placed on no new ledger while it stops.
        nodeControl.BeginHandOff();
        using var repeating = new CancellationTokenSource();
        Task repeat = Task.CompletedTask;
        try
        {
            LogStopStep(_logger, local.Node, StopStep.HandOff, StopStepPhase.Begins);
            await RequestHandOffAsync(cancellationToken).ConfigureAwait(false);
            using (ExecutionContext.SuppressFlow())
            {
                repeat = Task.Run(() => RepeatHandOffAsync(repeating.Token), CancellationToken.None);
            }

            await HandOffAsync(runtime, cancellationToken).ConfigureAwait(false);
            LogStopStep(_logger, local.Node, StopStep.HandOff, StopStepPhase.Ends);

            // (2) Wait for the calls admitted before the hand-off began, and the drains the node started.
            LogStopStep(_logger, local.Node, StopStep.AdmittedWork, StopStepPhase.Begins);
            try
            {
                await nodeControl.CloseAdmissionAsync().WaitAsync(control.TransitionTimeout, timeProvider, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                LogAdmittedWorkOutlasted(_logger, local.Node, control.TransitionTimeout);
            }

            LogStopStep(_logger, local.Node, StopStep.AdmittedWork, StopStepPhase.Ends);

            // (3) A late install may have made the node primary again.
            LogStopStep(_logger, local.Node, StopStep.HandOffAgain, StopStepPhase.Begins);
            await HandOffAsync(runtime, cancellationToken).ConfigureAwait(false);
            LogStopStep(_logger, local.Node, StopStep.HandOffAgain, StopStepPhase.Ends);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The host's stop timeout passed: the node stops as a crash would, and its ledgers are taken over (§5.2).
            LogStopCancelled(_logger, local.Node);
        }
        finally
        {
            // (4) and (5) run however steps 1 to 3 ended.
            await StopRuntimeAsync(runtime, repeating, repeat).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Stops the Journals and the work still running and waits for them to end, the Journals' disposal, the admitted calls
    /// and the drains the node started, the loop, the record check, and the hand-off requests (§5.2 step 4), then disposes
    /// what it used (step 5), both within <c>TransitionTimeout</c> of the cancellation. The bound starts before anything
    /// the steps run, and the stop awaits nothing past it: the runtime's cancellation, which takes effect at once and whose
    /// registered callbacks run on the thread pool until each returns, Orleans' grain-call cancellations among them, and
    /// the Journals' disposal and the runtime's, each started on the thread pool, so no synchronous part of them runs on
    /// the stop's own path. The disposal
    /// stops every leased read, so it ends once the replica calls they await end; when the disposal outlasts what step 4
    /// left of the bound,
    /// the stop goes on and the silo's disposal of this participant awaits the disposal, for a whole bound more, after
    /// which one which began once step 4's work had ended is a defect.
    /// </summary>
    private async Task StopRuntimeAsync(NodeRuntime runtime, CancellationTokenSource repeating, Task repeat)
    {
        // The stop claims the runtime first, so the host's disposal during the bounded wait finds none to dispose again;
        // one which claimed it first stopped and disposed it already.
        if (Interlocked.Exchange(ref _runtime, null) is null)
        {
            await repeating.CancelAsync().ConfigureAwait(false);
            return;
        }

        var cancelled = timeProvider.GetTimestamp();
        LogStopStep(_logger, local.Node, StopStep.Cancel, StopStepPhase.Begins);

        // The Journals stop before the writer registry is disposed, so the writers decide their outstanding proposals from
        // their view rather than leave them Ambiguous (journals.md §9). Their disposal runs its own code before it first
        // yields, so it starts on the thread pool. The cancellation takes effect before the node's control stops, and its
        // callbacks, which run on the thread pool and may run long, are awaited only within the bound below.
        StopJournals();
        pressure.SetObjectStoreThrottled(null);
        if (objectLedgers.Pacer is { } pacer)
        {
            pacer.RateSource = null;
        }

        var stopped = Task.Run(runtime.StopJournalsAsync, CancellationToken.None);
        var cancelling = runtime.CancelAsync();
        nodeControl.Stop();
        var repeatCancelled = repeating.CancelAsync();
        var work = Task.WhenAll(stopped, cancelling, repeatCancelled, nodeControl.CloseAdmissionAsync(), _loop, _governance, _archiveCheck, repeat);
        await work.WaitAsync(Remaining(cancelled), timeProvider, CancellationToken.None).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        var workEnded = work.IsCompleted;
        if (!workEnded)
        {
            LogCancelledWorkOutlasted(_logger, local.Node, control.TransitionTimeout);
        }

        LogStopStep(_logger, local.Node, StopStep.Cancel, StopStepPhase.Ends);
        LogStopStep(_logger, local.Node, StopStep.Dispose, StopStepPhase.Begins);
        var disposal = Task.Run(() => runtime.DisposeAsync().AsTask(), CancellationToken.None);
        var remaining = Remaining(cancelled);
        if (remaining > TimeSpan.Zero)
        {
            await disposal.WaitAsync(remaining, timeProvider, CancellationToken.None).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        if (disposal.IsCompleted)
        {
            LogStopStep(_logger, local.Node, StopStep.Dispose, StopStepPhase.Ends);
            await disposal.ConfigureAwait(false);
            return;
        }

        // The silo's disposal of this participant awaits it, within a bound of its own.
        Volatile.Write(ref _disposal, disposal);
        if (remaining <= TimeSpan.Zero)
        {
            LogDisposalAfterTheBound(_logger, local.Node, control.TransitionTimeout);
            return;
        }

        // Step 4's work ended within the bound or outlasted it. Once it ended, the Journals' disposal had ended and the
        // reader's disposal ends every lease's replica calls under their cancelled lifetimes (ledgers.md §2.5), so the
        // disposal ends promptly; step 4 may have left it any part of the bound, so outlasting the rest is no defect, and one
        // still running a whole bound later, which the silo's disposal awaits, is. A disposal which step 4's work outlasted
        // may wait on the Journals, as a write whose replicas never answer holds them, and owes no bound.
        if (workEnded)
        {
            Volatile.Write(ref _disposalOwesItsBound, true);
        }

        LogDisposalDeferred(_logger, local.Node, control.TransitionTimeout);
    }

    /// <summary>The steps of a silo's stop (orleans.md §5.2), each of whose start and end the stop logs.</summary>
    private enum StopStep
    {
        /// <summary>Step 1: the hand-off.</summary>
        HandOff = 1,

        /// <summary>Step 2: the wait for the work admitted before the hand-off began.</summary>
        AdmittedWork = 2,

        /// <summary>Step 3: the hand-off of any ledger a late install made the node primary of.</summary>
        HandOffAgain = 3,

        /// <summary>Step 4: the Journals' stop and the cancellation of the runtime's work.</summary>
        Cancel = 4,

        /// <summary>Step 5: the runtime's disposal.</summary>
        Dispose = 5,
    }

    /// <summary>Whether a stop step begins or ends.</summary>
    private enum StopStepPhase
    {
        /// <summary>The step begins.</summary>
        Begins,

        /// <summary>The step ends.</summary>
        Ends,
    }

    /// <summary>Returns what is left of the stop's bound, which began at <paramref name="cancelled"/>, or zero once it passed.</summary>
    private TimeSpan Remaining(long cancelled)
    {
        var remaining = control.TransitionTimeout - timeProvider.GetElapsedTime(cancelled);
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    /// <summary>
    /// Waits until the node's map view names it primary of no open ledger which another active silo can take,
    /// following the view's changes, which the loop's reports and reads bring, and requesting a registry re-read each
    /// control interval: a retirement changes no membership, which the resolver's re-reads follow, so a view which predates
    /// it would count the retired node a target the controller never places on.
    /// </summary>
    private async Task HandOffAsync(NodeRuntime runtime, CancellationToken cancellationToken)
    {
        var node = local.Node;
        while (runtime.View.Current is var map && HasHandOffTarget(map, node))
        {
            try
            {
                _ = await runtime.View.WaitForChangeAsync(map.Epoch, cancellationToken).WaitAsync(control.ControlInterval, timeProvider, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Membership or the registry may have changed: the loop checks whether a target still exists.
                registry.RequestReread();
            }
        }
    }

    private bool HasHandOffTarget(LedgerMapValue map, NodeId node)
    {
        var hasOpenNodePrimary = false;
        var hasOpenObjectStorePrimary = false;
        foreach (var ledger in map.Ledgers)
        {
            if (ledger.IsOpen && ledger.Primary.Node == node)
            {
                var isObjectStore = ledger.Location switch
                {
                    LedgerLocation.ObjectStore => true,
                    LedgerLocation.Nodes => false,
                    null => throw new UnreachableException("conventions.md §15: a ledger location holds a case."),
                };
                if (isObjectStore)
                {
                    hasOpenObjectStorePrimary = true;
                }
                else
                {
                    hasOpenNodePrimary = true;
                }
            }
        }

        if (!hasOpenNodePrimary && !hasOpenObjectStorePrimary)
        {
            return false;
        }

        // Open object-store ledgers — and open node-backed ledgers during drain-all — can be handed off to any other
        // live active silo (once drain-all Phase 2 retires all diskful nodes, only surviving diskless silos remain
        // active, so a diskful-only cluster exits hand-off immediately while a mixed cluster hands off to diskless
        // silos; orleans.md §5.14).
        return control.LedgerPlacement == LedgerPlacement.ObjectStore || hasOpenObjectStorePrimary || registry.Current?.DrainAllRevision > 0
            ? ObjectStoreTargetExists(node)
            : QuorumTargetExists(node);
    }

    /// <summary>
    /// Returns the registered nodes whose silos membership shows dead, by the controller grain's projection (orleans.md §5.7),
    /// each with the generation of the silo the registry lists for it, so a node which returns and dies again is another
    /// death; the node's failure-triggered drains count them out of its ledgers' live replicas (§5.6).
    /// </summary>
    internal static FrozenDictionary<NodeId, int> DeadNodes(NodeRegistryValue? registry, IReadOnlyDictionary<SiloAddress, ClusterMember> members, int observerGeneration) =>
        registry?.Nodes
            .Where(entry => MembershipProjection.Standing(members.TryGetValue(entry.Silo, out var member) ? member.Status : null, entry.Silo.Generation, observerGeneration) == MembershipStanding.Dead)
            .ToFrozenDictionary(static entry => entry.Node, static entry => entry.Silo.Generation) ?? [];

    private FrozenDictionary<NodeId, int> DeadNodes() => DeadNodes(registry.Current, membership.CurrentSnapshot.Members, silo.SiloAddress.Generation);

    /// <summary>
    /// Returns whether the registry and membership views show at least <c>N</c> other active diskful nodes' silos active,
    /// among which the controller places a quorum ledger hand-off's replicas, the stopping node holding none.
    /// </summary>
    private bool QuorumTargetExists(NodeId node)
    {
        var members = membership.CurrentSnapshot.Members;
        return registry.Current?.Nodes.Count(entry => entry.Node != node && !entry.IsRetired && !entry.Diskless
            && members.TryGetValue(entry.Silo, out var member) && member.Status == SiloStatus.Active) >= control.Replicas;
    }

    /// <summary>
    /// Returns whether the registry and membership views show at least one other active node's silo active, which can
    /// take over an object-store ledger primary on hand-off (orleans.md §5.14).
    /// </summary>
    private bool ObjectStoreTargetExists(NodeId node)
    {
        var members = membership.CurrentSnapshot.Members;
        return registry.Current?.Nodes.Any(entry => entry.Node != node && !entry.IsRetired
            && members.TryGetValue(entry.Silo, out var member) && member.Status == SiloStatus.Active) == true;
    }

    /// <summary>
    /// Asks the controller grain once to hand off the node's ledgers, waiting at most <c>ControlInterval</c> for its answer;
    /// a failed or unanswered request is logged, and the repetition asks again. Only the cancellation of
    /// <paramref name="cancellationToken"/> ends it with an exception, so a request which fails as the host cancels the
    /// stop never cuts the stop short.
    /// </summary>
    private async Task RequestHandOffAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ClusterController.HandOffAsync(local.Node, silo.SiloAddress.Generation).WaitAsync(control.ControlInterval, timeProvider, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException && !(failure is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            LogHandOffFailed(_logger, local.Node, failure);
        }
    }

    /// <summary>Asks the controller grain to hand off the node's ledgers each control interval after the first request, until stopped.</summary>
    private async Task RepeatHandOffAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(control.ControlInterval, timeProvider, cancellationToken).ConfigureAwait(false);
                await RequestHandOffAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// Gets whether the node drains: it serves, its hand-off not begun, and its silo's archive gate passed. The control
    /// loop starts drains only while it holds, and each report carries it, so the controller's choice of drainers follows
    /// the node's own condition (orleans.md §5.6).
    /// </summary>
    /// <summary>Returns the ranges of the open ledgers <paramref name="map"/> lists with <paramref name="node"/> as primary.</summary>
    internal static ImmutableHashSet<KeyRange> PrimaryRanges(LedgerMapValue map, NodeId node) =>
        [.. map.Ledgers.Where(ledger => ledger.IsOpen && ledger.Primary.Node == node).Select(static ledger => ledger.Range)];

    private bool Drains => nodeControl.State == LocalNodeControl.ControlState.Serving && archive.Gate.IsPassed && (!objectLedgers.IsConfigured || objectLedgers.Gate.IsPassed);

    private async Task RunAsync(NodeRuntime runtime)
    {
        var cancellationToken = runtime.Stopping;
        var node = local.Node;
        var lastRead = timeProvider.GetUtcNow();
        var lastSweep = lastRead;
        var lastExpiry = lastRead;
        var expiryInterval = options.Journals.ExpiryInterval;
        var confirmed = DateTimeOffset.MinValue;
        var drainToo = ImmutableArray<LedgerId>.Empty;
        var reportedUnserved = new HashSet<LedgerId>();
        Task<Reported?>? report = null;
        var expiry = Task.CompletedTask;
        var preloaded = ImmutableHashSet<KeyRange>.Empty;
        while (!cancellationToken.IsCancellationRequested)
        {
            var waited = false;
            try
            {
                // A newly unserved ledger wakes the loop once: the report it starts, or the next, carries it.
                if (await WaitIntervalAsync(runtime, reportedUnserved, cancellationToken).ConfigureAwait(false) is { } unserved)
                {
                    reportedUnserved.UnionWith(unserved);
                }

                waited = true;

                var now = timeProvider.GetUtcNow();
                if (report is { IsCompleted: true } done)
                {
                    report = null;
                    if (done.Result is { } reported)
                    {
                        drainToo = reported.DrainToo;
                        confirmed = reported.ConfirmedAt > confirmed ? reported.ConfirmedAt : confirmed;
                        reportedUnserved = [.. reported.Unserved];
                    }
                }

                // While the Journals serve, an expiry scan starts each ExpiryInterval, one at a time on the thread pool, so
                // neither its candidates' collection nor a long scan holds the node's reports; the stop awaits it with the
                // loop (journals.md §8.6).
                if (expiry.IsCompleted && now - lastExpiry >= expiryInterval && journals.State == LocalJournals.JournalsState.Serving)
                {
                    expiry = Task.Run(() => ExpireTombstonesAsync(runtime, cancellationToken), CancellationToken.None);
                    lastExpiry = now;
                }

                if (now - lastRead >= control.MapRefreshInterval && now - confirmed >= control.MapRefreshInterval)
                {
                    _ = await runtime.View.ReadAsync(cancellationToken).ConfigureAwait(false);
                    lastRead = now;
                }

                if (runtime.Reconciler is { } reconciler)
                {
                    _ = await reconciler.ReconcileAsync(cancellationToken).ConfigureAwait(false);
                    if (now - lastSweep >= control.ReplicaSweepInterval)
                    {
                        _ = await reconciler.SweepAsync(cancellationToken).ConfigureAwait(false);
                        lastSweep = now;
                    }
                }

                // On the LSM, the node keeps loaded the section metadata of the ranges it is primary of, whose journals its
                // Journals read, once its silo's check of the store's record passed (OR-I20): the set changes at its start
                // and with each takeover, handoff, split, and merge which gives it a ledger or takes one away. The loads run
                // on their own, and a failed one leaves its section to load on demand (archive-lsm.md §8.7).
                if (runtime.LsmStore is { } lsm && archive.Gate.IsPassed && PrimaryRanges(runtime.View.Current, node) is var ranges && !ranges.SetEquals(preloaded))
                {
                    preloaded = ranges;
                    _ = lsm.PreloadAsync(ranges);
                }

                report ??= ReportAsync(runtime, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                LogIntervalFailed(_logger, node, failure);
            }

            // The node drains only once its silo's check of the store's record passed (OR-I20, orleans.md §5.13). Each
            // interval starts the drains due which are not in flight; each ends on its own, and the stop awaits them (§5.6).
            // The start has a try of its own, so a failed map read, reconciliation, or sweep holds no drain, a
            // failure-triggered one least of all (OR-I23).
            try
            {
                if (waited && Drains)
                {
                    await StartDrainsAsync(drainToo).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                LogIntervalFailed(_logger, node, failure);
            }
        }

        await Task.WhenAll(report ?? Task.CompletedTask, expiry).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    /// <summary>
    /// Runs one expiry scan of the node's Journals (journals.md §8.6) and logs what it proposed at Debug: every outcome of an
    /// <c>Expired</c> is expected, a candidate being a hint. A failed scan is logged, and the next interval scans again; a
    /// failure the stop brings, as the Journals' disposal does, is not logged.
    /// </summary>
    private async Task ExpireTombstonesAsync(NodeRuntime runtime, CancellationToken cancellationToken)
    {
        try
        {
            var scan = await runtime.Journals.ExpireTombstonesAsync(cancellationToken).ConfigureAwait(false);
            LogExpiryScan(_logger, local.Node, scan.Proposed, scan.Expired, scan.Rejected, scan.Moved, scan.Fenced, scan.Ambiguous, scan.Failed);
            if (scan.Failed > 0)
            {
                LogExpiryProposalsFailed(_logger, local.Node, scan.Failed, scan.Proposed);
            }
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            if (!cancellationToken.IsCancellationRequested && journals.State == LocalJournals.JournalsState.Serving)
            {
                LogExpiryScanFailed(_logger, local.Node, failure);
            }
        }
    }

    /// <summary>
    /// Waits one control interval, or until the writer registry lists a ledger unserved which the loop has not reported, and
    /// answers the unserved ledgers in that case.
    /// </summary>
    private async Task<ImmutableArray<LedgerId>?> WaitIntervalAsync(NodeRuntime runtime, IReadOnlySet<LedgerId> reportedUnserved, CancellationToken cancellationToken)
    {
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var interval = Task.Delay(control.ControlInterval, timeProvider, waiting.Token);
        var unserved = runtime.Writers.WaitForUnservedAsync(reportedUnserved, waiting.Token);
        _ = await Task.WhenAny(interval, unserved).ConfigureAwait(false);
        await waiting.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(interval, unserved).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        cancellationToken.ThrowIfCancellationRequested();
        return unserved.IsCompletedSuccessfully ? unserved.Result : null;
    }

    /// <summary>
    /// Reports the node to the controller grain and brings its view to the reply's epoch (OR-I19); answers the closed
    /// ledgers to drain too, when the reply confirmed the view's epoch, and the unserved ledgers it reported.
    /// </summary>
    private async Task<Reported?> ReportAsync(NodeRuntime runtime, CancellationToken cancellationToken)
    {
        try
        {
            var lost = local.StoreIfOpen?.ListCorruptReplicas() ?? [];
            // A stopping node, or one whose Journals are refused, reports its hand-off, so a new controller activation knows
            // it pending from its first report (orleans.md §5.6).
            var refused = journals.State == LocalJournals.JournalsState.Refused || archive.Gate.Passed.IsFaulted || objectLedgers.Gate.Passed.IsFaulted;
            var handingOff = nodeControl.State == LocalNodeControl.ControlState.HandingOff || refused;
            var report = await nodeControl.RunAsync(controller => Task.FromResult(controller.Report(pressure.UnderPressure, lost, handingOff, refused, archive.Gate.IsPassed, Drains))).ConfigureAwait(false);
            // Bounded below the control intervals after which a node's archive-log share falls to its floor, so a hung call
            // never holds the next report past them (orleans.md §5.6, OR-I22); a reply which arrives later is discarded.
            var reply = await ClusterController.ReportAsync(report).WaitAsync(control.ControlInterval * (ArchiveLogRequestShare.ReplyIntervals - 1), timeProvider, cancellationToken).ConfigureAwait(false);
            _ = await nodeControl.RunAsync(async controller =>
            {
                await controller.ObserveAsync(reply, cancellationToken).ConfigureAwait(true);
                return true;
            }).ConfigureAwait(false);
            var confirmed = runtime.View.Current.Epoch == reply.MapEpoch ? timeProvider.GetUtcNow() : DateTimeOffset.MinValue;
            return new Reported(reply.DrainToo, confirmed, report.Unserved);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                LogReportFailed(_logger, local.Node, failure);
            }

            return null;
        }
    }

    /// <summary>
    /// Governs the node's resources each control interval until the runtime stops (orleans.md §5.9): measures the disk and
    /// records the node's pressure, observes its primaries' throttles, records the node's resources and primaries for the
    /// meter and <see cref="DurableStorageStatus"/>, reports its pressure while it lasts, and requests the WAL-release
    /// rolls its oldest replicas need.
    /// </summary>
    private async Task GovernAsync(NodeRuntime runtime)
    {
        var cancellationToken = runtime.Stopping;
        var node = local.Node;
        var reports = Task.CompletedTask;
        var rolls = Task.CompletedTask;
        var wakeupRolls = Task.CompletedTask;
        NodeResources? resources = null;
        try
        {
            while (true)
            {
                await Task.Delay(control.ControlInterval, timeProvider, cancellationToken).ConfigureAwait(false);

                // Poll wakeup heap roll hints unconditionally (before runtime.Disk is null) so both diskful and
                // diskless silos bound primary-side wakeup memory via routine ledger rolls (orleans.md §5.15).
                if (wakeupRolls.IsCompleted && runtime.WakeupHandler is { } wakeupHandler)
                {
                    var wakeupLedgers = wakeupHandler.CollectLedgersNeedingRoll();
                    if (!wakeupLedgers.IsEmpty)
                    {
                        wakeupRolls = RequestWakeupRollsAsync(wakeupLedgers, cancellationToken);
                    }
                }

                if (runtime.Disk is null)
                {
                    // A diskless silo holds no local WAL or disk budget (orleans.md §5.14), so disk measurement and WAL-release
                    // rolls are skipped, while throttle observation still tracks open object-store primaries and cleans up closed ones.
                    try
                    {
                        pressure.Observe(node, runtime.View.Current);
                    }
                    catch (Exception failure) when (failure is not OutOfMemoryException)
                    {
                        LogThrottleObservationFailed(_logger, node, failure);
                    }

                    await RecordAsync(runtime, resources: null).ConfigureAwait(false);
                    continue;
                }

                bool governs;
                (governs, resources) = await MeasureAsync(runtime, cancellationToken).ConfigureAwait(false) switch
                {
                    NodeMeasurement.Measured measured => (true, measured.Resources),
                    NodeMeasurement.Failed => (true, resources),
                    NodeMeasurement.Stopped => (false, resources),
                    null => throw new UnreachableException(),
                };
                if (!governs)
                {
                    break;
                }

                try
                {
                    pressure.Observe(node, runtime.View.Current);
                }
                catch (Exception failure) when (failure is not OutOfMemoryException)
                {
                    LogThrottleObservationFailed(_logger, node, failure);
                }

                if (reports.IsCompleted && pressure.UnderPressure)
                {
                    reports = ReportPressureAsync(runtime.View.Current, cancellationToken);
                }

                if (rolls.IsCompleted)
                {
                    rolls = RequestReleaseRollsAsync(runtime, cancellationToken);
                }

                // The record goes on through a failed disk measurement, with the last measured resources.
                await RecordAsync(runtime, resources).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }

        await Task.WhenAll(reports, rolls, wakeupRolls).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    private async Task RequestWakeupRollsAsync(ImmutableArray<LedgerId> ledgers, CancellationToken cancellationToken)
    {
        var node = local.Node;
        var clusterController = ClusterController;
        await Task.WhenAll(ledgers.Select(async ledger =>
        {
            try
            {
                await clusterController.RequestRollAsync(ledger).WaitAsync(control.ControlInterval, timeProvider, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is not OutOfMemoryException && !cancellationToken.IsCancellationRequested)
            {
                LogWakeupRollRequestFailed(_logger, node, ledger, failure);
            }
        })).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    /// <summary>
    /// Asks the controller grain for the WAL-release rolls the node's oldest replicas need (OR-I13), waiting at most
    /// <c>ControlInterval</c> for each answer; a failed request is logged, and the next interval asks again while the
    /// replicas need it.
    /// </summary>
    private async Task RequestReleaseRollsAsync(NodeRuntime runtime, CancellationToken cancellationToken)
    {
        var node = local.Node;
        IReadOnlyList<LedgerId> ledgers;
        try
        {
            var store = local.Store;
            ledgers = WalRelease.Rolls(store.ListRetention(), store.GetRetention().CurrentFile, runtime.ReleaseDistance, runtime.View.Current);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            LogReleaseRollsFailed(_logger, node, failure);
            return;
        }

        var clusterController = ClusterController;
        await Task.WhenAll(ledgers.Select(async ledger =>
        {
            try
            {
                await clusterController.RequestRollAsync(ledger).WaitAsync(control.ControlInterval, timeProvider, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is not OutOfMemoryException && !cancellationToken.IsCancellationRequested)
            {
                LogReleaseRollRequestFailed(_logger, node, ledger, failure);
            }
        })).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    /// <summary>
    /// Measures the node's disk and records its pressure: answers the node's resources, or <c>Failed</c> once a failure was
    /// logged, after which the next interval measures again, or <c>Stopped</c> once the failure stopped the process, after
    /// which the node governs nothing more.
    /// </summary>
    private async Task<NodeMeasurement> MeasureAsync(NodeRuntime runtime, CancellationToken cancellationToken)
    {
        try
        {
            var store = local.Store;
            var disk = runtime.Disk ?? throw new UnreachableException("MeasureAsync runs only on a diskful node.");
            var measurement = disk.Judge(await store.MeasureDiskAsync(cancellationToken).ConfigureAwait(false));
            pressure.SetUnderPressure(measurement.UnderPressure);
            var retention = store.GetRetention();
            return new NodeMeasurement.Measured(new NodeResources(
                timeProvider.GetUtcNow(),
                measurement.UsedBytes,
                measurement.FreeBytes,
                disk.BudgetBytes,
                measurement.UnderPressure,
                retention.RetainedFiles,
                retention.LiveReplicas));
        }
        catch (Exception failure) when (failure is not OutOfMemoryException && !cancellationToken.IsCancellationRequested)
        {
            if (FatalErrorHandler.IsReported(failure))
            {
                return NodeMeasurement.Stopped.Instance;
            }

            LogMeasurementFailed(_logger, local.Node, failure);
            return NodeMeasurement.Failed.Instance;
        }
    }

    /// <summary>
    /// Records the node's last measured resources, <see langword="null"/> before the first, and each open ledger it is
    /// primary of, as its controller observes them on its scheduler, for the meter and <see cref="DurableStorageStatus"/>
    /// (orleans.md §5.9); records nothing once the node's control has stopped. Nothing else fails it: any other failure is a
    /// defect, asserted and thrown.
    /// </summary>
    private async Task RecordAsync(NodeRuntime runtime, NodeResources? resources)
    {
        ImmutableArray<PrimaryObservation> primaries;
        try
        {
            primaries = await nodeControl.RunAsync(controller => Task.FromResult(controller.ObservePrimaries(runtime.Journals.AppliedBelow))).ConfigureAwait(false);
        }
        catch (NodeControlStoppedException)
        {
            return;
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            Debug.Fail($"Node {local.Node}'s observation of its primaries failed, a defect: {failure}");
            throw;
        }

        var causes = pressure.ThrottleCauses;
        runtime.Metrics.Record(new DurableStorageNodeStatus(
            local.Node,
            timeProvider.GetUtcNow(),
            resources,
            [.. primaries.Select(primary => new DurableStorageLedgerStatus(
                primary.Ledger,
                primary.AppliedBelow,
                primary.ContiguousTail,
                primary.Watermark,
                primary.Undrained,
                causes.TryGetValue(primary.Ledger, out var throttledBy) ? throttledBy : []))]));
    }

    /// <summary>
    /// Reports the node's disk pressure to the node control grain of the primary of every open ledger it replicates on
    /// another node, so each throttles (OR-I10), waiting at most <c>ControlInterval</c> for each; a failed report is logged,
    /// and the next interval reports again while the pressure lasts.
    /// </summary>
    private async Task ReportPressureAsync(LedgerMapValue map, CancellationToken cancellationToken)
    {
        var node = local.Node;
        var primaries = map.Ledgers
            .Where(ledger => ledger.IsOpen && ledger.Primary.Node != node && ledger.IsOnNode(node))
            .Select(static ledger => ledger.Primary.Node)
            .Distinct();
        await Task.WhenAll(primaries.Select(async primary =>
        {
            try
            {
                await grains.GetGrain<INodeControlGrain>(primary.Value).ReportPressureAsync(node).WaitAsync(control.ControlInterval, timeProvider, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is not OutOfMemoryException && !cancellationToken.IsCancellationRequested)
            {
                LogPressureReportFailed(_logger, node, primary, failure);
            }
        })).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    /// <summary>
    /// Starts the node's drains due which are not in flight, once the silo's archive gate passed; each drain ends on its
    /// own and logs its own failure as it ends, so a fast failure is not held behind a slow drain, and its ledger drains
    /// again at a later interval. On the LSM each drain commits its watermark to the manifest; on archive logs the start
    /// takes the gate's evidence, and each completed ledger's drop is submitted to the ledger map (orleans.md §5.6). A
    /// blocked archive-log drain's critical event is the node's drains' own, once per block (event 6244); each attempt
    /// still logs its warning here.
    /// </summary>
    private async Task StartDrainsAsync(ImmutableArray<LedgerId> drainToo)
    {
        var clusterController = ClusterController;
        var evidence = await archive.Gate.Passed.ConfigureAwait(false);
        _ = evidence switch
        {
            ArchiveGateEvidence.Logs logs => await nodeControl.StartLogDrainsAsync(logs.Gate, drainToo, (change, token) => clusterController.CommitLedgerMapAsync(change).WaitAsync(token), LogDrainFailure).ConfigureAwait(false),
            ArchiveGateEvidence.Lsm => await nodeControl.StartDrainsAsync(drainToo, (change, token) => clusterController.CommitManifestAsync(change).WaitAsync(token), LogDrainFailure).ConfigureAwait(false),
            null => throw new InvalidOperationException("A passed gate holds its evidence."),
        };

        void LogDrainFailure(LedgerId ledger, Exception failure)
        {
            if (evidence.Value is ArchiveGateEvidence.Logs && failure is not ArchiveLogDrainBlockedException)
            {
                // The node's archive-log drains logged it, once a backoff window (orleans.md §5.6).
                return;
            }

            if (failure is DrainMemoryExceededException exceeded)
            {
                LogDrainMemoryExceeded(_logger, local.Node, ledger, exceeded.Segment.Start, exceeded.Budget, exceeded);
            }
            else
            {
                LogDrainFailed(_logger, local.Node, ledger, failure);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Node {Node}'s control interval failed; the next one runs.")]
    private static partial void LogIntervalFailed(ILogger logger, NodeId node, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Node {Node}'s measurement of its disk failed; the next interval measures again.")]
    private static partial void LogMeasurementFailed(ILogger logger, NodeId node, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Node {Node}'s observation of its primaries' throttles failed; the next interval observes again.")]
    private static partial void LogThrottleObservationFailed(ILogger logger, NodeId node, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Node {Node}'s disk pressure report to primary {Primary} failed; it reports again at the next interval while the pressure lasts.")]
    private static partial void LogPressureReportFailed(ILogger logger, NodeId node, NodeId primary, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Node {Node}'s choice of the WAL-release rolls its oldest replicas need failed; the next interval chooses again.")]
    private static partial void LogReleaseRollsFailed(ILogger logger, NodeId node, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Node {Node}'s WAL-release roll request for ledger {Ledger} failed; the next interval asks again while its replica needs it.")]
    private static partial void LogReleaseRollRequestFailed(ILogger logger, NodeId node, LedgerId ledger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Node {Node}'s wakeup-memory roll request for ledger {Ledger} failed.")]
    private static partial void LogWakeupRollRequestFailed(ILogger logger, NodeId node, LedgerId ledger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Node {Node}'s report to the controller grain failed; it reports again at the next interval.")]
    private static partial void LogReportFailed(ILogger logger, NodeId node, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Node {Node}'s drain of ledger {Ledger} failed; it drains again at a later interval.")]
    private static partial void LogDrainFailed(ILogger logger, NodeId node, LedgerId ledger, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Node {Node}'s expiry scan proposed {Proposed} Expireds: {Expired} expired, {Rejected} rejected, {Moved} moved, {Fenced} fenced, {Ambiguous} ambiguous, and {Failed} failed.")]
    private static partial void LogExpiryScan(ILogger logger, NodeId node, int proposed, int expired, int rejected, int moved, int fenced, int ambiguous, int failed);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Node {Node}'s expiry scan met {Failed} failed proposals of {Proposed}; the next interval proposes their Expireds again while warm state or the key indexes hold them deleted.")]
    private static partial void LogExpiryProposalsFailed(ILogger logger, NodeId node, int failed, int proposed);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Node {Node}'s expiry scan failed; it scans again after the next expiry interval.")]
    private static partial void LogExpiryScanFailed(ILogger logger, NodeId node, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Node {Node} cannot drain ledger {Ledger}: its segment at {Start} passes the archive's drain memory, {Budget} bytes, so the ledger drains no further until DrainMemoryBytes grows.")]
    private static partial void LogDrainMemoryExceeded(ILogger logger, NodeId node, LedgerId ledger, Slot start, long budget, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Node {Node}'s hand-off request failed as it stops; it asks again.")]
    private static partial void LogHandOffFailed(ILogger logger, NodeId node, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Node {Node}'s admitted control work outlasted {Timeout} as it stops; the runtime cancels it.")]
    private static partial void LogAdmittedWorkOutlasted(ILogger logger, NodeId node, TimeSpan timeout);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Node {Node} could not check the archive store's record before its Journals serve; it checks again until the store answers, logging every tenth failure after it.")]
    private static partial void LogJournalsCheckFailed(ILogger logger, NodeId node, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Node {Node} could not check the archive store's record, failure {Failures}; it checks again.")]
    private static partial void LogJournalsCheckFailedAgain(ILogger logger, NodeId node, int failures, Exception exception);

    [LoggerMessage(EventId = 6243, Level = LogLevel.Information, Message = "Node {Node}'s Journals serve: the archive store's record holds the silo's journal format and MaxBodyBytes, after {Failures} failed checks (OR-I8).")]
    private static partial void LogJournalsServe(ILogger logger, NodeId node, int failures);

    [LoggerMessage(EventId = 6239, Level = LogLevel.Critical, Message = "Node {Node}'s Journals never serve: the archive store's record holds the caller parameter {Parameter} as {Stored}, and this silo is configured with {Configured} (OR-I8). Set the silo's value to the record's, which is fixed for the cluster's life; the node hands its ledgers off and serves its replicas and its control plane meanwhile.")]
    private static partial void LogJournalsParameterRefused(ILogger logger, NodeId node, string parameter, string stored, string configured, Exception exception);

    [LoggerMessage(EventId = 6240, Level = LogLevel.Critical, Message = "Node {Node}'s Journals never serve: the archive store's record breaks its format, a corruption of the cluster's shared store. The node hands its ledgers off and serves its replicas and its control plane meanwhile.")]
    private static partial void LogJournalsRecordCorrupt(ILogger logger, NodeId node, Exception exception);

    [LoggerMessage(EventId = 6241, Level = LogLevel.Critical, Message = "Node {Node} never drains, reads or writes the archive otherwise, or serves its Journals: its archive store holds {Field} as {Stored}, where {Expected} is expected, the cluster's identity in its node registry or this silo's store's own field, so the silo is configured with another store than its cluster's (OR-I8, OR-I20). Fix the silo's store configuration; the node hands its ledgers off and serves its replicas and its control plane meanwhile. A store whose record this silo's first registration created before another store won keeps that record until it is deleted by hand.")]
    private static partial void LogArchiveStoreRefused(ILogger logger, NodeId node, string field, string stored, string expected, Exception exception);

    [LoggerMessage(EventId = 6247, Level = LogLevel.Critical, Message = "Node {Node} never serves its Journals or uses its object-ledger store: its object-ledger store record does not match the cluster's registered store or layout (OL-I1, orleans.md §5.14). Fix the silo's store configuration; the node hands its ledgers off meanwhile.")]
    private static partial void LogObjectLedgerStoreRefused(ILogger logger, NodeId node, Exception exception);

    [LoggerMessage(EventId = 6248, Level = LogLevel.Critical, Message = "Node {Node} never serves its Journals or uses its object-ledger store: the object-ledger store's record breaks its format, a corruption of the cluster's shared store (OL-I1). The node hands its ledgers off meanwhile.")]
    private static partial void LogObjectLedgerRecordCorrupt(ILogger logger, NodeId node, Exception exception);

    [LoggerMessage(EventId = 6246, Level = LogLevel.Critical, Message = "Node {Node} never drains, reads or writes the archive otherwise, or serves its Journals: the archive store refused its check of the record definitely, which no repetition changes. Fix the silo's store configuration; the node hands its ledgers off and serves its replicas and its control plane meanwhile.")]
    private static partial void LogArchiveStoreRequestRefused(ILogger logger, NodeId node, Exception exception);

    [LoggerMessage(EventId = 6242, Level = LogLevel.Critical, Message = "Node {Node}'s Journals never serve: checking the archive store's record failed with a defect, which no repetition changes. The node hands its ledgers off and serves its replicas and its control plane meanwhile.")]
    private static partial void LogJournalsCheckDefect(ILogger logger, NodeId node, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Node {Node}'s cancelled work and its Journals' outstanding writes outlasted {Timeout} as it stops; the runtime disposes under them.")]
    private static partial void LogCancelledWorkOutlasted(ILogger logger, NodeId node, TimeSpan timeout);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Node {Node}'s cancelled work and its Journals' outstanding writes spent the stop's whole bound of {Timeout}; the stop goes on, and the silo's disposal awaits its runtime's disposal, the Journals' among it.")]
    private static partial void LogDisposalAfterTheBound(ILogger logger, NodeId node, TimeSpan timeout);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Node {Node}'s runtime disposal outlasted what its cancelled work left of the stop's bound of {Timeout}; the stop goes on, and the silo's disposal awaits it for a whole bound more.")]
    private static partial void LogDisposalDeferred(ILogger logger, NodeId node, TimeSpan timeout);

    [LoggerMessage(Level = LogLevel.Error, Message = "Node {Node}'s runtime disposal did not end within {Timeout} of the silo's disposal though its cancelled work and its Journals' disposal ended within the stop's bound, a defect; the silo's disposal goes on without it.")]
    private static partial void LogDisposalOutlasted(ILogger logger, NodeId node, TimeSpan timeout);

    [LoggerMessage(Level = LogLevel.Error, Message = "Node {Node}'s runtime disposal, which outlasted its stop, did not end within {Timeout} of the silo's disposal either; the silo's disposal goes on without it.")]
    private static partial void LogDisposalAbandoned(ILogger logger, NodeId node, TimeSpan timeout);

    [LoggerMessage(Level = LogLevel.Error, Message = "Node {Node}'s runtime disposal, which the silo's disposal went on without, failed.")]
    private static partial void LogAbandonedDisposalFailed(ILogger logger, NodeId node, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Node {Node}'s stop: step {Step} {Phase} (orleans.md §5.2).")]
    private static partial void LogStopStep(ILogger logger, NodeId node, StopStep step, StopStepPhase phase);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Node {Node}'s stop was cancelled by the host before its hand-off ended; it stops as a crash would, and its ledgers are taken over.")]
    private static partial void LogStopCancelled(ILogger logger, NodeId node);

    /// <summary>What a report learned: the closed ledgers to drain too, when its reply confirmed the view's epoch, and the unserved ledgers it reported.</summary>
    private sealed record Reported(ImmutableArray<LedgerId> DrainToo, DateTimeOffset ConfirmedAt, ImmutableArray<LedgerId> Unserved);
}

/// <summary>
/// The node's Ledgers runtime once started, and the node's composition root (orleans.md §5.2, §5.6): its map view; its
/// reader, which reads its own replicas first; its writer registry, which serves each ledger the map makes the node primary
/// of; its Journals, which admit over the registry, and the caller parameters they record with the archive store; its node
/// controller, with the LSM's drain and compaction workers, which runs on the runtime's exclusive scheduler; the reconciler
/// of its replicas with the map; and the stopping token under which all their work runs. It disposes the registry, then
/// the reader, after the Journals' disposal has begun, so the writers decide the Journals' proposals from their view.
/// </summary>
internal sealed class NodeRuntime : IAsyncDisposable
{
    /// <summary>
    /// The caller parameter of the archive store's record which holds the cluster's journal format, Orleans'
    /// <c>JournalFormatKey</c> (OR-I8), beside the Journals' own (<see cref="JournalStore.StoreParameters"/>).
    /// </summary>
    public const string JournalFormatParameter = "format";

    private readonly ConcurrentExclusiveSchedulerPair _schedulers = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly LedgerReader _reader;
    private readonly LedgerApplyLoop? _applyLoop;
    private readonly JournalStateDatabase? _stateDatabase;
    private readonly BlockCache? _blockCache;
    private readonly Action? _clearObjectStorePacing;
    private readonly Lock _lock = new();
    private Task? _journalsStopped;

    private NodeRuntime(
        NodeId node,
        int generation,
        NodeDisk? disk,
        ulong releaseDistance,
        LedgerMapView view,
        LedgerStore? store,
        Func<bool> isActive,
        Func<IReadOnlyDictionary<NodeId, int>> deadNodes,
        ArchiveLogNodeShare? objectStoreShare,
        Func<bool>? objectLedgersReady,
        Action? clearObjectStorePacing,
        ControlOptions control,
        TimeProvider timeProvider,
        ILoggerFactory loggers,
        NodeMetrics metrics,
        LedgerReader reader,
        LedgerWriterRegistry writers,
        NodeArchive nodeArchive,
        IArchiveStore archiveStore,
        LsmArchiveStore? lsmStore,
        BlockCache? blockCache,
        JournalStateDatabase? stateDatabase,
        LedgerApplyLoop? applyLoop,
        JournalStore journals,
        WakeupStreamHandler? wakeupHandler,
        LocalStreamDrainCoordinator? localDrainCoordinator,
        IReadOnlyDictionary<string, string> recordParameters)
    {
        Disk = disk;
        ReleaseDistance = releaseDistance;
        Metrics = metrics;
        View = view;
        _reader = reader;
        _stateDatabase = stateDatabase;
        _applyLoop = applyLoop;
        _blockCache = blockCache;
        _clearObjectStorePacing = clearObjectStorePacing;
        Writers = writers;
        Archive = archiveStore;
        LsmStore = lsmStore;
        Journals = journals;
        WakeupHandler = wakeupHandler;
        LocalDrainCoordinator = localDrainCoordinator;
        RecordParameters = recordParameters;
        var executor = new TransitionExecutor(Writers, loggers.CreateLogger<TransitionExecutor>());
        LogDrains = nodeArchive.Value as NodeArchive.Logs is { } logs ? logs.Drains : null;
        Controller = new NodeController(node, generation, view, Writers, _reader, executor, isActive, deadNodes, nodeArchive, timeProvider, control.LoadWindow, control.MaxConcurrentDrains, loggers.CreateLogger<NodeController>(), metrics.Meter, objectStoreShare, objectLedgersReady, (ledger, range, deadline, token) => Journals.PrepareReleaseAsync(ledger, range, deadline, token), localDrainCoordinator);
        Reconciler = store is not null ? new ReplicaReconciler(store, view) : null;
    }

    /// <summary>Gets the node's disk policy, which the governance loop judges each measurement by, or <see langword="null"/> on a diskless node.</summary>
    public NodeDisk? Disk { get; }

    /// <summary>Gets the node's instruments on the composition's meter, which end with the runtime.</summary>
    public NodeMetrics Metrics { get; }

    /// <summary>Gets the resource distance <c>F</c>, in WAL files, at which the node's oldest replicas are rolled for WAL release.</summary>
    public ulong ReleaseDistance { get; }

    /// <summary>Gets the node's view of <c>ledger-map</c>.</summary>
    public LedgerMapView View { get; }

    /// <summary>Gets the node's writer registry, over which the Journals admit.</summary>
    public LedgerWriterRegistry Writers { get; }

    /// <summary>Gets the node's ledger reader, which its writers, drains, and executor share; for row 4.2's Journals and for tests.</summary>
    public LedgerReader Reader => _reader;

    /// <summary>
    /// Gets the cluster's archive store as the node reads it, which the node's Journals read histories from (orleans.md §2):
    /// on the LSM, over the node's manifest view and the tail cache its compaction worker shares; on archive logs, the
    /// silo's store over the cluster's container.
    /// </summary>
    public IArchiveStore Archive { get; }

    /// <summary>
    /// Gets <see cref="Archive"/> on the LSM, which the node preloads with the ranges it is primary of, or
    /// <see langword="null"/> on archive logs (archive-lsm.md §2.5, orleans.md §5.6).
    /// </summary>
    public LsmArchiveStore? LsmStore { get; }

    /// <summary>Gets the node's Journals, which serve once the archive store's record holds <see cref="RecordParameters"/> (OR-I8).</summary>
    public JournalStore Journals { get; }

    /// <summary>Gets the node's wakeup local-stream handler when registered, or <see langword="null"/>.</summary>
    internal WakeupStreamHandler? WakeupHandler { get; }

    /// <summary>Gets the node's closed-ledger local-stream drain coordinator when local streams are registered, or <see langword="null"/>.</summary>
    internal LocalStreamDrainCoordinator? LocalDrainCoordinator { get; }

    /// <summary>Gets the node's journal state database when configured with local state storage, or <see langword="null"/>; for tests.</summary>
    internal JournalStateDatabase? StateDatabase => _stateDatabase;

    /// <summary>
    /// Gets the caller parameters the silo records with the archive store, and checks before its Journals serve (OR-I8,
    /// archive.md §2.4): the Journals' own, <c>max-body-bytes</c>, and the journal format, <see cref="JournalFormatParameter"/>.
    /// </summary>
    public IReadOnlyDictionary<string, string> RecordParameters { get; }

    /// <summary>Gets the node's controller, which runs only on <see cref="Scheduler"/>.</summary>
    public NodeController Controller { get; }

    /// <summary>Gets the node's archive-log drains on archive logs, or <see langword="null"/> on the LSM; for tests.</summary>
    internal ArchiveLogNodeDrains? LogDrains { get; }

    /// <summary>Gets the reconciler of the node's replicas with the map, or <see langword="null"/> on a diskless node.</summary>
    public ReplicaReconciler? Reconciler { get; }

    /// <summary>Gets the exclusive scheduler on which the node controller runs, one task at a time.</summary>
    public TaskScheduler Scheduler => _schedulers.ExclusiveScheduler;

    /// <summary>Gets the token under which all the runtime's work runs, which <see cref="CancelAsync"/> cancels.</summary>
    public CancellationToken Stopping => _stopping.Token;

    /// <summary>
    /// Starts the node's runtime over its map view and store: builds its reader, writer registry, archive store and the
    /// node's work on it, and Journals, whose construction is the silo's start check (journals.md §8.4, OR-I17), and the
    /// caller parameters the Journals record (<see cref="RecordParametersOf"/>); disposes whatever it built when a later
    /// step throws. On the LSM the node drains and compacts over its manifest view; on archive logs it builds its
    /// <see cref="ArchiveLogNodeDrains"/>, whose share of <c>ArchiveLogRequestsPerSecond</c> each report reply sets and
    /// whose floor divides the budget by <paramref name="activeNodes"/>, and gives the writer registry its
    /// <see cref="ArchiveLogNodeDrains.OnSegmentEnded"/> sink (orleans.md §5.6).
    /// </summary>
    /// <exception cref="JournalStoreLimitException">A start check of the Journals fails, naming its bound.</exception>
    /// <exception cref="ArgumentException">
    /// The writers' <c>MaxProposalBytes</c> holds less than the payload limit, or the caller parameters cannot be recorded.
    /// </exception>
    public static async Task<NodeRuntime> StartAsync(
        NodeId node,
        int generation,
        NodeDisk? disk,
        NodePressure pressure,
        ulong releaseDistance,
        LedgerMapView view,
        ILedgerReplicaTransport transport,
        LedgerStore? store,
        SiloArchive archive,
        SiloObjectLedgers objectLedgers,
        Func<bool> isActive,
        Func<IReadOnlyDictionary<NodeId, int>> deadNodes,
        Func<int> activeNodes,
        int maxBatchLength,
        LedgerWriterOptions writers,
        LedgerLeaseOptions leaseOptions,
        JournalStoreOptions journals,
        string journalFormat,
        ArchiveOptions lsmOptions,
        ArchiveLogOptions logOptions,
        ControlOptions control,
        TimeSpan blockedDrainRetryInterval,
        TimeProvider timeProvider,
        ILoggerFactory loggers,
        IJournalIndex? journalIndex,
        LocalStreamRegistration? localStreams = null,
        CancellationToken cancellationToken = default)
    {
        NodeMetrics? metrics = null;
        LedgerReader? reader = null;
        LedgerWriterRegistry? registry = null;
        BlockCache? blockCache = null;
        JournalStateDatabase? stateDatabase = null;
        LedgerApplyLoop? applyLoop = null;
        JournalStore? journalStore = null;
        try
        {
            var streams = localStreams ?? LocalStreamRegistration.Empty;
            ValidateLocalStreamParity(streams.Handlers, streams.Processors);
            var parameters = RecordParametersOf(journals, journalFormat, streams.Handlers);
            metrics = new NodeMetrics(node, pressure);
            reader = new LedgerReader(view, transport, timeProvider, node)
            {
                LeaseOptions = leaseOptions,
                ObjectStoreProvider = objectLedgers.IsConfigured ? objectLedgers.Gate.RequirePassed : null,
            };
            ArchiveLogNodeShare? objectStoreShare = null;
            Action? clearObjectStorePacing = null;
            if (objectLedgers.IsConfigured)
            {
                double ObjectStoreFloor() => ArchiveLogRequestShare.Floor(control.ObjectStoreRequestsPerSecond, Math.Max(1, activeNodes()));

                objectStoreShare = new ArchiveLogNodeShare(ObjectStoreFloor, control.ControlInterval);
                if (objectLedgers.Pacer is { } pacer)
                {
                    pacer.RateSource = () => objectStoreShare.RateAt(timeProvider.GetUtcNow()) ?? ObjectStoreFloor();
                    pressure.SetObjectStoreThrottled(() => pacer.IsBudgetExhausted);
                    clearObjectStorePacing = () =>
                    {
                        pressure.SetObjectStoreThrottled(null);
                        pacer.RateSource = null;
                    };
                }
            }

            if (journalIndex is not null)
            {
                stateDatabase = await JournalStateDatabase.OpenAsync(journalIndex, reader, timeProvider: timeProvider, cancellationToken: cancellationToken).ConfigureAwait(false);
                applyLoop = new LedgerApplyLoop(reader, stateDatabase.Applier, timeProvider);
                stateDatabase.Attach(applyLoop);
            }

            ILocalKeyPresenceLookup? presenceLookup = stateDatabase is not null ? new JournalStateLocalKeyPresenceLookup(stateDatabase) : null;
            LocalStreamDrainCoordinator? localDrainCoordinator = streams.Processors.IsEmpty
                ? null
                : new LocalStreamDrainCoordinator(view, reader, streams.Processors, control.LocalDrainMemoryBytes, presenceLookup);

            IArchiveStore archiveStore;
            LsmArchiveStore? lsmStore = null;
            NodeArchive nodeArchive;
            switch (archive)
            {
                case SiloArchive.Logs logs:
                    {
                        var drains = new ArchiveLogNodeDrains(
                            node,
                            control.ArchiveLogRequestsPerSecond,
                            activeNodes,
                            writers.MaxSegmentKeys,
                            control.ControlInterval,
                            logOptions,
                            timeProvider,
                            metrics.Meter,
                            loggers.CreateLogger<ArchiveLogNodeDrains>(),
                            localDrainCoordinator)
                        {
                            RetryBlockedAfter = blockedDrainRetryInterval,
                        };
                        registry = new LedgerWriterRegistry(reader, maxBatchLength, writers, timeProvider, loggers, drains.OnSegmentEnded);
                        archiveStore = logs.Store;
                        nodeArchive = new NodeArchive.Logs(drains);
                        break;
                    }

                case SiloArchive.Lsm lsm:
                    {
                        registry = new LedgerWriterRegistry(reader, maxBatchLength, writers, timeProvider, loggers);
                        var objects = lsm.Objects;
                        var manifest = new ManifestView(objects);
                        var tails = new TailCache(objects, lsmOptions);
                        if (!string.IsNullOrWhiteSpace(lsmOptions.BlockCachePath) && lsmOptions.BlockCacheBytes > 0)
                        {
                            blockCache = await BlockCache.OpenAsync(lsmOptions.BlockCachePath, lsmOptions, cancellationToken).ConfigureAwait(false);
                        }

                        lsmStore = new LsmArchiveStore(objects, manifest, tails, lsmOptions, timeProvider, blockCache);
                        archiveStore = lsmStore;
                        nodeArchive = new NodeArchive.Lsm(new DrainWorker(objects, manifest, reader, lsmOptions, timeProvider, blockCache, localDrainCoordinator), new CompactionWorker(objects, tails, manifest, lsmOptions, blockCache), manifest);
                        break;
                    }

                default:
                    throw new UnreachableException("A silo's archive store is the LSM or archive logs.");
            }

            journalStore = new JournalStore(registry, archiveStore, journals, timeProvider, stateDatabase, streams.Handlers);
            return new NodeRuntime(node, generation, disk, releaseDistance, view, store, isActive, deadNodes, objectStoreShare, () => objectLedgers.IsConfigured && objectLedgers.Gate.IsPassed, clearObjectStorePacing, control, timeProvider, loggers, metrics, reader, registry, nodeArchive, archiveStore, lsmStore, blockCache, stateDatabase, applyLoop, journalStore, streams.WakeupHandler, localDrainCoordinator, parameters);
        }
        catch
        {
            pressure.SetObjectStoreThrottled(null);
            if (objectLedgers.Pacer is { } pacer)
            {
                pacer.RateSource = null;
            }

            if (journalStore is not null)
            {
                await journalStore.DisposeAsync().ConfigureAwait(false);
            }

            if (registry is not null)
            {
                await registry.DisposeAsync().ConfigureAwait(false);
            }

            if (applyLoop is not null)
            {
                await applyLoop.DisposeAsync().ConfigureAwait(false);
            }

            if (reader is not null)
            {
                await reader.DisposeAsync().ConfigureAwait(false);
            }

            if (stateDatabase is not null)
            {
                await stateDatabase.DisposeAsync().ConfigureAwait(false);
            }

            blockCache?.Dispose();
            metrics?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Validates exact set equality between registered <see cref="ILocalStreamHandler"/> and
    /// <see cref="ILocalStreamProcessor"/> stream names (orleans.md §5.15, LG-I16).
    /// </summary>
    /// <exception cref="ArgumentException">The registered handler and processor stream sets differ or contain duplicates.</exception>
    internal static void ValidateLocalStreamParity(
        IEnumerable<ILocalStreamHandler> handlers,
        IEnumerable<ILocalStreamProcessor> processors)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        ArgumentNullException.ThrowIfNull(processors);

        var handlerStreams = new HashSet<LocalStreamName>();
        foreach (var handler in handlers)
        {
            ArgumentNullException.ThrowIfNull(handler);
            if (!handlerStreams.Add(handler.Stream))
            {
                throw new ArgumentException($"Duplicate local stream handler registered for '{handler.Stream}'.", nameof(handlers));
            }
        }

        var processorStreams = new HashSet<LocalStreamName>();
        foreach (var processor in processors)
        {
            ArgumentNullException.ThrowIfNull(processor);
            if (!processorStreams.Add(processor.Stream))
            {
                throw new ArgumentException($"Duplicate local stream processor registered for '{processor.Stream}'.", nameof(processors));
            }
        }

        if (!handlerStreams.SetEquals(processorStreams))
        {
            var missingProcessors = handlerStreams.Except(processorStreams).Select(static s => s.Value).Order(StringComparer.Ordinal);
            var missingHandlers = processorStreams.Except(handlerStreams).Select(static s => s.Value).Order(StringComparer.Ordinal);
            throw new ArgumentException(
                $"Registered local stream handlers and processors must match exactly (missing processors: [{string.Join(", ", missingProcessors)}]; missing handlers: [{string.Join(", ", missingHandlers)}]).");
        }
    }

    /// <summary>
    /// Returns the caller parameters Journals over <paramref name="journals"/> record with the archive store, their own
    /// (<see cref="JournalStore.StoreParametersOf"/>) and <paramref name="journalFormat"/> as
    /// <see cref="JournalFormatParameter"/>, once the store's record could hold them (OR-I8): the one definition of the set,
    /// which a new cluster's first registration records too.
    /// </summary>
    /// <exception cref="ArgumentException">A parameter is malformed, as a format key holding a line feed; the message names it.</exception>
    public static IReadOnlyDictionary<string, string> RecordParametersOf(
        JournalStoreOptions journals,
        string journalFormat,
        IEnumerable<ILocalStreamHandler>? localStreamHandlers = null)
    {
        ArgumentNullException.ThrowIfNull(journals);
        var parameters = new Dictionary<string, string>(JournalStore.StoreParametersOf(journals, localStreamHandlers), StringComparer.Ordinal)
        {
            [JournalFormatParameter] = journalFormat,
        };
        ArchiveStoreRecord.CheckParameters(parameters);
        return parameters;
    }

    /// <summary>Cancels the runtime's work.</summary>
    public Task CancelAsync() => _stopping.CancelAsync();

    /// <summary>
    /// Stops any registered <see cref="WakeupStreamHandler"/> timer loops and in-flight dispatches, then disposes the
    /// node's Journals, which retires every admission and waits for the outcomes of their proposals while the registry
    /// lives (journals.md §9, orleans.md §5.15). Every call returns the one disposal.
    /// </summary>
    public Task StopJournalsAsync()
    {
        lock (_lock)
        {
            return _journalsStopped ??= StopWakeupAndJournalsAsync();
        }
    }

    private async Task StopWakeupAndJournalsAsync()
    {
        if (WakeupHandler is { } wakeup)
        {
            await wakeup.StopAsync().ConfigureAwait(false);
        }

        await Journals.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Cancels the runtime's work; disposes the Journals, if they were not, then the writer registry, which answers every
    /// proposal the Journals' disposal still waits for, then the apply loop (surfacing any fault from its
    /// <see cref="LedgerApplyLoop.Completion"/>), then the reader, then awaits the Journals' disposal and disposes the
    /// state database, block cache, meter, and stopping token, each whatever the one before it ended with; throws the
    /// failures together once all ran.
    /// </summary>
    /// <exception cref="AggregateException">Disposing any runtime component failed or the apply loop faulted.</exception>
    public async ValueTask DisposeAsync()
    {
        _clearObjectStorePacing?.Invoke();
        await _stopping.CancelAsync().ConfigureAwait(false);
        var journals = StopJournalsAsync();
        List<Exception>? failures = null;

        async ValueTask RunStepAsync(Func<ValueTask> step)
        {
            try
            {
                await step().ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                (failures ??= []).Add(failure);
            }
        }

        try
        {
            await RunStepAsync(() => Writers.DisposeAsync()).ConfigureAwait(false);
            if (_applyLoop is not null)
            {
                await RunStepAsync(async () =>
                {
                    await _applyLoop.DisposeAsync().ConfigureAwait(false);
                    await _applyLoop.Completion.ConfigureAwait(false);
                }).ConfigureAwait(false);
            }

            await RunStepAsync(() => _reader.DisposeAsync()).ConfigureAwait(false);
            await RunStepAsync(() => new ValueTask(journals)).ConfigureAwait(false);
            if (_stateDatabase is not null)
            {
                await RunStepAsync(async () =>
                {
                    if (!_stateDatabase.IsCorrupt)
                    {
                        try
                        {
                            await _stateDatabase.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                        }
                        catch (JournalIndexCorruptException)
                        {
                            // A corrupt derived index is discarded and rebuilt on the next open.
                        }
                    }

                    await _stateDatabase.DisposeAsync().ConfigureAwait(false);
                }).ConfigureAwait(false);
            }
        }
        finally
        {
            _blockCache?.Dispose();
            Metrics.Dispose();

            // The scheduler pair is never completed: work which outlasted the stop's wait may still continue on it,
            // and a completed pair would drop that continuation. It holds no thread or handle of its own.
            _stopping.Dispose();
        }

        if (failures is not null)
        {
            throw new AggregateException("Disposing the node's Ledgers runtime failed.", failures);
        }
    }
}
