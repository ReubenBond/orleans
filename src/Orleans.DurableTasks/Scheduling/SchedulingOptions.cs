namespace Orleans.DurableTasks;

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

[GenerateSerializer]
public class SchedulingOptions
{
    [Id(0)]
    public DateTime? DueTime { get; init; }

    [Id(1)]
    public string? RetryPolicy { get; init; }
}

#pragma warning restore CS1998 // Async method lacks 'await' operators and will run synchronously
#pragma warning restore ORLEANS0009 // Grain interfaces methods must return a compatible type
