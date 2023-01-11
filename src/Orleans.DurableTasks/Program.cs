using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Tracing;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using System.Threading.Tasks.Sources;
using Orleans.DurableTasks.Remoting;
using Orleans.Transactions.Abstractions;
using Orleans.Runtime;

namespace Orleans.DurableTasks;

public class Program
{
    static void Main(string[] args)
    {
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

#if false
public interface ITransferGrain : IGrain
{
#pragma warning disable ORLEANS0009 // Grain interfaces methods must return a compatible type
    DurableTask<bool> Transfer(IAccountGrain source, IAccountGrain destination, int amount);
#pragma warning restore ORLEANS0009 // Grain interfaces methods must return a compatible type
}

public interface IAccountGrain : IGrain
{
#pragma warning disable ORLEANS0009 // Grain interfaces methods must return a compatible type
    DurableTask Credit(int amount);
#pragma warning restore ORLEANS0009 // Grain interfaces methods must return a compatible type

    // Deducting funds might fail and return false if the account would be overdrawn
#pragma warning disable ORLEANS0009 // Grain interfaces methods must return a compatible type
    DurableTask<bool> TryDebit(int amount);
#pragma warning restore ORLEANS0009 // Grain interfaces methods must return a compatible type
}

internal interface ICopyProcessorGrain : IGrain
{
#pragma warning disable ORLEANS0009 // Grain interfaces methods must return a compatible type
    DurableTask Copy(string source, string destination, string startRowId, string endRowId);
#pragma warning restore ORLEANS0009 // Grain interfaces methods must return a compatible type
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
#pragma warning disable ORLEANS0009 // Grain interfaces methods must return a compatible type
    DurableTask ProcessSubscription();
    DurableTask ProcessCancellation();
#pragma warning restore ORLEANS0009 // Grain interfaces methods must return a compatible type
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

// Example: eShop order process
public interface IBuyerAccount : IGrain { }
public interface IPaymentService { }
public interface ICatalogService
{
    Task CheckStock(.......)
}
public interface ILogisticsService { }
[GenerateSerializer]
public record class Order();
public interface IOrderProcessor : IGrain
{
#pragma warning disable ORLEANS0009 // Grain interfaces methods must return a compatible type
    DurableTask ProcessOrderAsync(IBuyerAccount buyer, Order order);
#pragma warning restore ORLEANS0009 // Grain interfaces methods must return a compatible type
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

    public async DurableTask CancelOrder()
    {
        if (State.Status is OrderStatus.None or OrderStatus.Created)
        {
            State.Status = OrderStatus.Canceled;
            await WriteStateAsync();
        }
    }

    public async DurableTask<Guid> GenerateId()
    {
        return Guid.NewGuid();
    }

    public async DurableTask ProcessOrderAsync(IBuyerAccount buyer, Order order)
    {
        var status = await DurableTaskContext.CurrentTask!.GetOrAddStateAsync("status", OrderStatus.None);
        if (status.Value is OrderStatus.None)
        {
            State.Status = status.Value = OrderStatus.Created;
            await WriteStateAsync();
        }

        if (status.Value is OrderStatus.Created)
        {
            await DurableTask.Delay(TimeSpan.FromMinutes(1)).AsStep("wait-for-confirmation");
            if (State.Status is OrderStatus.Canceled)
            {
                // The order was canceled during the grace period.
                return;
            }

            State.Status = status.Value = OrderStatus.Confirmed;
            await WriteStateAsync();
        }

        if (status.Value is OrderStatus.Confirmed)
        {
            await _catalogService.CheckStock(order);
            State.Status = status.Value = OrderStatus.Created;
            await WriteStateAsync();
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

*/

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

public class SchedulingOptions
{
    public DateTimeOffset? DueTime { get; init; }
    public RetryOptions? RetryOptions { get; init; }
    public string? RetryPolicy { get; init; }
}

public class RetryOptions
{
    public double BackoffCoefficient { get; init; } = 2;
    public TimeSpan FirstRetryInterval { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaximumRetryInterval { get; init; } = TimeSpan.FromMinutes(5);
    public int MaximumNumberOfAttempts { get; init; }

    // NOTE: this is inherently not serializable. Would it therefore likely be better to specify retry using a named policy, rather than serializing the entire policy?
    // Question is, what implications would that have on xplat? 
    public Func<Exception, bool>? RetryFilter { get; init; }
}

public interface IMyGrainWithDurableTasks : IGrain
{
#pragma warning disable ORLEANS0009 // Grain interfaces methods must return a compatible type
    DurableTask MyDurableTaskMethod(int a, string b);
    DurableTask<string> MyDurableTaskMethod2(int a, string b);
    DurableTask<T> MyDurableTaskMethod3<T>(T a);
#pragma warning restore ORLEANS0009 // Grain interfaces methods must return a compatible type
}