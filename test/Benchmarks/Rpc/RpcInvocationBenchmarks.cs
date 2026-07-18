using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Orleans.Serialization.Invocation;

namespace Benchmarks.Rpc;

[Config(typeof(RpcBenchmarkConfig))]
[BenchmarkCategory("Rpc", "Invocation")]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByParams)]
public class RpcInvocationBenchmarks
{
    private RpcInvocationBenchmarkTarget _target;
    private IInvokable _valueTaskRequest;
    private IInvokable _taskRequest;
    private IInvokable[] _polymorphicRequests;

    [Params(1, 16, 256)]
    public int InvocationCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _target = new();
        var holder = new BenchmarkTargetHolder(_target);
        var requests = typeof(RpcInvocationBenchmarks).Assembly.GetTypes()
            .Where(type => typeof(IInvokable).IsAssignableFrom(type)
                && !type.IsAbstract
                && type.Name.StartsWith("Invokable_IRpcInvocationBenchmarkTarget_", StringComparison.Ordinal))
            .Select(type => (IInvokable)Activator.CreateInstance(type))
            .ToDictionary(request => request.GetMethod().Name);

        _valueTaskRequest = Initialize(requests[nameof(IRpcInvocationBenchmarkTarget.ValueTaskCall0)], holder);
        _taskRequest = Initialize(requests[nameof(IRpcInvocationBenchmarkTarget.TaskCall)], holder);
        _polymorphicRequests =
        [
            _valueTaskRequest,
            Initialize(requests[nameof(IRpcInvocationBenchmarkTarget.ValueTaskCall1)], holder),
            Initialize(requests[nameof(IRpcInvocationBenchmarkTarget.ValueTaskCall2)], holder),
            Initialize(requests[nameof(IRpcInvocationBenchmarkTarget.ValueTaskCall3)], holder),
        ];
    }

    public static void Profile(string operation, TimeSpan duration)
    {
        var benchmark = new RpcInvocationBenchmarks { InvocationCount = 256 };
        benchmark.Setup();
        try
        {
            Func<int> invoke = operation switch
            {
                "monomorphic" => benchmark.MonomorphicValueTask,
                "polymorphic" => benchmark.PolymorphicValueTask,
                "task" => benchmark.MonomorphicTask,
                "control" => benchmark.DirectValueTaskControl,
                _ => throw new ArgumentException($"Unknown RPC profile operation '{operation}'.", nameof(operation)),
            };

            var stopAt = DateTime.UtcNow + duration;
            var invocations = 0L;
            while (DateTime.UtcNow < stopAt)
            {
                _ = invoke();
                invocations += benchmark.InvocationCount;
            }

            Console.WriteLine($"{operation}: {invocations:N0} invocations in {duration.TotalSeconds:N0} seconds");
        }
        finally
        {
            benchmark.Cleanup();
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        foreach (var request in _polymorphicRequests)
        {
            request.Dispose();
        }

        _taskRequest.Dispose();
    }

    [Benchmark(Baseline = true)]
    public int DirectValueTaskControl()
    {
        var result = 0;
        for (var i = 0; i < InvocationCount; i++)
        {
            result += _target.ValueTaskCall0(42).Result;
        }

        return result;
    }

    [Benchmark]
    public int MonomorphicValueTask()
    {
        var result = 0;
        for (var i = 0; i < InvocationCount; i++)
        {
            result += Invoke(_valueTaskRequest);
        }

        return result;
    }

    [Benchmark]
    public int PolymorphicValueTask()
    {
        var result = 0;
        for (var i = 0; i < InvocationCount; i++)
        {
            result += Invoke(_polymorphicRequests[i & 3]);
        }

        return result;
    }

    [Benchmark]
    public int MonomorphicTask()
    {
        var result = 0;
        for (var i = 0; i < InvocationCount; i++)
        {
            result += Invoke(_taskRequest);
        }

        return result;
    }

    private static IInvokable Initialize(IInvokable request, ITargetHolder holder)
    {
        request.SetTarget(holder);
        request.SetArgument(0, 42);
        return request;
    }

    private static int Invoke(IInvokable request)
    {
        var response = request.Invoke().Result;
        try
        {
            return response.GetResult<int>();
        }
        finally
        {
            response.Dispose();
        }
    }
}

public interface IRpcInvocationBenchmarkTarget : IGrain
{
    ValueTask<int> ValueTaskCall0(int value);

    ValueTask<int> ValueTaskCall1(int value);

    ValueTask<int> ValueTaskCall2(int value);

    ValueTask<int> ValueTaskCall3(int value);

    Task<int> TaskCall(int value);
}

internal sealed class RpcInvocationBenchmarkTarget : IRpcInvocationBenchmarkTarget
{
    public ValueTask<int> ValueTaskCall0(int value) => new(value + 1);

    public ValueTask<int> ValueTaskCall1(int value) => new(value + 2);

    public ValueTask<int> ValueTaskCall2(int value) => new(value + 3);

    public ValueTask<int> ValueTaskCall3(int value) => new(value + 4);

    public Task<int> TaskCall(int value) => Task.FromResult(value + 1);
}

internal sealed class BenchmarkTargetHolder(object target) : ITargetHolder
{
    public object GetTarget() => target;

    public object GetComponent(Type componentType) => null;
}
