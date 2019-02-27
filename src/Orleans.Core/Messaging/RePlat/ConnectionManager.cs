using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Runtime.Messaging
{
    internal sealed class ConnectionManager
    {
        private readonly ConcurrentDictionary<string, ConnectionMessageSender> connections
            = new ConcurrentDictionary<string, ConnectionMessageSender>();
        private readonly OutboundConnectionFactory connectionBuilder;
        private readonly IServiceProvider serviceProvider;

        public ConnectionManager(OutboundConnectionFactory connectionBuilder, IServiceProvider serviceProvider)
        {
            this.connectionBuilder = connectionBuilder;
            this.serviceProvider = serviceProvider;
        }

        public void Add(string endPoint, ConnectionMessageSender sender)
        {
            var c = this.connections;
            ConnectionMessageSender existing = default;
            while (!c.TryAdd(endPoint, sender)
                && c.TryGetValue(endPoint, out existing)
                && !c.TryUpdate(endPoint, sender, existing))
            {
            }

            if (existing != null && !ReferenceEquals(existing, sender))
            {
                existing.Abort();
            }
        }

        public ConnectionMessageSender GetConnection(string endPoint)
        {
            this.connections.TryGetValue(endPoint, out var result);

            if (result == null)
            {
                var sender = ActivatorUtilities.CreateInstance<ConnectionMessageSender>(this.serviceProvider);
                result = this.connections.GetOrAdd(endPoint, sender);

                if (ReferenceEquals(result, sender))
                {
                    var connectionTask = this.connectionBuilder.Connect(
                        endPoint,
                        context =>
                        {
                            context.Items[ConnectionMessageSender.ContextItemKey] = sender;
                        });

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await connectionTask.ConfigureAwait(false);
                        }
                        catch
                        {
                            this.Remove(endPoint, sender);
                        }
                    });
                }
            }

            return result;
        }

        public void Remove(string endPoint, ConnectionMessageSender connection = null)
        {
            if (this.connections.TryGetValue(endPoint, out var existing))
            {
                if (ReferenceEquals(existing, connection))
                {
                    var item = new KeyValuePair<string, ConnectionMessageSender>(endPoint, existing);
                    ((IDictionary<string, ConnectionMessageSender>)this.connections).Remove(item);
                }
            }
        }
    }
}
