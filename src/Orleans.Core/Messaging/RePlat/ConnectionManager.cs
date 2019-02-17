using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Orleans.Configuration;

namespace Orleans.Runtime.Messaging
{
    internal sealed class ConnectionManager
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource<ConnectionMessageSender>> connections
            = new ConcurrentDictionary<string, TaskCompletionSource<ConnectionMessageSender>>();
        private readonly OutboundConnectionFactory connectionBuilder;

        public ConnectionManager(OutboundConnectionFactory connectionBuilder)
        {
            this.connectionBuilder = connectionBuilder;
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
                    Task.Run(async () =>
                    {
                        try
                        {
                            await this.connectionBuilder.ConnectAsync(this, endPoint, tcs);
                        }
                        catch
                        {
                            this.Remove(endPoint, null);
                        }
                    });
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
    }
}
