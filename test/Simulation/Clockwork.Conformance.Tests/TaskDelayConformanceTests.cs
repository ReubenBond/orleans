using System.Reflection;
using System.Threading.Tasks;

namespace Orleans.Testing.Simulation.Conformance.Tests;

/// <summary>End-to-end rewrite and runtime coverage for every .NET 11 <c>Task.Delay</c> overload.</summary>
public sealed class TaskDelayConformanceTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string Source = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        namespace Conf { public static class DelayProbe {
            public static Task Milliseconds() => Task.Delay(0);
            public static Task TimeSpanDelay() => Task.Delay(TimeSpan.Zero);
            public static Task MillisecondsCancellation() => Task.Delay(0, CancellationToken.None);
            public static Task TimeSpanCancellation() => Task.Delay(TimeSpan.Zero, CancellationToken.None);
            public static Task Provider() => Task.Delay(TimeSpan.Zero, TimeProvider.System);
            public static Task ProviderCancellation() => Task.Delay(TimeSpan.Zero, TimeProvider.System, CancellationToken.None);
        } }
        """;

    private readonly RewriteFixture _fixture = new();
    private readonly Lazy<StagedProbe> _probe;

    public TaskDelayConformanceTests() =>
        _probe = new Lazy<StagedProbe>(() =>
            _fixture.StageControlledTasks("Conf.TaskDelay", "Conf.DelayProbe", Source, optimize: true));

    public static TheoryData<string> DelayMethods =>
        new()
        {
            "Milliseconds",
            "TimeSpanDelay",
            "MillisecondsCancellation",
            "TimeSpanCancellation",
            "Provider",
            "ProviderCancellation",
        };

    [Theory]
    [MemberData(nameof(DelayMethods))]
    public void EveryDelayOverloadCompletesInsideSimulation(string methodName)
    {
        using var host = new SimulationHost(Start);
        var task = (Task)host.Invoke(Method(methodName))!;
        Assert.True(task.IsCompletedSuccessfully);
    }

    [Theory]
    [MemberData(nameof(DelayMethods))]
    public async Task OnlyRewrittenDelayOverloadsRequireActiveSimulation(string methodName)
    {
        UninstrumentedProbe uninstrumented = _fixture.CompileUninstrumented(
            $"Conf.UninstrumentedTaskDelay.{methodName}",
            "Conf.DelayProbe",
            Source);
        var task = (Task)uninstrumented.Method(methodName).Invoke(null, null)!;
        await task;

        Assert.True(task.IsCompletedSuccessfully);
        SimulationNotActiveExceptionAssert.Throws(Method(methodName));
    }

    private MethodInfo Method(string name) => _probe.Value.Method(name);

    public void Dispose() => _fixture.Dispose();
}
