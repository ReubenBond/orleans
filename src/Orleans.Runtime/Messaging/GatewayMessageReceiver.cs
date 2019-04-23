using System;
using System.Runtime.ExceptionServices;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace Orleans.Runtime.Messaging
{
    internal sealed class GatewayMessageReceiver : ConnectionMessageReceiver
    {
        private readonly MessageCenter messageCenter;
        private readonly ILocalSiloDetails siloDetails;
        private readonly MultiClusterOptions multiClusterOptions;
        private readonly CounterStatistic loadSheddingCounter;
        private readonly CounterStatistic gatewayTrafficCounter;
        private readonly OverloadDetector overloadDetector;
        private readonly MessageFactory messageFactory;
        private readonly ILogger<GatewayMessageReceiver> log;

        public GatewayMessageReceiver(
            ConnectionContext connection,
            IMessageSerializerFactory serializerFactory,
            MessageCenter messageCenter,
            ILocalSiloDetails siloDetails,
            OverloadDetector overloadDetector,
            IOptions<MultiClusterOptions> multiClusterOptions,
            MessageFactory messageFactory,
            ILogger<GatewayMessageReceiver> log)
            : base(connection, serializerFactory.GetSerializer(true), log)
        {
            this.messageCenter = messageCenter;
            this.siloDetails = siloDetails;
            this.overloadDetector = overloadDetector;
            this.messageFactory = messageFactory;
            this.log = log;
            this.multiClusterOptions = multiClusterOptions.Value;
            this.loadSheddingCounter = CounterStatistic.FindOrCreate(StatisticNames.GATEWAY_LOAD_SHEDDING);
            this.gatewayTrafficCounter = CounterStatistic.FindOrCreate(StatisticNames.GATEWAY_RECEIVED);
        }

        private Gateway Gateway => this.messageCenter.Gateway;

        protected override void OnReceivedMessage(Message msg)
        {
            // Don't process messages that have already timed out
            if (msg.IsExpired)
            {
                msg.DropExpiredMessage(MessagingStatisticsGroup.Phase.Receive);
                return;
            }

            gatewayTrafficCounter.Increment();

            // return address translation for geo clients (replace sending address cli/* with gcl/*)
            if (this.multiClusterOptions.HasMultiClusterNetwork && msg.SendingAddress.Grain.Category != UniqueKey.Category.GeoClient)
            {
                msg.SendingGrain = GrainId.NewClientId(msg.SendingAddress.Grain.PrimaryKey, this.siloDetails.ClusterId);
            }

            // Are we overloaded?
            if (this.overloadDetector.Overloaded)
            {
                MessagingStatisticsGroup.OnRejectedMessage(msg);
                Message rejection = this.messageFactory.CreateRejectionResponse(msg, Message.RejectionTypes.GatewayTooBusy, "Shedding load");
                this.messageCenter.TryDeliverToProxy(rejection);
                if (this.log.IsEnabled(LogLevel.Debug)) this.log.Debug("Rejecting a request due to overloading: {0}", msg.ToString());
                loadSheddingCounter.Increment();
                return;
            }

            SiloAddress targetAddress = Gateway?.TryToReroute(msg);
            msg.SendingSilo = this.messageCenter.MyAddress;

            if (targetAddress == null)
            {
                // reroute via Dispatcher
                msg.TargetSilo = null;
                msg.TargetActivation = null;
                msg.ClearTargetAddress();

                if (msg.TargetGrain.IsSystemTarget)
                {
                    msg.TargetSilo = this.messageCenter.MyAddress;
                    msg.TargetActivation = ActivationId.GetSystemActivation(msg.TargetGrain, this.messageCenter.MyAddress);
                }

                MessagingStatisticsGroup.OnMessageReRoute(msg);
                this.messageCenter.RerouteMessage(msg);
            }
            else
            {
                // send directly
                msg.TargetSilo = targetAddress;
                this.messageCenter.SendMessage(msg);
            }
        }

        protected override void OnReceiveMessageFail(Message message, Exception exception)
        {
            var msg = message;
            // If deserialization completely failed or the message was one-way, rethrow the exception
            // so that it can be handled at another level.
            if (msg?.Headers == null || msg.Direction != Message.Directions.Request)
            {
                ExceptionDispatchInfo.Capture(exception).Throw();
            }

            // The message body was not successfully decoded, but the headers were.
            // Send a fast fail to the caller.
            MessagingStatisticsGroup.OnRejectedMessage(msg);
            var response = this.messageFactory.CreateResponseMessage(msg);
            response.Result = Message.ResponseTypes.Error;
            response.BodyObject = Response.ExceptionResponse(exception);

            // Send the error response and continue processing the next message.
            this.messageCenter.SendMessage(response);
        }
    }
}
