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

namespace Orleans.DurableTasks;

public class Program
{
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
}

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