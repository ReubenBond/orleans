using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Durable.Journaling;
using Orleans.Durable.Messaging;
using Orleans.Durable.Tasks;
using Orleans.Placement;
using Orleans.Runtime;
using Orleans.Serialization;

namespace Tester.AzureUtils.DurableJobs;

/// <summary>A request to ship <see cref="Quantity"/> units of <see cref="Item"/>.</summary>
[GenerateSerializer]
public sealed record ShipmentRequest([property: Id(0)] string Item, [property: Id(1)] int Quantity);

/// <summary>A shipment the warehouse's journal records: the message which requested it and the silo which handled it.</summary>
[GenerateSerializer]
public sealed record WarehouseShipment(
    [property: Id(0)] string MessageId,
    [property: Id(1)] string Item,
    [property: Id(2)] int Quantity,
    [property: Id(3)] string Silo);

/// <summary>The application's message types.</summary>
public sealed class ApplicationMessages(Serializer serializer)
{
    public MessageType<ShipmentRequest> Ship { get; } = new("qualification.ship.v1", serializer);
}

/// <summary>One thing an application grain did: what, on which silo, and when.</summary>
public sealed record ProbeEvent(string Event, string Silo, DateTimeOffset At);

/// <summary>
/// What the application's grains did, observed outside their journals, so a test sees work which no client call caused,
/// such as a scheduled message which a Durable Jobs wakeup delivered.
/// </summary>
public sealed class ApplicationProbe
{
    private readonly ConcurrentQueue<ProbeEvent> _events = new();

    public void Record(string @event, string silo) => _events.Enqueue(new(@event, silo, TimeProvider.System.GetUtcNow()));

    public IReadOnlyList<ProbeEvent> Events => [.. _events];

    /// <summary>Returns the first record of <paramref name="event"/>, or <see langword="null"/> before there is one.</summary>
    public ProbeEvent? Find(string @event) => _events.FirstOrDefault(entry => entry.Event == @event);
}

/// <summary>Sends shipment requests through its outbox, which journals each with the grain's state.</summary>
public interface IDispatchGrain : IGrainWithStringKey
{
    /// <summary>Stages a shipment request to <paramref name="warehouse"/>, delivered at <paramref name="deliverAt"/> if set.</summary>
    Task SendAsync(string messageId, GrainId warehouse, ShipmentRequest request, DateTimeOffset? deliverAt);

    /// <summary>Returns the message IDs this grain's journal records as sent.</summary>
    Task<List<string>> GetSentAsync();

    /// <summary>Deactivates the activation once idle, so the next call replays the grain's journal.</summary>
    Task DeactivateAsync();
}

/// <summary>Records each shipment request its inbox receives in its journal, once per message ID.</summary>
public interface IWarehouseGrain : IGrainWithStringKey
{
    /// <summary>Returns the silo which hosts the activation.</summary>
    Task<string> GetSiloAsync();

    /// <summary>Returns the shipments the grain's journal records, in order.</summary>
    Task<List<WarehouseShipment>> GetShipmentsAsync();

    /// <summary>Deactivates the activation once idle, so the next call replays the grain's journal.</summary>
    Task DeactivateAsync();
}

/// <summary>Runs a durable fulfillment which waits on a durable delay, which resumes through a Durable Jobs wakeup.</summary>
public interface IFulfillmentGrain : IGrainWithStringKey
{
    /// <summary>Waits <paramref name="settle"/> durably, then returns twice <paramref name="quantity"/>.</summary>
    DurableTask<int> FulfillAsync(int quantity, TimeSpan settle);

    /// <summary>Returns the silo which hosts the activation.</summary>
    Task<string> GetSiloAsync();
}

[PreferLocalPlacement]
public sealed class DispatchGrain(
    IOutbox outbox,
    IDurableStateManager state,
    ApplicationMessages messages,
    [FromKeyedServices("sent")] IDurableList<string> sent) : Grain, IDispatchGrain
{
    public async Task SendAsync(string messageId, GrainId warehouse, ShipmentRequest request, DateTimeOffset? deliverAt)
    {
        outbox.Send(messages.Ship, MessageId.Create("shipments", messageId), warehouse, request, deliverAt);
        sent.Add(messageId);
        await state.WriteStateAsync();
    }

    public Task<List<string>> GetSentAsync() => Task.FromResult(sent.ToList());

    public Task DeactivateAsync()
    {
        this.DeactivateOnIdle();
        return Task.CompletedTask;
    }
}

[PreferLocalPlacement]
public sealed class WarehouseGrain(
    IInbox inbox,
    ApplicationMessages messages,
    ApplicationProbe probe,
    ILocalSiloDetails silo,
    [FromKeyedServices("shipments")] IDurableList<WarehouseShipment> shipments) : Grain, IWarehouseGrain
{
    private string Silo => silo.SiloAddress.ToParsableString();

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        probe.Record($"activated:warehouse:{this.GetPrimaryKeyString()}", Silo);
        inbox.Register(messages.Ship, Handle);
        return base.OnActivateAsync(cancellationToken);
    }

    public Task<string> GetSiloAsync() => Task.FromResult(Silo);

    public Task<List<WarehouseShipment>> GetShipmentsAsync() => Task.FromResult(shipments.ToList());

    public Task DeactivateAsync()
    {
        this.DeactivateOnIdle();
        return Task.CompletedTask;
    }

    private void Handle(ShipmentRequest request, IInboxHandlerContext context)
    {
        var messageId = context.Message.Envelope.MessageId.Key.ToString();
        probe.Record($"shipped:{messageId}", Silo);
        shipments.Add(new(messageId, request.Item, request.Quantity, Silo));
        context.Complete();
    }
}

[PreferLocalPlacement]
public sealed class FulfillmentGrain(ApplicationProbe probe, ILocalSiloDetails silo) : DurableGrain, IFulfillmentGrain
{
    private string Silo => silo.SiloAddress.ToParsableString();

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        probe.Record($"activated:fulfillment:{this.GetPrimaryKeyString()}", Silo);
        return base.OnActivateAsync(cancellationToken);
    }

    public async DurableTask<int> FulfillAsync(int quantity, TimeSpan settle)
    {
        probe.Record($"fulfilling:{this.GetPrimaryKeyString()}", Silo);
        await DurableTask.Delay(settle).WithId("settle");
        probe.Record($"fulfilled:{this.GetPrimaryKeyString()}", Silo);
        return checked(quantity * 2);
    }

    public Task<string> GetSiloAsync() => Task.FromResult(Silo);
}

/// <summary>The JSON journal format's metadata for the application grains' state.</summary>
[JsonSerializable(typeof(WarehouseShipment))]
[JsonSerializable(typeof(string))]
internal sealed partial class MixedProviderApplicationJsonContext : JsonSerializerContext;
