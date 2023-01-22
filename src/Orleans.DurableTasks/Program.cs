using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using System.Threading.Tasks.Sources;
using Orleans.DurableTasks.Remoting;
using Orleans.Transactions.Abstractions;
using Orleans.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration.Internal;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Orleans.DurableTasks;

public static class DurableTaskHostingExtensions
{
    public static ISiloBuilder AddDurableTasks(this ISiloBuilder siloBuilder)
    {

        siloBuilder.Services.AddTransient<VolatileDurableTaskGrainStorage>();
        siloBuilder.Services.AddFromExisting<IDurableTaskGrainStorage, VolatileDurableTaskGrainStorage>();

        siloBuilder.Services.AddSingleton<DurableTaskGrainExtensionShared>();
        siloBuilder.Services.AddScoped<DurableTaskGrainExtension>();
        siloBuilder.Services.AddFromExisting<IDurableTaskGrainRuntime, DurableTaskGrainExtension>();
        siloBuilder.Services.AddTransientKeyedService<Type, IGrainExtension>(typeof(IDurableTaskGrainExtension), (sp, _) => sp.GetRequiredService<DurableTaskGrainExtension>());
        siloBuilder.Services.AddTransientKeyedService<Type, IGrainExtension>(typeof(IDurableTaskServer), (sp, _) => sp.GetRequiredService<DurableTaskGrainExtension>());
        siloBuilder.Services.AddTransientKeyedService<Type, IGrainExtension>(typeof(IDurableTaskClient), (sp, _) => sp.GetRequiredService<DurableTaskGrainExtension>());

        siloBuilder.Services.AddSingleton<DefaultRetryPolicy>();
        siloBuilder.Services.AddSingleton<ISystemClock, SystemClock>();
        return siloBuilder;
    }
}

#pragma warning disable ORLEANS0009 // Grain interfaces methods must return a compatible type
#pragma warning disable CS1998 // Async method lacks 'await' operators and will run synchronously
public class Program
{
    public interface IBankGrain : IGrainWithStringKey
    {
        DurableTask<bool> Transfer(IAccountGrain source, IAccountGrain destination, long amount);
    }

    public interface IAccountGrain : IGrainWithStringKey
    {
        DurableTask<bool> Withdraw(long amount);
        DurableTask Deposit(long amount);
        ValueTask<long> GetBalance();
    }

    public class BankGrain : IBankGrain
    {
        public async DurableTask<bool> Transfer(
            IAccountGrain source,
            IAccountGrain destination,
            long amount)
        {
            bool success = await source.Withdraw(amount).AsStep("withdraw");
            if (!success) return false;
            await destination.Deposit(amount).AsStep("deposit");
            return success;
        }
    }

    public class AccountGrain : Grain<long>, IAccountGrain
    {
        public async DurableTask Deposit(long amount) => State += amount;

        public async DurableTask<bool> Withdraw(long amount)
        {
            if (State >= amount)
            {
                State -= amount;
                return true;
            }

            return false;
        }

        public ValueTask<long> GetBalance() => new(State);
    }

    public interface IClientGrain : IGrainWithStringKey
    {
        Task Run();
        DurableTask RunWorkflow();
    }

    public class ClientGrain : Grain, IClientGrain
    {
        public async Task Run()
        {
            var client = this.GrainFactory;
            var bankGrain = client.GetGrain<IBankGrain>("first-tech");
            var billGates = client.GetGrain<IAccountGrain>("billg");
            var me = client.GetGrain<IAccountGrain>("rebond");

            var scheduledTask = await bankGrain
                .Transfer(billGates, me, 1_000_000_000)
                .ScheduleAsync("transfer123");

            var success = await scheduledTask;
            Console.WriteLine(success ? "Success!" : "Fail :(");
            Console.WriteLine("BillG balance: " + await billGates.GetBalance());
            Console.WriteLine("Me balance: " + await me.GetBalance());
        }

        public async DurableTask RunWorkflow()
        {
            var client = this.GrainFactory;
            var bankGrain = client.GetGrain<IBankGrain>("first-tech");
            var billGates = client.GetGrain<IAccountGrain>("billg");
            var me = client.GetGrain<IAccountGrain>("rebond");

            var randomId = await DurableTask.Run(Guid.NewGuid).AsStep("generate-random-id");
            Console.WriteLine(randomId);

            // If the task is interrupted (eg, power outage) and is retried, it will only sleep for the remaining time.
            var slept = await DurableTask.Delay(TimeSpan.FromSeconds(1)).AsStep("wait-for-confirmation");
            Console.WriteLine("slept? " + slept);

            var scheduledTask = await bankGrain
                .Transfer(billGates, me, 1_000_000_000)
                .ScheduleAsync("transfer123");

            var success = await scheduledTask;
            Console.WriteLine(success ? "Success!" : "Fail :(");
            Console.WriteLine("BillG balance: " + await billGates.GetBalance());
            Console.WriteLine("Me balance: " + await me.GetBalance());
        }
    }

    public static async Task Main(string[] args)
    {
        var hostBuilder = Host.CreateDefaultBuilder(args)
            .UseOrleans(siloBuilder =>
            {
                siloBuilder.UseLocalhostClustering();
                siloBuilder.AddMemoryGrainStorageAsDefault();
                siloBuilder.AddDurableTasks();
            })
            .ConfigureLogging(logging =>
            {
                //logging.AddFilter((category, level) => category is not null && category.StartsWith("Orleans.DurableTasks"));
            })
            .UseConsoleLifetime();
        using var host = hostBuilder.Build();
        await host.StartAsync();

        var client = host.Services.GetRequiredService<IClusterClient>();

        var bankGrain = client.GetGrain<IBankGrain>("first-tech");
        var billGates = client.GetGrain<IAccountGrain>("billg");
        var me = client.GetGrain<IAccountGrain>("rebond");

        // await await?!
        await await billGates.Deposit(120_000_000_000).ScheduleAsync("create-pc-industry");

        var scheduledTask = await bankGrain
            .Transfer(billGates, me, 20)
            .ScheduleAsync("transfer123");

        var success = await scheduledTask;
        Console.WriteLine(success ? "Success!" : "Fail :(");
        Console.WriteLine("BillG balance: " + await billGates.GetBalance());
        Console.WriteLine("Me balance: " + await me.GetBalance());

        var clientGrain = client.GetGrain<IClientGrain>("client");
        Console.WriteLine("Now to do the same thing via a regular grain call");
        await clientGrain.Run();
        Console.WriteLine("Now to do a similar thing via a grain workflow call");
        await await clientGrain.RunWorkflow().ScheduleAsync("my-client-wf");

        Console.WriteLine("Done!");

        await host.WaitForShutdownAsync();
    }

#if false
    public static async Task Main(string[] args)
    {
        var prog = new Program();

        /*
        var result = await prog.GoBeDurable("Bob", 47);
        Console.WriteLine(result);
        */

        // The above is shorthand for the following.

        // Define a task. No task code will be executed until it is scheduled.
        DurableTask<int> taskDefinition = prog.GoBeDurable("Bob", 47);

        // Schedule the task. This would write the definition above to storage, alongside the options specified here (retry, identity, etc)
        ScheduledTask<int> scheduledResult = await taskDefinition.ScheduleAsync(
            "foo",
            new SchedulingOptions
            {
                DueTime = DateTimeOffset.UtcNow,
                RetryOptions = new RetryOptions
                {
                    MaximumNumberOfAttempts = 3,
                    RetryFilter = RetryFilter
                }
            });

        // Await the completion of the scheduled task and print the result
        int finalResult = await scheduledResult;
        Console.WriteLine(finalResult);

        static bool RetryFilter(Exception exception)
        {
            return true;
        }
    }

    public async DurableTask<int> GoBeDurable(string name, int bestNumber)
    {
        // Await some task
        await Task.Yield();

        // Schedule a child task and await its result
        await GoToSleep(1000);

        // Return a result to the caller
        return bestNumber * name.GetHashCode();
    }

    public async DurableTask GoToSleep(int delayMillis)
    {
        await Task.Yield();
    }
#endif
}

public interface ITransferGrain : IGrain
{
    DurableTask<bool> Transfer(IAccountGrain source, IAccountGrain destination, int amount);
}

public interface IAccountGrain : IGrain
{
    DurableTask Credit(int amount);

    // Deducting funds might fail and return false if the account would be overdrawn
    DurableTask<bool> TryDebit(int amount);
}

internal interface ICopyProcessorGrain : IGrain
{
    DurableTask Copy(string source, string destination, string startRowId, string endRowId);
}

internal interface IDbServiceFactory
{
    IDbService GetDb(string connectionString);
}

internal interface IDbService
{
    IAsyncEnumerable<(string Key, string Value)> ReadRangeAsync(string startRowId, string endRowId, int limit);
    ValueTask InsertOrUpdateRowAsync(string key, string value);
}

#if false
// Example: ETL - load data from one database and store it into another.
// This example uses a fake Database API for simplicity.
// 
internal class CopyProcessorGrain : Grain, ICopyProcessorGrain
{
    private readonly IDbServiceFactory _dbServiceFactory;

    [GenerateSerializer]
    internal class CopyState
    {
        [Id(0)]
        public string? LastCopiedRow { get; set; }
    }

    public CopyProcessorGrain(IDbServiceFactory dbServiceFactory)
    {
        _dbServiceFactory = dbServiceFactory;
    }

    public async DurableTask Copy(string source, string destination, string startRowId, string endRowId)
    {
        var ctx = DurableTaskContext.CurrentTask!;
        var sourceDb = _dbServiceFactory.GetDb(source);
        var destinationDb = _dbServiceFactory.GetDb(destination);

        var state = ctx.GetState<CopyState>("currentRow");
        state.Value.LastCopiedRow ??= startRowId;

        while (!ctx.IsCancellationRequested)
        {
            var hasRows = false;
            await foreach (var (rowKey, rowValue) in sourceDb.ReadRangeAsync(startRowId: state.Value.LastCopiedRow, endRowId: endRowId, limit: 100))
            {
                await destinationDb.InsertOrUpdateRowAsync(rowKey, rowValue);
                state.Value.LastCopiedRow = rowKey;
                hasRows = true;
            }

            if (!hasRows)
            {
                // Done!
                break;
            }

            // Update the state of the workflow in case it gets terminated and needs to restart.
            // This does not need to happen for every iteration: it's ok to only perform it occasionally (eg, every 100 iterations) to improve performance.
            await state.WriteStateAsync();
        }
    }
}
#endif

#if false
public interface IStateManager
{
    IPersistentState<T> GetState<T>(string name);
    ValueTask WriteStateAsync();
}


// Example: soft-delete with a 30-day delayed hard-delete
public interface ISubscriptionGrain : IGrain
{
    Task Subscribe(IAccountGrain account);
    Task CancelSubscription();
}

interface ISubscriptionGrainInternal : IGrain
{
    DurableTask ProcessSubscription();
    DurableTask ProcessCancellation();
}

[GenerateSerializer]
public class CustomerAccount
{
    [Id(0)] public string? AccountId { get; set; }
}

[GenerateSerializer]
public class SubscriptionGrainState
{
    [Id(0)] public DateTime NextBillingCycle { get; set; }
    [Id(1)] public DateTime? CurrentBillingCycleStart { get; set; }
    [Id(2)] public DateTime? CanceledSince { get; set; }
    [Id(3)] public SubscriptionStatus SubscriptionStatus { get; set; }
    [Id(4)] public CustomerAccount? Account { get; set; }
    [Id(5)] public string? NextBillingProcessId { get; set; }
}

[GenerateSerializer]
public record class CurrencyAmount(double Amount, string Currency);

public enum SubscriptionStatus
{
    None,
    Valid,
    Canceled,
    PaymentError,
}

public class SubscriptionGrain : Grain<SubscriptionGrainState>, ISubscriptionGrain
{
    const string SubscriptionTaskName = "process";
    const string CancelTaskName = "cancel";
    const double Fee = 100.0;
    static readonly TimeSpan BillingPeriod = TimeSpan.FromDays(30);

    readonly IPaymentGateway _paymentGateway;

    public SubscriptionGrain(IPaymentGateway paymentGateway)
    {
        _paymentGateway = paymentGateway;
    }

    public async Task Subscribe(CustomerAccount account)
    {
        State.Account = account;
        State.CurrentBillingCycleStart = DateTime.UtcNow;
        State.NextBillingCycle = State.CurrentBillingCycleStart.Value.Add(BillingPeriod);
        await WriteStateAsync();

        await this.AsReference<ISubscriptionGrainInternal>()
          .ProcessSubscription()
          .ScheduleAsync(SubscriptionTaskName);
    }

    public async Task CancelSubscription(CustomerAccount account)
    {
        await this.AsReference<ISubscriptionGrainInternal>()
          .ProcessCancellation()
          .ScheduleAsync("cancel");
    }

    public async DurableTask ProcessCancellation()
    {
        // If the subscription was already paused before we started execution, remember that fact and bail out.
        var alreadyCanceled = await DurableTaskContext.CurrentTask!.GetOrAddStateAsync<bool>("already-canceled", State.CanceledSince.HasValue);
        if (alreadyCanceled.Value)
        {
            return;
        }

        var canceledSince = await DurableTaskContext.CurrentTask!.GetOrAddStateAsync<DateTime>("canceled-since", DateTime.UtcNow);
        State.CanceledSince = canceledSince.Value;

        // Since the state was updated, we need to persist that change.
        await WriteStateAsync();

        // Get a task from the list of scheduled tasks on the instance.
        await GetTask(SubscriptionTaskName).CancelAsync();

        // We are a friendly company, so we will refund the remaining amount.
        if (State.CurrentBillingCycleStart.HasValue && State.SubscriptionStatus == SubscriptionStatus.Valid)
        {
            var refundDays = State.CurrentBillingCycleStart.Value + BillingPeriod - canceledSince.Value;
            var refundAmount = Fee * refundDays.TotalDays / BillingPeriod.TotalDays;
            await ProcessPayment(new CurrencyAmount(refundAmount, "USD")).AsStep("process-refund");
        }
    }

    public async DurableTask ProcessSubscription()
    {
        try
        {
            // Try charging the account.
            // If successful, update the status to valid and schedule the subsequent billing cycle.
            await ProcessPayment(new CurrencyAmount(Fee, "USD")).AsStep("process-payment");
            State.SubscriptionStatus = SubscriptionStatus.Valid;
            State.CurrentBillingCycleStart = State.NextBillingCycle;
            State.NextBillingCycle = State.NextBillingCycle.Add(BillingPeriod);

            // Schedule to charge the customer again at the start of the next billing cycle.
            await this.AsReference<ISubscriptionGrainInternal>()
              .ProcessSubscription()
              // The semantics of this must be that it reschedules the task with the specified id, clearing its state
              // Should there be an API to differentiate 
              .ScheduleAsync(SubscriptionTaskName, State.NextBillingCycle);
        }
        catch
        {
            // Indicate that the account has not been paid and try again in a day.
            State.SubscriptionStatus = SubscriptionStatus.PaymentError;
            var nextAttempt = DateTime.UtcNow.Date.AddDays(1);

            // Schedule to charge the customer again at the start of the next billing cycle.
            await this.AsReference<ISubscriptionGrainInternal>()
              .ProcessSubscription()
              .ScheduleAsync(SubscriptionTaskName, nextAttempt);
            return;
        }
    }

    private async DurableTask ProcessPayment(CurrencyAmount amount)
    {
        // Generate an idempotency key for the payments API
        // This is used to ensure exactly-once processing of payments.
        var key = await DurableTaskContext.CurrentTask!.GetOrAddStateAsync<string>(
            "payment-idempotency-key",
            static () => Guid.NewGuid().ToString("N"));

        await _paymentGateway.ProcessCharge(
          customer: State.Account,
          amount: amount.Amount,
          currency: amount.Currency,
          idempotencyKey: key.Value);
    }
}

// Experiment - using a different base class for orchestrators
public abstract class DurableTaskOrchestrator
{
}

public interface ISubscriptionProcessor
{
    Task CreateSubscription();
    Task PauseSubscription(DateTimeOffset until);
    Task ResumeSubscription();
    Task UpdateBillingInformation();
    Task GetCurrentStatus();
}

public class SubscriptionProcessor : DurableTaskOrchestrator
{
    DurableTask Run(DurableTaskContext context);
}
#endif

// Example: eShop order process
public interface IBuyerAccount : IGrain { }

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
public interface IOrderProcessor : IGrain
{
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
public class OrderState
{
    [Id(0)]
    public OrderStatus Status { get; set; }
}

public class OrderProcessorGrain : Grain<OrderState>, IOrderProcessor
{
    private readonly IPaymentService _paymentService;
    private readonly ICatalogService _catalogService;
    private readonly ILogisticsService _logisticsService;

    public OrderProcessorGrain(IPaymentService paymentService, ICatalogService catalogService, ILogisticsService logisticsService)
    {
        _paymentService = paymentService;
        _catalogService = catalogService;
        _logisticsService = logisticsService;
    }

    public async DurableTask ProcessOrderAsync(IBuyerAccount buyer, Order order)
    {
        try
        {
            var confirmed = await DurableTask.Delay(TimeSpan.FromMinutes(1)).AsStep("wait-for-confirmation");
            if (!confirmed)
            {
                // The order was canceled using task management APIs within the grace period.
                State.Status = OrderStatus.Canceled;
                return;
            }

            var stockLevelResult = await _catalogService.CheckOrderStock(order).AsStep("check-stock");
            if (stockLevelResult.Any(item => !item.HasStock))
            {
                // There is insufficient stock. No charge has been made yet, but we would likely need to notify the user before terminating.
                State.Status = OrderStatus.InsufficientStock;
                return;
            }

            var invoice = await _paymentService.CreateInvoice(buyer, order).AsStep("create-invoice");

            // This might take a very long time (hours, days, indefinite)
            var paymentResult = await _paymentService.WaitForPayment(invoice).AsStep("get-that-bag");
            if (!paymentResult.IsSuccess)
            {
                State.Status = OrderStatus.PaymentFailed;
                return;
            }

            State.Status = OrderStatus.Paid;
            var shipmentDetails = await _logisticsService.CreateShipment(order).AsStep("create-shipment");
            if (!shipmentDetails.IsSuccess)
            {
                State.Status = OrderStatus.ShipmentFailed;
                return;
            }

            await _logisticsService.WaitForDelivery(shipmentDetails).AsStep("wait-for-delivery");
            State.Status = OrderStatus.Delivered;

            // Done...
        }
        catch (OperationCanceledException)
        {
            // Perform any cancellation cleanup. 
            // TODO: properly design cancellation. Eg, when a durable task is canceled, cancellation propagates to all pending steps in the task.
            // Note that it's important to allow cleanup code (catch/finally blocks, for ex) to execute for any method, so cancellation is cooperative
            // and upon recovery, the runtime must wait for a precondition set of tasks to have been started before triggering cancellation.
        }
    }
}

/*
public class TransferGrain : ITransferGrain
{
    public interface ITransactionContext : IAsyncDisposable, IDisposable
    {
        ValueTask CommitAsync();
        ValueTask AbortAsync();
    }

    public bool StartTransaction(string key, [NotNullWhen(true)] out ITransactionContext? transaction)
    {
        transaction = default;
        return false;
    }

    public async DurableTask<bool> Transfer(IAccountGrain source, IAccountGrain destination, int amount)
    {
        if (StartTransaction("step1", out var txn))
        {
            var state = await _state.JoinWriteTransaction();

            await txn.CommitAsync();
        }
        var ctx = ScheduledTaskContext.Current!;

        // Durably schedule the debit task and wait for completion. The fixed id ensures that exactly one operation will be scheduled.
        // Subsequent calls (eg, during recovery) will receive a reference the same logical task.
        bool fundsAvailable = await source.TryDebit(amount).InvokeAsync(ctx.Id + "/debit");

        if (fundsAvailable)
        {
            await destination.Credit(amount).InvokeAsync(ctx.Id + "/credit");
        }

        return fundsAvailable;
    }
}

public interface IScheduledTaskCollection<TInterface> where TInterface : class
{
    void Defer(Func<TInterface, DurableTask> taskFunc);
    ValueTask CommitAsync();
}

public class AccountState { public int Balance { get; set; } }

public class AccountGrain : IAccountGrain
{
    private readonly ITransactionalState<AccountState> _state;
    public AccountGrain(ITransactionalState<AccountState> state) => _state = state;

    public async DurableTask<bool> TryDebit(int amount)
    {
        // Get the durable, mutable task context.
        var context = ScheduledTaskContext.Current!;

        // Enter a transaction. The result of the transactional invocation of the delegate
        // will be transactionally stored as "debitAmount" on the ScheduledTaskContext.
        // I.e, the transaction participants are ScheduledTaskContext and _state.
        // During recovery, if the value is present in the context, then the delegate will not be invoked a second time.
        var result = await context.GetOrAddInTransaction(
            "debitAmount",
            TransactionOption.CreateOrJoin,
            async () =>
            {
                // Join the transaction as a writer
                var currentValue = await _state.JoinTransaction(readOnly: false);

                if (currentValue.Balance >= amount)
                {
                    currentValue.Balance -= amount;
                    return true;
                }

                return false;
            });

        return result;
    }

    public async DurableTask Credit(int amount)
    {
        // Get the durable, mutable task context.
        var context = ScheduledTaskContext.Current!;

        // Enter a transaction
        await context.GetOrAddInTransaction(
            "creditAmount",
            TransactionOption.CreateOrJoin,
            async () =>
            {
                // Join the transaction as a writer
                var currentValue = await _state.JoinTransaction(readOnly: false);
                currentValue.Balance += amount;
            });
    }
}


public static class TransactionalStateExtensions
{
    public static ValueTask<IAsyncDisposable> EnterWriteTransaction<T>(this ITransactionalState<T> state, TransactionOption option) where T : class, new()
    {
        _ = option;
        _ = state;
        return default;
    }
}

#endif
*/


public interface IMyGrainWithDurableTasks : IGrain
{
    DurableTask MyDurableTaskMethod(int a, string b);
    DurableTask<string> MyDurableTaskMethod2(int a, string b);
    DurableTask<T> MyDurableTaskMethod3<T>(T a);
}

#pragma warning restore CS1998 // Async method lacks 'await' operators and will run synchronously
#pragma warning restore ORLEANS0009 // Grain interfaces methods must return a compatible type
