using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Messaging;
using Orleans.Serialization;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;
using System.Threading.Channels;

namespace Orleans.Runtime.Messaging
{
    internal class MessageCenter : ISiloMessageCenter, IDisposable
    {
        public Gateway Gateway { get; set; }
        private readonly ILogger log;
        private Action<Message> rerouteHandler;
        internal Func<Message, bool> ShouldDrop;
        private IHostedClient hostedClient;
        private Action<Message> sniffIncomingMessageHandler;

        internal OutboundMessageQueue OutboundQueue { get; set; }
        internal InboundMessageQueue InboundQueue { get; set; }
        internal SocketManager SocketManager;
        private readonly MessageFactory messageFactory;
        private readonly ILoggerFactory loggerFactory;
        private readonly ConnectionManager senderManager;
        private readonly Action<Message>[] messageHandlers;
        private SiloMessagingOptions messagingOptions;
        internal bool IsBlockingApplicationMessages { get; private set; }

        public void SetHostedClient(IHostedClient client) => this.hostedClient = client;

        public bool IsProxying => this.Gateway != null || this.hostedClient?.ClientId != null;

        public bool TryDeliverToProxy(Message msg)
        {
            if (!msg.TargetGrain.IsClient) return false;
            if (this.Gateway != null && this.Gateway.TryDeliverToProxy(msg)) return true;
            return this.hostedClient?.TryDispatchToClient(msg) ?? false;
        }
        
        // This is determined by the IMA but needed by the OMS, and so is kept here in the message center itself.
        public SiloAddress MyAddress { get; private set; }

        public MessageCenter(
            ILocalSiloDetails siloDetails,
            IOptions<SiloMessagingOptions> messagingOptions,
            IOptions<NetworkingOptions> networkingOptions,
            MessageFactory messageFactory,
            Factory<MessageCenter, Gateway> gatewayFactory,
            ILoggerFactory loggerFactory,
            IOptions<StatisticsOptions> statisticsOptions,
            ConnectionManager senderManager)
        {
            this.messagingOptions = messagingOptions.Value;
            this.loggerFactory = loggerFactory;
            this.senderManager = senderManager;
            this.log = loggerFactory.CreateLogger<MessageCenter>();
            this.messageFactory = messageFactory;
            this.MyAddress = siloDetails.SiloAddress;
            this.Initialize(networkingOptions, statisticsOptions);
            if (siloDetails.GatewayAddress != null)
            {
                Gateway = gatewayFactory(this);
            }

            messageHandlers = new Action<Message>[Enum.GetValues(typeof(Message.Categories)).Length];
        }

        private void Initialize(IOptions<NetworkingOptions> networkingOptions, IOptions<StatisticsOptions> statisticsOptions)
        {
            if (log.IsEnabled(LogLevel.Trace)) log.Trace("Starting initialization.");

            SocketManager = new SocketManager(networkingOptions, this.loggerFactory);
            InboundQueue = new InboundMessageQueue(this.loggerFactory.CreateLogger<InboundMessageQueue>(), statisticsOptions);
            OutboundQueue = new OutboundMessageQueue(this, this.loggerFactory.CreateLogger<OutboundMessageQueue>(), this.senderManager);

            if (log.IsEnabled(LogLevel.Trace)) log.Trace("Completed initialization.");
        }

        public void Start()
        {
            IsBlockingApplicationMessages = false;
            OutboundQueue.Start();
        }

        public void StartGateway(ClientObserverRegistrar clientRegistrar)
        {
            if (Gateway != null)
                Gateway.Start(clientRegistrar);
        }

        public void PrepareToStop()
        {
        }

        private void WaitToRerouteAllQueuedMessages()
        {
            DateTime maxWaitTime = DateTime.UtcNow + this.messagingOptions.ShutdownRerouteTimeout;
            while (DateTime.UtcNow < maxWaitTime)
            {
                var applicationMessageQueueLength = this.OutboundQueue.GetApplicationMessageCount();
                if (applicationMessageQueueLength == 0)
                    break;
                Thread.Sleep(100);
            }
            
        }

        public void Stop()
        {
            IsBlockingApplicationMessages = true;
            
            StopAcceptingClientMessages();

            try
            {
                WaitToRerouteAllQueuedMessages();
                OutboundQueue.Stop();
            }
            catch (Exception exc)
            {
                log.Error(ErrorCode.Runtime_Error_100110, "Stop failed.", exc);
            }

            try
            {
                SocketManager.Stop();
            }
            catch (Exception exc)
            {
                log.Error(ErrorCode.Runtime_Error_100111, "Stop failed.", exc);
            }
        }

        public void StopAcceptingClientMessages()
        {
            if (log.IsEnabled(LogLevel.Debug)) log.Debug("StopClientMessages");
            if (Gateway == null) return;

            try
            {
                Gateway.Stop();
            }
            catch (Exception exc) { log.Error(ErrorCode.Runtime_Error_100109, "Stop failed.", exc); }
            Gateway = null;
        }

        public Action<Message> RerouteHandler
        {
            set
            {
                if (rerouteHandler != null)
                    throw new InvalidOperationException("MessageCenter RerouteHandler already set");
                rerouteHandler = value;
            }
        }

        public void OnReceivedMessage(Message message)
        {
            var handler = this.messageHandlers[(int)message.Category];
            if (handler != null)
            {
                handler(message);
            }
            else
            {
                this.InboundQueue.PostMessage(message);
            }
        }

        public void RerouteMessage(Message message)
        {
            if (rerouteHandler != null)
                rerouteHandler(message);
            else
                SendMessage(message);
        }

        public Action<Message> SniffIncomingMessage
        {
            set
            {
                if (this.sniffIncomingMessageHandler != null)
                    throw new InvalidOperationException("IncomingMessageAcceptor SniffIncomingMessage already set");

                this.sniffIncomingMessageHandler = value;
            }

            get => this.sniffIncomingMessageHandler;
        }

        public Func<SiloAddress, bool> SiloDeadOracle { get; set; }

        public void SendMessage(Message msg)
        {
            // Note that if we identify or add other grains that are required for proper stopping, we will need to treat them as we do the membership table grain here.
            if (IsBlockingApplicationMessages && (msg.Category == Message.Categories.Application) && (msg.Result != Message.ResponseTypes.Rejection)
                && !Constants.SystemMembershipTableId.Equals(msg.TargetGrain))
            {
                // Drop the message on the floor if it's an application message that isn't a rejection
            }
            else
            {
                if (msg.SendingSilo == null)
                    msg.SendingSilo = MyAddress;
                OutboundQueue.SendMessage(msg);
            }
        }

        public bool TrySendLocal(Message message)
        {
            if (!message.TargetSilo.Matches(MyAddress))
            {
                return false;
            }

            if (log.IsEnabled(LogLevel.Trace)) log.Trace("Message has been looped back to this silo: {0}", message);
            MessagingStatisticsGroup.LocalMessagesSent.Increment();
            this.OnReceivedMessage(message);

            return true;
        }

        internal void SendRejection(Message msg, Message.RejectionTypes rejectionType, string reason)
        {
            MessagingStatisticsGroup.OnRejectedMessage(msg);
            if (string.IsNullOrEmpty(reason)) reason = string.Format("Rejection from silo {0} - Unknown reason.", MyAddress);
            Message error = this.messageFactory.CreateRejectionResponse(msg, rejectionType, reason);
            // rejection msgs are always originated in the local silo, they are never remote.
            this.OnReceivedMessage(error);
        }

        public ChannelReader<Message> GetReader(Message.Categories type) => InboundQueue.GetReader(type);


        public void RegisterLocalMessageHandler(Message.Categories category, Action<Message> handler)
        {
            messageHandlers[(int) category] = handler;
        }

        public void Dispose()
        {
            InboundQueue?.Dispose();
            OutboundQueue?.Dispose();

            GC.SuppressFinalize(this);
        }

        public int SendQueueLength { get { return OutboundQueue.GetCount(); } }

        public int ReceiveQueueLength { get { return InboundQueue.Count; } }

        /// <summary>
        /// Indicates that application messages should be blocked from being sent or received.
        /// This method is used by the "fast stop" process.
        /// <para>
        /// Specifically, all outbound application messages are dropped, except for rejections and messages to the membership table grain.
        /// Inbound application requests are rejected, and other inbound application messages are dropped.
        /// </para>
        /// </summary>
        public void BlockApplicationMessages()
        {
            if(log.IsEnabled(LogLevel.Debug)) log.Debug("BlockApplicationMessages");
            IsBlockingApplicationMessages = true;
        }

        public bool PrepareMessageForSend(Message msg)
        {
            // Don't send messages that have already timed out
            if (msg.IsExpired)
            {
                msg.DropExpiredMessage(MessagingStatisticsGroup.Phase.Send);
                return false;
            }

            // Fill in the outbound message with our silo address, if it's not already set
            if (msg.SendingSilo == null)
                msg.SendingSilo = this.MyAddress;


            // If there's no target silo set, then we shouldn't see this message; send it back
            if (msg.TargetSilo == null)
            {
                FailMessage(msg, "No target silo provided -- internal error");
                return false;
            }

            // If we know this silo is dead, don't bother
            if ((this.SiloDeadOracle != null) && this.SiloDeadOracle(msg.TargetSilo))
            {
                FailMessage(msg, String.Format("Target {0} silo is known to be dead", msg.TargetSilo.ToLongString()));
                return false;
            }
            
            return true;
        }

        public void FailMessage(Message msg, string reason)
        {
            MessagingStatisticsGroup.OnFailedSentMessage(msg);
            if (msg.Direction == Message.Directions.Request)
            {
                if (this.log.IsEnabled(LogLevel.Debug)) this.log.Debug(ErrorCode.MessagingSendingRejection, "Silo {siloAddress} is rejecting message: {message}. Reason = {reason}", this.MyAddress, msg, reason);
                // Done retrying, send back an error instead
                this.SendRejection(msg, Message.RejectionTypes.Transient, String.Format("Silo {0} is rejecting message: {1}. Reason = {2}", this.MyAddress, msg, reason));
            }
            else
            {
                this.log.Info(ErrorCode.Messaging_OutgoingMS_DroppingMessage, "Silo {siloAddress} is dropping message: {message}. Reason = {reason}", this.MyAddress, msg, reason);
                MessagingStatisticsGroup.OnDroppedSentMessage(msg);
            }
        }

        public void OnMessageSerializationFailure(Message msg, Exception exc)
        {
            // we only get here if we failed to serialize the msg (or any other catastrophic failure).
            // Request msg fails to serialize on the sending silo, so we just enqueue a rejection msg.
            // Response msg fails to serialize on the responding silo, so we try to send an error response back.
            this.log.LogWarning(
                (int)ErrorCode.MessagingUnexpectedSendError,
                "Unexpected error serializing message {Message}: {Exception}",
                msg,
                exc);

            MessagingStatisticsGroup.OnFailedSentMessage(msg);

            var retryCount = msg.RetryCount ?? 0;

            if (msg.Direction == Message.Directions.Request)
            {
                this.SendRejection(msg, Message.RejectionTypes.Unrecoverable, exc.ToString());
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
                this.log.LogWarning(
                    (int)ErrorCode.Messaging_OutgoingMS_DroppingMessage,
                    "Silo {SiloAddress} is dropping message which failed during serialization: {Message}. Exception = {Exception}",
                    this.MyAddress,
                    msg,
                    exc);

                MessagingStatisticsGroup.OnDroppedSentMessage(msg);
            }
        }
    }
}
