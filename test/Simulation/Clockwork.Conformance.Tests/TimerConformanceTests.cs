using System.Reflection;

namespace Orleans.Testing.Simulation.Conformance.Tests;

public sealed class TimerConformanceTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string Source = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        namespace Conf { public static class TimerProbe {
            public static Task<int> ThreadingOneShot()
            {
                var completion = new TaskCompletionSource<int>();
                Timer timer = null!;
                timer = new Timer(_ =>
                {
                    completion.SetResult(1);
                    timer.Dispose();
                }, null, 10, Timeout.Infinite);
                return completion.Task;
            }

            public static Task<int> ComponentOneShot()
            {
                var completion = new TaskCompletionSource<int>();
                var timer = new System.Timers.Timer(10) { AutoReset = false };
                timer.Elapsed += (_, _) =>
                {
                    completion.SetResult(2);
                    timer.Dispose();
                };
                timer.Start();
                return completion.Task;
            }

            public static async Task<bool> PeriodicTick()
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
                return await timer.WaitForNextTickAsync();
            }

            public static async Task DelayAndWaitAsync()
            {
                Task delay = Task.Delay(TimeSpan.FromMilliseconds(10), TimeProvider.System);
                await delay.WaitAsync(TimeSpan.FromMilliseconds(20), TimeProvider.System);
            }

            public static async Task<bool> CancelAfter()
            {
                using var source = new CancellationTokenSource(
                    TimeSpan.FromMilliseconds(10),
                    TimeProvider.System);
                try
                {
                    await Task.Delay(Timeout.Infinite, source.Token);
                    return false;
                }
                catch (OperationCanceledException)
                {
                    return source.IsCancellationRequested;
                }
            }

            public static Task<int> ProviderTimer()
            {
                var completion = new TaskCompletionSource<int>();
                ITimer timer = null!;
                timer = TimeProvider.System.CreateTimer(
                    _ =>
                    {
                        completion.SetResult(3);
                        timer.Dispose();
                    },
                    null,
                    TimeSpan.FromMilliseconds(10),
                    Timeout.InfiniteTimeSpan);
                return completion.Task;
            }
        } }
        """;

    private readonly RewriteFixture _fixture = new();
    private readonly Lazy<StagedProbe> _release;
    private readonly Lazy<StagedProbe> _debug;

    public TimerConformanceTests()
    {
        _release = new Lazy<StagedProbe>(() =>
            _fixture.StageControlledTasks("Conf.TimerRel", "Conf.TimerProbe", Source, optimize: true));
        _debug = new Lazy<StagedProbe>(() =>
            _fixture.StageControlledTasks("Conf.TimerDbg", "Conf.TimerProbe", Source, optimize: false));
    }

    public static TheoryData<bool> Optimize => new() { true, false };

    [Theory]
    [MemberData(nameof(Optimize))]
    public void TimerFamiliesAndTaskDeadlinesUseVirtualTime(bool optimize)
    {
        using var host = new SimulationHost(Start);

        Assert.Equal(1, Result<int>((Task<int>)host.Invoke(Method("ThreadingOneShot", optimize))!));
        Assert.Equal(2, Result<int>((Task<int>)host.Invoke(Method("ComponentOneShot", optimize))!));
        Assert.True(Result<bool>((Task<bool>)host.Invoke(Method("PeriodicTick", optimize))!));
        Assert.True(((Task)host.Invoke(Method("DelayAndWaitAsync", optimize))!).IsCompletedSuccessfully);
        Assert.True(Result<bool>((Task<bool>)host.Invoke(Method("CancelAfter", optimize))!));
        Assert.Equal(3, Result<int>((Task<int>)host.Invoke(Method("ProviderTimer", optimize))!));
    }

    [Theory]
    [MemberData(nameof(Optimize))]
    public void RewrittenTimerEntriesRequireActiveSimulation(bool optimize)
    {
        SimulationNotActiveExceptionAssert.Throws(Method("ThreadingOneShot", optimize));
        SimulationNotActiveExceptionAssert.Throws(Method("DelayAndWaitAsync", optimize));
    }

    private MethodInfo Method(string name, bool optimize) =>
        (optimize ? _release : _debug).Value.Method(name);

    private static TResult Result<TResult>(Task<TResult> task)
    {
        Assert.True(task.IsCompletedSuccessfully);
        return task.Result;
    }

    public void Dispose() => _fixture.Dispose();
}
