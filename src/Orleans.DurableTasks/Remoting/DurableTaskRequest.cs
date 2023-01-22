using System.Reflection;
using Orleans.CodeGeneration;
using Orleans.Runtime;
using Orleans.Invocation;
using Orleans.Serialization.Invocation;
using Orleans.Serialization.Activators;
using Orleans.Serialization;
using System.Diagnostics;

namespace Orleans.DurableTasks.Remoting;

/// <summary>
/// Represents a durable task request.
/// </summary>
public interface IDurableTaskRequest : IRequest
{
    /// <summary>
    /// Gets the task request context.
    /// </summary>
    DurableTaskRequestContext? Context { get; }

    /// <summary>
    /// Invoke the method on the target.
    /// </summary>
    /// <returns>The result of invocation.</returns>
    ValueTask<Response> InvokeImplementation(DurableTaskExecutionContext executionContext);

    //Task ScheduleRemoteAsync();
}

[GenerateSerializer] // Do not make this serializer transparent. We want the option to include information here in future and this is not nearly as perf-critical as regular method calls.
[SelfInvokingReturnType(nameof(InitializeRequest))]
public abstract class DurableTaskRequest : DurableTask, IDurableTaskRequest, ISchedulableTask
{
    // Note: we could save a field here by using RuntimeContext, but that will require making internals visible to this assembly.
    // For now, we're not doing that, just to make sure that we can get far without needing it, demonstrating the extensibility of Orleans.
    // It might be worthwhile making RuntimeContext public at some point, even if it is not the recommended approach.
    [NonSerialized]
    private readonly IGrainContextAccessor _grainContextAccessor;

    [NonSerialized]
    private readonly Serializer _serializer;

    [GeneratedActivatorConstructor]
    protected DurableTaskRequest(IGrainContextAccessor grainContextAccessor, Serializer serializer)
    {
        _grainContextAccessor = grainContextAccessor;
        _serializer = serializer;
    }

    /// <inheritdoc />
    [Id(0)]
    public DurableTaskRequestContext? Context { get; private set; }

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
    public void AddInvokeMethodOptions(InvokeMethodOptions options) => Options |= options;

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
    public override string ToString() => IRequest.ToString(this);

    // Called upon creation in generated code by the creating grain reference by virtue of the [SelfInvokingReturnType(nameof(InitializeRequest))] atttribute on this class.
    public DurableTask InitializeRequest(GrainReference targetGrainReference)
    {
        // Capture the request context.
        Context = new DurableTaskRequestContext
        {
            // TaskId will be filled in later, before submission, via an extension method at the call site.
            Target = targetGrainReference,
        };
        return this;
    }

    async ValueTask<DurableTaskExecutionContext> ISchedulableTask.ScheduleAsync(TaskId taskId, SchedulingOptions? options)
    {
        Debug.Assert(Context is not null);
        Context.SchedulingOptions = options;

        var callerContext = _grainContextAccessor.GrainContext;
        var runtime = GetRuntime(callerContext);

        return await runtime.EvaluateStepAsync(taskId, this);
    }

    /*
    async Task IDurableTaskRequest.ScheduleRemoteAsync()
    {
        // Submit this request to the remote service.
        Debug.Assert(Context is not null);
        Debug.Assert(Context.Target is not null);
        Debug.Assert(Context.Target is GrainReference);
        var remote = Context.Target.Cast<IDurableTaskGrainExtension>();
        await remote.ScheduleAsync(this);
    }
    */

    /// <inheritdoc/>
    internal override ValueTask<Response> InvokeAsync(DurableTaskExecutionContext executionContext)
    {
        throw new NotImplementedException("Durable task requests can not be invoked directly");
    }

    /// <inheritdoc/>
    ValueTask<Response> IInvokable.Invoke()
    {
        throw new NotImplementedException("Durable task requests can not be invoked directly");
    }

    /// <inheritdoc/>
    ValueTask<Response> IDurableTaskRequest.InvokeImplementation(DurableTaskExecutionContext executionContext) => InvokeInner().InvokeAsync(executionContext);

    // Generated
    protected abstract DurableTask InvokeInner();

    private static IDurableTaskGrainRuntime GetRuntime(IGrainContext callerContext)
    {
        if (callerContext is null)
        {
            throw new InvalidOperationException($"No {nameof(IGrainContext)} is in context");
        }

        if (callerContext.GetComponent<IDurableTaskGrainRuntime>() is not { } localRuntime)
        {
            throw new InvalidOperationException($"The current grain or client context, {callerContext} does not support calling durable tasks");
        }

        return localRuntime;
    }
}

/// <summary>
/// Represents a request to schedule a <see cref="DurableTask{TResult}"/>-returning method.
/// </summary>
[GenerateSerializer]
[SelfInvokingReturnType(nameof(InitializeRequest))]
public abstract class DurableTaskRequest<TResult> : DurableTask<TResult>, IDurableTaskRequest, ISchedulableTask
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
    public DurableTaskRequestContext? Context { get; private set; }

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
    public void AddInvokeMethodOptions(InvokeMethodOptions options) => Options |= options;

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
    public override string ToString() => IRequest.ToString(this);

    // Called upon creation in generated code by the creating grain reference by virtue of the [SelfInvokingReturnType(nameof(InitializeRequest))] atttribute on this class.
    public DurableTask<TResult> InitializeRequest(GrainReference targetGrainReference)
    {
        // Capture the request context.
        Context = new DurableTaskRequestContext
        {
            // TaskId will be filled in later, before submission, via an extension method at the call site.
            Target = targetGrainReference,
        };
        return this;
    }

    public async ValueTask<DurableTaskExecutionContext> ScheduleAsync(TaskId taskId, SchedulingOptions? options)
    {
        Debug.Assert(Context is not null);
        Context.SchedulingOptions = options;

        var callerContext = _grainContextAccessor.GrainContext;
        var runtime = GetRuntime(callerContext);

        return await runtime.EvaluateStepAsync(taskId, this);
    }

    /*
    public async Task ScheduleRemoteAsync()
    {
        // Submit this request to the remote service.
        Debug.Assert(Context is not null);
        Debug.Assert(Context.Target is not null);
        Debug.Assert(Context.Target is GrainReference);
        var remote = Context.Target.Cast<IDurableTaskGrainExtension>();
        await remote.ScheduleAsync(this);
    }
    */

    /// <inheritdoc/>
    internal override async ValueTask<Response> InvokeAsync(DurableTaskExecutionContext executionContext)
    {
        // Schedule this request with the remote service and return a pending respone, since the remote service
        // will call back into this
        Debug.Assert(Context is not null);
        Context.TaskId = executionContext.TaskId;
        Context.Caller = _grainContextAccessor.GrainContext?.GrainReference;
        var remote = Context.Target.Cast<IDurableTaskGrainExtension>();
        return await remote.ScheduleAsync(this);
    }

    /// <inheritdoc/>
    ValueTask<Response> IInvokable.Invoke() => throw new NotImplementedException("Durable task requests can not be invoked directly");

    /// <inheritdoc/>
    ValueTask<Response> IDurableTaskRequest.InvokeImplementation(DurableTaskExecutionContext executionContext) => InvokeInner().InvokeAsync(executionContext);

    // Generated
    protected abstract DurableTask<TResult> InvokeInner();

    private static IDurableTaskGrainRuntime GetRuntime(IGrainContext callerContext)
    {
        if (callerContext is null)
        {
            throw new InvalidOperationException($"No {nameof(IGrainContext)} is in context");
        }

        if (callerContext.GetComponent<IDurableTaskGrainRuntime>() is not { } localRuntime)
        {
            throw new InvalidOperationException($"The current grain or client context, {callerContext} does not support calling durable tasks");
        }

        return localRuntime;
    }
}

/// <summary>
/// Represents a pending result for a <see cref="DurableTask"/> or <see cref="DurableTask{TResult}"/> method.
/// </summary>
[GenerateSerializer, Immutable, UseActivator, SuppressReferenceTracking]
public sealed class PendingResponse : Response
{
    /// <summary>
    /// Gets the singleton instance of this class.
    /// </summary>
    public static PendingResponse Instance { get; } = new PendingResponse();

    /// <inheritdoc/>
    public override object? Result { get => null; set => throw new InvalidOperationException($"Type {nameof(PendingResponse)} is read-only"); } 

    /// <inheritdoc/>
    public override Exception? Exception { get => null; set => throw new InvalidOperationException($"Type {nameof(PendingResponse)} is read-only"); }

    /// <inheritdoc/>
    public override T GetResult<T>() => default!;

    /// <inheritdoc/>
    public override void Dispose() { }

    /// <inheritdoc/>
    public override string ToString() => "[Pending]";
}

/// <summary>
/// Activator for <see cref="PendingResponse"/>.
/// </summary>
[RegisterActivator]
internal sealed class DurableTaskPendingResponseActivator : IActivator<PendingResponse>
{
    /// <inheritdoc/>
    public PendingResponse Create() => PendingResponse.Instance;
}

/// <summary>
/// Represents an unkown task result for a <see cref="DurableTask"/> or <see cref="DurableTask{TResult}"/> method.
/// </summary>
[GenerateSerializer, Immutable, UseActivator, SuppressReferenceTracking]
public sealed class UnknownTaskResponse : Response
{
    /// <summary>
    /// Gets the singleton instance of this class.
    /// </summary>
    public static UnknownTaskResponse Instance { get; } = new UnknownTaskResponse();

    /// <inheritdoc/>
    public override object? Result { get => null; set => throw new InvalidOperationException($"Type {nameof(UnknownTaskResponse)} is read-only"); } 

    /// <inheritdoc/>
    public override Exception? Exception { get => null; set => throw new InvalidOperationException($"Type {nameof(UnknownTaskResponse)} is read-only"); }

    /// <inheritdoc/>
    public override T GetResult<T>() => default!;

    /// <inheritdoc/>
    public override void Dispose() { }

    /// <inheritdoc/>
    public override string ToString() => "[UnknownTask]";
}

/// <summary>
/// Activator for <see cref="UnknownTaskResponse"/>.
/// </summary>
[RegisterActivator]
internal sealed class DurableTaskUnknownTaskResponseActivator : IActivator<UnknownTaskResponse>
{
    /// <inheritdoc/>
    public UnknownTaskResponse Create() => UnknownTaskResponse.Instance;
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
