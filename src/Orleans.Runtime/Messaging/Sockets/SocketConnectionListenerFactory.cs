using System;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime.Messaging
{
    internal sealed class SocketConnectionListenerFactory : IConnectionListenerFactory
    {
        private readonly IApplicationLifetime applicationLifetime;
        private readonly SocketSchedulers schedulers;
        private readonly SharedMemoryPool memoryPool;
        private readonly SocketsTrace trace;

        public SocketConnectionListenerFactory(
            IApplicationLifetime applicationLifetime,
            ILoggerFactory loggerFactory,
            SocketSchedulers schedulers,
            SharedMemoryPool memoryPool)
        {
            if (loggerFactory == null)
            {
                throw new ArgumentNullException(nameof(loggerFactory));
            }

            this.applicationLifetime = applicationLifetime ?? throw new ArgumentNullException(nameof(applicationLifetime));
            this.schedulers = schedulers;
            this.memoryPool = memoryPool;
            var logger = loggerFactory.CreateLogger("Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets");
            this.trace = new SocketsTrace(logger);
        }

        public IConnectionListener Create(string endpoint, ConnectionDelegate connectionDelegate)
        {
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                throw new ArgumentNullException(nameof(endpoint));
            }

            if (connectionDelegate == null)
            {
                throw new ArgumentNullException(nameof(connectionDelegate));
            }

            return new SocketConnectionListener(endpoint, connectionDelegate, this.applicationLifetime, this.trace, this.schedulers, this.memoryPool);
        }
    }
}
