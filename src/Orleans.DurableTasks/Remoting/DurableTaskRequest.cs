using System.Reflection;
using Orleans.CodeGeneration;
using Orleans.Runtime;
using Orleans.Invocation;
using Orleans.Serialization.Invocation;

namespace Orleans.DurableTasks.Remoting;

public interface IDurableTaskRequest : IRequest
{
    /// <summary>
    /// Gets or sets the durable task request context.
    /// </summary>
    DurableTaskRequestContext? Context { get; set; }

    /// <summary>
    /// Invoke the method on the target.
    /// </summary>
    /// <returns>The result of invocation.</returns>
    ValueTask<Response> InvokeImplementation(DurableTaskExecutionContext executionContext);
}

[GenerateSerializer]
[SelfInvokingReturnType]
public abstract class DurableTaskRequest : DurableTask, IDurableTaskRequest
{
    // Note: we could save a field here by using RuntimeContext, but that will require making internals visible to this assembly.
    // For now, we're not doing that, just to make sure that we can get far without needing it, demonstrating the extensibility of Orleans.
    // It might be worthwhile making RuntimeContext public at some point, even if it is not the recommended approach.
    [NonSerialized]
    private readonly IGrainContextAccessor _grainContextAccessor;

    [GeneratedActivatorConstructor]
    protected DurableTaskRequest(IGrainContextAccessor grainContextAccessor)
    {
        _grainContextAccessor = grainContextAccessor;
    }

    /// <inheritdoc/>
    [Id(0)]
    public DurableTaskRequestContext? Context { get; set; }

    /// <summary>
    /// Gets the invocation options.
    /// </summary>
    [field: NonSerialized]
    public InvokeMethodOptions Options { get; private set; }

    /// <inheritdoc/>
    public virtual int GetArgumentCount() => 0;

    /// <summary>
    /// Incorporates the provided invocation options.
    /// </summary>
    /// <param name="options">
    /// The options.
    /// </param>
    public void AddInvokeMethodOptions(InvokeMethodOptions options)
    {
        Options |= options;
    }

    /// <inheritdoc/>
    public abstract object GetTarget();

    /// <inheritdoc/>
    public abstract void SetTarget(ITargetHolder holder);

    /// <inheritdoc/>
    public virtual object GetArgument(int index) => throw new ArgumentOutOfRangeException(message: "The request has zero arguments", null);

    /// <inheritdoc/>
    public virtual void SetArgument(int index, object value) => throw new ArgumentOutOfRangeException(message: "The request has zero arguments", null);

    /// <inheritdoc/>
    public abstract void Dispose();

    /// <inheritdoc/>
    public abstract string GetMethodName();

    /// <inheritdoc/>
    public abstract string GetInterfaceName();

    /// <inheritdoc/>
    public abstract string GetActivityName();

    /// <inheritdoc/>
    public abstract Type GetInterfaceType();

    /// <inheritdoc/>
    public abstract MethodInfo GetMethod();

    /// <inheritdoc/>
    public override string ToString() => ((IRequest)this).ToString();

    /// <inheritdoc/>
    protected internal override ValueTask InvokeAsyncUntypedCore(DurableTaskExecutionContext executionContext)
    {
        // This is invoked by the `DurableTask<T>.AsWorkflow(stepId, options)` method, so it is the first method called after the instance is constructed and its arguments populated (by generated code).

        // Take the execution context, propagate it to a new `DurableTaskRequestContext`
        var callerContext = _grainContextAccessor.GrainContext;
        if (callerContext.GetComponent<IDurableTaskGrainRuntime>() is null)
        {
            // TODO: ensure this is not possible
            throw new InvalidOperationException($"The current grain or client context, {callerContext} does not support calling durable tasks");
        }

        // Submit it to the runtime to send to the remote instance.

        // Wait for the execution context to be completed.
        // This means that it must be propagated either to the currently executing grain or (external) the HostedClient for completion.
        throw new NotImplementedException();
    }

    /// <inheritdoc/>
    async ValueTask<Response> IInvokable.Invoke()
    {
        // Called by Orleans RPC system to schedule a call on the grain locally.
        // This must ensure that the request is made durable (persisted).

        // Get the durable task grain runtime.
        var grainContext = _grainContextAccessor.GrainContext;
        var runtime = grainContext.GetComponent<IDurableTaskGrainRuntime>();

        // Ensure that the task is durably scheduled.
        // If the request has already completed, this will return the result of invocation.
        // If the request has not already completed, this will return an in-progress response.
        var response = await runtime.ScheduleOrPollAsync(this);

        return response;
    }

    /// <inheritdoc/>
    async ValueTask<Response> IDurableTaskRequest.InvokeImplementation(DurableTaskExecutionContext executionContext)
    {
        // The IDurableTaskGrainRuntime calls this method to execute the method body on the implementation.
        // By this stage, it must already have been made durable.
        try
        {
            await InvokeInner().InvokeAsync(executionContext);
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
[SelfInvokingReturnType]
public abstract class DurableTaskRequest<TResult> : DurableTask<TResult>, IDurableTaskRequest
{
    // Note: we could save a field here by using RuntimeContext, but that will require making internals visible to this assembly.
    // For now, we're not doing that, just to make sure that we can get far without needing it, demonstrating the extensibility of Orleans.
    // It might be worthwhile making RuntimeContext public at some point, even if it is not the recommended approach.
    [NonSerialized]
    private readonly IGrainContextAccessor _grainContextAccessor;

    [GeneratedActivatorConstructor]
    protected DurableTaskRequest(IGrainContextAccessor grainContextAccessor)
    {
        _grainContextAccessor = grainContextAccessor;
    }

    /// <inheritdoc/>
    [Id(0)]
    public DurableTaskRequestContext? Context { get; set; }

    /// <summary>
    /// Gets the invocation options.
    /// </summary>
    [field: NonSerialized]
    public InvokeMethodOptions Options { get; private set; }

    /// <inheritdoc/>
    public virtual int GetArgumentCount() => 0;

    /// <summary>
    /// Incorporates the provided invocation options.
    /// </summary>
    /// <param name="options">
    /// The options.
    /// </param>
    public void AddInvokeMethodOptions(InvokeMethodOptions options)
    {
        Options |= options;
    }

    /// <inheritdoc/>
    public abstract object GetTarget();

    /// <inheritdoc/>
    public abstract void SetTarget(ITargetHolder holder);

    /// <inheritdoc/>
    public virtual object GetArgument(int index) => throw new ArgumentOutOfRangeException(message: "The request has zero arguments", null);

    /// <inheritdoc/>
    public virtual void SetArgument(int index, object value) => throw new ArgumentOutOfRangeException(message: "The request has zero arguments", null);

    /// <inheritdoc/>
    public abstract void Dispose();

    /// <inheritdoc/>
    public abstract string GetMethodName();

    /// <inheritdoc/>
    public abstract string GetInterfaceName();

    /// <inheritdoc/>
    public abstract string GetActivityName();

    /// <inheritdoc/>
    public abstract Type GetInterfaceType();

    /// <inheritdoc/>
    public abstract MethodInfo GetMethod();

    /// <inheritdoc/>
    public override string ToString() => ((IRequest)this).ToString();

    /// <inheritdoc/>
    protected internal override ValueTask<TResult> InvokeAsyncTypedCore(DurableTaskExecutionContext executionContext)
    {
        // This is invoked by the `DurableTask<T>.AsWorkflow(stepId, options)` method, so it is the first method called after the instance is constructed and its arguments populated (by generated code).

        // Take the execution context, propagate it to `DurableTaskRequestContext`
        // Submit it to the runtime to send to the remote instance.

        // Wait for the execution context to be completed.
        // This means that it must be propagated either to the currently executing grain or (external) the HostedClient for completion.
        throw new NotImplementedException();
    }

    /// <inheritdoc/>
    protected internal override ValueTask InvokeAsyncUntypedCore(DurableTaskExecutionContext executionContext)
    {
        // This is invoked by the `DurableTask<T>.AsWorkflow(stepId, options)` method, so it is the first method called after the instance is constructed and its arguments populated (by generated code).

        // Take the execution context, propagate it to `DurableTaskRequestContext`
        // Submit it to the runtime to send to the remote instance.

        // Wait for the execution context to be completed.
        // This means that it must be propagated either to the currently executing grain or (external) the HostedClient for completion.
        throw new NotImplementedException();
    }

    /// <inheritdoc/>
    async ValueTask<Response> IInvokable.Invoke()
    {
        // Called by Orleans RPC system to schedule a call on the grain locally.
        // This must ensure that the request is made durable (persisted).

        // Get the durable task grain runtime.
        var grainContext = _grainContextAccessor.GrainContext;
        var runtime = grainContext.GetComponent<IDurableTaskGrainRuntime>();

        // Ensure that the task is durably scheduled.
        // If the request has already completed, this will return the result of invocation.
        // If the request has not already completed, this will return an in-progress response.
        var response = await runtime.ScheduleOrPollAsync(this);

        return response;
    }

    /// <inheritdoc/>
    async ValueTask<Response> IDurableTaskRequest.InvokeImplementation(DurableTaskExecutionContext executionContext)
    {
        // The IDurableTaskGrainRuntime calls this method to execute the method body on the implementation.
        // By this stage, it must already have been made durable.
        try
        {
            var result = await InvokeInner().InvokeAsync(executionContext);
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
