using System;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Logging;
using Orleans.Serialization;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using System.Threading.Tasks;

namespace Orleans.Runtime.Messaging
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Naming", "CA1711:IdentifiersShouldNotHaveIncorrectSuffix")]
    internal sealed class OutboundMessageQueue : IOutboundMessageQueue
    {
        private readonly MessageCenter messageCenter;
        private readonly ConnectionMessageSenderManager senderManager;
        private readonly ILogger logger;
        private bool stopped;

        public int GetCount()
        {
            int n = GetApplicationMessageCount();
            return n; // TODO
        }

        public int GetApplicationMessageCount()
        {
            return 0; // TODO
        }

        internal const string QUEUED_TIME_METADATA = "QueuedTime";

        internal OutboundMessageQueue(MessageCenter mc, IOptions<SiloMessagingOptions> options, SerializationManager serializationManager, ExecutorService executorService, ILoggerFactory loggerFactory, ConnectionMessageSenderManager senderManager)
        {
            messageCenter = mc;
            this.senderManager = senderManager;
            logger = loggerFactory.CreateLogger<OutboundMessageQueue>();
            stopped = false;
        }

        public void SendMessage(Message msg)
        {
            if (msg == null) throw new ArgumentNullException("msg", "Can't send a null message.");

            if (stopped)
            {
                logger.Info(ErrorCode.Runtime_Error_100112, "Message was queued for sending after outbound queue was stopped: {0}", msg);
                return;
            }

            // Don't process messages that have already timed out
            if (msg.IsExpired)
            {
                msg.DropExpiredMessage(MessagingStatisticsGroup.Phase.Send);
                return;
            }

            if (!msg.QueuedTime.HasValue)
            {
                msg.QueuedTime = DateTime.UtcNow;
            }

            // First check to see if it's really destined for a proxied client, instead of a local grain.
            if (messageCenter.IsProxying && messageCenter.TryDeliverToProxy(msg))
            {
                return;
            }

            if (msg.TargetSilo == null)
            {
                logger.Error(ErrorCode.Runtime_Error_100113, "Message does not have a target silo: " + msg + " -- Call stack is: " + Utils.GetStackTrace());
                messageCenter.SendRejection(msg, Message.RejectionTypes.Unrecoverable, "Message to be sent does not have a target silo");
                return;
            }
            
            if(!messageCenter.TrySendLocal(msg))
            {
                if (stopped)
                {
                    logger.Info(ErrorCode.Runtime_Error_100115, "Message was queued for sending after outbound queue was stopped: {0}", msg);
                    return;
                }

                // check for simulation of lost messages
                if(messageCenter?.ShouldDrop?.Invoke(msg) == true)
                {
                    logger.Info(ErrorCode.Messaging_SimulatedMessageLoss, "Message blocked by test");
                    messageCenter.SendRejection(msg, Message.RejectionTypes.Unrecoverable, "Message blocked by test");
                    return;
                }

                var senderTask = this.senderManager.GetConnection(msg.TargetSilo.Endpoint.ToString());
                if (senderTask.Status == TaskStatus.RanToCompletion)
                {
                    var sender = senderTask.GetAwaiter().GetResult();
                    sender.Send(msg);
                }
                else
                {
                    _ = SendAsync(senderTask, msg);

                    async Task SendAsync(Task<ConnectionMessageSender> task, Message m)
                    {
                        try
                        {
                            var sender = await task;
                            sender.Send(msg);
                        }
                        catch
                        {
                            this.messageCenter.RerouteMessage(m);
                        }
                    }
                }
            }
        }

        public void Start()
        {
            stopped = false;
        }

        public void Stop()
        {
            stopped = true;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Usage", "CA1816:CallGCSuppressFinalizeCorrectly")]
        public void Dispose()
        {
            stopped = true;
            GC.SuppressFinalize(this);
        }
    }
}
