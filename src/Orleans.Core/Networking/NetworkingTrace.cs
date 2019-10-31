using System;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime.Messaging
{
    internal sealed class NetworkingTrace : ILogger
    {
        private readonly ILogger log;
        private readonly DiagnosticListener diagnosticListener;

        public NetworkingTrace(ILoggerFactory loggerFactory)
        {
            this.log = loggerFactory.CreateLogger("Microsoft.Orleans.Networking");
            this.diagnosticListener = new DiagnosticListener("Microsoft.Orleans.Networking");
        }

        public IDisposable BeginScope<TState>(TState state)
        {
            return this.log.BeginScope(state);
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return this.log.IsEnabled(logLevel);
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            this.log.Log(logLevel, eventId, state, exception, formatter);
        }

        public void EnqueueMessageForSend(Message message)
        {
        }

        public void SendMessage(Message message)
        {
            //diagnosticListener.OnSendMessage(message);
        }

        public void ReceiveMessage(Message message)
        {
            //diagnosticListener.OnReceiveMessage(message);
        }
    }
}
