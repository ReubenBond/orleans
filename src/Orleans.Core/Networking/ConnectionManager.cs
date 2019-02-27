using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime.Messaging
{
    internal sealed class ConnectionManager
    {
        [ThreadStatic]
        private static int nextConnection;

        private static readonly TimeSpan CONNECTION_RETRY_DELAY = TimeSpan.FromMilliseconds(1000);
        private const int MaxConnectionsPerEndpoint = 1;
        private readonly ConcurrentDictionary<EndPoint, ConnectionEntry> connections = new ConcurrentDictionary<EndPoint, ConnectionEntry>();
        private readonly ConnectionFactory connectionFactory;
        private readonly INetworkingTrace trace;
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();

        public ConnectionManager(
            ConnectionFactory connectionBuilder,
            INetworkingTrace trace)
        {
            this.connectionFactory = connectionBuilder;
            this.trace = trace;
        }

        public int ConnectionCount
        {
            get
            {
                var count = 0;
                foreach (var entry in this.connections)
                {
                    var values = entry.Value.Connections;
                    if (values.IsDefault) continue;
                    count += values.Length;
                }

                return count;
            }
        }

        public ImmutableArray<EndPoint> GetConnectedEndpoints() => this.connections.Keys.ToImmutableArray();

        public ValueTask<Connection> GetConnection(EndPoint endpoint)
        {
            if (this.connections.TryGetValue(endpoint, out var entry) && entry.Connections.Length >= MaxConnectionsPerEndpoint)
            {
                var result = entry.Connections;
                nextConnection = (nextConnection + 1) % result.Length;
                return new ValueTask<Connection>(result[nextConnection]);
            }

            return this.GetConnectionInner(endpoint, entry);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private ValueTask<Connection> GetConnectionInner(EndPoint endpoint, ConnectionEntry entry)
        {
            TimeSpan delta;
            if (entry.LastFailure.HasValue
                && (delta = DateTime.UtcNow.Subtract(entry.LastFailure.Value)) < CONNECTION_RETRY_DELAY)
            {
                return new ValueTask<Connection>(Task.FromException<Connection>(new ConnectionFailedException($"Unable to connect to endpoint {endpoint}, will retry after {delta.TotalMilliseconds}ms")));
            }

            return this.GetConnectionAsync(endpoint);
        }

        private async ValueTask<Connection> GetConnectionAsync(EndPoint endpoint)
        {
            ImmutableArray<Connection> result;
            DateTime? lastFailure = default;

            while (true)
            {
                // Initialize the entry for this endpoint
                ConnectionEntry original;
                while (!this.connections.TryGetValue(endpoint, out original))
                {
                    original = new ConnectionEntry
                    {
                        ConnectionAttemptGuard = new SemaphoreSlim(1),
                        Connections = ImmutableArray<Connection>.Empty
                    };

                    if (this.connections.TryAdd(endpoint, original)) break;
                }

                if (original.Connections.Length >= MaxConnectionsPerEndpoint)
                {
                    result = original.Connections;
                    break;
                }

                var acquired = false;
                try
                {
                    acquired = await original.ConnectionAttemptGuard.WaitAsync(100);
                    if (!acquired) continue;

                    // Re-check that a new connection is warranted.
                    if (this.connections.TryGetValue(endpoint, out original) && original.Connections.Length >= MaxConnectionsPerEndpoint)
                    {
                        result = original.Connections;
                        break;
                    }

                    Connection connection = default;
                    try
                    {
                        // Start the new connection
                        connection = await this.ConnectAsync(endpoint);
                    }
                    catch (Exception exception)
                    {
                        lastFailure = DateTime.UtcNow;
                        throw new ConnectionFailedException($"Unable to connect to endpoint {endpoint}. See {nameof(exception.InnerException)}", exception);
                    }
                    finally
                    {
                        // Ensure the connection is added to the collection
                        ConnectionEntry updated;
                        do
                        {
                            if (connection != null)
                            {
                                result = original.Connections.Add(connection);
                                lastFailure = null;
                            }
                            else
                            {
                                result = original.Connections;

                                if (original.LastFailure.HasValue && lastFailure.HasValue)
                                {
                                    var ticks = Math.Max(lastFailure.Value.Ticks, original.LastFailure.Value.Ticks);
                                    lastFailure = new DateTime(ticks);
                                }
                            }

                            updated = new ConnectionEntry
                            {
                                ConnectionAttemptGuard = original.ConnectionAttemptGuard,
                                Connections = result,
                                LastFailure = lastFailure
                            };
                        } while (!(this.connections.TryUpdate(endpoint, updated, original) || this.connections.TryAdd(endpoint, updated)));
                    }
                    break;
                }
                finally
                {
                    if (acquired) original.ConnectionAttemptGuard.Release();
                }
            };

            nextConnection = (nextConnection + 1) % result.Length;
            return result[nextConnection];
        }

        private async ValueTask<Connection> ConnectAsync(EndPoint endpoint)
        {
            try
            {
                if (this.trace.IsEnabled(LogLevel.Information))
                {
                    this.trace.LogInformation(
                        "Establishing connection to endpoint {EndPoint}",
                        endpoint);
                }

                var connection = await this.connectionFactory.ConnectAsync(endpoint, this.cancellation.Token);

                _ = Task.Run(async () =>
                {
                    Exception error = default;
                    try
                    {
                        using (this.BeginConnectionScope(connection))
                        {
                            await connection.Run(this.cancellation.Token).ConfigureAwait(false);
                        }
                    }
                    catch (Exception exception)
                    {
                        error = exception;
                    }
                    finally
                    {
                        this.Remove(endpoint, connection);
                        if (error != null)
                        {
                            this.trace.LogWarning(
                                "Connection to endpoint {EndPoint} terminated with exception {Exception}",
                                endpoint,
                                error);
                        }
                        else
                        {
                            this.trace.LogInformation(
                               "Connection to endpoint {EndPoint} closed.",
                               endpoint);
                        }
                    }
                });

                return connection;
            }
            catch (Exception exception)
            {
                this.trace.LogWarning(
                    "Connection attempt to endpoint {EndPoint} failed with exception {Exception}",
                    endpoint,
                    exception);
                throw;
            }
        }

        internal void Remove(EndPoint endpoint, Connection connection)
        {
            if (connection is object)
            {
                while (this.connections.TryGetValue(endpoint, out var existing) && existing.Connections.Contains(connection))
                {
                    var updated = new ConnectionEntry
                    {
                        Connections = existing.Connections.Remove(connection),
                        ConnectionAttemptGuard = existing.ConnectionAttemptGuard
                    };

                    if (this.connections.TryUpdate(endpoint, updated, existing))
                    {
                        if (updated.Connections.Length == 0)
                        {
                            var dict = (IDictionary<EndPoint, ConnectionEntry>)this.connections;
                            var entry = new KeyValuePair<EndPoint, ConnectionEntry>(endpoint, updated);
                            dict.Remove(entry);
                        }

                        return;
                    }
                }
            }
        }

        public void Abort(EndPoint endpoint)
        {
            this.connections.TryRemove(endpoint, out var existing);

            if (!existing.Connections.IsDefault)
            {
                foreach (var connection in existing.Connections)
                {
                    try
                    {
                        connection.Close();
                    }
                    catch
                    {
                    }
                }
            }
        }

        public async Task Close()
        {
            try
            {
                this.cancellation.Cancel(throwOnFirstException: false);

                var cycles = 0;
                while (this.connections.Count > 0)
                {
                    foreach (var c in this.connections)
                    {
                        var entry = c.Value;
                        if (entry.Connections.IsDefaultOrEmpty) continue;
                        foreach (var connection in entry.Connections)
                        {
                            try
                            {
                                connection.Close();
                            }
                            catch
                            {
                            }
                        }
                    }

                    await Task.Delay(10);
                    if (++cycles > 100 && cycles % 500 == 0)
                    {
                        this.trace?.LogWarning("Waiting for {NumRemaining} connections to terminate", this.connections.Count);
                    }
                }
            }
            catch (Exception exception)
            {
                this.trace?.LogWarning("Exception during shutdown: {Exception}", exception);
            }
        }

        private IDisposable BeginConnectionScope(Connection connection)
        {
            if (this.trace.IsEnabled(LogLevel.Critical))
            {
                return this.trace.BeginScope(new ConnectionLogScope(connection.Context.ConnectionId));
            }

            return null;
        }

        private struct ConnectionEntry : IEquatable<ConnectionEntry>
        {
            public DateTime? LastFailure;
            public ImmutableArray<Connection> Connections;
            public SemaphoreSlim ConnectionAttemptGuard;

            public override bool Equals(object obj)
            {
                return obj is ConnectionEntry entry &&
                       this.Connections.Equals(entry.Connections) &&
                       ReferenceEquals(this.ConnectionAttemptGuard, entry.ConnectionAttemptGuard);
            }

            public bool Equals(ConnectionEntry other)
            {
                return this.Connections.Equals(other.Connections) &&
                       ReferenceEquals(this.ConnectionAttemptGuard, other.ConnectionAttemptGuard);
            }

            public override int GetHashCode()
            {
                var hashCode = -251647159;
                hashCode = hashCode * -1521134295 + EqualityComparer<ImmutableArray<Connection>>.Default.GetHashCode(this.Connections);
                hashCode = hashCode * -1521134295 + RuntimeHelpers.GetHashCode(this.ConnectionAttemptGuard);
                return hashCode;
            }
        }
    }
}
