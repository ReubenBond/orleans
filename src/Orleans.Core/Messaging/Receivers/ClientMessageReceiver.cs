using System;
using System.Runtime.ExceptionServices;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;
using Orleans.Messaging;

namespace Orleans.Runtime.Messaging
{
    internal sealed class ClientMessageReceiver : ConnectionMessageReceiver
    {
        private readonly ClientMessageCenter messageCenter;
        private readonly ILogger<ClientMessageReceiver> log;
        private readonly MessageFactory messageFactory;

        public ClientMessageReceiver(
            ConnectionContext connection,
            IMessageSerializer serializer,
            ClientMessageCenter messageCenter,
            ILogger<ClientMessageReceiver> log,
            MessageFactory messageFactory) : base(connection, serializer)
        {
            this.messageCenter = messageCenter;
            this.log = log;
            this.messageFactory = messageFactory;
        }

        protected override void OnReceivedMessage(Message message) => this.messageCenter.OnReceivedMessage(message);

        protected override void OnReceiveMessageFail(Message message, Exception exception)
        {
            // If deserialization completely failed or the message was one-way, rethrow the exception
            // so that it can be handled at another level.
            if (message?.Headers == null || message.Direction != Message.Directions.Request)
            {
                ExceptionDispatchInfo.Capture(exception).Throw();
            }

            // The message body was not successfully decoded, but the headers were.
            // Send a fast fail to the caller.
            MessagingStatisticsGroup.OnRejectedMessage(message);
            var response = this.messageFactory.CreateResponseMessage(message);
            response.Result = Message.ResponseTypes.Error;
            response.BodyObject = Response.ExceptionResponse(exception);

            // Send the error response and continue processing the next message.
            this.messageCenter.SendMessage(response);

            //this.log.LogWarning((int)ErrorCode.ProxyClientUnhandledExceptionWhileReceiving, exception, $"Unexpected/unhandled exception while receiving: {exception}. Restarting gateway receiver for connection {this.Connection.ConnectionId}.");
        }
    }
}
