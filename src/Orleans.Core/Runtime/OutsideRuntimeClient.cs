using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.ClientObservers;
using Orleans.CodeGeneration;
using Orleans.Configuration;
using Orleans.Messaging;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Invocation;
using static Orleans.Internal.StandardExtensions;

namespace Orleans
{
    internal class OutsideRuntimeClient : IRuntimeClient, IDisposable, IClusterConnectionStatusListener
    {
        internal static bool TestOnlyThrowExceptionDuringInit { get; set; }

        private readonly ILogger logger;
        private readonly ClientMessagingOptions clientMessagingOptions;

        private InvokableObjectManager localObjects;
        private bool disposing;
        private bool disposed;

        private readonly MessagingTrace messagingTrace;
        private readonly CallbackWorker[] _callbacks;
        private readonly InterfaceToImplementationMappingCache _interfaceToImplementationMapping;

        public IInternalGrainFactory InternalGrainFactory { get; private set; }

        private MessageFactory messageFactory;
        private readonly LocalClientDetails _localClientDetails;
        private readonly ILoggerFactory loggerFactory;

        private readonly SharedCallbackData sharedCallbackData;
        private readonly PeriodicTimer callbackTimer;
        private Task callbackTimerTask;

        public GrainAddress CurrentActivationAddress
        {
            get;
            private set;
        }

        public ClientGatewayObserver gatewayObserver { get; private set; }

        public string CurrentActivationIdentity
        {
            get { return CurrentActivationAddress.ToString(); }
        }

        public IGrainReferenceRuntime GrainReferenceRuntime { get; private set; }

        internal ClientMessageCenter MessageCenter { get; private set; }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Reliability", "CA2000:Dispose objects before losing scope",
            Justification = "MessageCenter is IDisposable but cannot call Dispose yet as it lives past the end of this method call.")]
        public OutsideRuntimeClient(
            LocalClientDetails localClientDetails,
            ILoggerFactory loggerFactory,
            IOptions<ClientMessagingOptions> clientMessagingOptions,
            MessagingTrace messagingTrace,
            IServiceProvider serviceProvider,
            TimeProvider timeProvider,
            InterfaceToImplementationMappingCache interfaceToImplementationMapping)
        {
            TimeProvider = timeProvider;
            _callbacks = new CallbackWorker[16];
            for (var i = 0; i < _callbacks.Length; i++)
            {
                _callbacks[i] = new(loggerFactory.CreateLogger<CallbackWorker>());
            }
            _interfaceToImplementationMapping = interfaceToImplementationMapping;
            this.ServiceProvider = serviceProvider;
            _localClientDetails = localClientDetails;
            this.loggerFactory = loggerFactory;
            this.messagingTrace = messagingTrace;
            this.logger = loggerFactory.CreateLogger<OutsideRuntimeClient>();
            this.clientMessagingOptions = clientMessagingOptions.Value;
            var period = Max(
                TimeSpan.FromMilliseconds(1),
                Min(
                    this.clientMessagingOptions.ResponseTimeout,
                    TimeSpan.FromSeconds(1)));
            this.callbackTimer = new PeriodicTimer(period, timeProvider);
            this.sharedCallbackData = new SharedCallbackData(
                this.loggerFactory.CreateLogger<CallbackData>(),
                this.clientMessagingOptions.ResponseTimeout);
        }

        internal void ConsumeServices()
        {
            try
            {
                var connectionLostHandlers = this.ServiceProvider.GetServices<ConnectionToClusterLostHandler>();
                foreach (var handler in connectionLostHandlers)
                {
                    this.ClusterConnectionLost += handler;
                }

                var gatewayCountChangedHandlers = this.ServiceProvider.GetServices<GatewayCountChangedHandler>();
                foreach (var handler in gatewayCountChangedHandlers)
                {
                    this.GatewayCountChanged += handler;
                }

                this.InternalGrainFactory = this.ServiceProvider.GetRequiredService<IInternalGrainFactory>();
                this.messageFactory = this.ServiceProvider.GetService<MessageFactory>();
                this.localObjects = new InvokableObjectManager(
                    ServiceProvider.GetRequiredService<ClientGrainContext>(),
                    this,
                    ServiceProvider.GetRequiredService<DeepCopier>(),
                    messagingTrace,
                    ServiceProvider.GetRequiredService<DeepCopier<Response>>(),
                    _interfaceToImplementationMapping,
                    loggerFactory.CreateLogger<ClientGrainContext>());

                this.callbackTimerTask = Task.Run(MonitorCallbackExpiry);

                this.GrainReferenceRuntime = this.ServiceProvider.GetRequiredService<IGrainReferenceRuntime>();

                // Client init / sign-on message
                logger.LogInformation((int)ErrorCode.ClientStarting, "Starting Orleans client with runtime version \"{RuntimeVersion}\", local address {LocalAddress} and client id {ClientId}", RuntimeVersion.Current, _localClientDetails.ClientAddress, _localClientDetails.ClientId);

                if (TestOnlyThrowExceptionDuringInit)
                {
                    throw new InvalidOperationException("TestOnlyThrowExceptionDuringInit");
                }
            }
            catch (Exception exc)
            {
                if (logger != null) logger.LogError((int)ErrorCode.Runtime_Error_100319, exc, "OutsideRuntimeClient constructor failed.");
                ConstructorReset();
                throw;
            }
        }

        public IServiceProvider ServiceProvider { get; private set; }

        public TimeProvider TimeProvider { get; }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            ConsumeServices();

            // Deliberately avoid capturing the current synchronization context during startup and execute on the default scheduler.
            // This helps to avoid any issues (such as deadlocks) caused by executing with the client's synchronization context/scheduler.
            await Task.Run(() => this.StartInternal(cancellationToken)).ConfigureAwait(false);

            logger.LogInformation((int)ErrorCode.ProxyClient_StartDone, "Started client with address {ActivationAddress} and id {ClientId}", CurrentActivationAddress.ToString(), _localClientDetails.ClientId);
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            this.callbackTimer.Dispose();
            if (this.callbackTimerTask is { } task)
            {
                await task.WaitAsync(cancellationToken);
            }

            if (MessageCenter is { } messageCenter)
            {
                await messageCenter.StopAsync(cancellationToken);
            }

            ConstructorReset();
        }

        // used for testing to (carefully!) allow two clients in the same process
        private async Task StartInternal(CancellationToken cancellationToken)
        {
            var retryFilter = ServiceProvider.GetService<IClientConnectionRetryFilter>();

            var gatewayManager = this.ServiceProvider.GetRequiredService<GatewayManager>();
            await ExecuteWithRetries(
                async () => await gatewayManager.StartAsync(cancellationToken),
                retryFilter,
                cancellationToken);

            MessageCenter = ActivatorUtilities.CreateInstance<ClientMessageCenter>(this.ServiceProvider);
            MessageCenter.RegisterLocalMessageHandler(this.HandleMessage);
            await ExecuteWithRetries(
                async () => await MessageCenter.StartAsync(cancellationToken),
                retryFilter,
                cancellationToken);
            CurrentActivationAddress = GrainAddress.NewActivationAddress(MessageCenter.MyAddress, _localClientDetails.ClientId.GrainId);

            this.gatewayObserver = new ClientGatewayObserver(gatewayManager);
            this.InternalGrainFactory.CreateObjectReference<IClientGatewayObserver>(this.gatewayObserver);

            await ExecuteWithRetries(
                async () => await this.ServiceProvider.GetRequiredService<ClientClusterManifestProvider>().StartAsync(),
                retryFilter,
                cancellationToken);

            static async Task ExecuteWithRetries(Func<Task> task, IClientConnectionRetryFilter retryFilter, CancellationToken cancellationToken)
            {
                do
                {
                    try
                    {
                        await task();
                        return;
                    }
                    catch (Exception exception) when (retryFilter is not null && !cancellationToken.IsCancellationRequested)
                    {
                        var shouldRetry = await retryFilter.ShouldRetryConnectionAttempt(exception, cancellationToken);
                        if (cancellationToken.IsCancellationRequested || !shouldRetry)
                        {
                            throw;
                        }
                    }
                }
                while (!cancellationToken.IsCancellationRequested);
            }
        }

        private void HandleMessage(Message message)
        {
            switch (message.Direction)
            {
                case Message.Directions.Response:
                    {
                        ReceiveResponse(message);
                        break;
                    }
                case Message.Directions.OneWay:
                case Message.Directions.Request:
                    {
                        this.localObjects.Dispatch(message);
                        break;
                    }
                default:
                    logger.LogError((int)ErrorCode.Runtime_Error_100327, "Message not supported: {Message}.", message);
                    break;
            }
        }

        public void SendResponse(Message request, Response response)
        {
            ThrowIfDisposed();
            var message = this.messageFactory.CreateResponseMessage(request);
            OrleansOutsideRuntimeClientEvent.Log.SendResponse(message);
            message.BodyObject = response;

            MessageCenter.SendMessage(message, targetCache: request);
        }

        public void SendRequest(GrainReference target, IInvokable request, IResponseCompletionSource context, InvokeMethodOptions options)
        {
            ThrowIfDisposed();
            var message = this.messageFactory.CreateMessage(request, options);
            OrleansOutsideRuntimeClientEvent.Log.SendRequest(message);

            message.InterfaceType = target.InterfaceType;
            message.InterfaceVersion = target.InterfaceVersion;
            var targetGrainId = target.GrainId;
            var oneWay = (options & InvokeMethodOptions.OneWay) != 0;
            message.SendingGrain = CurrentActivationAddress.GrainId;
            message.TargetGrain = targetGrainId;

            if (SystemTargetGrainId.TryParse(targetGrainId, out var systemTargetGrainId))
            {
                // If the silo isn't be supplied, it will be filled in by the sender to be the gateway silo
                message.TargetSilo = systemTargetGrainId.GetSiloAddress();
            }

            if (this.clientMessagingOptions.DropExpiredMessages && message.IsExpirableMessage())
            {
                // don't set expiration for system target messages.
                var ttl = request.GetDefaultResponseTimeout() ?? this.clientMessagingOptions.ResponseTimeout;
                message.TimeToLive = ttl;
            }

            if (!oneWay)
            {
                var callbackData = new CallbackData(this.sharedCallbackData, context, message);
                _callbacks[message.Id.Value & 0xf].RegisterCallback(callbackData);
            }
            else
            {
                context?.Complete();
            }

            if (logger.IsEnabled(LogLevel.Trace)) logger.LogTrace("Send {Message}", message);
            MessageCenter.SendMessage(message, targetCache: target);
        }

        public void ReceiveResponse(Message response)
        {
            OrleansOutsideRuntimeClientEvent.Log.ReceiveResponse(response);

            if (logger.IsEnabled(LogLevel.Trace)) logger.LogTrace("Received {Message}", response);

            _callbacks[response.Id.Value & 0xf].ReceiveResponse(response);
        }

        private void ConstructorReset()
        {
            Utils.SafeExecute(() => this.Dispose());
        }

        /// <inheritdoc />
        public TimeSpan GetResponseTimeout() => this.sharedCallbackData.ResponseTimeout;

        /// <inheritdoc />
        public void SetResponseTimeout(TimeSpan timeout) => this.sharedCallbackData.ResponseTimeout = timeout;

        public IAddressable CreateObjectReference(IAddressable obj)
        {
            if (obj is GrainReference)
                throw new ArgumentException("Argument obj is already a grain reference.", nameof(obj));

            if (obj is IGrainBase)
                throw new ArgumentException("Argument must not be a grain class.", nameof(obj));

            var observerId = obj is ClientObserver clientObserver
                ? clientObserver.GetObserverGrainId(_localClientDetails.ClientId)
                : ObserverGrainId.Create(_localClientDetails.ClientId);
            var reference = this.InternalGrainFactory.GetGrain(observerId.GrainId);

            if (!localObjects.TryRegister(obj, observerId))
            {
                throw new ArgumentException($"Failed to add new observer {reference} to localObjects collection.", "reference");
            }

            return reference;
        }

        public void DeleteObjectReference(IAddressable obj)
        {
            if (!(obj is GrainReference reference))
            {
                throw new ArgumentException("Argument reference is not a grain reference.");
            }

            if (!ObserverGrainId.TryParse(reference.GrainId, out var observerId))
            {
                throw new ArgumentException($"Reference {reference.GrainId} is not an observer reference");
            }

            if (!localObjects.TryDeregister(observerId))
            {
                throw new ArgumentException("Reference is not associated with a local object.", "reference");
            }
        }

        public void Dispose()
        {
            if (this.disposing) return;
            this.disposing = true;

            Utils.SafeExecute(() => this.callbackTimer.Dispose());

            Utils.SafeExecute(() => MessageCenter?.Dispose());

            this.ClusterConnectionLost = null;
            this.GatewayCountChanged = null;

            GC.SuppressFinalize(this);
            disposed = true;
        }

        public void BreakOutstandingMessagesToDeadSilo(SiloAddress deadSilo)
        {
            foreach (var callbackManager in _callbacks)
            {
                callbackManager.BreakOutstandingMessagesToDeadSilo(deadSilo);
            }
        }

        public async Task<int> GetRunningRequestsCount(GrainInterfaceType grainInterfaceType)
        {
            var tasks = new List<Task<int>>();
            foreach (var worker in _callbacks)
            {
                tasks.Add(worker.GetRunningRequestCount(grainInterfaceType));
            }

            await Task.WhenAll(tasks);
            return tasks.Sum(t => t.Result);
        }

        /// <inheritdoc />
        public event ConnectionToClusterLostHandler ClusterConnectionLost;

        /// <inheritdoc />
        public event GatewayCountChangedHandler GatewayCountChanged;

        /// <inheritdoc />
        public void NotifyClusterConnectionLost()
        {
            try
            {
                this.ClusterConnectionLost?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                this.logger.LogError((int)ErrorCode.ClientError, ex, "Error when sending cluster disconnection notification");
            }
        }

        /// <inheritdoc />
        public void NotifyGatewayCountChanged(int currentNumberOfGateways, int previousNumberOfGateways)
        {
            try
            {
                this.GatewayCountChanged?.Invoke(this, new GatewayCountChangedEventArgs(currentNumberOfGateways, previousNumberOfGateways));
            }
            catch (Exception ex)
            {
                this.logger.LogError((int)ErrorCode.ClientError, ex, "Error when sending gateway count changed notification");
            }
        }

        private async Task MonitorCallbackExpiry()
        {
            while (await callbackTimer.WaitForNextTickAsync())
            {
                foreach (var callbackManager in _callbacks)
                {
                    callbackManager.CheckForExpiredCallbacks();
                }
            }
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                ThrowObjectDisposedException();
            }

            void ThrowObjectDisposedException() => throw new ObjectDisposedException(nameof(OutsideRuntimeClient));
        }
    }
}