using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Invocation;

namespace Orleans.Vesuvius.Remoting;

[GenerateSerializer]
public abstract class DurableTaskRequestBase : RequestBase, IOutgoingGrainCallFilter, IOnDeserialized
{
    [NonSerialized]
    private IScheduledTaskRuntime? _runtime;

    [Id(0)]
    public ScheduledTaskContext? Context { get; set; }

    [GeneratedActivatorConstructor]
    protected DurableTaskRequestBase(IScheduledTaskRuntime runtime)
    {
        _runtime = runtime;
    }

    async Task IOutgoingGrainCallFilter.Invoke(IOutgoingGrainCallContext context)
    {
        SetScheduledTaskContext();
        await context.Invoke();
    }

    private void SetScheduledTaskContext()
    {
        var taskContext = ScheduledTaskContext.Current;
        if (taskContext == null)
        {
            ScheduledTaskContext.Clear();
        }
        else
        {
            Context = taskContext;
        }
    }

    public override async ValueTask<Response> Invoke()
    {
        Response response;
        var taskContext = this.Context;
        try
        {
            ScheduledTaskContext.SetCurrentContext(taskContext);
            response = await InvokeWrapped();
        }
        catch (Exception exception)
        {
            response = Response.FromException(exception);
        }
        finally
        {
            ScheduledTaskContext.Clear();
        }

        return response;
    }

    protected abstract ValueTask<Response> InvokeWrapped();

    public override void Dispose()
    {
       Context = null;
    }

    void IOnDeserialized.OnDeserialized(DeserializationContext context)
    {
        _runtime = context.ServiceProvider.GetRequiredService<IScheduledTaskRuntime>();
    }
}

[GenerateSerializer]
public sealed class DurableTaskResponse : Response
{
    [Id(0)]
    private Response? _response;

    [Id(1)]
    public ScheduledTaskContext? Context { get; set; }

    public static DurableTaskResponse Create(Response response, ScheduledTaskContext context)
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

[GenerateSerializer]
public abstract class DurableTaskRequest : DurableTaskRequestBase 
{
    [GeneratedActivatorConstructor]
    protected DurableTaskRequest(IScheduledTaskRuntime runtime) : base(runtime)
    {
    }

    protected override async ValueTask<Response> InvokeWrapped()
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

[GenerateSerializer]
public abstract class DurableTaskRequest<TResult> : DurableTaskRequestBase
{
    [GeneratedActivatorConstructor]
    protected DurableTaskRequest(IScheduledTaskRuntime runtime) : base(runtime)
    {
    }

    protected override async ValueTask<Response> InvokeWrapped()
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
