using System.Runtime.CompilerServices;
using Orleans.Runtime;
using Orleans.Runtime.Messaging;

namespace DurableWorkflows;

// Application-specific policy, shared by HTTP streaming and client-side waits.
public sealed class OrderStatusClient(IWorkflowGrain workflow, ILogger<OrderStatusClient> logger)
{
    public async IAsyncEnumerable<OrderStatusUpdate> WatchAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var failures = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using (var subscription = workflow.WatchOrderStatusAsync(cancellationToken)
                .WithBatchSize(1).GetAsyncEnumerator(cancellationToken))
            {
                while (true)
                {
                    bool moved;
                    try
                    {
                        moved = await subscription.MoveNextAsync();
                    }
                    catch (Exception exception) when (!cancellationToken.IsCancellationRequested && IsTransient(exception))
                    {
                        logger.LogWarning(exception, "Order {Workflow} status disconnected; reacquiring its snapshot", workflow);
                        break;
                    }

                    if (!moved)
                    {
                        logger.LogInformation("Order {Workflow} status owner stopped; reacquiring its snapshot", workflow);
                        break;
                    }

                    failures = 0;
                    yield return subscription.Current;
                }
            }

            // Dispose the old subscription before acquiring another; no abandoned response can overwrite its successor.
            var delay = TimeSpan.FromMilliseconds(100 * (1 << failures));
            failures = Math.Min(failures + 1, 5);
            await Task.Delay(delay, cancellationToken);
        }
    }

    public async Task<OrderStatus> WaitAsync(
        Func<OrderStatus, bool> predicate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        await foreach (var update in WatchAsync(cancellationToken))
        {
            if (predicate(update.Status))
            {
                return update.Status;
            }
        }

        throw new InvalidOperationException("The status watch ended before the predicate was satisfied.");
    }

    private static bool IsTransient(Exception exception) =>
        exception is SiloUnavailableException or EnumerationAbortedException or ConnectionFailedException or TimeoutException;
}
