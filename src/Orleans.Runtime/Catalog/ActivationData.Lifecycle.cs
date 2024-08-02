#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Core.Internal;
using Orleans.GrainDirectory;
using Orleans.Internal;
using Orleans.Runtime.Placement;
using Orleans.Runtime.Scheduler;

namespace Orleans.Runtime;

internal sealed partial class ActivationData
{
    public void Rehydrate(IRehydrationContext context)
    {
        ScheduleCommand(new Command.Rehydrate(context));
    }

    public void Activate(Dictionary<string, object>? requestContext, CancellationToken cancellationToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_shared.InternalRuntime.CollectionOptions.Value.ActivationTimeout);

        ScheduleCommand(new Command.Activate(requestContext, cts));
    }

    public void Migrate(Dictionary<string, object>? requestContext, CancellationToken cancellationToken = default)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_shared.InternalRuntime.CollectionOptions.Value.DeactivationTimeout);

        // We use a named work item since it is cheaper than allocating a Task and has the benefit of being named.
        _workItemGroup.QueueWorkItem(new MigrateWorkItem(this, requestContext, cts));
    }

    public void Deactivate(DeactivationReason reason, CancellationToken cancellationToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_shared.InternalRuntime.CollectionOptions.Value.DeactivationTimeout);

        StartDeactivating(reason);
        ScheduleCommand(new Command.Deactivate(cts));
    }

    public void DelayDeactivation(TimeSpan timespan)
    {
        if (timespan == TimeSpan.MaxValue || timespan == Timeout.InfiniteTimeSpan)
        {
            // Otherwise creates negative time.
            KeepAliveUntil = DateTime.MaxValue;
        }
        else if (timespan <= TimeSpan.Zero)
        {
            KeepAliveUntil = DateTime.MinValue;
        }
        else
        {
            KeepAliveUntil = GrainRuntime.TimeProvider.GetUtcNow().UtcDateTime + timespan;
        }
    }

    private void DeactivateStuckActivation()
    {
        IsStuckProcessingMessage = true;
        var msg = $"Activation {this} has been processing request {_blockingRequest} since {_busyDuration} and is likely stuck.";
        var reason = new DeactivationReason(DeactivationReasonCode.ActivationUnresponsive, msg);

        // Mark the grain as deactivating so that messages are forwarded instead of being invoked
        Deactivate(reason, cancellationToken: default);

        // Try to remove this activation from the catalog and directory
        // This leaves this activation dangling, stuck processing the current request until it eventually completes
        // (which likely will never happen at this point, since if the grain was deemed stuck then there is probably some kind of
        // application bug, perhaps a deadlock)
        UnregisterMessageTarget();
        _shared.InternalRuntime.GrainLocator.Unregister(Address, UnregistrationCause.Force).Ignore();
    }

    private async Task StartMigratingAsync(Dictionary<string, object>? requestContext, CancellationTokenSource cts)
    {
        lock (this)
        {
            // Avoid the cost of selecting a new location if the activation is not currently valid.
            if (State is not ActivationState.Valid)
            {
                return;
            }
        }

        SiloAddress newLocation;
        try
        {
            // Run placement to select a new host. If a new (different) host is not selected, do not migrate.
            var placementService = _shared.Runtime.ServiceProvider.GetRequiredService<PlacementService>();
            newLocation = await placementService.PlaceGrainAsync(Address.GrainId, requestContext, PlacementStrategy).WaitAsync(cts.Token);
            if (newLocation == Address.SiloAddress || newLocation is null)
            {
                // No more appropriate silo was selected for this grain. The migration attempt will be aborted.
                // This could be because this is the only (compatible) silo for the grain or because the placement director chose this
                // silo for some other reason.
                if (_shared.Logger.IsEnabled(LogLevel.Debug))
                {
                    if (newLocation is null)
                    {
                        _shared.Logger.LogDebug("Placement strategy {PlacementStrategy} failed to select a destination for migration of {GrainAddress}", PlacementStrategy, Address);
                    }
                    else
                    {
                        _shared.Logger.LogDebug("Placement strategy {PlacementStrategy} selected the current silo as the destination for migration of {GrainAddress}", PlacementStrategy, Address);
                    }
                }

                // Will not deactivate/migrate.
                return;
            }

            lock (this)
            {
                if (!StartDeactivating(new DeactivationReason(DeactivationReasonCode.Migrating, "Migrating to a new location")))
                {
                    // Grain is already deactivating, ignore the migration request.
                    return;
                }

                if (DehydrationContext is not null)
                {
                    // Migration has already started.
                    return;
                }

                // Set a migration context to capture any state which should be transferred.
                // Doing this signals to the deactivation process that a migration is occurring, so it is important that this happens before we begin deactivation.
                DehydrationContext = new(_shared.SerializerSessionPool, requestContext);
                ForwardingAddress = newLocation;
            }

            if (_shared.Logger.IsEnabled(LogLevel.Debug))
            {
                _shared.Logger.LogDebug("Migrating {GrainAddress} to {SiloAddress}", Address, newLocation);
            }

            // Start deactivation to prevent any other.
            ScheduleCommand(new Command.Deactivate(cts));
        }
        catch (Exception exception)
        {
            _shared.Logger.LogError(exception, "Error while selecting a migration destination for {GrainAddress}", Address);
            return;
        }
    }


    private void RehydrateInternal(IRehydrationContext context)
    {
        try
        {
            if (_shared.Logger.IsEnabled(LogLevel.Debug))
            {
                _shared.Logger.LogDebug("Rehydrating grain from previous activation");
            }

            lock (this)
            {
                if (State != ActivationState.Create)
                {
                    throw new InvalidOperationException($"Attempted to rehydrate a grain in the {State} state");
                }

                if (context.TryGetValue(GrainAddressMigrationContextKey, out GrainAddress? previousRegistration) && previousRegistration is not null)
                {
                    // Propagate the previous registration, so that the new activation can atomically replace it with its new address.
                    PreviousRegistration = previousRegistration;
                    if (_shared.Logger.IsEnabled(LogLevel.Debug))
                    {
                        _shared.Logger.LogDebug("Previous activation address was {PreviousRegistration}", previousRegistration);
                    }
                }

                if (_lifecycle is { } lifecycle)
                {
                    foreach (var participant in lifecycle.GetMigrationParticipants())
                    {
                        participant.OnRehydrate(context);
                    }
                }

                (GrainInstance as IGrainMigrationParticipant)?.OnRehydrate(context);
            }

            if (_shared.Logger.IsEnabled(LogLevel.Debug))
            {
                _shared.Logger.LogDebug("Rehydrated grain from previous activation");
            }
        }
        catch (Exception exception)
        {
            _shared.Logger.LogError(exception, "Error while rehydrating activation");
        }
        finally
        {
            (context as IDisposable)?.Dispose();
        }
    }

    private void OnDehydrate(IDehydrationContext context)
    {
        if (_shared.Logger.IsEnabled(LogLevel.Debug))
        {
            _shared.Logger.LogDebug("Dehydrating grain activation");
        }

        lock (this)
        {
            Debug.Assert(context is not null);

            // Note that these calls are in reverse order from Rehydrate, not for any particular reason other than symmetry.
            (GrainInstance as IGrainMigrationParticipant)?.OnDehydrate(context);

            if (_lifecycle is { } lifecycle)
            {
                foreach (var participant in lifecycle.GetMigrationParticipants())
                {
                    participant.OnDehydrate(context);
                }
            }

            if (IsUsingGrainDirectory)
            {
                context.TryAddValue(GrainAddressMigrationContextKey, Address);
            }
        }

        if (_shared.Logger.IsEnabled(LogLevel.Debug))
        {
            _shared.Logger.LogDebug("Dehydrated grain activation");
        }
    }

    private async Task ActivateAsync(Dictionary<string, object>? requestContextData, CancellationToken cancellationToken)
    {
        // A chain of promises that will have to complete in order to complete the activation
        // Register with the grain directory, register with the store if necessary and call the Activate method on the new activation.
        try
        {
            var success = await RegisterActivationInGrainDirectoryAndValidate();
            if (!success)
            {
                // If registration failed, bail out.
                return;
            }

            if (!SetState(ActivationState.Create, ActivationState.Activating))
            {
                // The activation has been told to deactivate.
                return;
            }

            success = await CallActivateAsync(requestContextData, cancellationToken);
            if (!success)
            {
                // If activation failed, bail out.
                return;
            }

            _shared.InternalRuntime.ActivationWorkingSet.OnActivated(this);
            if (_shared.Logger.IsEnabled(LogLevel.Debug))
            {
                _shared.Logger.LogDebug("InitActivation is done: {Address}", Address);
            }
        }
        catch (Exception exception)
        {
            _shared.Logger.LogError(exception, "Activation of grain {Grain} failed", this);
        }
        finally
        {
            _workSignal.Signal();
        }

        async Task<bool> CallActivateAsync(Dictionary<string, object>? requestContextData, CancellationToken cancellationToken)
        {
            if (_shared.Logger.IsEnabled(LogLevel.Debug))
            {
                _shared.Logger.LogDebug((int)ErrorCode.Catalog_BeforeCallingActivate, "Activating grain {Grain}", this);
            }

            // Start grain lifecycle within try-catch wrapper to safely capture any exceptions thrown from called function
            try
            {
                RequestContextExtensions.Import(requestContextData);
                if (_lifecycle is { } lifecycle)
                {
                    await lifecycle.OnStart(cancellationToken).WithCancellation("Timed out waiting for grain lifecycle to complete activation", cancellationToken);
                }

                if (State is not ActivationState.Activating)
                {
                    return false;
                }

                if (GrainInstance is IGrainBase grainBase)
                {
                    await grainBase.OnActivateAsync(cancellationToken).WithCancellation($"Timed out waiting for {nameof(IGrainBase.OnActivateAsync)} to complete", cancellationToken);
                }

                // Activate calls on this activation are finished
                if (!SetState(ActivationState.Activating, ActivationState.Valid))
                {
                    return false;
                }

                if (_shared.Logger.IsEnabled(LogLevel.Debug))
                {
                    _shared.Logger.LogDebug((int)ErrorCode.Catalog_AfterCallingActivate, "Finished activating grain {Grain}", this);
                }

                return true;
            }
            catch (Exception exception)
            {
                CatalogInstruments.ActivationFailedToActivate.Add(1);

                // Capture the exception so that it can be propagated to rejection messages
                var sourceException = (exception as OrleansLifecycleCanceledException)?.InnerException ?? exception;
                _shared.Logger.LogError((int)ErrorCode.Catalog_ErrorCallingActivate, sourceException, "Error activating grain {Grain}", this);

                // Unregister the activation from the directory so other silo don't keep sending message to it
                lock (this)
                {
                    if (SetState(ActivationState.Activating, ActivationState.FailedToActivate))
                    {
                        DeactivationReason = new(DeactivationReasonCode.ActivationFailed, sourceException, "Failed to activate grain.");
                    }
                }

                GetDeactivationCompletionSource().TrySetResult(true);

                if (IsUsingGrainDirectory && ForwardingAddress is null)
                {
                    try
                    {
                        await _shared.InternalRuntime.GrainLocator.Unregister(Address, UnregistrationCause.Force);
                    }
                    catch (Exception ex)
                    {
                        _shared.Logger.LogWarning(
                            (int)ErrorCode.Catalog_UnregisterAsync,
                            ex,
                            "Failed to unregister grain activation {Grain} after activation failed",
                            this);
                    }
                }

                // Unregister this as a message target after some period of time.
                // This is delayed so that consistently failing activation, perhaps due to an application bug or network
                // issue, does not cause a flood of doomed activations.
                // If the cancellation token was canceled, there is no need to wait an additional time, since the activation
                // has already waited some significant amount of time.
                if (!cancellationToken.IsCancellationRequested)
                {
                    ScheduleCommand(new Command.Delay(TimeSpan.FromSeconds(5)));
                }

                SetState(ActivationState.FailedToActivate, ActivationState.Invalid);
                ScheduleCommand(Command.UnregisterFromCatalog.Instance);

                return false;
            }
        }
    }

    private async ValueTask<bool> RegisterActivationInGrainDirectoryAndValidate()
    {
        bool success;

        // Currently, the only grain type that is not registered in the Grain Directory is StatelessWorker.
        // Among those that are registered in the directory, we currently do not have any multi activations.
        if (!IsUsingGrainDirectory)
        {
            // Grains which do not use the grain directory do not need to do anything here
            success = true;
        }
        else
        {
            Exception? registrationException;
            var previousRegistration = PreviousRegistration;
            try
            {
                while (true)
                {
                    var result = await _shared.InternalRuntime.GrainLocator.Register(Address, previousRegistration);
                    if (Address.Matches(result))
                    {
                        Address = result;
                        success = true;
                    }
                    else if (result?.SiloAddress is { } registeredSilo && registeredSilo.Equals(Address.SiloAddress))
                    {
                        if (_shared.Logger.IsEnabled(LogLevel.Debug))
                        {
                            _shared.Logger.LogDebug(
                                "The grain directory has an existing entry pointing to a different activation of this grain on this silo, {PreviousRegistration}."
                                + " This may indicate that the previous activation was deactivated but the directory was not successfully updated."
                                + " The directory will be updated to point to this activation.",
                                previousRegistration);
                        }

                        // Attempt to register this activation again, using the registration of the previous instance of this grain,
                        // which is registered to this silo. That activation must be a defunct predecessor of this activation,
                        // since the catalog only allows one activation of a given grain at a time.
                        // This could occur if the previous activation failed to unregister itself from the grain directory.
                        previousRegistration = result;
                        continue;
                    }
                    else
                    {
                        // Set the forwarding address so that messages enqueued on this activation can be forwarded to
                        // the existing activation.
                        ForwardingAddress = result?.SiloAddress;
                        if (ForwardingAddress is { } address)
                        {
                            DeactivationReason = new(DeactivationReasonCode.DuplicateActivation, $"This grain is active on another host ({address}).");
                        }

                        success = false;
                        CatalogInstruments.ActivationConcurrentRegistrationAttempts.Add(1);
                        if (_shared.Logger.IsEnabled(LogLevel.Debug))
                        {
                            // If this was a duplicate, it's not an error, just a race.
                            // Forward on all of the pending messages, and then forget about this activation.
                            _shared.Logger.LogDebug(
                                (int)ErrorCode.Catalog_DuplicateActivation,
                                "Tried to create a duplicate activation {Address}, but we'll use {ForwardingAddress} instead. "
                                + "GrainInstance type is {GrainInstanceType}. "
                                + "Full activation address is {Address}. We have {WaitingCount} messages to forward.",
                                Address,
                                ForwardingAddress,
                                GrainInstance?.GetType(),
                                Address.ToFullString(),
                                WaitingCount);
                        }
                    }

                    break;
                }

                registrationException = null;
            }
            catch (Exception exception)
            {
                registrationException = exception;
                _shared.Logger.LogWarning((int)ErrorCode.Runtime_Error_100064, registrationException, "Failed to register grain {Grain} in grain directory", ToString());
                success = false;
            }

            if (!success)
            {
                if (DeactivationReason.ReasonCode == DeactivationReasonCode.None)
                {
                    DeactivationReason = new(DeactivationReasonCode.InternalFailure, registrationException, "Failed to register activation in grain directory.");
                }

                SetState(ActivationState.Create, ActivationState.Invalid);
                UnregisterMessageTarget();
            }
        }

        return success;
    }

    /// <summary>
    /// Starts the deactivation process.
    /// </summary>
    public bool StartDeactivating(DeactivationReason reason)
    {
        lock (this)
        {
            if (State is ActivationState.Deactivating or ActivationState.Invalid or ActivationState.FailedToActivate)
            {
                return false;
            }


            if (DeactivationReason.ReasonCode == DeactivationReasonCode.None)
            {
                DeactivationReason = reason;
            }

            DeactivationStartTime = GrainRuntime.TimeProvider.GetUtcNow().UtcDateTime;
            State = ActivationState.Deactivating;
            _shared.InternalRuntime.ActivationWorkingSet.OnDeactivating(this);
        }

        return true;
    }

    /// <summary>
    /// Completes the deactivation process.
    /// </summary>
    /// <param name="cancellationToken">A cancellation which terminates graceful deactivation when cancelled.</param>
    private async Task FinishDeactivating(CancellationToken cancellationToken)
    {
        var migrated = false;
        try
        {
            if (_shared.Logger.IsEnabled(LogLevel.Trace))
            {
                _shared.Logger.LogTrace("FinishDeactivating activation {Activation}", this.ToDetailedString());
            }

            // Stop timers from firing.
            DisposeTimers();

            // Call OnDeactivateAsync(reason, cancellationToken)
            await CallGrainDeactivate(cancellationToken);

            if (DehydrationContext is { } context
                && ForwardingAddress is { } forwardingAddress
                && _shared.MigrationManager is { } migrationManager)
            {
                try
                {
                    // Populate the dehydration context.
                    if (context.RequestContext is { } requestContext)
                    {
                        RequestContextExtensions.Import(requestContext);
                    }
                    else
                    {
                        RequestContext.Clear();
                    }

                    OnDehydrate(context.MigrationContext);

                    // Send the dehydration context to the target host.
                    await migrationManager.MigrateAsync(forwardingAddress, Address.GrainId, context.MigrationContext);
                    _shared.InternalRuntime.GrainLocator.UpdateCache(Address.GrainId, forwardingAddress);
                    migrated = true;
                }
                catch (Exception exception)
                {
                    _shared.Logger.LogWarning(exception, "Failed to migrate grain {GrainAddress} to {SiloAddress}", Address.GrainId, forwardingAddress);
                }
                finally
                {
                    RequestContext.Clear();
                }
            }

            if (!migrated)
            {
                // Unregister from directory
                await _shared.InternalRuntime.GrainLocator.Unregister(Address, UnregistrationCause.Force);
            }

            if (_shared.Logger.IsEnabled(LogLevel.Trace))
            {
                _shared.Logger.LogTrace("Completed async portion of FinishDeactivating for activation {Activation}", this.ToDetailedString());
            }
        }
        catch (Exception ex)
        {
            _shared.Logger.LogWarning((int)ErrorCode.Catalog_DeactivateActivation_Exception, ex, "Exception when trying to deactivate {Activation}", this);
        }

        SetState(ActivationState.Deactivating, ActivationState.Invalid);

        if (IsStuckDeactivating)
        {
            CatalogInstruments.ActivationShutdownViaDeactivateStuckActivation();
        }
        else if (migrated)
        {
            CatalogInstruments.ActivationShutdownViaMigration();
        }
        else if (_isInWorkingSet)
        {
            CatalogInstruments.ActivationShutdownViaDeactivateOnIdle();
        }
        else
        {
            CatalogInstruments.ActivationShutdownViaCollection();
        }

        _shared.InternalRuntime.ActivationWorkingSet.OnDeactivated(this);

        try
        {
            await DisposeAsync();
        }
        catch (Exception exception)
        {
            _shared.Logger.LogWarning(exception, "Exception disposing activation {Activation}", this);
        }

        UnregisterMessageTarget();

        // Signal deactivation
        GetDeactivationCompletionSource().TrySetResult(true);
        _workSignal.Signal();

        if (_shared.Logger.IsEnabled(LogLevel.Trace))
        {
            _shared.Logger.LogTrace("Completed final portion of FinishDeactivating for activation {Activation}", this.ToDetailedString());
        }

        async Task CallGrainDeactivate(CancellationToken ct)
        {
            try
            {
                // Note: This call is being made from within Scheduler.Queue wrapper, so we are already executing on worker thread
                if (_shared.Logger.IsEnabled(LogLevel.Debug))
                    _shared.Logger.LogDebug(
                        (int)ErrorCode.Catalog_BeforeCallingDeactivate,
                        "About to call {Activation} grain's OnDeactivateAsync(...) method {GrainInstanceType}",
                        this,
                        GrainInstance?.GetType().FullName);

                // Call OnDeactivateAsync inline, but within try-catch wrapper to safely capture any exceptions thrown from called function
                try
                {
                    // just check in case this activation data is already Invalid or not here at all.
                    if (State == ActivationState.Deactivating)
                    {
                        RequestContext.Clear(); // Clear any previous RC, so it does not leak into this call by mistake.
                        if (GrainInstance is IGrainBase grainBase)
                        {
                            await grainBase.OnDeactivateAsync(DeactivationReason, ct).WithCancellation($"Timed out waiting for {nameof(IGrainBase.OnDeactivateAsync)} to complete", ct);
                        }

                        if (_lifecycle is { } lifecycle)
                        {
                            await lifecycle.OnStop(ct).WithCancellation("Timed out waiting for grain lifecycle to complete deactivation", ct);
                        }
                    }

                    if (_shared.Logger.IsEnabled(LogLevel.Debug))
                        _shared.Logger.LogDebug(
                            (int)ErrorCode.Catalog_AfterCallingDeactivate,
                            "Returned from calling {Activation} grain's OnDeactivateAsync(...) method {GrainInstanceType}",
                            this,
                            GrainInstance?.GetType().FullName);
                }
                catch (Exception exc)
                {
                    _shared.Logger.LogError(
                        (int)ErrorCode.Catalog_ErrorCallingDeactivate,
                        exc,
                        "Error calling grain's OnDeactivateAsync(...) method - Grain type = {GrainType} Activation = {Activation}",
                        GrainInstance?.GetType().FullName,
                        this);
                }
            }
            catch (Exception exc)
            {
                _shared.Logger.LogError(
                    (int)ErrorCode.Catalog_FinishGrainDeactivateAndCleanupStreams_Exception,
                    exc,
                    "CallGrainDeactivateAndCleanupStreams Activation = {Activation} failed.",
                    this);
            }
        }
    }

    private TaskCompletionSource<bool> GetDeactivationCompletionSource()
    {
        lock (this)
        {
            _extras ??= new();
            return _extras.DeactivationTask ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    ValueTask IGrainManagementExtension.DeactivateOnIdle()
    {
        Deactivate(new(DeactivationReasonCode.ApplicationRequested, $"{nameof(IGrainManagementExtension.DeactivateOnIdle)} was called."), CancellationToken.None);
        return default;
    }

    ValueTask IGrainManagementExtension.MigrateOnIdle()
    {
        Migrate(RequestContext.CallContextData?.Value.Values, CancellationToken.None);
        return default;
    }

    private void UnregisterMessageTarget()
    {
        _shared.InternalRuntime.Catalog.UnregisterMessageTarget(this);
    }

    void ICallChainReentrantGrainContext.OnEnterReentrantSection(Guid reentrancyId)
    {
        var tracker = GetComponent<ReentrantRequestTracker>();
        if (tracker is null)
        {
            tracker = new ReentrantRequestTracker();
            SetComponent(tracker);
        }

        tracker.EnterReentrantSection(reentrancyId);
    }

    void ICallChainReentrantGrainContext.OnExitReentrantSection(Guid reentrancyId)
    {
        var tracker = GetComponent<ReentrantRequestTracker>();
        if (tracker is null)
        {
            throw new InvalidOperationException("Attempted to exit reentrant section without entering it.");
        }

        tracker.LeaveReentrantSection(reentrancyId);
    }

    private bool IsReentrantSection(Guid reentrancyId)
    {
        if (reentrancyId == Guid.Empty)
        {
            return false;
        }

        var tracker = GetComponent<ReentrantRequestTracker>();
        if (tracker is null)
        {
            return false;
        }

        return tracker.IsReentrantSectionActive(reentrancyId);
    }

    private void ScheduleCommand(Command operation)
    {
        lock (this)
        {
            _pendingOperations ??= [];
            _pendingOperations.Add(operation);
        }

        _workSignal.Signal();
    }

    public void Dispose() => DisposeAsync().AsTask().Wait();

    public async ValueTask DisposeAsync()
    {
        _extras ??= new();
        if (_extras.IsDisposing) return;
        _extras.IsDisposing = true;

        DisposeTimers();

        try
        {
            var activator = _shared.GetComponent<IGrainActivator>();
            if (activator != null)
            {
                await activator.DisposeInstance(this, GrainInstance);
            }
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            SetGrainInstance(null);
        }
        catch (ObjectDisposedException)
        {
        }

        switch (_serviceScope)
        {
            case IAsyncDisposable asyncDisposable:
                await asyncDisposable.DisposeAsync();
                break;
            case IDisposable disposable:
                disposable.Dispose();
                break;
        }
    }
}
