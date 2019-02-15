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
        private readonly ConcurrentDictionary<IPEndPoint, TaskCompletionSource<ConnectionMessageSender>> connections
            = new ConcurrentDictionary<IPEndPoint, TaskCompletionSource<ConnectionMessageSender>>();
        private readonly IConnectionFactory connectionFactory;
        private readonly ConnectionDelegate connectionDelegate;

        public ConnectionMessageSenderManager(IConnectionFactory connectionFactory, IServiceProvider serviceProvider, IOptions<EndpointOptions> endpointOptions)
        {
            this.connectionFactory = connectionFactory;

            // Configure the connection builder using the user-defined options.
            var connectionBuilder = new ConnectionBuilder(serviceProvider);
            connectionBuilder.UseOrleansOutgoingConnectionHandler();
            endpointOptions.Value.ConfigureOutboundConnectionBuilder(connectionBuilder);
            this.connectionDelegate = connectionBuilder.Build();
        }

        public void Add(IPEndPoint endPoint, ConnectionMessageSender sender)
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

        public Task<ConnectionMessageSender> GetConnection(IPEndPoint endPoint)
        {
            this.connections.TryGetValue(endPoint, out var result);

            // Clean up defunct connections.
            if (result != null && result.Task.IsCompleted)
            {
                var status = result.Task.Status;
                if (status == TaskStatus.Canceled || status == TaskStatus.Faulted)
                {
                    var item = new KeyValuePair<IPEndPoint, TaskCompletionSource<ConnectionMessageSender>>(endPoint, result);
                    ((IDictionary<IPEndPoint, TaskCompletionSource<ConnectionMessageSender>>)this.connections).Remove(item);
                    result = default;
                }
            }

            if (result == null)
            {
                var tcs = new TaskCompletionSource<ConnectionMessageSender>();
                result = this.connections.GetOrAdd(endPoint, tcs);
                if (ReferenceEquals(result, tcs))
                {
                    Task.Run(() => ConnectAsync(endPoint, tcs));
                }
            }

            return result.Task;
        }

        public void Remove(IPEndPoint endPoint, ConnectionMessageSender connection = null)
        {
            if (this.connections.TryGetValue(endPoint, out var tcs))
            {
                var status = tcs.Task.Status;

                if (status == TaskStatus.RanToCompletion && ReferenceEquals(tcs.Task.GetAwaiter().GetResult(), connection)
                    || (status == TaskStatus.Canceled || status == TaskStatus.Faulted))
                {
                    var item = new KeyValuePair<IPEndPoint, TaskCompletionSource<ConnectionMessageSender>>(endPoint, tcs);
                    ((IDictionary<IPEndPoint, TaskCompletionSource<ConnectionMessageSender>>)this.connections).Remove(item);
                }
            }
        }

        private async Task ConnectAsync(IPEndPoint endPoint, TaskCompletionSource<ConnectionMessageSender> completion)
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
