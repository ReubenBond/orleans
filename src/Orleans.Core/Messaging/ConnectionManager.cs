using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime.Messaging
{
    internal sealed class ConnectionManager
    {
        private readonly ConcurrentDictionary<string, ConnectionMessageSender> connections
            = new ConcurrentDictionary<string, ConnectionMessageSender>();
        private readonly OutboundConnectionFactory connectionBuilder;
        private readonly ILogger<ConnectionManager> log;
        private readonly IServiceProvider serviceProvider;

        public ConnectionManager(
            OutboundConnectionFactory connectionBuilder,
            ILogger<ConnectionManager> log,
            IServiceProvider serviceProvider)
        {
            this.connectionBuilder = connectionBuilder;
            this.log = log;
            this.serviceProvider = serviceProvider;
        }

        public int ConnectionCount => this.connections.Count;

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
                    this.StartConnection(endPoint, sender);
                }
            }

            return result;
        }

        private void StartConnection(string endPoint, ConnectionMessageSender sender)
        {
            try
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
                    catch (Exception exception)
                    {
                        var delay = TimeSpan.FromSeconds(2);
                        this.log.LogWarning(
                            "Failed to connect to endpoint {EndPoint}: {Exception}. Waiting {Delay}ms before retry.",
                            endPoint,
                            exception,
                            delay.TotalMilliseconds);
                        await Task.Delay(delay);
                    }
                    finally
                    {
                        this.Remove(endPoint, sender);
                        sender.Abort();
                    }
                });
            }
            catch (Exception exception)
            {
                _ = Task.Run(async () =>
                {
                    var delay = TimeSpan.FromSeconds(2);
                    this.log.LogWarning(
                        "Failed to connect to endpoint {EndPoint}: {Exception}. Waiting {Delay}ms before retry.",
                        endPoint,
                        exception,
                        delay.TotalMilliseconds);
                    await Task.Delay(delay);
                    this.Remove(endPoint, sender);
                    sender.Abort();
                });
            }
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
