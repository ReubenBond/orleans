using System;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Networking.Shared;

namespace Orleans.Runtime.Messaging
{
    internal sealed class ConnectionManager
    {
        private static readonly TimeSpan CONNECTION_RETRY_DELAY = TimeSpan.FromMilliseconds(1000);
        private const int MaxConnectionsPerEndpoint = 1;

        [ThreadStatic]
        private static int nextConnection;

        private readonly ConcurrentDictionary<SiloAddress, ConnectionEntry> connections = new ConcurrentDictionary<SiloAddress, ConnectionEntry>();
        private readonly ConnectionFactory connectionFactory;
        private readonly INetworkingTrace trace;
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private readonly object collectionLock = new object();

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

        public ImmutableArray<SiloAddress> GetConnectedAddresses() => this.connections.Keys.ToImmutableArray();

        public ValueTask<Connection> GetConnection(SiloAddress siloAddress)
        {
            if (this.connections.TryGetValue(siloAddress, out var entry) && entry.Connections.Length >= MaxConnectionsPerEndpoint)
            {
                var result = entry.Connections;
                nextConnection = (nextConnection + 1) % result.Length;
                var connection = result[nextConnection];
                if (connection.IsValid) return new ValueTask<Connection>(connection);
            }

            return GetConnectionAsync(siloAddress);
        }

        private async ValueTask<Connection> GetConnectionAsync(SiloAddress endpoint)
        {
            ImmutableArray<Connection> result;
            DateTime? lastFailure = default;

            ConnectionEntry entry = default;
            var acquiredConnectionLock = false;
            try
            {
                while (true)
                {
                    entry = GetOrCreateEntry(endpoint, ref acquiredConnectionLock);

                    if (entry.Connections.Length >= MaxConnectionsPerEndpoint)
                    {
                        result = entry.Connections;
                        break;
                    }

                    ThrowIfRecentFailure(endpoint, entry);

                    // Lock the entry to ensure it will not be removed while the connectio attempt is occuring.
                    if (!acquiredConnectionLock)
                    {
                        await Task.Delay(10);
                        continue;
                    }

                    // Attempt to connect.
                    Connection connection = default;
                    Exception error = default;
                    try
                    {
                        connection = await this.ConnectAsync(endpoint);
                        error = default;
                    }
                    catch (Exception exception)
                    {
                        lastFailure = DateTime.UtcNow;
                        error = exception;
                    }

                    // Ensure the connection is added to the collection.
                    lock (this.collectionLock)
                    {
                        entry = this.connections[endpoint];

                        // If the connection attempt failed, update the collection to record the failed connection attempt.
                        if (connection is null)
                        {
                            result = entry.Connections;
                            if (entry.LastFailure.HasValue && lastFailure.HasValue)
                            {
                                var ticks = Math.Max(lastFailure.Value.Ticks, entry.LastFailure.Value.Ticks);
                                lastFailure = new DateTime(ticks);
                            }
                        }
                        else
                        {
                            result = entry.Connections.Add(connection);
                            lastFailure = null;
                        }

                        // Clean up defunct connections
                        foreach (var c in result)
                        {
                            if (!c.IsValid) result = result.Remove(c);
                        }

                        this.connections[endpoint] = entry.WithLastFailure(lastFailure).WithConnections(result);
                    }

                    if (error != null)
                    {
                        throw new ConnectionFailedException(
                            $"Unable to connect to endpoint {endpoint}. See {nameof(error.InnerException)}", error);
                    }

                    break;
                }
            }
            finally
            {
                if (acquiredConnectionLock) entry.ReleaseLock();
            }

            nextConnection = (nextConnection + 1) % result.Length;
            return result[nextConnection];
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ThrowIfRecentFailure(SiloAddress address, ConnectionEntry entry)
        {
            TimeSpan delta;
            if (entry.LastFailure.HasValue
                && (delta = DateTime.UtcNow.Subtract(entry.LastFailure.Value)) < CONNECTION_RETRY_DELAY)
            {
                throw new ConnectionFailedException($"Unable to connect to {address}, will retry after {delta.TotalMilliseconds}ms");
            }
        }

        private ConnectionEntry GetOrCreateEntry(SiloAddress address, ref bool locked)
        {
            lock (this.collectionLock)
            {
                if (!this.connections.TryGetValue(address, out var entry))
                {
                    // Initialize the entry for this endpoint
                    entry = ConnectionEntry.CreateNew();
                    locked = entry.TryLock();

                    this.connections[address] = entry;
                }
                else
                {
                    locked = entry.TryLock();
                }

                return entry;
            }
        }

        private async ValueTask<Connection> ConnectAsync(SiloAddress address)
        {
            try
            {
                if (this.trace.IsEnabled(LogLevel.Information))
                {
                    this.trace.LogInformation(
                        "Establishing connection to endpoint {EndPoint}",
                        address);
                }

                var connection = await this.connectionFactory.ConnectAsync(address.Endpoint, this.cancellation.Token);

                _ = Task.Run(async () =>
                {
                    Exception error = default;
                    try
                    {
                        using (this.BeginConnectionScope(connection))
                        {
                            await connection.Run();
                        }
                    }
                    catch (Exception exception)
                    {
                        error = exception;
                    }

                    this.Remove(address, connection);

                    if (error != null)
                    {
                        this.trace.LogWarning(
                            "Connection to endpoint {EndPoint} terminated with exception {Exception}",
                            address,
                            error);
                    }
                    else
                    {
                        this.trace.LogInformation(
                            "Connection to endpoint {EndPoint} closed.",
                            address);
                    }
                });

                return connection;
            }
            catch (Exception exception)
            {
                this.trace.LogWarning(
                    "Connection attempt to endpoint {EndPoint} failed with exception {Exception}",
                    address,
                    exception);
                throw;
            }
        }

        internal void Remove(SiloAddress siloAddress, Connection connection)
        {
            if (connection is null) return;

            lock (this.collectionLock)
            {
                if (this.connections.TryGetValue(siloAddress, out var existing))
                {
                    var updated = existing.WithConnections(existing.Connections.Remove(connection));

                    if (updated.Connections.Length == 0)
                    {
                        // Remove the entire entry.
                        var acquiredConnectionLock = false;
                        try
                        {
                            acquiredConnectionLock = existing.TryLock();
                            this.connections.TryRemove(siloAddress, out _);
                        }
                        finally
                        {
                            if (acquiredConnectionLock) existing.ReleaseLock();
                        }
                    }
                    else
                    {
                        // Remove just the single connection.
                        this.connections[siloAddress] = updated;
                    }
                }
            }
        }

        public void Abort(SiloAddress endpoint)
        {
            lock (this.collectionLock)
            {
                if (!this.connections.TryGetValue(endpoint, out var entry))
                {
                    // Already removed
                    return;
                }

                if (!entry.Connections.IsDefault)
                {
                    var exception = new ConnectionAbortedException($"Aborting connection to {endpoint}");
                    foreach (var connection in entry.Connections)
                    {
                        try
                        {
                            connection.Close(exception);
                        }
                        catch
                        {
                        }
                    }
                }

                var acquiredConnectionLock = false;
                try
                {
                    if (acquiredConnectionLock = entry.TryLock())
                    {
                        this.connections.TryRemove(endpoint, out _);
                    }
                }
                finally
                {
                    if (acquiredConnectionLock) entry.ReleaseLock();
                }
            }
        }

        public async Task Close(CancellationToken ct)
        {
            try
            {
                this.cancellation.Cancel(throwOnFirstException: false);

                var connectionAbortedException = new ConnectionAbortedException("Stopping");
                var cycles = 0;
                while (this.ConnectionCount > 0)
                {
                    foreach (var entry in this.connections.Values.ToImmutableList())
                    {
                        if (entry.Connections.IsDefaultOrEmpty) continue;
                        foreach (var connection in entry.Connections)
                        {
                            try
                            {
                                connection.Close(connectionAbortedException);
                            }
                            catch
                            {
                            }
                        }
                    }

                    if (ct.IsCancellationRequested) break;

                    await Task.Delay(10);
                    if (++cycles > 100 && cycles % 500 == 0 && this.ConnectionCount > 0)
                    {
                        this.trace?.LogWarning("Waiting for {NumRemaining} connections to terminate", this.ConnectionCount);
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

        private struct ConnectionEntry
        {
            public readonly DateTime? LastFailure;
            public readonly ImmutableArray<Connection> Connections;
            private readonly int[] lockObj;

            private ConnectionEntry(
                ImmutableArray<Connection> connections,
                DateTime? lastFailure,
                int[] lockObject)
            {
                this.Connections = connections;
                this.LastFailure = lastFailure;
                this.lockObj = lockObject;
            }

            public static ConnectionEntry CreateNew() => new ConnectionEntry(ImmutableArray<Connection>.Empty, default, new int[1]);

            public ConnectionEntry WithLastFailure(DateTime? lastFailure)
            {
                return new ConnectionEntry(this.Connections, lastFailure, this.lockObj);
            }

            public ConnectionEntry WithConnections(ImmutableArray<Connection> connections)
            {
                return new ConnectionEntry(connections, this.LastFailure, this.lockObj);
            }

            public bool TryLock()
            {
                return Interlocked.CompareExchange(ref this.lockObj[0], 1, 0) == 0;
            }

            public void ReleaseLock() => Interlocked.Exchange(ref this.lockObj[0], 0);
        }
    }
}
