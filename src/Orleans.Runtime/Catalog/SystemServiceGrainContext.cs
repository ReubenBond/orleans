using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.GrainDirectory;
using Orleans.Internal;
using Orleans.Serialization.Invocation;
using Orleans.Serialization.TypeSystem;

namespace Orleans.Runtime
{
    internal class SystemServiceGrainContext : IGrainContext, IGrainExtensionBinder, IAsyncDisposable, IGrainManagementExtension, IWorkItemScheduler, IThreadPoolWorkItem
    {
        private readonly GrainTypeSharedContext _shared;
        private readonly InsideRuntimeClient _runtimeClient;
        private readonly IServiceScope _serviceScope;
        private readonly GrainLifecycle _lifecycle;
        private readonly SingleWaiterAutoResetEvent _idleSignal = new() { RunContinuationsAsynchronously = true };
        private readonly ConcurrentQueue<Message> _pendingRequests = new();
        private readonly ConcurrentQueue<object> _pendingOperations = new();
        private readonly Action _onCompletedRequest;

        // The number of requests which are currently executing
        private const long PendingOperationsIncrement = 0x00000001_00000000;
        private const long PendingOperationsMask = 0x7FFFFFFF_00000000;
        private const long ConcurrentRequestsMask = 0x00000000_FFFFFFFF;
        private const long ActivationValidMask = PendingOperationsMask | ConcurrentRequestsMask;
        private const long CanInvokeRequestMask = unchecked((long)0xFFFFFFFF_00000000);
        private long _stateBits = ~ActivationValidMask;
        private ActivationState _state;

        private Dictionary<Type, object> _components = new();
        private GrainReference _selfReference;

        // Values which are needed less frequently and do not warrant living directly on activation for object size reasons.
        // The values in this field are typically used to represent termination state of an activation or features which are not
        // used by all services.
        private ServiceContextExtra _extras;

        // The task representing this activation's message loop.
        // This field is assigned and never read and exists only for debugging purposes (eg, in memory dumps, to associate a loop task with an activation).
#pragma warning disable IDE0052 // Remove unread private members
        private readonly Task _messageLoopTask;
#pragma warning restore IDE0052 // Remove unread private members

        public SystemServiceGrainContext(
            GrainAddress addr,
            IServiceProvider applicationServices,
            GrainTypeSharedContext shared)
        {
            _runtimeClient = shared.InternalRuntime.RuntimeClient;
            _onCompletedRequest = RecordRequestCompleted;
            _shared = shared;
            Address = addr ?? throw new ArgumentNullException(nameof(addr));
            _lifecycle = new(_shared.Logger);
            State = ActivationState.Create;
            _serviceScope = applicationServices.CreateScope();
            _messageLoopTask = Task.Factory.StartNew(obj => ((SystemServiceGrainContext)obj).RunMessageLoop(), this, TaskCreationOptions.DenyChildAttach);
        }

        private int ConcurrentRequestCount => (int)(_stateBits & ConcurrentRequestsMask);

        private bool TryRecordRunningRequest()
        {
            var status = Interlocked.Increment(ref _stateBits) & CanInvokeRequestMask;
            if (status == 0)
            {
                return true;
            }

            // Activation is waiting to process non-request operations
            RecordRequestCompleted();
            return false;
        }

        private void RecordRequestCompleted()
        {
            var count = Interlocked.Decrement(ref _stateBits) & ConcurrentRequestsMask;
            if (count == 0)
            {
                _idleSignal.Signal();
            }
        }

        private void RecordPendingOperation()
        {
            Interlocked.Add(ref _stateBits, PendingOperationsIncrement);

            // Operations are not added frequently, so we signal the processing loop even if there are requests running.
            // The loop is responsible for checking that it's safe to process operations.
            _idleSignal.Signal();
        }

        private void RecordOperationsCompleted(int count) => Interlocked.Add(ref _stateBits, -(count * PendingOperationsIncrement));

        public object GrainInstance { get; private set; }

        public GrainAddress Address { get; }

        public GrainReference GrainReference => _selfReference ??= _shared.GrainReferenceActivator.CreateReference(GrainId, default);

        private ActivationState State
        {
            get => _state;
            set
            {

// TODO
// TODO
// TODO
// TODO: DO NOT MERGE UNTIL THIS IS FIXED
// TODO
// TODO
// TODO

// TODO
// TODO
// TODO
// TODO: DO NOT MERGE UNTIL THIS IS FIXED
// TODO
// TODO
// TODO

// TODO
// TODO
// TODO
// TODO: DO NOT MERGE UNTIL THIS IS FIXED
// TODO
// TODO
// TODO

// TODO
// TODO
// TODO
// TODO: DO NOT MERGE UNTIL THIS IS FIXED
// TODO
// TODO
// TODO

// TODO
// TODO
// TODO
// TODO: DO NOT MERGE UNTIL THIS IS FIXED
// TODO
// TODO
// TODO
#if NET5_0_OR_GREATER
                if (value == ActivationState.Valid)
                {
                    Interlocked.And(ref _stateBits, ActivationValidMask);
                }
                else
                {
                    Interlocked.Or(ref _stateBits, ~ActivationValidMask);
                }
#endif

                _state = value;
            }
        }

        public IServiceProvider ActivationServices => _serviceScope.ServiceProvider;

        public ActivationId ActivationId => Address.ActivationId;

        public IGrainLifecycle ObservableLifecycle => _lifecycle;

        private ILifecycleObserver Lifecycle => _lifecycle;

        public GrainId GrainId => Address.GrainId;

        public Task Deactivated => GetDeactivationCompletionSource().Task;

        private Exception DeactivationException => _extras?.DeactivationReason.Exception;

        private DeactivationReason DeactivationReason
        {
            get => _extras?.DeactivationReason ?? default;
            set => GetOrCreateExtras().DeactivationReason = value;
        }

        private DateTime? DeactivationStartTime
        {
            get => _extras?.DeactivationStartTime;
            set => GetOrCreateExtras().DeactivationStartTime = value;
        }

        private bool IsStuckDeactivating
        {
            get => _extras?.IsStuckDeactivating ?? false;
            set => GetOrCreateExtras().IsStuckDeactivating = value;
        }

        private ServiceContextExtra GetOrCreateExtras()
        {
            if (_extras is {} extras) return extras;
            lock (this)
            {
                return _extras ??= new();
            }
        }

        public TTarget GetTarget<TTarget>() => (TTarget)GrainInstance;

        TComponent ITargetHolder.GetComponent<TComponent>()
        {
            var result = GetComponent<TComponent>();
            if (result is null && typeof(IGrainExtension).IsAssignableFrom(typeof(TComponent)))
            {
                var implementation = ActivationServices.GetServiceByKey<Type, IGrainExtension>(typeof(TComponent));
                if (implementation is not TComponent typedResult)
                {
                    throw new GrainExtensionNotInstalledException($"No extension of type {typeof(TComponent)} is installed on this instance and no implementations are registered for automated install");
                }

                SetComponent(typedResult);
                result = typedResult;
            }

            return result;
        }

        private TComponent GetComponent<TComponent>()
        {
            TComponent result;
            if (GrainInstance is TComponent serviceResult)
            {
                result = serviceResult;
            }
            else if (this is TComponent contextResult)
            {
                result = contextResult;
            }
            else
            {
                if (_components is not null)
                {
                    lock (this)
                    {
                        if (_components.TryGetValue(typeof(TComponent), out var resultObj))
                        {
                            result = (TComponent)resultObj;
                            return result;
                        }
                    }
                }

                result = _shared.GetComponent<TComponent>();
            }

            return result;
        }

        public void SetComponent<TComponent>(TComponent instance)
        {
            if (GrainInstance is TComponent)
            {
                throw new ArgumentException("Cannot override a component which is implemented by this service");
            }

            if (this is TComponent)
            {
                throw new ArgumentException("Cannot override a component which is implemented by this service context");
            }

            if (_components is null) _components = new();
            if (instance == null)
            {
                _components?.Remove(typeof(TComponent));
                return;
            }

            _components[typeof(TComponent)] = instance;
        }

        internal void SetServiceInstance(object grainInstance)
        {
            switch (ServiceInstance: GrainInstance, grainInstance)
            {
                case (null, not null):
                    _shared.OnCreateActivation(this);
                    break;
                case (not null, null):
                    _shared.OnDestroyActivation(this);
                    break;
            }

            if (grainInstance is ILifecycleParticipant<IGrainLifecycle> participant)
            {
                participant.Participate(ObservableLifecycle);
            }

            GrainInstance = grainInstance;
        }

        private List<Message> DequeueAllWaitingRequests()
        {
            var result = new List<Message>(_pendingRequests.Count);
            while (_pendingRequests.TryDequeue(out var message))
            {
                result.Add(message);
            }

            return result;
        }

        private void ScheduleOperation(object operation)
        {
            _pendingOperations.Enqueue(operation);
            RecordPendingOperation();
            _idleSignal.Signal();
        }

        public void Deactivate(DeactivationReason deactivationReason, CancellationToken? token = default)
        {
            if (!token.HasValue)
            {
                token = new CancellationTokenSource(_shared.InternalRuntime.CollectionOptions.Value.DeactivationTimeout).Token;
            }

            StartDeactivating(deactivationReason);
            ScheduleOperation(new Command.Deactivate(token.Value));
        }

        public async Task DeactivateAsync(DeactivationReason deactivationReason, CancellationToken? token)
        {
            Deactivate(deactivationReason, token);
            await GetDeactivationCompletionSource().Task;
        }

        public override string ToString() => $"[Activation: {Address.SiloAddress}/{GrainId.ToString()}{ActivationId}{GetActivationInfoString()} State={State}]";

        private string ToDetailedString(bool includeExtraDetails = false) =>
            $"[Activation: {Address.SiloAddress.ToLongString()}/{GrainId.ToString()}{ActivationId} {GetActivationInfoString()} "
            + $"State={State} NumRunning={_stateBits}";

        private string GetActivationInfoString()
        {
            var placementStrategy = _shared.PlacementStrategy;
            var placement = placementStrategy != null ? placementStrategy.GetType().Name : "";
            return GrainInstance is null ? $"#Placement={placement}" : $"#Type={RuntimeTypeNameFormatter.Format(GrainInstance?.GetType())} Placement={placement}";
        }

        public async ValueTask DisposeAsync()
        {
            var activator = GetComponent<IGrainActivator>();
            if (activator != null)
            {
                await activator.DisposeInstance(this, GrainInstance);
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

        bool IEquatable<IGrainContext>.Equals(IGrainContext other) => ReferenceEquals(this, other);

        public (TExtension, TExtensionInterface) GetOrSetExtension<TExtension, TExtensionInterface>(Func<TExtension> newExtensionFunc)
            where TExtension : TExtensionInterface
            where TExtensionInterface : IGrainExtension
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

        public TExtensionInterface GetExtension<TExtensionInterface>() where TExtensionInterface : IGrainExtension
        {
            if (GetComponent<TExtensionInterface>() is { } result)
            {
                return result;
            }

            var implementation = ActivationServices.GetServiceByKey<Type, IGrainExtension>(typeof(TExtensionInterface));
            if (implementation is not TExtensionInterface typedResult)
            {
                throw new GrainExtensionNotInstalledException($"No extension of type {typeof(TExtensionInterface)} is installed on this instance and no implementations are registered for automated install");
            }

            SetComponent(typedResult);
            return typedResult;
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
                    var isIdle = ConcurrentRequestCount == 0;
                    if (isIdle)
                    {
                        var dequeuedCount = 0;
                        try
                        {
                            while (_pendingOperations.TryDequeue(out var op))
                            {
                                ++dequeuedCount;
                                await ProcessOperation(op);
                            }
                        }
                        finally
                        {
                            RecordOperationsCompleted(dequeuedCount);
                        }
                    }

                    if (State != ActivationState.Valid)
                    {
                        ProcessRequestsToInvalidActivation();
                    }

                    while (_pendingRequests.TryDequeue(out var message) && HandleRequest(message))
                    {
                    }

                    await _idleSignal.WaitAsync();
                }
                catch (Exception exception)
                {
                    _shared.InternalRuntime.MessagingTrace.LogError(exception, "Error in grain message loop");
                }
            }

            async Task ProcessOperation(object op)
            {
                try
                {
                    switch (op)
                    {
                        case Command.Activate activation:
                            await ActivateAsync(activation.RequestContext, activation.CancellationToken);
                            break;
                        case Command.Deactivate deactivation:
                            await FinishDeactivating(deactivation.CancellationToken);
                            break;
                        case Command.Delay delay:
                            await Task.Delay(delay.Duration);
                            break;
                        case Command.UnregisterFromCatalog:
                            UnregisterMessageTarget();
                            break;
                        default:
                            throw new NotSupportedException(
                                $"Encountered unknown operation of type {op?.GetType().ToString() ?? "null"} {op}");
                    }
                }
                catch (Exception exception)
                {
                    _shared.Logger.LogError(exception, "Error in RunOnInactive for grain activation {Activation}", this);
                }
            }
        }

        /// <summary>
        /// Handle an incoming message and queue/invoke appropriate handler
        /// </summary>
        /// <returns><see langword="true"/> if the request has been invoked, and <see langword="false"/> otherwise.</returns>
        private bool HandleRequest(Message message)
        {
            _pendingRequests.Enqueue(message);
            var shouldScheduleInvocation = TryRecordRunningRequest();
            if (!shouldScheduleInvocation)
            {
                // The activation is not able to process a request right now.
                _idleSignal.Signal();
                return false;
            }

            // TODO: Run inline and push thread pool scheduling up the stack (eg, to MessageCenter),
            // possibly implementing IThreadPoolWorkItem on Message to reduce allocations/pooling needs
            ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
            return true;
        }

        void IThreadPoolWorkItem.Execute()
        {
            while (_pendingRequests.TryDequeue(out var message))
            {
                try
                {
                    var task = _runtimeClient.Invoke(this, message);

                    // When the request completes (which may have happened synchronously), signal that completion.
                    task.GetAwaiter().UnsafeOnCompleted(_onCompletedRequest);
                }
                catch
                {
                    RecordRequestCompleted();
                }
            }
        }

        public void ReceiveMessage(object message) => ReceiveMessage((Message)message);

        public IWorkItemScheduler Scheduler => this;

        public void ReceiveMessage(Message message)
        {
            _shared.InternalRuntime.MessagingTrace.OnDispatcherReceiveMessage(message);

            // Don't process messages that have already timed out
            if (message.IsExpired)
            {
                MessagingProcessingStatisticsGroup.OnDispatcherMessageProcessedError(message);
                _shared.InternalRuntime.MessagingTrace.OnDropExpiredMessage(message, MessagingStatisticsGroup.Phase.Dispatch);
                return;
            }

            if (message.Direction == Message.Directions.Response)
            {
                _shared.InternalRuntime.RuntimeClient.ReceiveResponse(message);
            }
            else // Request or OneWay
            {
                _ = HandleRequest(message);
            }
        }

        private void ProcessRequestsToInvalidActivation()
        {
            if (State is ActivationState.Create or ActivationState.Activating)
            {
                // Do nothing until the activation becomes either valid or invalid
                return;
            }

            if (State is ActivationState.Deactivating)
            {
                var deactivatingTime = DateTime.UtcNow - DeactivationStartTime.Value;
                if (deactivatingTime > _shared.MaxRequestProcessingTime && !IsStuckDeactivating)
                {
                    IsStuckDeactivating = true;
                    if (DeactivationReason.Description is { Length: > 0 })
                    {
                        var msg = $"Activation {this} has been deactivating since {DeactivationStartTime.Value} and is likely stuck";
                        DeactivationReason = new(DeactivationReason.ReasonCode, DeactivationReason.Description + ". " + msg);
                    }
                }
            }

            if (DeactivationException is null)
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
                    _shared.Logger.Debug(
                        ErrorCode.Catalog_RerouteAllQueuedMessages,
                        string.Format("RejectAllQueuedMessages: {0} msgs from Invalid activation {1}.", msgs.Count, this));
                _shared.InternalRuntime.LocalGrainDirectory.InvalidateCacheEntry(Address);
                _shared.InternalRuntime.MessageCenter.ProcessRequestsToInvalidActivation(
                    msgs,
                    Address,
                    forwardingAddress: null,
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

                if (_shared.Logger.IsEnabled(LogLevel.Debug)) _shared.Logger.LogDebug((int)ErrorCode.Catalog_RerouteAllQueuedMessages, "Rerouting {NumMessages} messages from invalid grain activation {Grain}", msgs.Count, this);
                _shared.InternalRuntime.LocalGrainDirectory.InvalidateCacheEntry(Address);
                _shared.InternalRuntime.MessageCenter.ProcessRequestsToInvalidActivation(msgs, Address, null, DeactivationReason.Description, DeactivationException);
            }
        }

        #region Activation

        public void Activate(Dictionary<string, object> requestContext, CancellationToken? cancellationToken)
        {
            cancellationToken ??= new CancellationTokenSource(_shared.InternalRuntime.CollectionOptions.Value.DeactivationTimeout).Token;

            ScheduleOperation(new Command.Activate(requestContext, cancellationToken.Value));
        }

        private async Task ActivateAsync(Dictionary<string, object> requestContextData, CancellationToken cancellationToken)
        {
            // A chain of promises that will have to complete in order to complete the activation
            // Register with the grain directory, register with the store if necessary and call the Activate method on the new activation.
            lock (this)
            {
                State = ActivationState.Activating;
            }

            if (_shared.Logger.IsEnabled(LogLevel.Debug))
            {
                _shared.Logger.LogDebug((int)ErrorCode.Catalog_BeforeCallingActivate, "Activating grain {Grain}",
                    this);
            }

            // Start grain lifecycle within try-catch wrapper to safely capture any exceptions thrown from called function
            try
            {
                RequestContextExtensions.Import(requestContextData);
                await Lifecycle.OnStart(cancellationToken);

                lock (this)
                {
                    if (State == ActivationState.Activating)
                    {
                        State = ActivationState.Valid; // Activate calls on this activation are finished
                    }
                }

                if (_shared.Logger.IsEnabled(LogLevel.Debug))
                {
                    _shared.Logger.LogDebug((int)ErrorCode.Catalog_AfterCallingActivate,
                        "Finished activating grain {Grain}", this);
                }

                if (_shared.Logger.IsEnabled(LogLevel.Debug))
                {
                    _shared.Logger.Debug("InitActivation is done: {0}", Address);
                }
            }
            catch (Exception exception)
            {
                CounterStatistic.FindOrCreate(StatisticNames.CATALOG_ACTIVATION_FAILED_TO_ACTIVATE).Increment();

                // Capture the exeption so that it can be propagated to rejection messages
                var sourceException = (exception as OrleansLifecycleCanceledException)?.InnerException ?? exception;
                _shared.Logger.LogError((int)ErrorCode.Catalog_ErrorCallingActivate, sourceException,
                    "Error activating service {Service}", this);

                // Unregister the activation from the directory so other silo don't keep sending message to it
                lock (this)
                {
                    State = ActivationState.FailedToActivate;
                    DeactivationReason = new(DeactivationReasonCode.ActivationFailed, sourceException, "Failed to activate grain.");
                }

                // Unregister this as a message target after some period of time.
                // This is delayed so that consistently failing activation, perhaps due to an application bug or network
                // issue, does not cause a flood of doomed activations.
                ScheduleOperation(new Command.Delay(TimeSpan.FromSeconds(5)));
                ScheduleOperation(new Command.UnregisterFromCatalog());

                lock (this)
                {
                    State = ActivationState.Invalid;
                }

                _shared.Logger.LogError(exception, "Activation of service {Service} failed", this);
            }
            finally
            {
                RequestContext.Clear();
                _idleSignal.Signal();
            }
        }

        #endregion

        #region Deactivation

        /// <summary>
        /// Starts the deactivation process.
        /// </summary>
        private void StartDeactivating(DeactivationReason deactivationReason)
        {
            lock (this)
            {
                switch (State)
                {
                    case ActivationState.Deactivating or ActivationState.Invalid or ActivationState.FailedToActivate:
                        return;
                    case ActivationState.Activating or ActivationState.Create:
                        throw new InvalidOperationException("Calling DeactivateOnIdle from within OnActivateAsync is not supported");
                }

                if (DeactivationReason.ReasonCode == DeactivationReasonCode.None)
                {
                    DeactivationReason = deactivationReason;
                }

                DeactivationStartTime = DateTime.UtcNow;
                State = ActivationState.Deactivating;
            }
        }

        /// <summary>
        /// Completes the deactivation process.
        /// </summary>
        /// <param name="cancellationToken">A cancellation which terminates graceful deactivation when cancelled.</param>
        private async Task FinishDeactivating(CancellationToken cancellationToken)
        {
            try
            {
                if (_shared.Logger.IsEnabled(LogLevel.Trace))
                {
                    _shared.Logger.LogTrace("FinishDeactivating activation {Activation}", this.ToDetailedString());
                }

                try
                {
                    // Note: This call is being made from within Scheduler.Queue wrapper, so we are already executing on worker thread
                    if (_shared.Logger.IsEnabled(LogLevel.Debug)) _shared.Logger.LogDebug((int)ErrorCode.Catalog_BeforeCallingDeactivate, "About to call {Service} service's OnDeactivateAsync() method {Type}", this, GrainInstance?.GetType().FullName);

                    // Call OnDeactivateAsync inline, but within try-catch wrapper to safely capture any exceptions thrown from called function
                    try
                    {
                        // just check in case this activation data is already Invalid or not here at all.
                        if (State == ActivationState.Deactivating)
                        {
                            RequestContext.Clear(); // Clear any previous RC, so it does not leak into this call by mistake.
                            await Lifecycle.OnStop(cancellationToken).WithCancellation(cancellationToken);
                        }

                        if (_shared.Logger.IsEnabled(LogLevel.Debug)) _shared.Logger.LogDebug((int)ErrorCode.Catalog_AfterCallingDeactivate, "Returned from calling {Service} service's OnDeactivateAsync() method {Type}", this, GrainInstance?.GetType().FullName);
                    }
                    catch (Exception exc)
                    {
                        _shared.Logger.LogError((int)ErrorCode.Catalog_ErrorCallingDeactivate, exc, "Error calling grain's OnDeactivateAsync() method - Grain type = {Type} Activation = {Service}", this, GrainInstance?.GetType().FullName);
                    }
                }
                catch (Exception exc)
                {
                    _shared.Logger.LogError((int)ErrorCode.Catalog_FinishGrainDeactivateAndCleanupStreams_Exception,
                        exc, "CallGrainDeactivateAndCleanupStreams Activation = {Service} failed", this);
                }

                // Unregister from directory
                await _shared.InternalRuntime.GrainLocator.Unregister(Address, UnregistrationCause.Force);
                if (_shared.Logger.IsEnabled(LogLevel.Trace))
                {
                    _shared.Logger.LogTrace("Completed async portion of FinishDeactivating for activation {Activation}", ToDetailedString());
                }
            }
            catch (Exception ex)
            {
                _shared.Logger.LogWarning((int)ErrorCode.Catalog_DeactivateActivation_Exception, ex, "Exception when trying to deactivate {Activation}", this);
            }

            lock (this)
            {
                State = ActivationState.Invalid;
            }

            if (IsStuckDeactivating)
            {
                CounterStatistic.FindOrCreate(StatisticNames.CATALOG_ACTIVATION_SHUTDOWN_VIA_DEACTIVATE_STUCK_ACTIVATION).Increment();
            }
            else
            {
                CounterStatistic.FindOrCreate(StatisticNames.CATALOG_ACTIVATION_SHUTDOWN_VIA_COLLECTION).Increment();
            }

            try
            {
                UnregisterMessageTarget();
                await DisposeAsync();
            }
            catch (Exception exception)
            {
                _shared.Logger.LogWarning(exception, "Exception disposing activation {Activation}", this);
            }

            // Signal deactivation
            GetDeactivationCompletionSource().TrySetResult(true);
            _idleSignal.Signal();

            if (_shared.Logger.IsEnabled(LogLevel.Trace))
            {
                _shared.Logger.LogTrace("Completed final portion of FinishDeactivating for activation {Activation}", this.ToDetailedString());
            }
        }

        private TaskCompletionSource<bool> GetDeactivationCompletionSource()
        {
            lock (this)
            {
                return GetOrCreateExtras().DeactivationTask ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        Task IGrainManagementExtension.DeactivateOnIdle()
        {
            Deactivate(new(DeactivationReasonCode.ApplicationRequested, $"{nameof(IGrainManagementExtension.DeactivateOnIdle)} was called."));
            return Task.CompletedTask;
        }

        private void UnregisterMessageTarget()
        {
            _shared.InternalRuntime.Catalog.UnregisterMessageTarget(this);
            if (GrainInstance is not null)
            {
                SetServiceInstance(null);
            }
        }

        #endregion

        /// <summary>
        /// Additional properties which are not needed for the majority of an activation's lifecycle.
        /// </summary>
        private class ServiceContextExtra
        {
            /// <summary>
            /// A <see cref="TaskCompletionSource{TResult}"/> which completes when a grain has deactivated.
            /// </summary>
            public TaskCompletionSource<bool> DeactivationTask { get; set; }

            public DateTime? DeactivationStartTime { get; set; }

            public bool IsStuckDeactivating { get; set; }

            public DeactivationReason DeactivationReason { get; set; }
        }

        private record Command
        {
            public record Deactivate(CancellationToken CancellationToken) : Command;
            public record Activate(Dictionary<string, object> RequestContext, CancellationToken CancellationToken) : Command;
            public record Delay(TimeSpan Duration) : Command;
            public record UnregisterFromCatalog : Command;
        }

        public void QueueAction(Action action) => ThreadPool.QueueUserWorkItem(_ => action());

        public void QueueTask(Task task) => task.Start(TaskScheduler.Default);

        public void QueueWorkItem(IThreadPoolWorkItem workItem) => ThreadPool.UnsafeQueueUserWorkItem(workItem, true);
    }
}
