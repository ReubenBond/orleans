using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.DurableTasks;
using Orleans.Journaling;

namespace Orleans.DurableTasks.Playground;

public static class CakeShop
{
    public static void Setup(IServiceCollection services)
    {
        services.AddSingleton<ICatalogService, CatalogService>();
        services.AddSingleton<IPaymentService, PaymentService>();
        services.AddSingleton<ILogisticsService, LogisticsService>();
    }

    public static async Task Run(IGrainFactory grainFactory)
    {
        var buyer = grainFactory.GetGrain<IBuyerAccount>(Guid.NewGuid());
        var order = new Order();
        var orderProcessor = grainFactory.GetGrain<IOrderProcessor>(Guid.NewGuid());
        var orderTask = await orderProcessor.ProcessOrderAsync(buyer, order).ScheduleAsync("order-66");
        var res = orderTask.AsTask();
        while (!res.IsCompleted)
        {
            await Task.Delay(1000);
            var status = await orderProcessor.GetStatus();
            Console.WriteLine($"... waiting for order to complete. Status: {status}");
        }

        await res;
        Console.WriteLine("Order completed!");
    }
}

// Example: eShop order process
public interface IBuyerAccount : IGrainWithGuidKey { }

[GenerateSerializer]
public record class Invoice();

[GenerateSerializer]
public record class PaymentResult()
{
    public bool IsSuccess { get; internal set; }
}

public interface IPaymentService
{
    DurableTask<Invoice> CreateInvoice(IBuyerAccount buyer, Order order);
    DurableTask<PaymentResult> WaitForPayment(Invoice invoice);
}

[GenerateSerializer]
public record class StockCheckResult(bool HasStock);

[GenerateSerializer]
public record class CreateShipmentResult(bool IsSuccess);

public interface ICatalogService
{
    DurableTask<List<StockCheckResult>> CheckOrderStock(Order order);
}
public interface ILogisticsService
{
    DurableTask<CreateShipmentResult> CreateShipment(Order order);
    DurableTask WaitForDelivery(CreateShipmentResult shipmentDetails);
}
[GenerateSerializer]
public record class Order();

public interface IOrderProcessor : IGrainWithGuidKey
{
    ValueTask<OrderStatus> GetStatus();
    DurableTask ProcessOrderAsync(IBuyerAccount buyer, Order order);
}

[GenerateSerializer]
public enum OrderStatus
{
    None,
    Created,
    Confirmed,
    Canceled,
    Paid,
    Shipped,
    Delivered,
    InsufficientStock,
    PaymentFailed,
    ShipmentFailed,
}

[GenerateSerializer]
public record class OrderState
{
    [Id(0)]
    public OrderStatus Status { get; set; }
}

public class OrderProcessorGrain : DurableGrain, IOrderProcessor
{
    private readonly IPaymentService _paymentService;
    private readonly ICatalogService _catalogService;
    private readonly ILogisticsService _logisticsService;

    private readonly DurableValue<OrderStatus> _status;
    private readonly DurableTaskCompletionSource<bool> _cancellation;

    public OrderProcessorGrain(
        IPaymentService paymentService,
        ICatalogService catalogService,
        ILogisticsService logisticsService)
    {
        _paymentService = paymentService;
        _catalogService = catalogService;
        _logisticsService = logisticsService;

        _cancellation = GetOrCreateTaskCompletionSource<bool>("cancellation");
        _status = GetOrCreateValue<OrderStatus>("order");
    }

    public async ValueTask CancelAsync()
    {
        _cancellation.TrySetResult(true);
        await WriteStateAsync();
    }

    public ValueTask<OrderStatus> GetStatus() => new(_status.Value);

    public async DurableTask ProcessOrderAsync(IBuyerAccount buyer, Order order)
    {
        var confirmed = await WaitForGracePeriod().AsStep("wait-for-grace-period");
        if (!confirmed)
        {
            // The order was canceled using task management APIs within the grace period.
            _status.Value = OrderStatus.Canceled;
            return;
        }

        var stockLevelResult = await _catalogService.CheckOrderStock(order).AsStep("check-stock");
        if (stockLevelResult.Any(item => !item.HasStock))
        {
            // There is insufficient stock. No charge has been made yet, but we would likely need to notify the user before terminating.
            _status.Value = OrderStatus.InsufficientStock;
            return;
        }

        var invoice = await _paymentService.CreateInvoice(buyer, order).AsStep("create-invoice");

        // This might take a very long time (hours, days, indefinite)
        var paymentResult = await _paymentService.WaitForPayment(invoice).AsStep("process-payment");
        if (!paymentResult.IsSuccess)
        {
            _status.Value = OrderStatus.PaymentFailed;
            return;
        }

        _status.Value = OrderStatus.Paid;
        var shipmentDetails = await _logisticsService.CreateShipment(order).AsStep("create-shipment");
        if (!shipmentDetails.IsSuccess)
        {
            _status.Value = OrderStatus.ShipmentFailed;
            return;
        }

        await _logisticsService.WaitForDelivery(shipmentDetails).AsStep("wait-for-delivery");
        _status.Value = OrderStatus.Delivered;

        // Done...
    }

    private async DurableTask<bool> WaitForGracePeriod()
    {
        var delayTask = DurableTask.Delay(TimeSpan.FromSeconds(1)).AsStep("wait-for-confirmation").AsTask();
        var cancellationTask = _cancellation.Task;
        return await Task.WhenAny(delayTask, cancellationTask).Unwrap();
    }
}

public class PaymentService : IPaymentService
{
    public DurableTask<Invoice> CreateInvoice(IBuyerAccount buyer, Order order)
    {
        return DurableTask.Run(() => new Invoice());
    }

    public DurableTask<PaymentResult> WaitForPayment(Invoice invoice)
    {
        return DurableTask.Run(() => new PaymentResult() { IsSuccess = true });
    }
}

public class CatalogService : ICatalogService
{
    public DurableTask<List<StockCheckResult>> CheckOrderStock(Order order) => DurableTask.Run(() => new List<StockCheckResult> { new StockCheckResult(true) });
}

public class LogisticsService : ILogisticsService
{
    public DurableTask<CreateShipmentResult> CreateShipment(Order order) => DurableTask.Run(() => new CreateShipmentResult(true));
    public DurableTask WaitForDelivery(CreateShipmentResult shipmentDetails) => DurableTask.Delay(TimeSpan.FromSeconds(5));
}
