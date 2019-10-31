using System;
using System.Diagnostics;
namespace Orleans.Runtime
{
    internal class MessagingTrace : DiagnosticListener
    {
        public const string Category = "Microsoft.Orleans.Message";

        public const string CreateMessageActivityName = Category + ".Create";
        public const string SendMessageActivityName = Category + ".Send";
        public const string ReceiveMessageActivityName = Category + ".Receive";
        public const string DropMessageActivityName = Category + ".Drop";
        public const string EnqueueInboundMessageActivityName = Category + ".Inbound.Enqueue";
        public const string DequeueInboundMessageActivityName = Category + ".Inbound.Dequeue";
        public const string ScheduleMessageActivityName = Category + ".Schedule";
        public const string EnqueueMessageOnActivationActivityName = Category + ".Activation.Enqueue";
        public const string InvokeMessageActivityName = Category + ".Invoke";

        public MessagingTrace() : base(Category)
        {
        }

        public void OnSendMessage(Message message)
        {
            if (this.IsEnabled(SendMessageActivityName))
            {
                this.Write(SendMessageActivityName, message);
            }
        }

        public void OnReceiveMessage(Message message)
        {
            if (this.IsEnabled(ReceiveMessageActivityName))
            {
                this.Write(ReceiveMessageActivityName, message);
            }
        }

        internal void OnDropMessage(Message message)
        {
            if (this.IsEnabled(DropMessageActivityName))
            {
                this.Write(DropMessageActivityName, message);
            }
        }

        public void OnEnqueueInboundMessage(Message message)
        {
            if (this.IsEnabled(EnqueueInboundMessageActivityName))
            {
                this.Write(EnqueueInboundMessageActivityName, message);
            }
        }

        public void OnDequeueInboundMessage(Message message)
        {
            if (this.IsEnabled(DequeueInboundMessageActivityName))
            {
                this.Write(DequeueInboundMessageActivityName, message);
            }
        }

        internal void OnCreateMessage(Message message)
        {
            if (this.IsEnabled(CreateMessageActivityName))
            {
                this.Write(CreateMessageActivityName, message);
            }
        }

        public void OnScheduleMessage(Message message)
        {
            if (this.IsEnabled(ScheduleMessageActivityName))
            {
                this.Write(ScheduleMessageActivityName, message);
            }
        }

        public void OnEnqueueMessageOnActivation(Message message)
        {
            if (this.IsEnabled(EnqueueMessageOnActivationActivityName))
            {
                this.Write(EnqueueMessageOnActivationActivityName, message);
            }
        }

        public void OnInvokeMessage(Message message)
        {
            if (this.IsEnabled(InvokeMessageActivityName))
            {
                this.Write(InvokeMessageActivityName, message);
            }
        }
    }
}
