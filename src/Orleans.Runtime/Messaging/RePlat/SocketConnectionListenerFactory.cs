using System;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime.Messaging.RePlat
{
    public sealed class SocketConnectionListenerFactory : IConnectionListenerFactory
    {
      //  private readonly IApplicationLifetime applicationLifetime;
        private readonly SocketsTrace trace;

        public SocketConnectionListenerFactory(
          //  IApplicationLifetime applicationLifetime,
            ILoggerFactory loggerFactory)
        {
        //    if (applicationLifetime == null)
            {
          //      throw new ArgumentNullException(nameof(applicationLifetime));
            }
            if (loggerFactory == null)
            {
                throw new ArgumentNullException(nameof(loggerFactory));
            }

          //  this.applicationLifetime = applicationLifetime;
            var logger = loggerFactory.CreateLogger("Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets");
            this.trace = new SocketsTrace(logger);
        }

        public IConnectionListener Create(string endPoint, ConnectionDelegate connectionDelegate)
        {
            if (string.IsNullOrWhiteSpace(endPoint))
            {
                throw new ArgumentNullException(nameof(endPoint));
            }

            if (connectionDelegate == null)
            {
                throw new ArgumentNullException(nameof(connectionDelegate));
            }

            return new SocketConnectionListener(endPoint, connectionDelegate, /*this.applicationLifetime,*/ this.trace);
        }
    }
}
