using System.Runtime.CompilerServices;
using Orleans.DurableTasks.Remoting;
using Orleans.Runtime;
using Orleans.Serialization.Invocation;

namespace Orleans.DurableTasks;

[InvokableBaseType(typeof(GrainReference), typeof(DurableTask), typeof(DurableTaskRequest))]
[AsyncMethodBuilder(typeof(DurableTaskMethodBuilder))]
[GenerateSerializer, SerializerTransparent]
[Alias("DurableTask")]
public abstract partial class DurableTask
{
    public static DurableTask<T> FromResult<T>(T value) => new CompletedDurableTask<T>(value);

    public static DurableTask Run(Action func) => new DelegateDurableTask(func);
    public static DurableTask<T> Run<T>(Func<T> func) => new DelegateDurableTask<T>(func);
    public static DurableTask Run(Func<ValueTask> func) => new AsyncDelegateDurableTask(func);
    public static DurableTask<T> Run<T>(Func<ValueTask<T>> func) => new AsyncDelegateDurableTask<T>(func);

    /// <summary>
    /// Invokes the task with the provided context.
    /// </summary>
    /// <param name="context">The task context.</param>
    /// <returns></returns>
    protected internal abstract ValueTask<Response> InvokeAsync(DurableTaskContext context);
}

[InvokableBaseType(typeof(GrainReference), typeof(DurableTask<>), typeof(DurableTaskRequest<>))]
[AsyncMethodBuilder(typeof(DurableTaskMethodBuilder<>))]
[GenerateSerializer, SerializerTransparent]
[Alias("DurableTask`1")]
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

    protected internal override ValueTask<Response> InvokeAsync(DurableTaskContext context) => new(Response.Completed);
}

/// <summary>
/// Represents a <see cref="DurableTask{TResult}"/> instance which invokes a delegate.
/// </summary>
internal sealed class AsyncDelegateDurableTask<TResult>(Func<ValueTask<TResult>> func) : DurableTask<TResult>
{
    protected internal override async ValueTask<Response> InvokeAsync(DurableTaskContext context)
    {
        try
        {
            DurableTaskContext.SetCurrentContext(context);
            return Response.FromResult(await func());
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
    protected internal override async ValueTask<Response> InvokeAsync(DurableTaskContext context)
    {
        try
        {
            DurableTaskContext.SetCurrentContext(context);
            await func();
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
    protected internal override ValueTask<Response> InvokeAsync(DurableTaskContext context)
    {
        try
        {
            DurableTaskContext.SetCurrentContext(context);
            return new(Response.FromResult(func()));
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
    protected internal override ValueTask<Response> InvokeAsync(DurableTaskContext context)
    {
        try
        {
            DurableTaskContext.SetCurrentContext(context);
            func();
            return new(Response.Completed);
        }
        catch (Exception exception)
        {
            return new(Response.FromException(exception));
        }
    }
}
