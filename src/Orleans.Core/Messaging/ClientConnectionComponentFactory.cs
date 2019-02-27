using System;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Runtime.Messaging
{
    internal sealed class ClientConnectionComponentFactory : ConnectionComponentFactory
    {
        private readonly IServiceProvider serviceProvider;

        public ClientConnectionComponentFactory(IServiceProvider serviceProvider)
        {
            this.serviceProvider = serviceProvider;
        }

        public override (ConnectionPreambleSender, ConnectionPreambleReceiver, ConnectionMessageReceiver) GetComponents(bool outbound, bool siloToSilo, ConnectionContext connection)
        {
            var preambleSender = GetPreambleSender(outbound);
            var receiver = ActivatorUtilities.CreateInstance<ClientMessageReceiver>(serviceProvider, connection);
            return (preambleSender, null, receiver);
        }

        private ConnectionPreambleSender GetPreambleSender(bool outbound)
        {
            if (!outbound)
            {
                return null;
            }

            return ActivatorUtilities.GetServiceOrCreateInstance<ClientPreambleSender>(serviceProvider);
        }
    }
}
