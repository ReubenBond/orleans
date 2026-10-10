using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.DurableMessaging;
using Orleans.Hosting;
using Orleans.Journaling;
using Orleans.Runtime;

#pragma warning disable ORLEANSEXP005

namespace Documentation.Grains.DurableMessaging;

internal static class MessagingConfiguration
{
    internal static void Configure(ISiloBuilder siloBuilder)
    {
        // <messaging_registration>
        siloBuilder.UseInMemoryDurableJobs();
        siloBuilder.AddVolatileJournalStorage();
        siloBuilder.AddDurableMessaging();
        MessagingProtocol.Register(siloBuilder.Services);
        // </messaging_registration>
    }
}

// <messaging_payload>
public static class MessagingSubjects
{
    public const string Notify = "notifications.notify.v1";
    public const string NotificationReceived = "notifications.received.v1";
    public const string ReserveStock = "inventory.reserve.v1";
    public const string Restock = "inventory.restock.v1";
    public const string ReservationResult = "inventory.reservation-result.v1";
    public const string ChargePayment = "payments.charge.v1";
    public const string PaymentResult = "payments.result.v1";
    public const string StockSnapshot = "inventory.snapshot.v1";
    public const string Shipment = "shipments.manifest.v1";
}

public static class MessagingProtocol
{
    public static void Register(IServiceCollection services)
    {
        services.AddDurableMessageType<Notify>(MessagingSubjects.Notify);
        services.AddDurableMessageType<NotificationReceived>(MessagingSubjects.NotificationReceived);
        services.AddDurableMessageType<ReserveStock>(MessagingSubjects.ReserveStock);
        services.AddDurableMessageType<Restock>(MessagingSubjects.Restock);
        services.AddDurableMessageType<ReservationResult>(MessagingSubjects.ReservationResult);
        services.AddDurableMessageType<ChargePayment>(MessagingSubjects.ChargePayment);
        services.AddDurableMessageType<PaymentResult>(MessagingSubjects.PaymentResult);
        services.AddDurableMessageType<StockSnapshot>(MessagingSubjects.StockSnapshot);
        services.AddDurableMessageType<Shipment>(MessagingSubjects.Shipment);
    }
}

[GenerateSerializer]
public sealed record Notify(
    [property: Id(0)] string? Text,
    [property: Id(2)] GrainId? ResponseDestination = null);

[GenerateSerializer]
public sealed record NotificationReceived(
    [property: Id(0)] string Text,
    [property: Id(1)] HierarchicalKey CommandId);
// </messaging_payload>

// <messaging_grain>
public interface INotificationGrain : IGrainWithStringKey, IDurableMessagingGrain
{
    ValueTask<int> GetCount();
}

public sealed class NotificationGrain(
    IDurableInbox inbox,
    IDurableOutbox outbox,
    [FromKeyedServices(MessagingSubjects.Notify)] DurableMessageType<Notify> notification,
    [FromKeyedServices(MessagingSubjects.NotificationReceived)] DurableMessageType<NotificationReceived> received,
    [FromKeyedServices("notification-count")] IDurableValue<int> count)
    : Grain, INotificationGrain
{
    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        inbox.RegisterHandlers(routes => routes
            .Register(notification, HandleNotification));
        return base.OnActivateAsync(cancellationToken);
    }

    public ValueTask<int> GetCount() => new(count.Value);

    private void HandleNotification(Notify message, IInboxHandlerContext context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message.Text);
        var nextCount = checked(count.Value + 1);
        if (message.ResponseDestination is { } recipient)
        {
            outbox.SendReply(received, context, recipient,
                new NotificationReceived(message.Text, context.Envelope.MessageId));
        }
        count.Value = nextCount;
        context.Complete();
    }
}
// </messaging_grain>

// <messaging_send>
public interface INotificationSenderGrain : IGrainWithStringKey, IDurableMessagingGrain
{
    Task SendAsync(HierarchicalKey commandId, GrainId receiver, string message);
}

public sealed class NotificationSenderGrain(
    IDurableOutbox outbox,
    IDurableStateManager stateManager,
    [FromKeyedServices("sent-count")] IDurableValue<int> sentCount,
    [FromKeyedServices(MessagingSubjects.Notify)] DurableMessageType<Notify> notification) : Grain, INotificationSenderGrain
{
    public async Task SendAsync(HierarchicalKey commandId, GrainId receiver, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        var nextCount = checked(sentCount.Value + 1);
        outbox.Send(notification, commandId, receiver, new Notify(message));
        sentCount.Value = nextCount;
        await stateManager.WriteStateAsync();
    }
}
// </messaging_send>

// <messaging_shipment>
[GenerateSerializer]
public sealed record Shipment(
    [property: Id(0)] ReserveStock Reservation,
    [property: Id(1)] byte[] Manifest);

public interface IShipmentSenderGrain : IGrainWithStringKey, IDurableMessagingGrain
{
    Task SendAsync(HierarchicalKey commandId, GrainId receiver, Shipment shipment);
}

public sealed class ShipmentSenderGrain(
    IDurableOutbox outbox,
    IDurableStateManager state,
    [FromKeyedServices(MessagingSubjects.Shipment)] DurableMessageType<Shipment> shipments)
    : Grain, IShipmentSenderGrain
{
    public async Task SendAsync(HierarchicalKey commandId, GrainId receiver, Shipment shipment)
    {
        outbox.Send(shipments, commandId, receiver, shipment);
        await state.WriteStateAsync();
    }
}
// </messaging_shipment>
