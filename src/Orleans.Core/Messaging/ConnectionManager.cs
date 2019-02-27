using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime.Messaging
{
    internal sealed class ConnectionManager
    {
        [ThreadStatic]
        private static int nextConnection;

        private const int MaxConnectionsPerEndpoint = 1;
        private readonly ConcurrentDictionary<string, ImmutableArray<ConnectionMessageSender>> connections
            = new ConcurrentDictionary<string, ImmutableArray<ConnectionMessageSender>>();
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

        public ImmutableArray<string> GetConnectedEndpoints() => this.connections.Keys.ToImmutableArray();
        
        public ConnectionMessageSender GetConnection(string endpoint)
        {
            ImmutableArray<ConnectionMessageSender> result;
            ImmutableArray<ConnectionMessageSender> original;

            ConnectionMessageSender sender = default;
            while (true)
            {
                if (this.connections.TryGetValue(endpoint, out original) && original.Length >= MaxConnectionsPerEndpoint)
                {
                    result = original;
                    break;
                }

                if (original.IsDefault) original = ImmutableArray<ConnectionMessageSender>.Empty;
                if (sender is null) sender = ActivatorUtilities.CreateInstance<ConnectionMessageSender>(this.serviceProvider);
                result = original.Add(sender);

                if (this.connections.TryUpdate(endpoint, result, original) || this.connections.TryAdd(endpoint, result))
                {
                    this.StartConnection(endpoint, sender);
                    break;
                }
            };

            nextConnection = (nextConnection + 1) % result.Length;
            return result[nextConnection];
        }

        private void StartConnection(string endpoint, ConnectionMessageSender sender)
        {
            try
            {
                if (this.log.IsEnabled(LogLevel.Information))
                {
                    this.log.LogInformation(
                        "Establishing connection to endpoint {EndPoint}",
                        endpoint);
                }

                var connectionTask = this.connectionBuilder.Connect(
                    endpoint,
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
                            "Connection to endpoint {EndPoint} terminated with exception {Exception}. Waiting {Delay}ms before retry.",
                            endpoint,
                            exception,
                            delay.TotalMilliseconds);
                        await Task.Delay(delay);
                    }
                    finally
                    {
                        this.log.LogInformation(
                           "Connection to endpoint {EndPoint} closed.",
                           endpoint);
                        this.Remove(endpoint, sender);
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
                        "Connection to endpoint {EndPoint} terminated with exception {Exception}. Waiting {Delay}ms before retry.",
                        endpoint,
                        exception,
                        delay.TotalMilliseconds);
                    await Task.Delay(delay);
                    this.Remove(endpoint, sender);
                    sender.Abort();
                });
            }
        }

        internal void Remove(string endpoint, ConnectionMessageSender connection)
        {
            if (connection is object)
            {
                while (this.connections.TryGetValue(endpoint, out var existing) && existing.Contains(connection))
                {
                    var updated = existing.Remove(connection);
                    if (this.connections.TryUpdate(endpoint, updated, existing))
                    {
                        if (updated.Length == 0)
                        {
                            var dict = ((IDictionary<string, ImmutableArray<ConnectionMessageSender>>)this.connections);
                            var entry = new KeyValuePair<string, ImmutableArray<ConnectionMessageSender>>(endpoint, updated);
                            dict.Remove(entry);
                        }

                        return;
                    }
                }
            }
        }

        public void Abort(string endpoint)
        {
            this.connections.TryRemove(endpoint, out var existing);

            if (existing != null)
            {
                foreach (var connection in existing)
                {
                    try
                    {
                        connection.Abort();
                    }
                    catch
                    {
                    }
                }
            }
        }
    }
}
