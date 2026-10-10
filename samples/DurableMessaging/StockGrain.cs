using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.DurableMessaging;
using Orleans.Journaling;

namespace DurableMessaging;

public interface IStockGrain : IGrainWithStringKey, IDurableMessagingGrain
{
    Task InitializeAsync(int quantity);
    Task<StockSnapshot> GetSnapshotAsync();
}

public sealed class StockGrain(
    IDurableInbox inbox,
    IDurableOutbox outbox,
    IDurableStateManager state,
    [FromKeyedServices(StockProtocol.Reserve)] DurableMessageType<ReserveStock> reserve,
    [FromKeyedServices(StockProtocol.Restock)] DurableMessageType<Restock> restock,
    [FromKeyedServices(StockProtocol.Result)] DurableMessageType<ReservationOutcome> result)
    : Grain, IStockGrain
{
    private readonly IDurableValue<Inventory> _inventory = state.GetOrAddState<IDurableValue<Inventory>>("stock");

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        inbox.RegisterHandlers(routes => routes
            .Register(reserve, this, static (request, grain, context) => grain.HandleReserveStock(request, context))
            .Register(restock, this, static (request, grain, context) => grain.HandleRestock(request, context)));
        return base.OnActivateAsync(cancellationToken);
    }

    public async Task InitializeAsync(int quantity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(quantity);
        if (_inventory.Value is not null)
        {
            throw new InvalidOperationException("Stock has already been initialized.");
        }
        _inventory.Value = new(quantity, 0, 0);
        await state.WriteStateAsync();
    }

    public Task<StockSnapshot> GetSnapshotAsync() => Task.FromResult(new StockSnapshot(
        _inventory.Value ?? throw new InvalidOperationException("Initialize stock first.")));

    private void HandleReserveStock(ReserveStock request, IInboxHandlerContext context)
    {
        var inventory = _inventory.Value ?? throw new InvalidOperationException("Initialize stock first.");
        var commandId = context.Envelope.MessageId;
        ReservationOutcome outcome = request.Quantity switch
        {
            <= 0 => new ReservationRejected(commandId, request.Quantity, inventory.Remaining,
                ReservationRejectionReason.InvalidQuantity),
            var quantity when quantity > inventory.Remaining => new ReservationRejected(commandId, quantity, inventory.Remaining,
                ReservationRejectionReason.InsufficientStock),
            var quantity => new ReservationAccepted(commandId, quantity, inventory.Remaining - quantity)
        };
        var reservations = inventory.Reservations;
        if (outcome is ReservationAccepted)
        {
            reservations = checked(reservations + 1);
        }
        var next = new Inventory(outcome.RemainingStock,
            reservations,
            checked(inventory.ProcessedRequests + 1));
        // SendReply encodes before staging; remaining changes run synchronously through return.
        outbox.SendReply(result, context, request.ReplyDestination, outcome);
        _inventory.Value = next;
        context.Complete();
    }

    private void HandleRestock(Restock request, IInboxHandlerContext context)
    {
        if (request.Quantity <= 0)
        {
            context.Fail("Restock quantity must be positive.");
            return;
        }
        var inventory = _inventory.Value ?? throw new InvalidOperationException("Initialize stock first.");
        var next = inventory with
        {
            Remaining = checked(inventory.Remaining + request.Quantity),
            ProcessedRequests = checked(inventory.ProcessedRequests + 1)
        };
        _inventory.Value = next;
        context.Complete();
    }
}
