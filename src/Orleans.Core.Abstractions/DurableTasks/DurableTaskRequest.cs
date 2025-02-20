#nullable enable
using System;
using System.Reflection;
using Orleans.CodeGeneration;
using Orleans.Invocation;
using Orleans.Serialization.Invocation;
using System.Diagnostics;
using System.Distributed.DurableTasks;
using System.Distributed.DurableTasks.Scheduling;
using Orleans.Runtime;
using System.Threading.Tasks;
using System.Threading;

namespace Orleans.DurableTasks;

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
    ValueTask<DurableTaskResponse> InvokeImplementation(DurableTaskContext executionContext);

    /// <summary>
    /// Returns a string representation of the request.
    /// </summary>
    /// <returns>A string representation of the request.</returns>
    public string ToMethodCallString() => ToMethodCallString(this);
}

public sealed class DurableTaskRequestShared(IGrainContextAccessor grainContextAccessor, IGrainFactory grainFactory)
{
    public IGrainContextAccessor GrainContextAccessor { get; } = grainContextAccessor;
    public IGrainFactory GrainFactory { get; } = grainFactory;
}

[GenerateSerializer]
[ReturnValueProxy(initializerMethodName: nameof(InitializeRequest))]
[Alias("DurableTaskRequest")]
[method: GeneratedActivatorConstructor]
public abstract class DurableTaskRequest(DurableTaskRequestShared shared) : DurableTask, IDurableTaskRequest, ISchedulableTask, IPollableTask
{
    // Note: we could save a field here by using RuntimeContext, but that will require making internals visible to this assembly.
    // For now, we're not doing that, just to make sure that we can get far without needing it, demonstrating the extensibility of Orleans.
    // It might be worthwhile making RuntimeContext public at some point, even if it is not the recommended approach.
    [NonSerialized]
    private readonly DurableTaskRequestShared _shared = shared;

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
        Context = new()
        {
            // TaskId will be filled in later, before submission, via an extension method at the call site.
            TargetId = targetGrainReference.GrainId,
        };
        return this;
    }

    async ValueTask<DurableTaskContext> ISchedulableTask.ScheduleAsync(TaskId taskId, SchedulingOptions? options)
    {
        Debug.Assert(Context is not null);
        Context.SchedulingOptions = options;

        var callerContext = _shared.GrainContextAccessor.GrainContext;
        var runtime = GetRuntime(callerContext);

        return await runtime.ScheduleAsync(taskId, this, CancellationToken.None);
    }

    /// <inheritdoc/>
    protected override async ValueTask<DurableTaskResponse> RunAsync(DurableTaskContext executionContext)
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
        var currentContext = _shared.GrainContextAccessor.GrainContext;
        if (currentContext is not null)
        {
            Context.CallerId = currentContext.GrainId;
        }

        var remote = _shared.GrainFactory.GetGrain<IDurableTaskGrainExtension>(Context.TargetId);
        return await remote.ScheduleAsync(this);
    }

    /// <inheritdoc/>
    ValueTask<DurableTaskResponse> IPollableTask.PollAsync()
    {
        Debug.Assert(Context is not null);
        Debug.Assert(Context.TaskId != TaskId.None);
        var remote = _shared.GrainFactory.GetGrain<IDurableTaskGrainExtension>(Context.TargetId);
        return remote.SubscribeOrPollAsync(Context.TaskId, null);
    }

    /// <inheritdoc/>
    ValueTask<Orleans.Serialization.Invocation.Response> IInvokable.Invoke()
        // This could be made to work... maybe pick a random task id, for example.
        => throw new NotImplementedException("Durable task requests can not be invoked directly");

    /// <inheritdoc/>
    ValueTask<DurableTaskResponse> IDurableTaskRequest.InvokeImplementation(DurableTaskContext executionContext) => DurableTaskRuntimeHelper.RunAsync(InvokeInner(), executionContext);

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
[method: GeneratedActivatorConstructor]
public abstract class DurableTaskRequest<TResult>(DurableTaskRequestShared shared) : DurableTask<TResult>, IDurableTaskRequest, ISchedulableTask, IPollableTask
{
    // Note: we could save a field here by using RuntimeContext, but that will require making internals visible to this assembly.
    // For now, we're not doing that, just to make sure that we can get far without needing it, demonstrating the extensibility of Orleans.
    // It might be worthwhile making RuntimeContext public at some point, even if it is not the recommended approach.
    [NonSerialized]
    private readonly DurableTaskRequestShared _shared = shared;

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
        Context = new()
        {
            // TaskId will be filled in later, before submission, via an extension method at the call site.
            TargetId = targetGrainReference.GrainId,
        };
        return this;
    }

    /// <inheritdoc/>
    public async ValueTask<DurableTaskContext> ScheduleAsync(TaskId taskId, SchedulingOptions? options)
    {
        Debug.Assert(Context is not null);
        Context.SchedulingOptions = options;

        var callerContext = _shared.GrainContextAccessor.GrainContext;
        var runtime = DurableTaskRequest.GetRuntime(callerContext);

        return await runtime.ScheduleAsync(taskId, this, CancellationToken.None);
    }

    /// <inheritdoc/>
    protected override async ValueTask<DurableTaskResponse> RunAsync(DurableTaskContext executionContext)
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
        var callerContext = _shared.GrainContextAccessor.GrainContext;
        if (callerContext is not null)
        {
            Context.CallerId = callerContext.GrainId;
        }

        var remote = _shared.GrainFactory.GetGrain<IDurableTaskGrainExtension>(Context.TargetId);
        return await remote.ScheduleAsync(this);
    }

    /// <inheritdoc/>
    ValueTask<DurableTaskResponse> IPollableTask.PollAsync()
    {
        Debug.Assert(Context is not null);
        Debug.Assert(Context.TaskId != TaskId.None);
        var remote = _shared.GrainFactory.GetGrain<IDurableTaskGrainExtension>(Context.TargetId);
        return remote.SubscribeOrPollAsync(Context.TaskId, null);
    }

    /// <inheritdoc/>
    ValueTask<Orleans.Serialization.Invocation.Response> IInvokable.Invoke() => throw new NotImplementedException("Durable task requests can not be invoked directly");

    /// <inheritdoc/>
    ValueTask<DurableTaskResponse> IDurableTaskRequest.InvokeImplementation(DurableTaskContext executionContext) => DurableTaskRuntimeHelper.RunAsync(InvokeInner(), executionContext);

    // Generated
    protected abstract DurableTask<TResult> InvokeInner();

    /// <inheritdoc/>
    public virtual TimeSpan? GetDefaultResponseTimeout() => null;
}

