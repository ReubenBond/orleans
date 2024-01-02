using System.Runtime.CompilerServices;
using Orleans.DurableTasks.Remoting;
using Orleans.Runtime;
using Orleans.Serialization.Invocation;

namespace Orleans.DurableTasks;

public interface IPollableTask
{
    ValueTask<Response> PollAsync();
}

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

    protected internal abstract ValueTask<Response> InvokeAsync(DurableTaskContext executionContext);
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
internal sealed class CompletedDurableTask<TResult>(TResult value) : DurableTask<TResult>, ICompletedDurableTask
{
    public TResult Result { get; } = value;

    protected internal override ValueTask<Response> InvokeAsync(DurableTaskContext executionContext) => new(Response.Completed);
}

/// <summary>
/// Represents a <see cref="DurableTask{TResult}"/> instance which invokes a delegate.
/// </summary>
internal sealed class AsyncDelegateDurableTask<TResult>(Func<ValueTask<TResult>> func) : DurableTask<TResult>
{
    private readonly Func<ValueTask<TResult>> _func = func;

    protected internal override async ValueTask<Response> InvokeAsync(DurableTaskContext executionContext)
    {
        try
        {
            DurableTaskContext.SetCurrentContext(executionContext);
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
internal sealed class AsyncDelegateDurableTask(Func<ValueTask> func) : DurableTask
{
    private readonly Func<ValueTask> _func = func;

    protected internal override async ValueTask<Response> InvokeAsync(DurableTaskContext executionContext)
    {
        try
        {
            DurableTaskContext.SetCurrentContext(executionContext);
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
internal sealed class DelegateDurableTask<TResult>(Func<TResult> func) : DurableTask<TResult>
{
    private readonly Func<TResult> _func = func;

    protected internal override ValueTask<Response> InvokeAsync(DurableTaskContext executionContext)
    {
        try
        {
            DurableTaskContext.SetCurrentContext(executionContext);
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
internal sealed class DelegateDurableTask(Action func) : DurableTask
{
    private readonly Action _func = func;

    protected internal override ValueTask<Response> InvokeAsync(DurableTaskContext executionContext)
    {
        try
        {
            DurableTaskContext.SetCurrentContext(executionContext);
            _func();
            return new(Response.Completed);
        }
        catch (Exception exception)
        {
            return new(Response.FromException(exception));
        }
    }
}
