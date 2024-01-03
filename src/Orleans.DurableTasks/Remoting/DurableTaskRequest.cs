using System.Reflection;
using Orleans.CodeGeneration;
using Orleans.Runtime;
using Orleans.Invocation;
using Orleans.Serialization.Invocation;
using Orleans.Serialization.Activators;
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
    ValueTask<Response> InvokeImplementation(DurableTaskContext executionContext);

    /// <summary>
    /// Returns a string representation of the request.
    /// </summary>
    /// <returns>A string representation of the request.</returns>
    public string ToMethodCallString() => ToMethodCallString(this);
}

[GenerateSerializer]
[ReturnValueProxy(initializerMethodName: nameof(InitializeRequest))]
[Alias("DurableTaskRequest")]
public abstract class DurableTaskRequest : DurableTask, IDurableTaskRequest, ISchedulableTask, IPollableTask
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

    /// <summary>
    /// Returns a string representation of the request.
    /// </summary>
    /// <returns>A string representation of the request.</returns>
    public string ToMethodCallString() => IRequest.ToMethodCallString(this);

    // Called upon creation in generated code by the creating grain reference by virtue of the [SelfInvokingReturnType(nameof(InitializeRequest))] attribute on this class.
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

    async ValueTask<DurableTaskContext> ISchedulableTask.ScheduleAsync(TaskId taskId, SchedulingOptions? options)
    {
        Debug.Assert(Context is not null);
        Context.SchedulingOptions = options;

        var callerContext = _grainContextAccessor.GrainContext;
        var runtime = GetRuntime(callerContext);

        return await runtime.EvaluateAsync(taskId, this, CancellationToken.None);
    }

    /// <inheritdoc/>
    protected internal override async ValueTask<Response> InvokeAsync(DurableTaskContext executionContext)
    {
        // Schedule this request with the remote service.
        // If the task has already been submitted then this will submit it again, which is an idempotent operation if:
        // * The task is semantically identical (same implementation and arguments).
        // * The task did not complete already and was subsequently cleaned up.
        // We can be sure that the task was not already cleaned up if we are calling from a grain which has a stable identifier, since
        // the caller must acknowledge completion before the task is eligible for garbage collection.
        // For the first point (identical implementation and arguments), we could store the task locally and verify it against its already-stored copy.
        // This check can also be performed remotely instead, since the remote host must have stored a copy of the request in order to be able to execute it.
        Debug.Assert(Context is not null);
        Context.TaskId = executionContext.Id;
        Context.Caller = _grainContextAccessor.GrainContext?.GrainReference;
        var remote = Context.Target.Cast<IDurableTaskGrainExtension>();
        return await remote.ScheduleAsync(this);
    }

    /// <inheritdoc/>
    ValueTask<Response> IPollableTask.PollAsync()
    {
        Debug.Assert(Context is not null);
        Debug.Assert(Context.TaskId != TaskId.None);
        var remote = Context.Target.Cast<IDurableTaskGrainExtension>();
        return remote.SubscribeOrPollAsync(Context.TaskId, null);
    }

    /// <inheritdoc/>
    ValueTask<Response> IInvokable.Invoke() => throw new NotImplementedException("Durable task requests can not be invoked directly");

    /// <inheritdoc/>
    ValueTask<Response> IDurableTaskRequest.InvokeImplementation(DurableTaskContext executionContext) => InvokeInner().InvokeAsync(executionContext);

    // Generated
    protected abstract DurableTask InvokeInner();

    internal static IDurableTaskGrainRuntime GetRuntime(IGrainContext callerContext)
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

    /// <inheritdoc/>
    public virtual TimeSpan? GetDefaultResponseTimeout() => null;
}

/// <summary>
/// Represents a request to schedule a <see cref="DurableTask{TResult}"/>-returning method.
/// </summary>
[GenerateSerializer]
[ReturnValueProxy(initializerMethodName: nameof(InitializeRequest))]
[Alias("DurableTaskRequest`1")]
public abstract class DurableTaskRequest<TResult> : DurableTask<TResult>, IDurableTaskRequest, ISchedulableTask, IPollableTask
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

    // Called upon creation in generated code by the creating grain reference by virtue of the [SelfInvokingReturnType(nameof(InitializeRequest))] attribute on this class.
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

    /// <inheritdoc/>
    public async ValueTask<DurableTaskContext> ScheduleAsync(TaskId taskId, SchedulingOptions? options)
    {
        Debug.Assert(Context is not null);
        Context.SchedulingOptions = options;

        var callerContext = _grainContextAccessor.GrainContext;
        var runtime = DurableTaskRequest.GetRuntime(callerContext);

        return await runtime.EvaluateAsync(taskId, this, CancellationToken.None);
    }

    /// <inheritdoc/>
    protected internal override async ValueTask<Response> InvokeAsync(DurableTaskContext executionContext)
    {
        // Schedule this request with the remote service.
        // If the task has already been submitted then this will submit it again, which is an idempotent operation if:
        // * The task is semantically identical (same implementation and arguments).
        // * The task did not complete already and was subsequently cleaned up.
        // We can be sure that the task was not already cleaned up if we are calling from a grain which has a stable identifier, since
        // the caller must acknowledge completion before the task is eligible for garbage collection.
        // For the first point (identical implementation and arguments), we could store the task locally and verify it against its already-stored copy.
        // This check can also be performed remotely instead, since the remote host must have stored a copy of the request in order to be able to execute it.
        Debug.Assert(Context is not null);
        Context.TaskId = executionContext.Id;
        Context.Caller = _grainContextAccessor.GrainContext?.GrainReference;
        var remote = Context.Target.Cast<IDurableTaskGrainExtension>();
        return await remote.ScheduleAsync(this);
    }

    /// <inheritdoc/>
    ValueTask<Response> IPollableTask.PollAsync()
    {
        Debug.Assert(Context is not null);
        Debug.Assert(Context.TaskId != TaskId.None);
        var remote = Context.Target.Cast<IDurableTaskGrainExtension>();
        return remote.SubscribeOrPollAsync(Context.TaskId, null);
    }

    /// <inheritdoc/>
    ValueTask<Response> IInvokable.Invoke() => throw new NotImplementedException("Durable task requests can not be invoked directly");

    /// <inheritdoc/>
    ValueTask<Response> IDurableTaskRequest.InvokeImplementation(DurableTaskContext executionContext) => InvokeInner().InvokeAsync(executionContext);

    // Generated
    protected abstract DurableTask<TResult> InvokeInner();

    /// <inheritdoc/>
    public virtual TimeSpan? GetDefaultResponseTimeout() => null;
}

/// <summary>
/// Represents a pending result for a <see cref="DurableTask"/> or <see cref="DurableTask{TResult}"/> method.
/// </summary>
[GenerateSerializer, Immutable, UseActivator, SuppressReferenceTracking]
[Alias("PendingResponse")]
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
internal sealed class PendingResponseActivator : IActivator<PendingResponse>
{
    /// <inheritdoc/>
    public PendingResponse Create() => PendingResponse.Instance;
}

/// <summary>
/// Represents a pending result for a <see cref="DurableTask"/> or <see cref="DurableTask{TResult}"/> method.
/// </summary>
[GenerateSerializer, Immutable, UseActivator, SuppressReferenceTracking]
[Alias("SubscribedResponse")]
public sealed class SubscribedResponse : Response
{
    /// <summary>
    /// Gets the singleton instance of this class.
    /// </summary>
    public static SubscribedResponse Instance { get; } = new SubscribedResponse();

    /// <inheritdoc/>
    public override object? Result { get => null; set => throw new InvalidOperationException($"Type {nameof(SubscribedResponse)} is read-only"); } 

    /// <inheritdoc/>
    public override Exception? Exception { get => null; set => throw new InvalidOperationException($"Type {nameof(SubscribedResponse)} is read-only"); }

    /// <inheritdoc/>
    public override T GetResult<T>() => default!;

    /// <inheritdoc/>
    public override void Dispose() { }

    /// <inheritdoc/>
    public override string ToString() => "[Subscribed]";
}

/// <summary>
/// Activator for <see cref="SubscribedResponse"/>.
/// </summary>
[RegisterActivator]
internal sealed class SubscribedResponseActivator : IActivator<SubscribedResponse>
{
    /// <inheritdoc/>
    public SubscribedResponse Create() => SubscribedResponse.Instance;
}

/// <summary>
/// Represents an unknown task result for a <see cref="DurableTask"/> or <see cref="DurableTask{TResult}"/> method.
/// </summary>
[GenerateSerializer, Immutable, UseActivator, SuppressReferenceTracking]
[Alias("UnknownTaskResponse")]
public sealed class UnknownTaskResponse : Response
{
    /// <summary>
    /// Gets the singleton instance of this class.
    /// </summary>
    public static UnknownTaskResponse Instance { get; } = new UnknownTaskResponse();

    /// <inheritdoc/>
    public override object? Result
    {
        get => throw GetKeyNotFoundException();
        set => throw new InvalidOperationException($"Type {nameof(UnknownTaskResponse)} is read-only");
    } 

    /// <inheritdoc/>
    public override Exception? Exception
    {
        get => throw GetKeyNotFoundException();
        set => throw new InvalidOperationException($"Type {nameof(UnknownTaskResponse)} is read-only");
    }

    /// <inheritdoc/>
    public override T GetResult<T>() => default!;

    /// <inheritdoc/>
    public override void Dispose() { }

    /// <inheritdoc/>
    public override string ToString() => "[UnknownTask]";

    private static Exception GetKeyNotFoundException() => new KeyNotFoundException("A task with the specified identifier was not found.");
}

/// <summary>
/// Activator for <see cref="UnknownTaskResponse"/>.
/// </summary>
[RegisterActivator]
internal sealed class UnknownTaskResponseActivator : IActivator<UnknownTaskResponse>
{
    /// <inheritdoc/>
    public UnknownTaskResponse Create() => UnknownTaskResponse.Instance;
}
