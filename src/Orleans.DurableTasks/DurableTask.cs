using System.Runtime.CompilerServices;
using Orleans.DurableTasks.Remoting;
using Orleans.Runtime;

namespace Orleans.DurableTasks;

[InvokableBaseType(typeof(GrainReference), typeof(DurableTask), typeof(DurableTaskRequest))]
[AsyncMethodBuilder(typeof(DurableTaskMethodBuilder))]
public abstract class DurableTask
{
    public static DurableTask<T> FromResult<T>(T value) => new CompletedDurableTask<T>(value);

    public static DurableTask Run(Action func) => new DelegateDurableTask(func);
    public static DurableTask<T> Run<T>(Func<T> func) => new DelegateDurableTask<T>(func);
    public static DurableTask Run(Func<ValueTask> func) => new AsyncDelegateDurableTask(func);
    public static DurableTask<T> Run<T>(Func<ValueTask<T>> func) => new AsyncDelegateDurableTask<T>(func);

    internal ValueTask InvokeAsync(DurableTaskExecutionContext executionContext) => InvokeAsyncUntypedCore(executionContext);
    protected internal abstract ValueTask InvokeAsyncUntypedCore(DurableTaskExecutionContext executionContext);
}

[InvokableBaseType(typeof(GrainReference), typeof(DurableTask<>), typeof(DurableTaskRequest<>))]
[AsyncMethodBuilder(typeof(DurableTaskMethodBuilder<>))]
public abstract class DurableTask<TResult> : DurableTask
{
    internal new ValueTask<TResult> InvokeAsync(DurableTaskExecutionContext executionContext) => InvokeAsyncTypedCore(executionContext);
    protected internal abstract ValueTask<TResult> InvokeAsyncTypedCore(DurableTaskExecutionContext executionContext);
}

internal interface ICompletedDurableTask
{
}

/// <summary>
/// Represents a completed <see cref="DurableTask{TResult}"/> instance.
/// </summary>
internal sealed class CompletedDurableTask<TResult> : DurableTask<TResult>, ICompletedDurableTask
{
    public CompletedDurableTask(TResult value) => Result = value;

    public TResult Result { get; }

    protected internal override ValueTask<TResult> InvokeAsyncTypedCore(DurableTaskExecutionContext executionContext) => new(Result);
    protected internal override ValueTask InvokeAsyncUntypedCore(DurableTaskExecutionContext executionContext) => default;
}

/// <summary>
/// Represents a <see cref="DurableTask{TResult}"/> instance which invokes a delegate.
/// </summary>
internal sealed class AsyncDelegateDurableTask<TResult> : DurableTask<TResult>
{
    private readonly Func<ValueTask<TResult>> _func;
    public AsyncDelegateDurableTask(Func<ValueTask<TResult>> func) => _func = func;

    protected internal override async ValueTask<TResult> InvokeAsyncTypedCore(DurableTaskExecutionContext executionContext)
    {
        DurableTaskExecutionContext.SetCurrentContext(executionContext);
        return await _func();
    }

    protected internal override async ValueTask InvokeAsyncUntypedCore(DurableTaskExecutionContext executionContext) 
    {
        DurableTaskExecutionContext.SetCurrentContext(executionContext);
        _ = await _func();
    }
}

/// <summary>
/// Represents a <see cref="DurableTask{TResult}"/> instance which invokes a delegate.
/// </summary>
internal sealed class AsyncDelegateDurableTask : DurableTask
{
    private readonly Func<ValueTask> _func;
    public AsyncDelegateDurableTask(Func<ValueTask> func) => _func = func;

    protected internal override async ValueTask InvokeAsyncUntypedCore(DurableTaskExecutionContext executionContext) 
    {
        DurableTaskExecutionContext.SetCurrentContext(executionContext);
        await _func();
    }
}

/// <summary>
/// Represents a <see cref="DurableTask{TResult}"/> instance which invokes a delegate.
/// </summary>
internal sealed class DelegateDurableTask<TResult> : DurableTask<TResult>
{
    private readonly Func<TResult> _func;
    public DelegateDurableTask(Func<TResult> func) => _func = func;

    protected internal override ValueTask<TResult> InvokeAsyncTypedCore(DurableTaskExecutionContext executionContext)
    {
        DurableTaskExecutionContext.SetCurrentContext(executionContext, out var previous);
        try
        {
            return new(_func());
        }
        catch (Exception exception)
        {
            return ValueTask.FromException<TResult>(exception);
        }
        finally
        {
            DurableTaskExecutionContext.SetCurrentContext(previous);
        }
    }

    protected internal override ValueTask InvokeAsyncUntypedCore(DurableTaskExecutionContext executionContext) 
    {
        DurableTaskExecutionContext.SetCurrentContext(executionContext, out var previous);
        try
        {
            _ = _func();
            return default;
        }
        catch (Exception exception)
        {
            return ValueTask.FromException(exception);
        }
        finally
        {
            DurableTaskExecutionContext.SetCurrentContext(previous);
        }
    }
}

/// <summary>
/// Represents a <see cref="DurableTask{TResult}"/> instance which invokes a delegate.
/// </summary>
internal sealed class DelegateDurableTask : DurableTask
{
    private readonly Action _func;
    public DelegateDurableTask(Action func) => _func = func;

    protected internal override ValueTask InvokeAsyncUntypedCore(DurableTaskExecutionContext executionContext) 
    {
        DurableTaskExecutionContext.SetCurrentContext(executionContext, out var previous);
        try
        {
            _func();
            return default;
        }
        catch (Exception exception)
        {
            return ValueTask.FromException(exception);
        }
        finally
        {
            DurableTaskExecutionContext.SetCurrentContext(previous);
        }
    }
}
