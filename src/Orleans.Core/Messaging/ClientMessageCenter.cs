using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.Messaging;
using Orleans.Serialization;

namespace Orleans.Messaging
{
    // <summary>
    // This class is used on the client only.
    // It provides the client counterpart to the Gateway and GatewayAcceptor classes on the silo side.
    // 
    // There is one ClientMessageCenter instance per OutsideRuntimeClient. There can be multiple ClientMessageCenter instances
    // in a single process, but because RuntimeClient keeps a static pointer to a single OutsideRuntimeClient instance, this is not
    // generally done in practice.
    // 
    // Each ClientMessageCenter keeps a collection of GatewayConnection instances. Each of these represents a bidirectional connection
    // to a single gateway endpoint. Requests are assigned to a specific connection based on the target grain ID, so that requests to
    // the same grain will go to the same gateway, in sending order. To do this efficiently and scalably, we bucket grains together
    // based on their hash code mod a reasonably large number (currently 8192).
    // 
    // When the first message is sent to a bucket, we assign a gateway to that bucket, selecting in round-robin fashion from the known
    // gateways. If this is the first message to be sent to the gateway, we will create a new connection for it and assign the bucket to
    // the new connection. Either way, all messages to grains in that bucket will be sent to the assigned connection as long as the
    // connection is live.
    // 
    // Connections stay live as long as possible. If a socket error or other communications error occurs, then the client will try to 
    // reconnect twice before giving up on the gateway. If the connection cannot be re-established, then the gateway is deemed (temporarily)
    // dead, and any buckets assigned to the connection are unassigned (so that the next message sent will cause a new gateway to be selected).
    // There is no assumption that this death is permanent; the system will try to reuse the gateway every 5 minutes.
    // 
    // The list of known gateways is managed by the GatewayManager class. See comments there for details...
    // =======================================================================================================================================
    // Locking and lock protocol:
    // The ClientMessageCenter instance itself may be accessed by many client threads simultaneously, and each GatewayConnection instance
    // is accessed by its own thread, by the thread for its Receiver, and potentially by client threads from within the ClientMessageCenter.
    // Thus, we need locks to protect the various data structured from concurrent modifications.
    // 
    // Each GatewayConnection instance has a "lockable" field that is used to lock local information. This lock is used by both the GatewayConnection
    // thread and the Receiver thread.
    // 
    // The ClientMessageCenter instance also has a "lockable" field. This lock is used by any client thread running methods within the instance.
    // 
    // Note that we take care to ensure that client threads never need locked access to GatewayConnection state and GatewayConnection threads never need
    // locked access to ClientMessageCenter state. Thus, we don't need to worry about lock ordering across these objects.
    // 
    // Finally, the GatewayManager instance within the ClientMessageCenter has two collections, knownGateways and knownDead, that it needs to
    // protect with locks. Rather than using a "lockable" field, each collection is lcoked to protect the collection.
    // All sorts of threads can run within the GatewayManager, including client threads and GatewayConnection threads, so we need to
    // be careful about locks here. The protocol we use is to always take GatewayManager locks last, to only take them within GatewayManager methods,
    // and to always release them before returning from the method. In addition, we never simultaneously hold the knownGateways and knownDead locks,
    // so there's no need to worry about the order in which we take and release those locks.
    // </summary>
    internal class ClientMessageCenter : IMessageCenter, IDisposable
    {
        internal readonly SerializationManager SerializationManager;

        internal static readonly TimeSpan MINIMUM_INTERCONNECT_DELAY = TimeSpan.FromMilliseconds(100);   // wait one tenth of a second between connect attempts
        internal const int CONNECT_RETRY_COUNT = 2;                                                      // Retry twice before giving up on a gateway server

        internal GrainId ClientId { get; private set; }
        public IRuntimeClient RuntimeClient { get; }
        internal bool Running { get; private set; }

        internal readonly GatewayManager GatewayManager;
        internal readonly Channel<Message> PendingInboundMessages;
        private readonly Action<Message>[] messageHandlers;
        private int numMessages;
        // The grainBuckets array is used to select the connection to use when sending an ordered message to a grain.
        // Requests are bucketed by GrainID, so that all requests to a grain get routed through the same bucket.
        // Each bucket holds a (possibly null) weak reference to a GatewayConnection object. That connection instance is used
        // if the WeakReference is non-null, is alive, and points to a live gateway connection. If any of these conditions is
        // false, then a new gateway is selected using the gateway manager, and a new connection established if necessary.
        private readonly WeakReference<ConnectionMessageSender>[] grainBuckets;
        private readonly ILogger logger;
        private readonly object lockable;
        public SiloAddress MyAddress { get; private set; }
        private readonly QueueTrackingStatistic queueTracking;
        private int numberOfConnectedGateways = 0;
        private readonly MessageFactory messageFactory;
        private readonly IClusterConnectionStatusListener connectionStatusListener;
        private readonly ConnectionManager connectionManager;
        private StatisticsLevel statisticsLevel;

        public ClientMessageCenter(
            IOptions<GatewayOptions> gatewayOptions,
            IOptions<ClientMessagingOptions> clientMessagingOptions,
            IPAddress localAddress,
            int gen,
            GrainId clientId,
            IGatewayListProvider gatewayListProvider,
            SerializationManager serializationManager,
            IRuntimeClient runtimeClient,
            MessageFactory messageFactory,
            IClusterConnectionStatusListener connectionStatusListener,
            ILoggerFactory loggerFactory,
            IOptions<StatisticsOptions> statisticsOptions,
            ConnectionManager connectionManager)
        {
            this.messageHandlers = new Action<Message>[Enum.GetValues(typeof(Message.Categories)).Length];
            this.connectionManager = connectionManager;
            this.SerializationManager = serializationManager;
            lockable = new object();
            MyAddress = SiloAddress.New(new IPEndPoint(localAddress, 0), gen);
            ClientId = clientId;
            this.RuntimeClient = runtimeClient;
            this.messageFactory = messageFactory;
            this.connectionStatusListener = connectionStatusListener;
            Running = false;
            GatewayManager = new GatewayManager(gatewayOptions.Value, gatewayListProvider, loggerFactory);
            PendingInboundMessages = Channel.CreateUnbounded<Message>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = true
            });
            numMessages = 0;
            grainBuckets = new WeakReference<ConnectionMessageSender>[clientMessagingOptions.Value.ClientSenderBuckets];
            logger = loggerFactory.CreateLogger<ClientMessageCenter>();
            if (logger.IsEnabled(LogLevel.Debug)) logger.Debug("Proxy grain client constructed");
            IntValueStatistic.FindOrCreate(
                StatisticNames.CLIENT_CONNECTED_GATEWAY_COUNT,
                () =>
                {
                    /*lock (gatewayConnections)
                    {
                        return gatewayConnections.Count;
                    }*/

                    return 0;
                });
            statisticsLevel = statisticsOptions.Value.CollectionLevel;
            if (statisticsLevel.CollectQueueStats())
            {
                queueTracking = new QueueTrackingStatistic("ClientReceiver", statisticsOptions);
            }
        }

        public void Start()
        {
            Running = true;
            if (this.statisticsLevel.CollectQueueStats())
            {
                queueTracking.OnStartExecution();
            }
            if (logger.IsEnabled(LogLevel.Debug)) logger.Debug("Proxy grain client started");
        }

        public void PrepareToStop()
        {
            // put any pre stop logic here.
        }

        public void Stop()
        {
            Running = false;

            Utils.SafeExecute(() =>
            {
                PendingInboundMessages.Writer.Complete();
            });

            if (this.statisticsLevel.CollectQueueStats())
            {
                queueTracking.OnStopExecution();
            }
            GatewayManager.Stop();

            var exception = new ConnectionAbortedException("Stopping");
            /*foreach (var gateway in .Values.ToArray())
            {
                if (gateway.TryGetTarget(out var connection)) connection.Context.Abort(exception);
            }*/
        }

        public ChannelReader<Message> GetReader(Message.Categories type)
            => PendingInboundMessages.Reader;

        public void OnReceivedMessage(Message message)
        {
            var handler = this.messageHandlers[(int)message.Category];
            if (handler != null)
            {
                handler(message);
            }
            else
            {
                PendingInboundMessages.Writer.TryWrite(message);
            }
        }

        public void SendMessage(Message msg)
        {
            if (!Running)
            {
                this.logger.Error(ErrorCode.ProxyClient_MsgCtrNotRunning, $"Ignoring {msg} because the Client message center is not running");
                return;
            }

            var gatewaySender = this.GetGatewayConnection(msg);
            try
            {
                gatewaySender.Send(msg);
                // TODO: Fix log message
                if (logger.IsEnabled(LogLevel.Trace)) logger.Trace(ErrorCode.ProxyClient_QueueRequest, "Sending message {0} via gateway {1}", msg, gatewaySender );
            }
            catch (InvalidOperationException)
            {
                // This exception can be thrown if the gateway connection we selected was closed since we checked (i.e., we lost the race)
                // If this happens, we reject if the message is targeted to a specific silo, or try again if not
                RejectOrResend(msg);
            }
        }

        private ConnectionMessageSender GetGatewayConnection(Message msg)
        {
            ConnectionMessageSender gatewayConnection;

            // If there's a specific gateway specified, use it
            if (msg.TargetSilo != null && GatewayManager.GetLiveGateways().Contains(msg.TargetSilo.ToGatewayUri()))
            {
                gatewayConnection = this.connectionManager.GetConnection(msg.TargetSilo.Endpoint.ToString());
            }
            // For untargeted messages to system targets, and for unordered messages, pick a next connection in round robin fashion.
            else if (msg.TargetGrain.IsSystemTarget || msg.IsUnordered)
            {
                // Get the cached list of live gateways.
                // Pick a next gateway name in a round robin fashion.
                // See if we have a live connection to it.
                // If Yes, use it.
                // If not, create a new GatewayConnection and start it.
                // If start fails, we will mark this connection as dead and remove it from the GetCachedLiveGatewayNames.
                int msgNumber = Interlocked.Increment(ref numMessages);
                IList<Uri> gatewayNames = GatewayManager.GetLiveGateways();
                int numGateways = gatewayNames.Count;
                if (numGateways == 0)
                {
                    RejectMessage(msg, "No gateways available");
                    logger.Warn(ErrorCode.ProxyClient_CannotSend, "Unable to send message {0}; gateway manager state is {1}", msg, GatewayManager);
                    return null;
                }

                var gatewayName = gatewayNames[msgNumber % numGateways].ToIPEndPoint().ToString();
                gatewayConnection = this.connectionManager.GetConnection(gatewayName);
            }
            // Otherwise, use the buckets to ensure ordering.
            else
            {
                var index = msg.TargetGrain.GetHashCode_Modulo((uint)grainBuckets.Length);
                lock (lockable)
                {
                    // Repeated from above, at the declaration of the grainBuckets array:
                    // Requests are bucketed by GrainID, so that all requests to a grain get routed through the same bucket.
                    // Each bucket holds a (possibly null) weak reference to a GatewayConnection object. That connection instance is used
                    // if the WeakReference is non-null, is alive, and points to a live gateway connection. If any of these conditions is
                    // false, then a new gateway is selected using the gateway manager, and a new connection established if necessary.
                    var weakRef = grainBuckets[index];
                    if (weakRef == null || !weakRef.TryGetTarget(out gatewayConnection))
                    {
                        var addr = GatewayManager.GetLiveGateway();
                        if (addr == null)
                        {
                            RejectMessage(msg, "No gateways available");
                            logger.Warn(ErrorCode.ProxyClient_CannotSend_NoGateway, "Unable to send message {0}; gateway manager state is {1}", msg, GatewayManager);
                            return null;
                        }
                        if (logger.IsEnabled(LogLevel.Trace)) logger.Trace(ErrorCode.ProxyClient_NewBucketIndex, "Starting new bucket index {0} for ordered messages to grain {1}", index, msg.TargetGrain);

                        gatewayConnection = this.connectionManager.GetConnection(addr.ToIPEndPoint().ToString());
                        if (logger.IsEnabled(LogLevel.Debug)) logger.Debug(ErrorCode.ProxyClient_CreatedGatewayToGrain, "Creating gateway to {0} for message to grain {1}, bucket {2}, grain id hash code {3}X", addr, msg.TargetGrain, index,
                                            msg.TargetGrain.GetHashCode().ToString("x"));
                        grainBuckets[index] = new WeakReference<ConnectionMessageSender>(gatewayConnection);
                    }
                }
            }

            return gatewayConnection;
        }

        private void RejectOrResend(Message msg)
        {
            if (msg.TargetSilo != null)
            {
                RejectMessage(msg, string.Format("Target silo {0} is unavailable", msg.TargetSilo));
            }
            else
            {
                SendMessage(msg);
            }
        }

        public Task<IGrainTypeResolver> GetGrainTypeResolver(IInternalGrainFactory grainFactory)
        {
            var silo = GetLiveGatewaySiloAddress();
            return GetTypeManager(silo, grainFactory).GetClusterGrainTypeResolver();
        }

        public Task<Streams.ImplicitStreamSubscriberTable> GetImplicitStreamSubscriberTable(IInternalGrainFactory grainFactory)
        {
            var silo = GetLiveGatewaySiloAddress();
            return GetTypeManager(silo, grainFactory).GetImplicitStreamSubscriberTable(silo);
        }

        public void RegisterLocalMessageHandler(Message.Categories category, Action<Message> handler)
        {
        }

        public void RejectMessage(Message msg, string reason, Exception exc = null)
        {
            if (!Running) return;
            
            if (msg.Direction != Message.Directions.Request)
            {
                if (logger.IsEnabled(LogLevel.Debug)) logger.Debug(ErrorCode.ProxyClient_DroppingMsg, "Dropping message: {0}. Reason = {1}", msg, reason);
            }
            else
            {
                if (logger.IsEnabled(LogLevel.Debug)) logger.Debug(ErrorCode.ProxyClient_RejectingMsg, "Rejecting message: {0}. Reason = {1}", msg, reason);
                MessagingStatisticsGroup.OnRejectedMessage(msg);
                Message error = this.messageFactory.CreateRejectionResponse(msg, Message.RejectionTypes.Unrecoverable, reason, exc);
                OnReceivedMessage(error);
            }
        }

        /// <summary>
        /// For testing use only
        /// </summary>
        public void Disconnect()
        {
            var exception = new ConnectionAbortedException("Disconnecting");
            /*foreach (var connection in gatewayConnections.Values.ToArray())
            {
                connection.Context.Abort(exception);
            }*/
        }

        /// <summary>
        /// For testing use only.
        /// </summary>
        public void Reconnect()
        {
            throw new NotImplementedException("Reconnect");
        }

        public int SendQueueLength
        {
            get { return 0; }
        }

        public int ReceiveQueueLength
        {
            get { return 0; }
        }

        private IClusterTypeManager GetTypeManager(SiloAddress destination, IInternalGrainFactory grainFactory)
        {
            return grainFactory.GetSystemTarget<IClusterTypeManager>(Constants.TypeManagerId, destination);
        }

        private SiloAddress GetLiveGatewaySiloAddress()
        {
            var gateway = GatewayManager.GetLiveGateway();

            if (gateway == null)
            {
                throw new OrleansException("Not connected to a gateway");
            }

            return gateway.ToSiloAddress();
        }

        internal void UpdateClientId(GrainId clientId)
        {
            if (ClientId.Category != UniqueKey.Category.Client)
                throw new InvalidOperationException("Only handshake client ID can be updated with a cluster ID.");

            if (clientId.Category != UniqueKey.Category.GeoClient)
                throw new ArgumentException("Handshake client ID can only be updated  with a geo client.", nameof(clientId));

            ClientId = clientId;
        }

        internal void OnGatewayConnectionOpen()
        {
            Interlocked.Increment(ref numberOfConnectedGateways);
        }

        internal void OnGatewayConnectionClosed()
        {
            if (Interlocked.Decrement(ref numberOfConnectedGateways) == 0)
            {
                this.connectionStatusListener.NotifyClusterConnectionLost();
            }
        }

        public void Dispose()
        {
            PendingInboundMessages.Writer.TryComplete();
            /*if (gatewayConnections != null)
                foreach (var item in gatewayConnections)
                {
                    item.Value.Context.Abort();
                }*/
            GatewayManager.Dispose();
        }
        
        public bool PrepareMessageForSend(Message msg)
        {
            // Check to make sure we're not stopped
            if (!Running)
            {
                // Recycle the message we've dequeued. Note that this will recycle messages that were queued up to be sent when the gateway connection is declared dead
                msg.TargetActivation = null;
                msg.TargetSilo = null;
                this.SendMessage(msg);
                return false;
            }

            if (msg.TargetSilo != null) return true;

            if (msg.TargetGrain.IsSystemTarget)
                msg.TargetActivation = ActivationId.GetSystemActivation(msg.TargetGrain, msg.TargetSilo);

            return true;
        }

        public void OnMessageSerializationFailure(Message msg, Exception exc)
        {
            // we only get here if we failed to serialize the msg (or any other catastrophic failure).
            // Request msg fails to serialize on the sender, so we just enqueue a rejection msg.
            // Response msg fails to serialize on the responding silo, so we try to send an error response back.
            this.logger.LogWarning(
                (int)ErrorCode.ProxyClient_SerializationError,
                "Unexpected error serializing message {Message}: {Exception}",
                msg,
                exc);

            MessagingStatisticsGroup.OnFailedSentMessage(msg);

            var retryCount = msg.RetryCount ?? 0;

            if (msg.Direction == Message.Directions.Request)
            {
                this.RejectMessage(msg, $"Unable to serialize message. Encountered exception: {exc?.GetType()}: {exc?.Message}", exc);
            }
            else if (msg.Direction == Message.Directions.Response && retryCount < 1)
            {
                // if we failed sending an original response, turn the response body into an error and reply with it.
                // unless we have already tried sending the response multiple times.
                msg.Result = Message.ResponseTypes.Error;
                msg.BodyObject = Response.ExceptionResponse(exc);
                msg.RetryCount = retryCount + 1;
                this.SendMessage(msg);
            }
            else
            {
                this.logger.LogWarning(
                    (int)ErrorCode.ProxyClient_DroppingMsg,
                    "Gateway client is dropping message which failed during serialization: {Message}. Exception = {Exception}",
                    msg,
                    exc);

                MessagingStatisticsGroup.OnDroppedSentMessage(msg);
            }
        }
    }
}
