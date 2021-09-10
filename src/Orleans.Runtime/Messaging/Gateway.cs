using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.ClientObservers;
using Orleans.Configuration;

namespace Orleans.Runtime.Messaging
{
    internal class Gateway : IConnectedClientCollection
    {
        private readonly GatewayClientCleanupAgent _clientCleanupAgent;

        // clients is the main authorative collection of all connected clients. 
        // Any client currently in the system appears in this collection. 
        // In addition, we use clientConnections collection for fast retrival of ClientState. 
        // Anything that appears in those 2 collections should also appear in the main clients collection.
        private readonly ConcurrentDictionary<ClientGrainId, ClientState> _clients = new();
        private readonly Dictionary<GatewayInboundConnection, ClientState> _clientConnections = new();
        private readonly SiloAddress _gatewayAddress;
        private readonly ILogger<ClientState> _clientStateLog;
        private readonly MessageFactory _messageFactory;
        private readonly MessageCenter _messageCenter;
        private readonly ClientsReplyRoutingCache clientsReplyRoutingCache;

        private readonly ILogger _log;
        private readonly CounterStatistic _gatewaySends;
        private readonly SiloMessagingOptions _messagingOptions;
        private long _clientsCollectionVersion = 0;

        public Gateway(
            MessageCenter msgCtr, 
            ILocalSiloDetails siloDetails, 
            MessageFactory messageFactory,
            ILoggerFactory loggerFactory,
            IOptions<SiloMessagingOptions> options)
        {
            _gatewaySends = CounterStatistic.FindOrCreate(StatisticNames.GATEWAY_SENT);
            _messagingOptions = options.Value;
            _log = loggerFactory.CreateLogger<Gateway>();
            _clientCleanupAgent = new GatewayClientCleanupAgent(this, loggerFactory, _messagingOptions.ClientDropTimeout);
            clientsReplyRoutingCache = new ClientsReplyRoutingCache(_messagingOptions.ResponseTimeout);
            _gatewayAddress = siloDetails.GatewayAddress;
            _clientStateLog = loggerFactory.CreateLogger<ClientState>();
            _messageFactory = messageFactory;
            _messageCenter = msgCtr;
        }

        public static ActivationAddress GetClientActivationAddress(GrainId clientId, SiloAddress siloAddress)
        {
            // Need to pick a unique deterministic ActivationId for this client.
            // We store it in the grain directory and there for every GrainId we use ActivationId as a key
            // so every GW needs to behave as a different "activation" with a different ActivationId (its not enough that they have different SiloAddress)
            string stringToHash = clientId.ToString() + siloAddress.Endpoint + siloAddress.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Guid hash = Utils.CalculateGuidHash(stringToHash);
            var activationId = ActivationId.GetActivationId(hash);
            return ActivationAddress.GetAddress(siloAddress, clientId, activationId);
        }

        internal void Start()
        {
            _clientCleanupAgent.Start();
        }

        internal async Task SendStopSendMessages(IInternalGrainFactory grainFactory)
        {
            lock (_clients)
            {
                foreach (var client in _clients)
                {
                    if (client.Value.IsConnected)
                    {
                        var observer = ClientGatewayObserver.GetObserver(grainFactory, client.Key);
                        observer.StopSendingToGateway(this._gatewayAddress);
                    }
                }
            }
            await Task.Delay(this._messagingOptions.ClientGatewayShutdownNotificationTimeout);
        }

        internal void Stop()
        {
            _clientCleanupAgent.Stop();
        }

        long IConnectedClientCollection.Version => Interlocked.Read(ref _clientsCollectionVersion);

        List<GrainId> IConnectedClientCollection.GetConnectedClientIds()
        {
            var result = new List<GrainId>();
            foreach (var pair in _clients)
            {
                result.Add(pair.Key.GrainId);
            }

            return result;
        }

        internal void RecordOpenedConnection(GatewayInboundConnection connection, ClientGrainId clientId)
        {
            _log.LogInformation((int)ErrorCode.GatewayClientOpenedSocket, "Recorded opened connection from endpoint {EndPoint}, client ID {ClientId}.", connection.RemoteEndPoint, clientId);
            lock (_clients)
            {
                if (_clients.TryGetValue(clientId, out var clientState))
                {
                    var oldSocket = clientState.Connection;
                    if (oldSocket != null)
                    {
                        // The old socket will be closed by itself later.
                        _clientConnections.Remove(oldSocket);
                    }
                }
                else
                {
                    clientState = new ClientState(clientId, this);
                    _clients[clientId] = clientState;
                    MessagingStatisticsGroup.ConnectedClientCount.Increment();
                }
                clientState.RecordConnection(connection);
                _clientConnections[connection] = clientState;
                _clientsCollectionVersion++;
            }
        }

        internal void RecordClosedConnection(GatewayInboundConnection connection)
        {
            if (connection == null) return;

            ClientState clientState;
            lock (_clients)
            {
                if (!_clientConnections.Remove(connection, out clientState)) return;

                clientState.RecordDisconnection();
                _clientsCollectionVersion++;
            }

            _log.LogInformation(
                (int)ErrorCode.GatewayClientClosedSocket,
                "Recorded closed socket from endpoint {Endpoint}, client ID {clientId}.",
                connection.RemoteEndPoint?.ToString() ?? "null",
                clientState.Id);
        }

        internal SiloAddress TryToReroute(Message msg)
        {
            // ** Special routing rule for system target here **
            // When a client make a request/response to/from a SystemTarget, the TargetSilo can be set to either 
            //  - the GatewayAddress of the target silo (for example, when the client want get the cluster typemap)
            //  - the "internal" Silo-to-Silo address, if the client want to send a message to a specific SystemTarget
            //    activation that is on a silo on which there is no gateway available (or if the client is not
            //    connected to that gateway)
            // So, if the TargetGrain is a SystemTarget we always trust the value from Message.TargetSilo and forward
            // it to this address...
            // EXCEPT if the value is equal to the current GatewayAdress: in this case we will return
            // null and the local dispatcher will forward the Message to a local SystemTarget activation
            if (msg.TargetGrain.IsSystemTarget() && !IsTargetingLocalGateway(msg.TargetSilo))
                return msg.TargetSilo;

            // for responses from ClientAddressableObject to ClientGrain try to use clientsReplyRoutingCache for sending replies directly back.
            if (!msg.SendingGrain.IsClient() || !msg.TargetGrain.IsClient()) return null;

            if (msg.Direction != Message.Directions.Response) return null;

            SiloAddress gateway;
            return clientsReplyRoutingCache.TryFindClientRoute(msg.TargetGrain, out gateway) ? gateway : null;
        }

        internal void DropExpiredRoutingCachedEntries()
        {
            lock (_clients)
            {
                clientsReplyRoutingCache.DropExpiredEntries();
            }
        }

        private bool IsTargetingLocalGateway(SiloAddress siloAddress)
        {
            // Special case if the address used by the client was loopback
            return this._gatewayAddress.Matches(siloAddress)
                || (IPAddress.IsLoopback(siloAddress.Endpoint.Address)
                    && siloAddress.Endpoint.Port == this._gatewayAddress.Endpoint.Port
                    && siloAddress.Generation == this._gatewayAddress.Generation);
        }

        // There is NO need to acquire individual ClientState lock, since we only close an older socket.
        internal void DropDisconnectedClients()
        {
            foreach (var kv in _clients)
            {
                if (kv.Value.ReadyToDrop())
                {
                    lock (_clients)
                    {
                        if (_clients.TryGetValue(kv.Key, out var client) && client.ReadyToDrop())
                        {
                            if (_log.IsEnabled(LogLevel.Information))
                            {
                                _log.LogInformation(
                                    (int)ErrorCode.GatewayDroppingClient,
                                    "Dropping client {ClientId}, {IdleDuration} after disconnect with no reconnect",
                                    kv.Key,
                                    DateTime.UtcNow.Subtract(client.DisconnectedSince));
                            }

                            _clients.TryRemove(kv.Key, out _);
                            client.OnDropped();
                            _clientsCollectionVersion++;
                            MessagingStatisticsGroup.ConnectedClientCount.DecrementBy(1);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// See if this message is intended for a grain we're proxying, and queue it for delivery if so.
        /// </summary>
        /// <param name="msg"></param>
        /// <returns>true if the message should be delivered to a proxied grain, false if not.</returns>
        internal bool TryDeliverToProxy(Message msg)
        {
            // See if it's a grain we're proxying.
            var targetGrain = msg.TargetGrain;
            if (!ClientGrainId.TryParse(targetGrain, out var clientId))
            {
                return false;
            }

            if (!_clients.TryGetValue(clientId, out var client))
            {
                return false;
            }
            
            // When this Gateway receives a message from client X to client addressable object Y
            // it needs to record the original Gateway address through which this message came from (the address of the Gateway that X is connected to)
            // it will use this Gateway to re-route the REPLY from Y back to X.
            if (msg.SendingGrain.IsClient())
            {
                clientsReplyRoutingCache.RecordClientRoute(msg.SendingGrain, msg.SendingSilo);
            }

            msg.TargetSilo = null;
            // Override the SendingSilo only if the sending grain is not 
            // a system target
            if (!msg.SendingGrain.IsSystemTarget())
            {
                msg.SendingSilo = _gatewayAddress;
            }

            client.SendMessage(msg);
            return true;
        }

        private class ClientState
        {
#pragma warning disable IDE0052 // Remove unread private members
            /// <summary>
            /// The Task which represents the message draining process.
            /// </summary>
            private readonly Task _outgoingMessageDrainTask;
#pragma warning restore IDE0052 // Remove unread private members
            private readonly Gateway _gateway;
            private readonly SingleWaiterAutoResetEvent _signal = new() { RunContinuationsAsynchronously = true };

            private ConcurrentQueue<Message> _pendingMessages { get; } = new();
            internal GatewayInboundConnection Connection { get; private set; }
            internal DateTime DisconnectedSince { get; private set; }
            internal ClientGrainId Id { get; }
            private bool _dropped;

            public bool IsConnected => this.Connection != null;

            internal ClientState(ClientGrainId id, Gateway gateway)
            {
                Id = id;
                _gateway = gateway;
                _outgoingMessageDrainTask = Task.Run(DrainOutgoingMessages);
            }

            internal void SendMessage(Message message)
            {
                if (Connection is { IsValid: true } connection)
                {
                    // Send the message immediately, without waking the pending message processor loop
                    connection.Send(message);
                }
                else
                {
                    _pendingMessages.Enqueue(message);
                    _signal.Signal();

                    if (_gateway._clientStateLog.IsEnabled(LogLevel.Trace))
                    {
                        _gateway._clientStateLog.Trace("Queued message {Message} for client {ClientId}", message, Id);
                    }
                }
            }

            internal void RecordDisconnection()
            {
                if (Connection == null) return;

                DisconnectedSince = DateTime.UtcNow;
                Connection = null;
                _signal.Signal();
            }

            internal void RecordConnection(GatewayInboundConnection connection)
            {
                Connection = connection;
                DisconnectedSince = DateTime.MaxValue;
                _signal.Signal();
            }

            internal bool ReadyToDrop()
            {
                return !IsConnected && DateTime.UtcNow.Subtract(DisconnectedSince) >= _gateway._messagingOptions.ClientDropTimeout;
            }

            internal void OnDropped()
            {
                _dropped = true;
                _signal.Signal();
            }

            private async Task DrainOutgoingMessages()
            {
                while (true)
                {
                    Message message = null;
                    try
                    {
                        await _signal.WaitAsync();
                        while (_pendingMessages.TryDequeue(out message))
                        {
                            if (Connection is { IsValid: true } connection)
                            {
                                connection.Send(message);
                                message = null;
                            }
                            else if (_dropped)
                            {
                                // Most likely, the messages have all expired by now, but this allows for consistent and centralized handling.
                                _gateway._messageCenter.RerouteMessage(message);
                                message = null;
                            }
                            else
                            {
                                // Re-enqueue the message on the back of the queue.
                                // Message ordering is not guaranteed.
                                _pendingMessages.Enqueue(message);
                                message = null;

                                // Wait for something to change, such as a reconnection.
                                break;
                            }
                        }
                    }
                    catch (Exception exception)
                    {
                        if (message is not null)
                        {
                            _gateway._clientStateLog.LogError(exception, "Error in outbound client message send loop while sending message {Message}", message);
                        }
                        else
                        {
                            _gateway._clientStateLog.LogError(exception, "Error in outbound client message send loop");
                        }
                    }
                }
            }
        }

        private class GatewayClientCleanupAgent : TaskSchedulerAgent
        {
            private readonly Gateway gateway;
            private readonly TimeSpan clientDropTimeout;

            internal GatewayClientCleanupAgent(Gateway gateway, ILoggerFactory loggerFactory, TimeSpan clientDropTimeout)
                : base(loggerFactory)
            {
                this.gateway = gateway;
                this.clientDropTimeout = clientDropTimeout;
            }

            protected override async Task Run()
            {
                while (!Cts.IsCancellationRequested)
                {
                    gateway.DropDisconnectedClients();
                    gateway.DropExpiredRoutingCachedEntries();
                    await Task.Delay(clientDropTimeout);
                }
            }
        }

        // this cache is used to record the addresses of Gateways from which clients connected to.
        // it is used to route replies to clients from client addressable objects
        // without this cache this Gateway will not know how to route the reply back to the client 
        // (since clients are not registered in the directory and this Gateway may not be proxying for the client for whom the reply is destined).
        private class ClientsReplyRoutingCache
        {
            // for every client: the Gateway to use to route repies back to it plus the last time that client connected via this Gateway.
            private readonly ConcurrentDictionary<GrainId, Tuple<SiloAddress, DateTime>> clientRoutes = new();
            private readonly TimeSpan TIME_BEFORE_ROUTE_CACHED_ENTRY_EXPIRES;

            internal ClientsReplyRoutingCache(TimeSpan responseTimeout)
            {
                TIME_BEFORE_ROUTE_CACHED_ENTRY_EXPIRES = responseTimeout.Multiply(5);
            }

            internal void RecordClientRoute(GrainId client, SiloAddress gateway)
            {
                clientRoutes[client] = new(gateway, DateTime.UtcNow);
            }

            internal bool TryFindClientRoute(GrainId client, out SiloAddress gateway)
            {
                if (clientRoutes.TryGetValue(client, out var tuple))
                {
                    gateway = tuple.Item1;
                    return true;
                }

                gateway = null;
                return false;
            }

            internal void DropExpiredEntries()
            {
                var expiredTime = DateTime.UtcNow - TIME_BEFORE_ROUTE_CACHED_ENTRY_EXPIRES;
                foreach (var client in clientRoutes)
                {
                    if (client.Value.Item2 < expiredTime)
                    {
                        clientRoutes.TryRemove(client.Key, out _);
                    }
                }
            }
        }
    }
}
