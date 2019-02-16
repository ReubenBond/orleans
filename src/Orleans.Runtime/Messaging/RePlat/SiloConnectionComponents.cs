using System;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.DependencyInjection;
using static Orleans.Runtime.Messaging.ConnectionBuilderExtensions;

namespace Orleans.Runtime.Messaging.RePlat
{
    internal sealed class SiloConnectionComponentFactory : ConnectionComponentFactory
    {
        private readonly IServiceProvider serviceProvider;

        public SiloConnectionComponentFactory(IServiceProvider serviceProvider)
        {
            this.serviceProvider = serviceProvider;
        }

        public override (ConnectionPreambleSender, ConnectionPreambleReceiver, ConnectionMessageReceiver) GetComponents(bool outbound, bool siloToSilo, ConnectionContext connection)
        {
            var preambleSender = GetPreambleSender(serviceProvider, outbound, siloToSilo);
            var preambleReceiver = GetPreambleReceiver(serviceProvider, outbound, siloToSilo);
            var receiver = GetReceiver(serviceProvider, outbound, siloToSilo, connection);
            return (preambleSender, preambleReceiver, receiver);
        }
        
        private static ConnectionPreambleReceiver GetPreambleReceiver(IServiceProvider serviceProvider, bool outbound, bool siloToSilo)
        {
            if (outbound)
            {
                return null;
            }

            if (siloToSilo) return ActivatorUtilities.GetServiceOrCreateInstance<SiloPreambleReceiver>(serviceProvider);
            else return ActivatorUtilities.GetServiceOrCreateInstance<GatewayPreambleReceiver>(serviceProvider);
        }

        private static ConnectionPreambleSender GetPreambleSender(IServiceProvider serviceProvider, bool outbound, bool siloToSilo)
        {
            if (!outbound)
            {
                return null;
            }

            if (siloToSilo) return ActivatorUtilities.GetServiceOrCreateInstance<SiloPreambleSender>(serviceProvider);
            else return ActivatorUtilities.GetServiceOrCreateInstance<ClientPreambleSender>(serviceProvider);
        }

        private static ConnectionMessageReceiver GetReceiver(IServiceProvider serviceProvider, bool outbound, bool siloToSilo, ConnectionContext connection)
        {
            if (siloToSilo) return ActivatorUtilities.CreateInstance<SiloMessageReceiver>(serviceProvider, connection);
            else if (outbound) return ActivatorUtilities.CreateInstance<ClientMessageReceiver>(serviceProvider, connection);
            else return ActivatorUtilities.CreateInstance<GatewayMessageReceiver>(serviceProvider, connection);
        }
    }
}
