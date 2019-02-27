using System;
using System.Runtime.ExceptionServices;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime.Messaging
{
    internal sealed class SiloMessageReceiver : ConnectionMessageReceiver
    {
        private readonly MessageCenter messageCenter;
        private readonly ILogger<SiloMessageReceiver> log;
        private readonly MessageFactory messageFactory;

        public SiloMessageReceiver(ConnectionContext connection, IMessageSerializer serializer, MessageCenter messageCenter, MessageFactory messageFactory, ILogger<SiloMessageReceiver> log) : base(connection, serializer)
        {
            this.messageCenter = messageCenter;
            this.messageFactory = messageFactory;
            this.log = log;
        }

        protected override void OnReceivedMessage(Message msg)
        {
            // See it's a Ping message, and if so, short-circuit it
            var requestContext = msg.RequestContextData;
            if (requestContext != null &&
                requestContext.TryGetValue(RequestContext.PING_APPLICATION_HEADER, out var pingObj) &&
                pingObj is bool &&
                (bool)pingObj)
            {
                MessagingStatisticsGroup.OnPingReceive(msg.SendingSilo);

                if (log.IsEnabled(LogLevel.Trace)) log.Trace("Responding to Ping from {0}", msg.SendingSilo);

                if (!msg.TargetSilo.Equals(messageCenter.MyAddress)) // got ping that is not destined to me. For example, got a ping to my older incarnation.
                {
                    MessagingStatisticsGroup.OnRejectedMessage(msg);
                    Message rejection = this.messageFactory.CreateRejectionResponse(msg, Message.RejectionTypes.Unrecoverable,
                        $"The target silo is no longer active: target was {msg.TargetSilo.ToLongString()}, but this silo is {messageCenter.MyAddress.ToLongString()}. " +
                        $"The rejected ping message is {msg}.");
                    messageCenter.OutboundQueue.SendMessage(rejection);
                }
                else
                {
                    var response = this.messageFactory.CreateResponseMessage(msg);
                    response.BodyObject = Response.Done;
                    this.messageCenter.SendMessage(response);
                }
                return;
            }

            // sniff message headers for directory cache management
            this.messageCenter.SniffIncomingMessage?.Invoke(msg);

            // Don't process messages that have already timed out
            if (msg.IsExpired)
            {
                msg.DropExpiredMessage(MessagingStatisticsGroup.Phase.Receive);
                return;
            }

            // If we've stopped application message processing, then filter those out now
            // Note that if we identify or add other grains that are required for proper stopping, we will need to treat them as we do the membership table grain here.
            if (messageCenter.IsBlockingApplicationMessages && (msg.Category == Message.Categories.Application) && !Constants.SystemMembershipTableId.Equals(msg.SendingGrain))
            {
                // We reject new requests, and drop all other messages
                if (msg.Direction != Message.Directions.Request) return;

                MessagingStatisticsGroup.OnRejectedMessage(msg);
                var reject = this.messageFactory.CreateRejectionResponse(msg, Message.RejectionTypes.Unrecoverable, "Silo stopping");
                this.messageCenter.SendMessage(reject);
                return;
            }

            // Make sure the message is for us. Note that some control messages may have no target
            // information, so a null target silo is OK.
            if ((msg.TargetSilo == null) || msg.TargetSilo.Matches(messageCenter.MyAddress))
            {
                // See if it's a message for a client we're proxying.
                if (messageCenter.IsProxying && messageCenter.TryDeliverToProxy(msg)) return;

                // Nope, it's for us
                messageCenter.InboundQueue.PostMessage(msg);
                return;
            }

            if (!msg.TargetSilo.Endpoint.Equals(messageCenter.MyAddress.Endpoint))
            {
                // If the message is for some other silo altogether, then we need to forward it.
                if (log.IsEnabled(LogLevel.Trace)) log.Trace("Forwarding message {0} from {1} to silo {2}", msg.Id, msg.SendingSilo, msg.TargetSilo);
                messageCenter.OutboundQueue.SendMessage(msg);
                return;
            }

            // If the message was for this endpoint but an older epoch, then reject the message
            // (if it was a request), or drop it on the floor if it was a response or one-way.
            if (msg.Direction == Message.Directions.Request)
            {
                MessagingStatisticsGroup.OnRejectedMessage(msg);
                Message rejection = this.messageFactory.CreateRejectionResponse(msg, Message.RejectionTypes.Transient,
                    string.Format("The target silo is no longer active: target was {0}, but this silo is {1}. The rejected message is {2}.",
                        msg.TargetSilo.ToLongString(), messageCenter.MyAddress.ToLongString(), msg));

                // Invalidate the remote caller's activation cache entry.
                if (msg.TargetAddress != null) rejection.AddToCacheInvalidationHeader(msg.TargetAddress);

                messageCenter.OutboundQueue.SendMessage(rejection);
                if (log.IsEnabled(LogLevel.Debug)) log.Debug("Rejecting an obsolete request; target was {0}, but this silo is {1}. The rejected message is {2}.",
                    msg.TargetSilo.ToLongString(), messageCenter.MyAddress.ToLongString(), msg);
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
