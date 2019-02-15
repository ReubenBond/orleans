using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace Orleans.Runtime.Messaging
{
    internal sealed class ConnectionMessageSenderManager
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource<ConnectionMessageSender>> connections
            = new ConcurrentDictionary<string, TaskCompletionSource<ConnectionMessageSender>>();
        private readonly IConnectionFactory connectionFactory;
        private readonly ConnectionDelegate connectionDelegate;

        public ConnectionMessageSenderManager(IConnectionFactory connectionFactory, IServiceProvider serviceProvider, IOptions<ConnectionOptions> connectionOptions)
        {
            this.connectionFactory = connectionFactory;
            this.connectionDelegate = CreateOutboundConnectionDelegate(serviceProvider, connectionOptions.Value);
        }

        private ConnectionDelegate CreateOutboundConnectionDelegate(
            IServiceProvider serviceProvider,
            ConnectionOptions endpointOptions)
        {
            // Configure the connection builder using the user-defined options.
            var connectionBuilder = new ConnectionBuilder(serviceProvider);
            endpointOptions.ConfigureConnectionBuilder(connectionBuilder);
            connectionBuilder.UseOrleansOutboundConnectionHandler();
            return connectionBuilder.Build();
        }

        public void Add(string endPoint, ConnectionMessageSender sender)
        {
            var updated = new TaskCompletionSource<ConnectionMessageSender>();
            updated.SetResult(sender);

            var c = this.connections;
            TaskCompletionSource<ConnectionMessageSender> existing = default;
            while (!c.TryAdd(endPoint, updated)
                && c.TryGetValue(endPoint, out existing)
                && !c.TryUpdate(endPoint, updated, existing))
            {
            }

            if (existing != null && !ReferenceEquals(existing, updated))
            {
                if (existing.TrySetResult(sender)) return;
                if (existing.Task.Status == TaskStatus.RanToCompletion)
                {
                    var e = existing.Task.GetAwaiter().GetResult();
                    e?.Abort();
                }
            }
        }

        public Task<ConnectionMessageSender> GetConnection(string endPoint)
        {
            this.connections.TryGetValue(endPoint, out var result);

            // Clean up defunct connections.
            if (result != null && result.Task.IsCompleted)
            {
                var status = result.Task.Status;
                if (status == TaskStatus.Canceled || status == TaskStatus.Faulted)
                {
                    var item = new KeyValuePair<string, TaskCompletionSource<ConnectionMessageSender>>(endPoint, result);
                    ((IDictionary<string, TaskCompletionSource<ConnectionMessageSender>>)this.connections).Remove(item);
                    result = default;
                }
            }

            if (result == null)
            {
                var tcs = new TaskCompletionSource<ConnectionMessageSender>();
                result = this.connections.GetOrAdd(endPoint, tcs);
                if (ReferenceEquals(result, tcs))
                {
                    Task.Run(() => this.ConnectAsync(endPoint, tcs));
                }
            }

            return result.Task;
        }

        public void Remove(string endPoint, ConnectionMessageSender connection = null)
        {
            if (this.connections.TryGetValue(endPoint, out var tcs))
            {
                var status = tcs.Task.Status;

                if (status == TaskStatus.RanToCompletion && ReferenceEquals(tcs.Task.GetAwaiter().GetResult(), connection)
                    || (status == TaskStatus.Canceled || status == TaskStatus.Faulted))
                {
                    var item = new KeyValuePair<string, TaskCompletionSource<ConnectionMessageSender>>(endPoint, tcs);
                    ((IDictionary<string, TaskCompletionSource<ConnectionMessageSender>>)this.connections).Remove(item);
                }
            }
        }

        private async Task ConnectAsync(string endPoint, TaskCompletionSource<ConnectionMessageSender> completion)
        {
            try
            {
                var context = await this.connectionFactory.Connect(endPoint);
                var middlewareTask = this.connectionDelegate(context);
                var sender = context.Features.Get<ConnectionMessageSender>();
                if (sender == null)
                {
                    var exception = new ConnectionAbortedException($"Connection does not have the required {nameof(ConnectionMessageSender)} feature");
                    context.Abort(exception);
                    throw exception;
                }

                Task.Run(async () =>
                {
                    try
                    {
                        await middlewareTask.ConfigureAwait(false);
                    }
                    finally
                    {
                        // Remove the defunct connection.
                        context.Abort();
                        this.connections.TryUpdate(endPoint, new TaskCompletionSource<ConnectionMessageSender>(), completion);
                    }
                }).Ignore();

                completion.TrySetResult(sender);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
                this.Remove(endPoint, null);
            }
            finally
            {
                completion.TrySetCanceled();
            }
        }
    }
}
