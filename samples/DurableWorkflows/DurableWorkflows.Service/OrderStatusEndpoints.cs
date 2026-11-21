using System.Text.Json;

namespace DurableWorkflows;

public static class OrderStatusEndpoints
{
    public static IEndpointRouteBuilder MapOrderStatusEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/workflows/orders/{orderId}/status",
            async (IClusterClient client, string orderId, CancellationToken cancellationToken) =>
                await client.GetGrain<IWorkflowGrain>(orderId).GetOrderStatusAsync(cancellationToken));
        endpoints.MapGet("/workflows/orders/{orderId}/status/watch", WatchAsync);
        endpoints.MapGet("/workflows/orders/{orderId}/status/terminal",
            async (IClusterClient client, ILogger<OrderStatusClient> logger, string orderId, CancellationToken cancellationToken) =>
                await new OrderStatusClient(client.GetGrain<IWorkflowGrain>(orderId), logger)
                    .WaitAsync(status => status.IsTerminal, cancellationToken));
        return endpoints;
    }

    private static async Task WatchAsync(
        IClusterClient client,
        ILogger<OrderStatusClient> logger,
        string orderId,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        response.ContentType = "application/x-ndjson";
        var status = new OrderStatusClient(client.GetGrain<IWorkflowGrain>(orderId), logger);
        await foreach (var update in status.WatchAsync(cancellationToken))
        {
            await JsonSerializer.SerializeAsync(response.Body, update, JsonSerializerOptions.Web, cancellationToken);
            await response.WriteAsync("\n", cancellationToken);
            await response.Body.FlushAsync(cancellationToken);
        }
    }
}
