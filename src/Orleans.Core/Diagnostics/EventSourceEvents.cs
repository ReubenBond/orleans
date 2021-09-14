using System;
using System.Diagnostics.Tracing;
using System.Threading;

namespace Orleans.Runtime
{
    [EventSource(Name = "Microsoft-Orleans-CallBackData")]
    internal sealed class OrleansCallBackDataEvent : EventSource
    {
        public static readonly OrleansCallBackDataEvent Log = new OrleansCallBackDataEvent();

        [NonEvent]
        public void OnTimeout(Message message)
        {
            if (this.IsEnabled())
            {
                this.OnTimeout();
            }
        }

        [Event(1, Level = EventLevel.Warning)]
        private void OnTimeout() => this.WriteEvent(1);

        [NonEvent]
        public void OnTargetSiloFail(Message message)
        {
            if (this.IsEnabled())
            {
                this.OnTargetSiloFail();
            }
        }

        [Event(2, Level = EventLevel.Warning)]
        private void OnTargetSiloFail() => this.WriteEvent(2);

        [NonEvent]
        public void DoCallback(Message message)
        {
            if (this.IsEnabled())
            {
                this.DoCallback();
            }
        }

        [Event(3, Level = EventLevel.Verbose)]
        private void DoCallback() => this.WriteEvent(3);
    }

    [EventSource(Name = "Microsoft-Orleans-OutsideRuntimeClient")]
    internal sealed class OrleansOutsideRuntimeClientEvent : EventSource
    {
        public static readonly OrleansOutsideRuntimeClientEvent Log = new OrleansOutsideRuntimeClientEvent();

        [NonEvent]
        public void SendRequest(Message message)
        {
            if (this.IsEnabled())
            {
                this.SendRequest();
            }
        }

        [Event(1, Level = EventLevel.Verbose)]
        private void SendRequest() => this.WriteEvent(1);

        [NonEvent]
        public void ReceiveResponse(Message message)
        {
            if (this.IsEnabled())
            {
                this.ReceiveResponse();
            }
        }

        [Event(2, Level = EventLevel.Verbose)]
        private void ReceiveResponse() => this.WriteEvent(2);

        [NonEvent]
        public void SendResponse(Message message)
        {
            if (this.IsEnabled())
            {
                this.SendResponse();
            }
        }

        [Event(3, Level = EventLevel.Verbose)]
        private void SendResponse() => this.WriteEvent(3);
    }

    [EventSource(Name = "Microsoft-Orleans-Dispatcher")]
    internal sealed class OrleansDispatcherEvent : EventSource
    {
        public static readonly OrleansDispatcherEvent Log = new OrleansDispatcherEvent();

        [NonEvent]
        public void ReceiveMessage(Message message)
        {
            if (this.IsEnabled())
            {
                this.ReceiveMessage();
            }
        }

        [Event(1, Level = EventLevel.Verbose)]
        private void ReceiveMessage() => WriteEvent(1);
    }

    [EventSource(Name = "Microsoft-Orleans-InsideRuntimeClient")]
    internal sealed class OrleansInsideRuntimeClientEvent : EventSource
    {
        public static readonly OrleansInsideRuntimeClientEvent Log = new OrleansInsideRuntimeClientEvent();

        [NonEvent]
        public void SendRequest(Message message)
        {
            if (this.IsEnabled())
            {
                this.SendRequest();
            }
        }

        [Event(1, Level = EventLevel.Verbose)]
        private void SendRequest() => WriteEvent(1);

        [NonEvent]
        public void ReceiveResponse(Message message)
        {
            if (this.IsEnabled())
            {
                this.ReceiveResponse();
            }
        }

        [Event(2, Level = EventLevel.Verbose)]
        private void ReceiveResponse() => WriteEvent(2);

        [NonEvent]
        public void SendResponse(Message message)
        {
            if (this.IsEnabled())
            {
                this.SendResponse();
            }
        }

        [Event(3, Level = EventLevel.Verbose)]
        private void SendResponse() => WriteEvent(3);
    }

    [EventSource(Name = "Microsoft-Orleans-IncomingMessageAgent")]
    internal sealed class OrleansIncomingMessageAgentEvent : EventSource
    {
        public static readonly OrleansIncomingMessageAgentEvent Log = new OrleansIncomingMessageAgentEvent();

        [NonEvent]
        public void ReceiveMessage(Message message)
        {
            if (this.IsEnabled())
            {
                this.ReceiveMessage();
            }
        }

        [Event(1, Level = EventLevel.Verbose)]
        private void ReceiveMessage() => WriteEvent(1);
    }

    [EventSource(Name = "Orleans.Messaging")]
    internal sealed class MessagingEventSource : EventSource
    {
        public static readonly MessagingEventSource Log = new ();
        private long _numActiveConnections;
        private long _numSentRemoteMessages;
        private long _numReceivedRemoteMessages;
        private readonly PollingCounter _activeConnectionsCounter;
        private readonly IncrementingPollingCounter _connectionSentMessagesCounter;
        private readonly IncrementingPollingCounter _connectionReceivedMessagesCounter;
        private readonly EventCounter _connectionOutgoingMessageActiveTime;
        private readonly EventCounter _connectionOutgoingMessageIdleTime;
        private readonly EventCounter _connectionMessageSerializationTime;
        private MessagingEventSource()
        {
            _connectionOutgoingMessageIdleTime = new EventCounter("connection-outgoing-idle-time", this)
            {
                DisplayName = "Outgoing Message Processing Idle Time",
                DisplayUnits = "μs",
            };
            _connectionOutgoingMessageActiveTime = new EventCounter("connection-outgoing-active-time", this)
            {
                DisplayName = "Outgoing Message Processing Active Time",
                DisplayUnits = "μs",
            };
            _connectionMessageSerializationTime = new EventCounter("connection-message-serialization-time", this)
            {
                DisplayName = "Message Serialization Time",
                DisplayUnits = "μs",
            };
            _activeConnectionsCounter = new PollingCounter("connection-count", this, () => _numActiveConnections)
            {
                DisplayName = "Active Connections"
            };
            _connectionSentMessagesCounter = new IncrementingPollingCounter("connection-messages-sent-count", this, () => _numSentRemoteMessages)
            {
                DisplayName = "Messages Sent To Remote"
            };
            _connectionReceivedMessagesCounter = new IncrementingPollingCounter("connection-messages-received-count", this, () => _numReceivedRemoteMessages)
            {
                DisplayName = "Messages Received From Remote"
            };
        }

        [NonEvent]
        public void OnConnectionOutgoingMessageActiveTime(ValueStopwatch activeTime)
        {
            _connectionOutgoingMessageActiveTime.WriteMetric(activeTime.TotalMicroseconds);
        }

        [NonEvent]
        public void OnConnectionOutgoingMessageIdleTime(ValueStopwatch idleTime)
        {
            _connectionOutgoingMessageIdleTime.WriteMetric(idleTime.TotalMicroseconds);
        }

        [NonEvent]
        public void OnConnectionMessageSerializationTime(ValueStopwatch idleTime)
        {
            _connectionMessageSerializationTime.WriteMetric(idleTime.TotalMicroseconds);
        }

        [NonEvent]
        internal void OnConnectionStart()
        {
            Interlocked.Increment(ref _numActiveConnections);
        }

        [NonEvent]
        internal void OnConnectionStop()
        {
            Interlocked.Decrement(ref _numActiveConnections);
        }

        [NonEvent]
        internal void OnMessageSendRemote() => Interlocked.Increment(ref _numSentRemoteMessages);

        [NonEvent]
        internal void OnMessageReceiveRemote() => Interlocked.Increment(ref _numReceivedRemoteMessages);
    }
}
