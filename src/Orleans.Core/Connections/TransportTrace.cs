using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace Orleans.Networking.Transport
{
    internal sealed class TransportTrace : DiagnosticListener, ILogger
    {
        private readonly ILogger _log;

        public TransportTrace(ILoggerFactory loggerFactory) : base(typeof(TransportTrace).FullName)
        {
            this._log = loggerFactory.CreateLogger("Orleans.Runtime.Messaging");
        }

        public IDisposable BeginScope<TState>(TState state)
        {
            return this._log.BeginScope(state);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsEnabled(LogLevel logLevel)
        {
            return this._log.IsEnabled(logLevel);
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            this._log.Log(logLevel, eventId, state, exception, formatter);
        }
    }
}
