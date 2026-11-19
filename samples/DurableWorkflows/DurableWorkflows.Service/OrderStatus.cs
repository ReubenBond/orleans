using System.Runtime.CompilerServices;
using Orleans.Journaling;

namespace DurableWorkflows;

public partial interface IWorkflowGrain
{
    Task<OrderStatus> GetOrderStatusAsync(CancellationToken cancellationToken = default);
    IAsyncEnumerable<OrderStatusUpdate> WatchOrderStatusAsync(CancellationToken cancellationToken = default);
}

[GenerateSerializer]
public enum OrderStage
{
    NotStarted,
    Preparing,
    Shipping,
    Compensating,
    Completed,
    Compensated
}

[GenerateSerializer, Immutable]
public sealed record OrderStatus(
    [property: Id(0)] string OrderId,
    [property: Id(1)] OrderStage Stage,
    [property: Id(2)] Guid LifecycleId = default)
{
    public bool IsTerminal => Stage is OrderStage.Completed or OrderStage.Compensated;
}

// Versions are comparable only within one subscription and lifecycle. Every subscription starts with a resync.
[GenerateSerializer, Immutable]
public sealed record OrderStatusUpdate(
    [property: Id(0)] OrderStatus Status,
    [property: Id(1)] Guid SubscriptionId,
    [property: Id(2)] long Version,
    [property: Id(3)] bool IsResync);

public sealed partial class WorkflowGrain
{
    public async Task<OrderStatus> GetOrderStatusAsync(CancellationToken cancellationToken = default)
    {
        await foreach (var update in WatchOrderStatusAsync(cancellationToken))
        {
            return update.Status;
        }

        throw new InvalidOperationException("The status watch ended before its initial snapshot.");
    }

    public async IAsyncEnumerable<OrderStatusUpdate> WatchOrderStatusAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var subscriptionId = Guid.NewGuid();
        var initial = true;
        var lifecycleId = Guid.Empty;
        await foreach (var change in _orderStatus.WatchAsync(cancellationToken: cancellationToken))
        {
            var status = change.Value ?? new(WorkflowId, OrderStage.NotStarted);
            yield return new(
                status,
                subscriptionId,
                change.Version,
                IsResync: initial || status.LifecycleId != lifecycleId);
            initial = false;
            lifecycleId = status.LifecycleId;
        }
    }

    private void SetOrderStage(OrderStage stage)
    {
        // Replay revisits earlier steps. Persisted progress belongs to this order for its entire lifetime.
        if (_orderStatus.Value is not { } current)
        {
            _orderStatus.Value = new(WorkflowId, stage, Guid.NewGuid());
        }
        else if (stage > current.Stage)
        {
            _orderStatus.Value = current with { Stage = stage };
        }
    }
}
