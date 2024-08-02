#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Core.Internal;
using Orleans.Runtime.Scheduler;
using Orleans.Serialization.Invocation;
using Orleans.Serialization.Session;
using Orleans.Serialization.TypeSystem;

namespace Orleans.Runtime;

/// <summary>
/// Maintains additional per-activation state that is required for Orleans internal operations.
/// MUST lock this object for any concurrent access
/// Consider: compartmentalize by usage, e.g., using separate interfaces for data for catalog, etc.
/// </summary>
internal sealed partial class ActivationData
    : IGrainContext,
    ICollectibleGrainContext,
    IGrainExtensionBinder,
    IActivationWorkingSetMember,
    IGrainTimerRegistry,
    IGrainManagementExtension,
    ICallChainReentrantGrainContext,
    IAsyncDisposable,
    IDisposable
{
    private const string GrainAddressMigrationContextKey = "sys.addr";
    private readonly GrainTypeSharedContext _shared;
    private readonly IServiceScope _serviceScope;
    private readonly WorkItemGroup _workItemGroup;
    private readonly List<(Message Message, CoarseStopwatch QueuedTime)> _waitingRequests = [];
    private readonly Dictionary<Message, CoarseStopwatch> _runningRequests = [];
    private readonly SingleWaiterAutoResetEvent _workSignal = new() { RunContinuationsAsynchronously = true };
    private GrainLifecycle? _lifecycle;
    private List<Command>? _pendingOperations;
    private Message? _blockingRequest;
    private bool _isInWorkingSet;
    private CoarseStopwatch _busyDuration;
    private CoarseStopwatch _idleDuration;
    private GrainReference? _selfReference;

    // Values which are needed less frequently and do not warrant living directly on activation for object size reasons.
    // The values in this field are typically used to represent termination state of an activation or features which are not
    // used by all grains, such as grain timers.
    private ActivationDataExtra? _extras;

    // The task representing this activation's message loop.
    // This field is assigned and never read and exists only for debugging purposes (eg, in memory dumps, to associate a loop task with an activation).
#pragma warning disable IDE0052 // Remove unread private members
    private readonly Task _messageLoopTask;
#pragma warning restore IDE0052 // Remove unread private members

    public ActivationData(
        GrainAddress grainAddress,
        Func<IGrainContext, WorkItemGroup> createWorkItemGroup,
        IServiceProvider applicationServices,
        GrainTypeSharedContext shared)
    {
        ArgumentNullException.ThrowIfNull(grainAddress);
        _shared = shared;
        Address = grainAddress;
        _serviceScope = applicationServices.CreateScope();
        _isInWorkingSet = true;
        _workItemGroup = createWorkItemGroup(this);
        _messageLoopTask = this.RunOrQueueTask(RunMessageLoop);
    }

    public IGrainRuntime GrainRuntime => _shared.Runtime;
    public object? GrainInstance { get; private set; }
    public GrainAddress Address { get; private set; }
    public GrainReference GrainReference => _selfReference ??= _shared.GrainReferenceActivator.CreateReference(Address.GrainId, default);
    public ActivationState State { get; private set; } = ActivationState.Create;
    public PlacementStrategy PlacementStrategy => _shared.PlacementStrategy;
    public DateTime CollectionTicket { get; set; }
    public IServiceProvider ActivationServices => _serviceScope.ServiceProvider;
    public ActivationId ActivationId => Address.ActivationId;
    public IGrainLifecycle ObservableLifecycle
    {
        get
        {
            if (_lifecycle is { } lifecycle) return lifecycle;
            lock (this) { return _lifecycle ??= new GrainLifecycle(_shared.Logger); }
        }
    }

    internal GrainTypeSharedContext Shared => _shared;

    public bool IsExemptFromCollection => _shared.CollectionAgeLimit == Timeout.InfiniteTimeSpan;
    public DateTime KeepAliveUntil { get; set; } = DateTime.MinValue;
    public bool IsValid => State is ActivationState.Valid;

    /// <summary>
    /// Returns a value indicating whether or not this placement strategy requires activations to be registered in
    /// the grain directory.
    /// </summary>
    internal bool IsUsingGrainDirectory => PlacementStrategy.IsUsingGrainDirectory;

    public int WaitingCount => _waitingRequests.Count;
    public bool IsInactive => !IsCurrentlyExecuting && _waitingRequests.Count == 0;
    private bool IsCurrentlyExecuting => _runningRequests.Count > 0;
    public IWorkItemScheduler Scheduler => _workItemGroup;
    public Task Deactivated => GetDeactivationCompletionSource().Task;

    public SiloAddress? ForwardingAddress
    {
        get => _extras?.ForwardingAddress;
        set
        {
            lock (this)
            {
                _extras ??= new();
                _extras.ForwardingAddress = value;
            }
        }
    }

    /// <summary>
    /// Gets the previous directory registration for this grain, if known.
    /// This is used to update the grain directory to point to the new registration during activation.
    /// </summary>
    public GrainAddress? PreviousRegistration
    {
        get => _extras?.PreviousRegistration;
        set
        {
            lock (this)
            {
                _extras ??= new();
                _extras.PreviousRegistration = value;
            }
        }
    }

    private Exception? DeactivationException => _extras?.DeactivationReason.Exception;

    private DeactivationReason DeactivationReason
    {
        get => _extras?.DeactivationReason ?? default;
        set
        {
            lock (this)
            {
                _extras ??= new();
                _extras.DeactivationReason = value;
            }
        }
    }

    private HashSet<IGrainTimer>? Timers
    {
        get => _extras?.Timers;
        set
        {
            lock (this)
            {
                _extras ??= new();
                _extras.Timers = value;
            }
        }
    }

    private DateTime? DeactivationStartTime
    {
        get => _extras?.DeactivationStartTime;
        set
        {
            lock (this)
            {
                _extras ??= new();
                _extras.DeactivationStartTime = value;
            }
        }
    }

    private bool IsStuckDeactivating
    {
        get => _extras?.IsStuckDeactivating ?? false;
        set
        {
            lock (this)
            {
                _extras ??= new();
                _extras.IsStuckDeactivating = value;
            }
        }
    }

    private bool IsStuckProcessingMessage
    {
        get => _extras?.IsStuckProcessingMessage ?? false;
        set
        {
            lock (this)
            {
                _extras ??= new();
                _extras.IsStuckProcessingMessage = value;
            }
        }
    }

    private DehydrationContextHolder? DehydrationContext
    {
        get => _extras?.DehydrationContext;
        set
        {
            lock (this)
            {
                _extras ??= new();
                _extras.DehydrationContext = value;
            }
        }
    }

    public TimeSpan CollectionAgeLimit => _shared.CollectionAgeLimit;

    public TTarget? GetTarget<TTarget>() where TTarget : class => (TTarget?)GrainInstance;

    TComponent? ITargetHolder.GetComponent<TComponent>() where TComponent : class
    {
        var result = GetComponent<TComponent>();
        if (result is null && typeof(IGrainExtension).IsAssignableFrom(typeof(TComponent)))
        {
            var implementation = ActivationServices.GetKeyedService<IGrainExtension>(typeof(TComponent));
            if (implementation is not TComponent typedResult)
            {
                throw new GrainExtensionNotInstalledException($"No extension of type {typeof(TComponent)} is installed on this instance and no implementations are registered for automated install");
            }

            SetComponent(typedResult);
            result = typedResult;
        }

        return result;
    }

    public TComponent? GetComponent<TComponent>() where TComponent : class
    {
        TComponent? result;
        if (GrainInstance is TComponent grainResult)
        {
            result = grainResult;
        }
        else if (this is TComponent contextResult)
        {
            result = contextResult;
        }
        else if (_extras is { } components && components.TryGetValue(typeof(TComponent), out var resultObj))
        {
            result = (TComponent)resultObj;
        }
        else if (ActivationServices.GetService<TComponent>() is { } component)
        {
            SetComponent(component);
            result = component;
        }
        else
        {
            result = _shared.GetComponent<TComponent>();
        }

        return result;
    }

    public void SetComponent<TComponent>(TComponent? instance) where TComponent : class
    {
        if (GrainInstance is TComponent)
        {
            throw new ArgumentException("Cannot override a component which is implemented by this grain");
        }

        if (this is TComponent)
        {
            throw new ArgumentException("Cannot override a component which is implemented by this grain context");
        }

        lock (this)
        {
            if (instance == null)
            {
                _extras?.Remove(typeof(TComponent));
                return;
            }

            _extras ??= new();
            _extras[typeof(TComponent)] = instance;
        }
    }

    internal void SetGrainInstance(object? grainInstance)
    {
        switch (GrainInstance, grainInstance)
        {
            case (null, not null):
                _shared.OnCreateActivation(this);
                GetComponent<IActivationLifecycleObserver>()?.OnCreateActivation(this);
                break;
            case (not null, null):
                _shared.OnDestroyActivation(this);
                GetComponent<IActivationLifecycleObserver>()?.OnDestroyActivation(this);
                break;
        }

        if (grainInstance is ILifecycleParticipant<IGrainLifecycle> participant)
        {
            participant.Participate(ObservableLifecycle);
        }

        GrainInstance = grainInstance;
    }

    public bool SetState(ActivationState currentState, ActivationState state)
    {
        lock (this)
        {
            if (State != currentState)
            {
                return false;
            }

            State = state;
            return true;
        }
    }

    internal List<Message> DequeueAllWaitingRequests()
    {
        lock (this)
        {
            var result = new List<Message>(_waitingRequests.Count);
            foreach (var (message, _) in _waitingRequests)
            {
                // Local-only messages are not allowed to escape the activation.
                if (message.IsLocalOnly)
                {
                    continue;
                }

                result.Add(message);
            }

            _waitingRequests.Clear();
            return result;
        }
    }

    /// <summary>
    /// Returns how long this activation has been idle.
    /// </summary>
    public TimeSpan GetIdleness() => _idleDuration.Elapsed;

    /// <summary>
    /// Returns whether this activation has been idle long enough to be collected.
    /// </summary>
    public bool IsStale() => GetIdleness() >= _shared.CollectionAgeLimit;

    bool IEquatable<IGrainContext>.Equals(IGrainContext? other) => ReferenceEquals(this, other);

    public (TExtension, TExtensionInterface) GetOrSetExtension<TExtension, TExtensionInterface>(Func<TExtension> newExtensionFunc)
        where TExtension : class, TExtensionInterface
        where TExtensionInterface : class, IGrainExtension
    {
        TExtension implementation;
        if (GetComponent<TExtensionInterface>() is object existing)
        {
            if (existing is TExtension typedResult)
            {
                implementation = typedResult;
            }
            else
            {
                throw new InvalidCastException($"Cannot cast existing extension of type {existing.GetType()} to target type {typeof(TExtension)}");
            }
        }
        else
        {
            implementation = newExtensionFunc();
            SetComponent<TExtensionInterface>(implementation);
        }

        var reference = GrainReference.Cast<TExtensionInterface>();
        return (implementation, reference);
    }

    public TExtensionInterface GetExtension<TExtensionInterface>()
        where TExtensionInterface : class, IGrainExtension
    {
        if (GetComponent<TExtensionInterface>() is TExtensionInterface result)
        {
            return result;
        }

        var implementation = ActivationServices.GetKeyedService<IGrainExtension>(typeof(TExtensionInterface));
        if (implementation is not TExtensionInterface typedResult)
        {
            throw new GrainExtensionNotInstalledException($"No extension of type {typeof(TExtensionInterface)} is installed on this instance and no implementations are registered for automated install");
        }

        SetComponent(typedResult);
        return typedResult;
    }

    bool IActivationWorkingSetMember.IsCandidateForRemoval(bool wouldRemove)
    {
        const int IdlenessLowerBound = 10_000;
        lock (this)
        {
            var inactive = IsInactive && _idleDuration.ElapsedMilliseconds > IdlenessLowerBound;

            // This instance will remain in the working set if it is either not pending removal or if it is currently active.
            _isInWorkingSet = !wouldRemove || !inactive;
            return inactive;
        }
    }

    private async Task RunMessageLoop()
    {
        // Note that this loop never terminates. That might look strange, but there is a reason for it:
        // a grain must always accept and process any incoming messages. How a grain processes
        // those messages is up to the grain's state to determine. If the grain has not yet
        // completed activating, it will let the messages continue to queue up until it completes activation.
        // If the grain failed to activate, messages will be responded to with a rejection.
        // If the grain has terminated, messages will be forwarded on to a new instance of this grain.
        // The loop will eventually be garbage collected when the grain gets deactivated and there are no
        // rooted references to it.
        while (true)
        {
            try
            {
                if (!IsCurrentlyExecuting)
                {
                    List<Command>? operations = null;
                    lock (this)
                    {
                        if (_pendingOperations is { Count: > 0 })
                        {
                            operations = _pendingOperations;
                            _pendingOperations = null;
                        }
                    }

                    if (operations is not null)
                    {
                        await ProcessOperationsAsync(operations);
                    }
                }

                ProcessPendingRequests();

                await _workSignal.WaitAsync();
            }
            catch (Exception exception)
            {
                _shared.InternalRuntime.MessagingTrace.LogError(exception, "Error in grain message loop");
            }
        }

        void ProcessPendingRequests()
        {
            var i = 0;

            do
            {
                Message? message = null;
                lock (this)
                {
                    if (_waitingRequests.Count <= i)
                    {
                        break;
                    }

                    message = _waitingRequests[i].Message;

                    // If the activation is not valid, reject all pending messages except for local-only messages.
                    // Local-only messages are used for internal system operations and should not be rejected while the grain is valid or deactivating.
                    if (State != ActivationState.Valid && !(message.IsLocalOnly && State is ActivationState.Deactivating))
                    {
                        ProcessRequestsToInvalidActivation();
                        break;
                    }

                    try
                    {
                        if (!MayInvokeRequest(message))
                        {
                            // The activation is not able to process this message right now, so try the next message.
                            ++i;

                            if (_blockingRequest != null)
                            {
                                var currentRequestActiveTime = _busyDuration.Elapsed;
                                if (currentRequestActiveTime > _shared.MaxRequestProcessingTime && !IsStuckProcessingMessage)
                                {
                                    DeactivateStuckActivation();
                                }
                                else if (currentRequestActiveTime > _shared.MaxWarningRequestProcessingTime)
                                {
                                    // Consider: Handle long request detection for reentrant activations -- this logic only works for non-reentrant activations
                                    _shared.Logger.LogWarning(
                                        (int)ErrorCode.Dispatcher_ExtendedMessageProcessing,
                                        "Current request has been active for {CurrentRequestActiveTime} for grain {Grain}. Currently executing {BlockingRequest}. Trying to enqueue {Message}.",
                                        currentRequestActiveTime,
                                        ToDetailedString(),
                                        _blockingRequest,
                                        message);
                                }
                            }

                            continue;
                        }

                        // If the current message is incompatible, deactivate this activation and eventually forward the message to a new incarnation.
                        if (message.InterfaceVersion > 0)
                        {
                            var compatibilityDirector = _shared.InternalRuntime.CompatibilityDirectorManager.GetDirector(message.InterfaceType);
                            var currentVersion = _shared.InternalRuntime.GrainVersionManifest.GetLocalVersion(message.InterfaceType);
                            if (!compatibilityDirector.IsCompatible(message.InterfaceVersion, currentVersion))
                            {
                                // Add this activation to cache invalidation headers.
                                message.CacheInvalidationHeader ??= [];
                                message.CacheInvalidationHeader.Add(new GrainAddressCacheUpdate(Address, validAddress: null));

                                var reason = new DeactivationReason(
                                    DeactivationReasonCode.IncompatibleRequest,
                                    $"Received incompatible request for interface {message.InterfaceType} version {message.InterfaceVersion}. This activation supports interface version {currentVersion}.");

                                Deactivate(reason, cancellationToken: default);
                                return;
                            }
                        }
                    }
                    catch (Exception exception)
                    {
                        if (!message.IsLocalOnly)
                        {
                            _shared.InternalRuntime.MessageCenter.RejectMessage(message, Message.RejectionTypes.Transient, exception);
                        }

                        _waitingRequests.RemoveAt(i);
                        continue;
                    }

                    // Process this message, removing it from the queue.
                    _waitingRequests.RemoveAt(i);

                    Debug.Assert(State == ActivationState.Valid || message.IsLocalOnly);
                    RecordRunning(message, message.IsAlwaysInterleave);
                }

                // Start invoking the message outside of the lock
                InvokeIncomingRequest(message);
            }
            while (true);
        }

        void RecordRunning(Message message, bool isInterleavable)
        {
            var stopwatch = CoarseStopwatch.StartNew();
            _runningRequests.Add(message, stopwatch);

            if (_blockingRequest != null || isInterleavable) return;

            // This logic only works for non-reentrant activations
            // Consider: Handle long request detection for reentrant activations.
            _blockingRequest = message;
            _busyDuration = stopwatch;
        }

        void ProcessRequestsToInvalidActivation()
        {
            if (State is ActivationState.Create or ActivationState.Activating)
            {
                // Do nothing until the activation becomes either valid or invalid
                return;
            }

            if (State is ActivationState.Deactivating)
            {
                // Determine whether to declare this activation as stuck
                var deactivatingTime = GrainRuntime.TimeProvider.GetUtcNow().UtcDateTime - DeactivationStartTime!.Value;
                if (deactivatingTime > _shared.MaxRequestProcessingTime && !IsStuckDeactivating)
                {
                    IsStuckDeactivating = true;
                    if (DeactivationReason.Description is { Length: > 0 } && DeactivationReason.ReasonCode != DeactivationReasonCode.ActivationUnresponsive)
                    {
                        DeactivationReason = new(DeactivationReasonCode.ActivationUnresponsive,
                            $"{DeactivationReason.Description}. Activation {this} has been deactivating since {DeactivationStartTime.Value} and is likely stuck");
                    }
                }

                if (!IsStuckDeactivating && !IsStuckProcessingMessage)
                {
                    // Do not forward messages while the grain is still deactivating and has not been declared stuck, since they
                    // will be forwarded to the same grain instance.
                    return;
                }
            }

            if (DeactivationException is null || ForwardingAddress is { })
            {
                // Either this was a duplicate activation or it was at some point successfully activated
                // Forward all pending messages
                RerouteAllQueuedMessages();
            }
            else
            {
                // Reject all pending messages
                RejectAllQueuedMessages();
            }
        }

        bool MayInvokeRequest(Message incoming)
        {
            if (!IsCurrentlyExecuting)
            {
                return true;
            }

            // Otherwise, allow request invocation if the grain is reentrant or the message can be interleaved
            if (incoming.IsAlwaysInterleave)
            {
                return true;
            }

            if (_blockingRequest is null)
            {
                return true;
            }

            if (_blockingRequest.IsReadOnly && incoming.IsReadOnly)
            {
                return true;
            }

            // Handle call-chain reentrancy
            if (incoming.GetReentrancyId() is Guid id
                && IsReentrantSection(id))
            {
                return true;
            }

            if (GetComponent<GrainCanInterleave>() is GrainCanInterleave canInterleave)
            {
                try
                {
                    return canInterleave.MayInterleave(GrainInstance, incoming);
                }
                catch (Exception exception)
                {
                    _shared.Logger?.LogError(exception, "Error invoking MayInterleave predicate on grain {Grain} for message {Message}", this, incoming);
                    throw;
                }
            }

            return false;
        }

        async Task ProcessOperationsAsync(List<Command> operations)
        {
            foreach (var op in operations)
            {
                try
                {
                    switch (op)
                    {
                        case Command.Rehydrate command:
                            RehydrateInternal(command.Context);
                            break;
                        case Command.Activate command:
                            try
                            {
                                await ActivateAsync(command.RequestContext, command.Cts.Token);
                            }
                            finally
                            {
                                command.Cts.Dispose();
                            }
                            break;
                        case Command.Deactivate command:
                            try
                            {
                                await FinishDeactivating(command.Cts.Token);
                            }
                            finally
                            {
                                command.Cts.Dispose();
                            }

                            break;
                        case Command.Delay command:
                            await Task.Delay(command.Duration, GrainRuntime.TimeProvider);
                            break;
                        case Command.UnregisterFromCatalog:
                            UnregisterMessageTarget();
                            break;
                        default:
                            throw new NotSupportedException($"Encountered unknown operation of type {op?.GetType().ToString() ?? "null"} {op}");
                    }
                }
                catch (Exception exception)
                {
                    _shared.Logger.LogError(exception, "Error in RunOnInactive for grain activation {Activation}", this);
                }
            }
        }
    }

    /// <summary>
    /// Handle an incoming message and queue/invoke appropriate handler
    /// </summary>
    /// <param name="message"></param>
    private void InvokeIncomingRequest(Message message)
    {
        MessagingProcessingInstruments.OnDispatcherMessageProcessedOk(message);
        _shared.InternalRuntime.MessagingTrace.OnScheduleMessage(message);

        try
        {
            var task = _shared.InternalRuntime.RuntimeClient.Invoke(this, message);

            // Note: This runs for all outcomes - both Success or Fault
            if (task.IsCompleted)
            {
                OnCompletedRequest(this, message);
            }
            else
            {
                _ = OnCompleteAsync(this, message, task);
            }
        }
        catch
        {
            OnCompletedRequest(this, message);
        }

        static async ValueTask OnCompleteAsync(ActivationData activation, Message message, Task task)
        {
            await task.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
            OnCompletedRequest(activation, message);
        }

        static void OnCompletedRequest(ActivationData activation, Message message)
        {
            lock (activation)
            {
                activation._runningRequests.Remove(message);

                // If the message is meant to keep the activation active, reset the idle timer and ensure the activation
                // is in the activation working set.
                if (message.IsKeepAlive)
                {
                    activation._idleDuration = CoarseStopwatch.StartNew();

                    if (!activation._isInWorkingSet)
                    {
                        activation._isInWorkingSet = true;
                        activation._shared.InternalRuntime.ActivationWorkingSet.OnActive(activation);
                    }
                }

                // The below logic only works for non-reentrant activations
                if (activation._blockingRequest is null || message.Equals(activation._blockingRequest))
                {
                    activation._blockingRequest = null;
                    activation._busyDuration = default;
                }
            }

            // Signal the message pump to see if there is another request which can be processed now that this one has completed
            activation._workSignal.Signal();
        }
    }

    public void ReceiveMessage(object message) => ReceiveMessage((Message)message);
    public void ReceiveMessage(Message message)
    {
        _shared.InternalRuntime.MessagingTrace.OnDispatcherReceiveMessage(message);

        // Don't process messages that have already timed out
        if (message.IsExpired)
        {
            MessagingProcessingInstruments.OnDispatcherMessageProcessedError(message);
            _shared.InternalRuntime.MessagingTrace.OnDropExpiredMessage(message, MessagingInstruments.Phase.Dispatch);
            return;
        }

        if (message.Direction == Message.Directions.Response)
        {
            ReceiveResponse(message);
        }
        else // Request or OneWay
        {
            ReceiveRequest(message);
        }
    }

    private void ReceiveResponse(Message message)
    {
        lock (this)
        {
            if (State is ActivationState.Invalid or ActivationState.FailedToActivate)
            {
                _shared.InternalRuntime.MessagingTrace.OnDispatcherReceiveInvalidActivation(message, State);

                // Always process responses
                _shared.InternalRuntime.RuntimeClient.ReceiveResponse(message);
                return;
            }

            MessagingProcessingInstruments.OnDispatcherMessageProcessedOk(message);
            _shared.InternalRuntime.RuntimeClient.ReceiveResponse(message);
        }
    }

    private void ReceiveRequest(Message message)
    {
        var overloadException = CheckOverloaded();
        if (overloadException != null && !message.IsLocalOnly)
        {
            MessagingProcessingInstruments.OnDispatcherMessageProcessedError(message);
            _shared.InternalRuntime.MessageCenter.RejectMessage(message, Message.RejectionTypes.Overloaded, overloadException, "Target activation is overloaded " + this);
            return;
        }

        lock (this)
        {
            _waitingRequests.Add((message, CoarseStopwatch.StartNew()));
        }

        _workSignal.Signal();
    }

    /// <summary>
    /// Rejects all messages enqueued for the provided activation.
    /// </summary>
    private void RejectAllQueuedMessages()
    {
        lock (this)
        {
            List<Message> msgs = DequeueAllWaitingRequests();
            if (msgs == null || msgs.Count <= 0) return;

            if (_shared.Logger.IsEnabled(LogLevel.Debug))
                _shared.Logger.LogDebug(
                    (int)ErrorCode.Catalog_RerouteAllQueuedMessages,
                    "RejectAllQueuedMessages: {Count} messages from invalid activation {Activation}.",
                    msgs.Count,
                    this);
            _shared.InternalRuntime.GrainLocator.InvalidateCache(Address);
            _shared.InternalRuntime.MessageCenter.ProcessRequestsToInvalidActivation(
                msgs,
                Address,
                forwardingAddress: ForwardingAddress,
                failedOperation: DeactivationReason.Description,
                exc: DeactivationException,
                rejectMessages: true);
        }
    }

    private void RerouteAllQueuedMessages()
    {
        lock (this)
        {
            List<Message> msgs = DequeueAllWaitingRequests();
            if (msgs is not { Count: > 0 })
            {
                return;
            }

            if (_shared.Logger.IsEnabled(LogLevel.Debug))
            {
                if (ForwardingAddress is { } address)
                {
                    _shared.Logger.LogDebug((int)ErrorCode.Catalog_RerouteAllQueuedMessages, "Rerouting {NumMessages} messages from invalid grain activation {Grain} to {ForwardingAddress}.", msgs.Count, this, address);
                }
                else
                {
                    _shared.Logger.LogDebug((int)ErrorCode.Catalog_RerouteAllQueuedMessages, "Rerouting {NumMessages} messages from invalid grain activation {Grain}.", msgs.Count, this);
                }
            }

            _shared.InternalRuntime.GrainLocator.InvalidateCache(Address);
            _shared.InternalRuntime.MessageCenter.ProcessRequestsToInvalidActivation(msgs, Address, ForwardingAddress, DeactivationReason.Description, DeactivationException);
        }
    }

    /// <summary>
    /// Additional properties which are not needed for the majority of an activation's lifecycle.
    /// </summary>
    private sealed class ActivationDataExtra : Dictionary<object, object>
    {
        private const int IsStuckProcessingMessageFlag = 1 << 0;
        private const int IsStuckDeactivatingFlag = 1 << 1;
        private const int IsDisposingFlag = 1 << 2;
        private byte _flags;

        public HashSet<IGrainTimer>? Timers { get => GetValueOrDefault<HashSet<IGrainTimer>>(nameof(Timers)); set => SetOrRemoveValue(nameof(Timers), value); }

        /// <summary>
        /// During rehydration, this may contain the address for the previous (recently dehydrated) activation of this grain.
        /// </summary>
        public GrainAddress? PreviousRegistration { get => GetValueOrDefault<GrainAddress>(nameof(PreviousRegistration)); set => SetOrRemoveValue(nameof(PreviousRegistration), value); }

        /// <summary>
        /// If State == Invalid, this may contain a forwarding address for incoming messages
        /// </summary>
        public SiloAddress? ForwardingAddress { get => GetValueOrDefault<SiloAddress>(nameof(ForwardingAddress)); set => SetOrRemoveValue(nameof(ForwardingAddress), value); }

        /// <summary>
        /// A <see cref="TaskCompletionSource{TResult}"/> which completes when a grain has deactivated.
        /// </summary>
        public TaskCompletionSource<bool>? DeactivationTask { get => GetDeactivationInfoOrDefault()?.DeactivationTask; set => EnsureDeactivationInfo().DeactivationTask = value; }

        public DateTime? DeactivationStartTime { get => GetDeactivationInfoOrDefault()?.DeactivationStartTime; set => EnsureDeactivationInfo().DeactivationStartTime = value; }

        public DeactivationReason DeactivationReason { get => GetDeactivationInfoOrDefault()?.DeactivationReason ?? default; set => EnsureDeactivationInfo().DeactivationReason = value; }

        /// <summary>
        /// When migrating to another location, this contains the information to preserve across activations.
        /// </summary>
        public DehydrationContextHolder? DehydrationContext { get => GetValueOrDefault<DehydrationContextHolder>(nameof(DehydrationContext)); set => SetOrRemoveValue(nameof(DehydrationContext), value); }

        private DeactivationInfo? GetDeactivationInfoOrDefault() => GetValueOrDefault<DeactivationInfo>(nameof(DeactivationInfo));
        private DeactivationInfo EnsureDeactivationInfo()
        {
            ref var info = ref CollectionsMarshal.GetValueRefOrAddDefault(this, nameof(DeactivationInfo), out _);
            info ??= new DeactivationInfo();
            return (DeactivationInfo)info;
        }

        public bool IsStuckProcessingMessage { get => GetFlag(IsStuckProcessingMessageFlag); set => SetFlag(IsStuckProcessingMessageFlag, value); }
        public bool IsStuckDeactivating { get => GetFlag(IsStuckDeactivatingFlag); set => SetFlag(IsStuckDeactivatingFlag, value); }
        public bool IsDisposing { get => GetFlag(IsDisposingFlag); set => SetFlag(IsDisposingFlag, value); }

        private void SetFlag(int flag, bool value)
        {
            if (value)
            {
                _flags |= (byte)flag;
            }
            else
            {
                _flags &= (byte)~flag;
            }
        }

        private bool GetFlag(int flag) => (_flags & flag) != 0;
        private T? GetValueOrDefault<T>(object key)
        {
            TryGetValue(key, out var result);
            return (T?)result;
        }

        private void SetOrRemoveValue(object key, object? value)
        {
            if (value is null)
            {
                Remove(key);
            }
            else
            {
                base[key] = value;
            }
        }

        private sealed class DeactivationInfo
        {
            public DateTime? DeactivationStartTime;
            public DeactivationReason DeactivationReason;
            public TaskCompletionSource<bool>? DeactivationTask;
        }
    }

    private abstract class Command
    {
        protected Command() { }

        public sealed class Deactivate(CancellationTokenSource cts) : Command
        {
            public CancellationTokenSource Cts { get; } = cts;
        }

        public sealed class Activate(Dictionary<string, object>? requestContext, CancellationTokenSource cts) : Command
        {
            public Dictionary<string, object>? RequestContext { get; } = requestContext;
            public CancellationTokenSource Cts { get; } = cts;
        }

        public sealed class Rehydrate(IRehydrationContext context) : Command
        {
            public readonly IRehydrationContext Context = context;
        }

        public sealed class Delay(TimeSpan duration) : Command
        {
            public TimeSpan Duration { get; } = duration;
        }

        public sealed class UnregisterFromCatalog : Command
        {
            public static readonly UnregisterFromCatalog Instance = new();
        }
    }

    private sealed class ReentrantRequestTracker : Dictionary<Guid, int>
    {
        public void EnterReentrantSection(Guid reentrancyId)
        {
            Debug.Assert(reentrancyId != Guid.Empty);
            ref var count = ref CollectionsMarshal.GetValueRefOrAddDefault(this, reentrancyId, out _);
            ++count;
        }

        public void LeaveReentrantSection(Guid reentrancyId)
        {
            Debug.Assert(reentrancyId != Guid.Empty);
            ref var count = ref CollectionsMarshal.GetValueRefOrNullRef(this, reentrancyId);
            if (Unsafe.IsNullRef(ref count))
            {
                return;
            }

            if (--count <= 0)
            {
                Remove(reentrancyId);
            }
        }

        public bool IsReentrantSectionActive(Guid reentrancyId)
        {
            Debug.Assert(reentrancyId != Guid.Empty);
            return TryGetValue(reentrancyId, out var count) && count > 0;
        }
    }

    private sealed class DehydrationContextHolder(SerializerSessionPool sessionPool, Dictionary<string, object>? requestContext)
    {
        public readonly MigrationContext MigrationContext = new(sessionPool);
        public readonly Dictionary<string, object>? RequestContext = requestContext;
    }

    private sealed class MigrateWorkItem(ActivationData activation, Dictionary<string, object>? requestContext, CancellationTokenSource cts) : WorkItemBase
    {
        public override string Name => "Migrate";

        public override IGrainContext GrainContext => activation;

        public override void Execute() => activation.StartMigratingAsync(requestContext, cts).Ignore();
    }
}
