namespace Orleans.DurableTasks;

public abstract partial class DurableTask
{
    public static async DurableTask<bool> Delay(TimeSpan duration)
    {
        // TODO: If we introduce the concept of per-task state (in addition to "steps"), then this could be a state variable instead of a step,
        // although it's immutable and therefore "steps" offer little downside.
        var until = await DurableTask.Run(() => DateTimeOffset.UtcNow.Add(duration)).AsStep("until");

        var delay = until.Subtract(DateTimeOffset.UtcNow);
        var cancellationToken = CancellationToken.None;
        var maxDelay = TimeSpan.FromMilliseconds(int.MaxValue);
        while (delay > maxDelay)
        {
            delay -= maxDelay;
            var task2 = await Task.WhenAny(Task.Delay(maxDelay, cancellationToken)).ConfigureAwait(false);
            if (task2.IsCanceled)
            {
                await Task.Yield();
                return false;
            }
        }

        var task3 = await Task.WhenAny(Task.Delay(delay, cancellationToken)).ConfigureAwait(false);
        if (task3.IsCanceled)
        {
            await Task.Yield();
            return false;
        }

        return true;
    }

    public static DurableTask<bool> DelayUntil(DateTime dateTime) => DelayUntil(new DateTimeOffset(dateTime));
    public static async DurableTask<bool> DelayUntil(DateTimeOffset dateTime)
    {
        var delay = dateTime.Subtract(DateTimeOffset.UtcNow);
        var cancellationToken = CancellationToken.None;
        var maxDelay = TimeSpan.FromMilliseconds(int.MaxValue);
        while (delay > maxDelay)
        {
            delay -= maxDelay;
            var task2 = await Task.WhenAny(Task.Delay(maxDelay, cancellationToken)).ConfigureAwait(false);
            if (task2.IsCanceled)
            {
                await Task.Yield();
                return false;
            }
        }

        var task3 = await Task.WhenAny(Task.Delay(delay, cancellationToken)).ConfigureAwait(false);
        if (task3.IsCanceled)
        {
            await Task.Yield();
            return false;
        }

        return true;
    }
}

public abstract partial class DurableTask
{
    public static async DurableTask<T> WhenAny<T>(params ScheduledTask<T>[] tasks)
    {
        List<Task<T>> taskList = [];
        foreach (var task in tasks)
        {
            taskList.Add(task.AsTask());    
        }

        var winningTask = await Task.WhenAny(taskList).ConfigureAwait(false);
        return await winningTask;
    }
}

