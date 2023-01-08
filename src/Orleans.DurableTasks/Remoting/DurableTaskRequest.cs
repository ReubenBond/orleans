using Orleans.Runtime;
using Orleans.Serialization.Invocation;

namespace Orleans.DurableTasks.Remoting;

[GenerateSerializer]
public abstract class DurableTaskRequest : RequestBase, IOutgoingGrainCallFilter
{
    // Note: we could save a field here by using RuntimeContext, but that will require making internals visible to this assembly.
    // For now, we're not doing that, just to make sure that we can get far without needing it, demonstrating the extensibility of Orleans.
    // It might be worthwhile making RuntimeContext public at some point, even if it is not the recommended approach.
    [NonSerialized]
    private readonly IGrainContextAccessor _grainContextAccessor;

    [Id(0)]
    public DurableTaskRequestContext? Context { get; set; }

    [GeneratedActivatorConstructor]
    protected DurableTaskRequest(IGrainContextAccessor grainContextAccessor)
    {
        _grainContextAccessor = grainContextAccessor;
    }

    Task IOutgoingGrainCallFilter.Invoke(IOutgoingGrainCallContext context)
    {
        Context = DurableTaskRequestContext.Current ?? throw new InvalidOperationException($"Attempt to call a {nameof(DurableTask)} method without an ambient {nameof(DurableTaskRequestContext)}");
        return context.Invoke();
    }

    public override async ValueTask<Response> Invoke()
    {
        // Get the durable task grain runtime.
        var grainContext = _grainContextAccessor.GrainContext;
        var runtime = grainContext.GetComponent<IDurableTaskGrainRuntime>();

        // Ensure that the task is durably scheduled.
        // If the request has already completed, this will return the result of invocation.
        // If the request has not already completed, this will return an in-progress response.
        var response = await runtime.ScheduleOrPollAsync(this);

        return response;
    }

    /// <summary>
    /// Invoke the method on the target.
    /// </summary>
    /// <returns></returns>
    public async ValueTask<Response> InvokeImplementation()
    {
        Response response;
        try
        {
            DurableTaskRequestContext.SetCurrentContext(Context);
            response = await InvokeImplementationCore();
        }
        catch (Exception exception)
        {
            response = Response.FromException(exception);
        }
        finally
        {
            DurableTaskRequestContext.Clear();
        }

        return response;
    }

    protected abstract ValueTask<Response> InvokeImplementationCore();

    public override void Dispose()
    {
       Context = null;
    }
}

[GenerateSerializer]
public sealed class DurableTaskResponse : Response
{
    [Id(0)]
    private Response? _response;

    [Id(1)]
    public DurableTaskRequestContext? Context { get; set; }

    public static DurableTaskResponse Create(Response response, DurableTaskRequestContext context)
    {
        return new DurableTaskResponse
        {
            _response = response,
            Context = context
        };
    }

    public override object? Result { get => _response!.Result; set => _response!.Result = value; }
    public override Exception? Exception { get => _response!.Exception; set => _response!.Exception = value; }
    public override void Dispose()
    {
        Context = null;
        _response!.Dispose();
    }

    public override T GetResult<T>() => _response!.GetResult<T>();
}

/// <summary>
/// Represents a request to schedule a <see cref="DurableTask"/>-returning method.
/// </summary>
[GenerateSerializer]
public abstract class VoidDurableTaskRequest : DurableTaskRequest 
{
    [GeneratedActivatorConstructor]
    protected VoidDurableTaskRequest(IGrainContextAccessor grainContextAccessor) : base(grainContextAccessor)
    {
    }

    protected override async ValueTask<Response> InvokeImplementationCore()
    {
        try
        {
            await InvokeInner();
            return Response.Completed;
        }
        catch (Exception exception)
        {
            return Response.FromException(exception);
        }
    }

    // Generated
    protected abstract DurableTask InvokeInner();
}

/// <summary>
/// Represents a request to schedule a <see cref="DurableTask{TResult}"/>-returning method.
/// </summary>
[GenerateSerializer]
public abstract class DurableTaskRequest<TResult> : DurableTaskRequest
{
    [GeneratedActivatorConstructor]
    protected DurableTaskRequest(IGrainContextAccessor grainContextAccessor) : base(grainContextAccessor)
    {
    }

    protected override async ValueTask<Response> InvokeImplementationCore()
    {
        try
        {
            var result = await InvokeInner();
            return Response.FromResult(result);
        }
        catch (Exception exception)
        {
            return Response.FromException(exception);
        }
    }

    // Generated
    protected abstract DurableTask<TResult> InvokeInner();
}
