using Orleans.Serialization.Invocation;
using Xunit;

namespace Orleans.CodeGenerator.Tests;

[TestCategory("BVT")]
public class GeneratedInvokableInvocationTests
{
    private static readonly Dictionary<string, Type> RequestTypes = typeof(GeneratedInvokableInvocationTests).Assembly.GetTypes()
        .Where(type => typeof(IInvokable).IsAssignableFrom(type)
            && !type.IsAbstract
            && type.Name.StartsWith("Invokable_IGeneratedInvokableTarget_", StringComparison.Ordinal))
        .Select(type => (Type: type, Request: (IInvokable)Activator.CreateInstance(type)!))
        .ToDictionary(entry => entry.Request.GetMethod().Name, entry => entry.Type);

    private readonly GeneratedInvokableTarget _target = new();

    [Theory]
    [InlineData(nameof(IGeneratedInvokableTarget.ValueTaskResult))]
    [InlineData(nameof(IGeneratedInvokableTarget.TaskResult))]
    public async Task Result_invocations_support_sync_and_async_completion(string methodName)
    {
        foreach (var behavior in new[] { InvocationBehavior.SynchronousSuccess, InvocationBehavior.AsynchronousSuccess })
        {
            using var response = await Invoke(methodName, behavior);
            Assert.Null(response.Exception);
            Assert.Equal(42, response.GetResult<int>());
        }
    }

    [Theory]
    [InlineData(nameof(IGeneratedInvokableTarget.ValueTaskVoid))]
    [InlineData(nameof(IGeneratedInvokableTarget.TaskVoid))]
    public async Task Void_task_invocations_support_sync_and_async_completion(string methodName)
    {
        foreach (var behavior in new[] { InvocationBehavior.SynchronousSuccess, InvocationBehavior.AsynchronousSuccess })
        {
            using var response = await Invoke(methodName, behavior);
            Assert.Same(Response.Completed, response);
        }
    }

    [Theory]
    [InlineData(nameof(IGeneratedInvokableTarget.ValueTaskResult))]
    [InlineData(nameof(IGeneratedInvokableTarget.TaskResult))]
    [InlineData(nameof(IGeneratedInvokableTarget.ValueTaskVoid))]
    [InlineData(nameof(IGeneratedInvokableTarget.TaskVoid))]
    public async Task Task_invocations_wrap_sync_and_async_exceptions(string methodName)
    {
        foreach (var behavior in new[] { InvocationBehavior.SynchronousFailure, InvocationBehavior.AsynchronousFailure })
        {
            using var response = await Invoke(methodName, behavior);
            Assert.IsType<InvalidOperationException>(response.Exception);
        }
    }

    [Fact]
    public async Task Void_invocation_wraps_exceptions()
    {
        using var success = await Invoke(nameof(IGeneratedInvokableTarget.Void), InvocationBehavior.SynchronousSuccess);
        Assert.Same(Response.Completed, success);

        using var failure = await Invoke(nameof(IGeneratedInvokableTarget.Void), InvocationBehavior.SynchronousFailure);
        Assert.IsType<InvalidOperationException>(failure.Exception);
    }

    private async ValueTask<Response> Invoke(string methodName, InvocationBehavior behavior)
    {
        var request = (IInvokable)Activator.CreateInstance(RequestTypes[methodName])!;
        try
        {
            request.SetTarget(new TargetHolder(_target));
            request.SetArgument(0, behavior);
            return await request.Invoke();
        }
        finally
        {
            request.Dispose();
        }
    }

    private sealed class TargetHolder(object target) : ITargetHolder
    {
        public object GetTarget() => target;

        public object? GetComponent(Type componentType) => null;
    }
}

public enum InvocationBehavior
{
    SynchronousSuccess,
    AsynchronousSuccess,
    SynchronousFailure,
    AsynchronousFailure,
}

public interface IGeneratedInvokableTarget : IGrain
{
    ValueTask<int> ValueTaskResult(InvocationBehavior behavior);

    Task<int> TaskResult(InvocationBehavior behavior);

    ValueTask ValueTaskVoid(InvocationBehavior behavior);

    Task TaskVoid(InvocationBehavior behavior);

    void Void(InvocationBehavior behavior);
}

internal sealed class GeneratedInvokableTarget : IGeneratedInvokableTarget
{
    public ValueTask<int> ValueTaskResult(InvocationBehavior behavior) => behavior switch
    {
        InvocationBehavior.SynchronousSuccess => new(42),
        InvocationBehavior.AsynchronousSuccess => CompleteValueTaskResultAsync(),
        InvocationBehavior.SynchronousFailure => throw CreateException(),
        InvocationBehavior.AsynchronousFailure => FailValueTaskResultAsync(),
        _ => throw new ArgumentOutOfRangeException(nameof(behavior)),
    };

    public Task<int> TaskResult(InvocationBehavior behavior) => behavior switch
    {
        InvocationBehavior.SynchronousSuccess => Task.FromResult(42),
        InvocationBehavior.AsynchronousSuccess => CompleteTaskResultAsync(),
        InvocationBehavior.SynchronousFailure => throw CreateException(),
        InvocationBehavior.AsynchronousFailure => FailTaskResultAsync(),
        _ => throw new ArgumentOutOfRangeException(nameof(behavior)),
    };

    public ValueTask ValueTaskVoid(InvocationBehavior behavior) => behavior switch
    {
        InvocationBehavior.SynchronousSuccess => ValueTask.CompletedTask,
        InvocationBehavior.AsynchronousSuccess => CompleteValueTaskAsync(),
        InvocationBehavior.SynchronousFailure => throw CreateException(),
        InvocationBehavior.AsynchronousFailure => FailValueTaskAsync(),
        _ => throw new ArgumentOutOfRangeException(nameof(behavior)),
    };

    public Task TaskVoid(InvocationBehavior behavior) => behavior switch
    {
        InvocationBehavior.SynchronousSuccess => Task.CompletedTask,
        InvocationBehavior.AsynchronousSuccess => CompleteTaskAsync(),
        InvocationBehavior.SynchronousFailure => throw CreateException(),
        InvocationBehavior.AsynchronousFailure => FailTaskAsync(),
        _ => throw new ArgumentOutOfRangeException(nameof(behavior)),
    };

    public void Void(InvocationBehavior behavior)
    {
        if (behavior is InvocationBehavior.SynchronousFailure)
        {
            throw CreateException();
        }
    }

    private static async ValueTask<int> CompleteValueTaskResultAsync()
    {
        await Task.Yield();
        return 42;
    }

    private static async ValueTask<int> FailValueTaskResultAsync()
    {
        await Task.Yield();
        throw CreateException();
    }

    private static async Task<int> CompleteTaskResultAsync()
    {
        await Task.Yield();
        return 42;
    }

    private static async Task<int> FailTaskResultAsync()
    {
        await Task.Yield();
        throw CreateException();
    }

    private static async ValueTask CompleteValueTaskAsync() => await Task.Yield();

    private static async ValueTask FailValueTaskAsync()
    {
        await Task.Yield();
        throw CreateException();
    }

    private static async Task CompleteTaskAsync() => await Task.Yield();

    private static async Task FailTaskAsync()
    {
        await Task.Yield();
        throw CreateException();
    }

    private static InvalidOperationException CreateException() => new("Generated invokable test exception");
}
