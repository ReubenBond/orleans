using System.Runtime.CompilerServices;
using Orleans.DurableTasks.Remoting;
using Orleans.Runtime;
using Orleans.Serialization.Invocation;

namespace Orleans.DurableTasks;

[InvokableBaseType(typeof(GrainReference), typeof(DurableTask), typeof(DurableTaskRequest))]
[AsyncMethodBuilder(typeof(DurableTaskMethodBuilder))]
[GenerateSerializer, SerializerTransparent]
public abstract partial class DurableTask
{
    public static DurableTask<T> FromResult<T>(T value) => new CompletedDurableTask<T>(value);

    public static DurableTask Run(Action func) => new DelegateDurableTask(func);
    public static DurableTask<T> Run<T>(Func<T> func) => new DelegateDurableTask<T>(func);
    public static DurableTask Run(Func<ValueTask> func) => new AsyncDelegateDurableTask(func);
    public static DurableTask<T> Run<T>(Func<ValueTask<T>> func) => new AsyncDelegateDurableTask<T>(func);

    internal abstract ValueTask<Response> InvokeAsync(DurableTaskExecutionContext executionContext);
}

[InvokableBaseType(typeof(GrainReference), typeof(DurableTask<>), typeof(DurableTaskRequest<>))]
[AsyncMethodBuilder(typeof(DurableTaskMethodBuilder<>))]
[GenerateSerializer, SerializerTransparent]
public abstract class DurableTask<TResult> : DurableTask
{
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

    internal override ValueTask<Response> InvokeAsync(DurableTaskExecutionContext executionContext) => new(Response.Completed);
}

/// <summary>
/// Represents a <see cref="DurableTask{TResult}"/> instance which invokes a delegate.
/// </summary>
internal sealed class AsyncDelegateDurableTask<TResult> : DurableTask<TResult>
{
    private readonly Func<ValueTask<TResult>> _func;
    public AsyncDelegateDurableTask(Func<ValueTask<TResult>> func) => _func = func;

    internal override async ValueTask<Response> InvokeAsync(DurableTaskExecutionContext executionContext)
    {
        try
        {
            DurableTaskExecutionContext.SetCurrentContext(executionContext);
            return Response.FromResult(await _func());
        }
        catch (Exception exception)
        {
            return Response.FromException(exception);
        }
    }
}

/// <summary>
/// Represents a <see cref="DurableTask{TResult}"/> instance which invokes a delegate.
/// </summary>
internal sealed class AsyncDelegateDurableTask : DurableTask
{
    private readonly Func<ValueTask> _func;
    public AsyncDelegateDurableTask(Func<ValueTask> func) => _func = func;

    internal override async ValueTask<Response> InvokeAsync(DurableTaskExecutionContext executionContext)
    {
        try
        {
            DurableTaskExecutionContext.SetCurrentContext(executionContext);
            await _func();
            return Response.Completed;
        }
        catch (Exception exception)
        {
            return Response.FromException(exception);
        }
    }
}

/// <summary>
/// Represents a <see cref="DurableTask{TResult}"/> instance which invokes a delegate.
/// </summary>
internal sealed class DelegateDurableTask<TResult> : DurableTask<TResult>
{
    private readonly Func<TResult> _func;
    public DelegateDurableTask(Func<TResult> func) => _func = func;

    internal override ValueTask<Response> InvokeAsync(DurableTaskExecutionContext executionContext)
    {
        try
        {
            DurableTaskExecutionContext.SetCurrentContext(executionContext);
            return new(Response.FromResult(_func()));
        }
        catch (Exception exception)
        {
            return new(Response.FromException(exception));
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

    internal override ValueTask<Response> InvokeAsync(DurableTaskExecutionContext executionContext)
    {
        try
        {
            DurableTaskExecutionContext.SetCurrentContext(executionContext);
            _func();
            return new(Response.Completed);
        }
        catch (Exception exception)
        {
            return new(Response.FromException(exception));
        }
    }
}
