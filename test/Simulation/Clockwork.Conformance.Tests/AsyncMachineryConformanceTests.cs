using System.Reflection;
using System.Threading.Tasks;

namespace Orleans.Testing.Simulation.Conformance.Tests;

/// <summary>
/// End-to-end semantic conformance for the controlled async machinery: ordinary
/// <c>async</c>/<c>await</c> source is compiled (in both Debug and Release, whose lowered state machines
/// differ materially), rewritten with the controlled-task rule set so the compiler-generated builder and
/// awaiter types are retargeted onto Clockwork's controlled equivalents, and then executed inside a live
/// <see cref="SimulationHost"/>. Because the cluster advances on a single logical
/// thread and only ever pumps the simulation scheduler, a probe that awaits an initially-incomplete task
/// can only make progress through the deterministic loop - proving the state machine is controlled rather
/// than escaping to the thread pool.
/// </summary>
public sealed class AsyncMachineryConformanceTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // A method-level async local so the state machine is a nested compiler-generated type, awaiting two
    // Task<int> antecedents in sequence, plus a Task.Yield chain and a ConfigureAwait(false) await.
    private const string Source = """
        using System.Threading.Tasks;
        namespace Conf { public static class AsyncProbe {
            public static Task<int> Add(Task<int> a, Task<int> b) => AddImpl(a, b);
            private static async Task<int> AddImpl(Task<int> a, Task<int> b) => await a + await b;

            public static Task<int> AddSync(int a, int b) => AddImpl(Task.FromResult(a), Task.FromResult(b));

            public static async Task<int> YieldSum()
            {
                int sum = 0;
                for (int i = 1; i <= 3; i++)
                {
                    await Task.Yield();
                    sum += i;
                }
                return sum;
            }

            public static Task<int> Configured(Task<int> a) => ConfiguredImpl(a);
            private static async Task<int> ConfiguredImpl(Task<int> a) => await a.ConfigureAwait(false) + 1;

            public static async Task<int> Faulting(Task<int> a) => await a;
        } }
        """;

    private readonly RewriteFixture _fixture = new();

    public static TheoryData<bool> Optimizations => new() { false, true };

    [Theory]
    [MemberData(nameof(Optimizations))]
    public void SynchronouslyCompletedAwaitsRunToCompletion(bool optimize)
    {
        StagedProbe probe = Stage(optimize);
        using var host = new SimulationHost(Start);

        object? task = host.Invoke(probe.Method("AddSync"), 10, 5);
        Assert.Equal(15, Result<int>(task));
    }

    [Theory]
    [MemberData(nameof(Optimizations))]
    public void AwaitingIncompleteTasksResumesThroughTheControlledLoop(bool optimize)
    {
        StagedProbe probe = Stage(optimize);
        using var host = new SimulationHost(Start);

        var a = new TaskCompletionSource<int>();
        var b = new TaskCompletionSource<int>();

        object? task = host.InvokeWithWork(
            probe.Method("Add"),
            [a.Task, b.Task],
            () => a.SetResult(3),
            () => b.SetResult(4));

        Assert.Equal(7, Result<int>(task));
    }

    [Theory]
    [MemberData(nameof(Optimizations))]
    public void YieldSuspendsAndResumesDeterministically(bool optimize)
    {
        StagedProbe probe = Stage(optimize);
        using var host = new SimulationHost(Start);

        object? task = host.Invoke(probe.Method("YieldSum"));
        Assert.Equal(6, Result<int>(task));
    }

    [Theory]
    [MemberData(nameof(Optimizations))]
    public void ConfigureAwaitFalseStaysControlled(bool optimize)
    {
        StagedProbe probe = Stage(optimize);
        using var host = new SimulationHost(Start);

        var a = new TaskCompletionSource<int>();

        // The only way this resumes is through the coordinator's loop. Had ConfigureAwait(false) escaped
        // to the thread pool, the single-threaded cluster pump alone would never complete it.
        object? task = host.InvokeWithWork(
            probe.Method("Configured"),
            [a.Task],
            () => a.SetResult(99));

        Assert.Equal(100, Result<int>(task));
    }

    [Theory]
    [MemberData(nameof(Optimizations))]
    public void FaultedAntecedentPropagatesTheOriginalException(bool optimize)
    {
        StagedProbe probe = Stage(optimize);
        using var host = new SimulationHost(Start);

        var a = new TaskCompletionSource<int>();
        var boom = new InvalidTimeZoneException("boom");

        object? task = host.InvokeWithWork(
            probe.Method("Faulting"),
            [a.Task],
            () => a.SetException(boom));

        var faulted = (Task)task!;
        Assert.Equal(TaskStatus.Faulted, faulted.Status);
        Assert.Same(boom, faulted.Exception!.InnerException);
    }

    private StagedProbe Stage(bool optimize) =>
        _fixture.StageControlledTasks(
            $"Conf.Async.{(optimize ? "Rel" : "Dbg")}", "Conf.AsyncProbe", Source, optimize);

    private static T Result<T>(object? task)
    {
        var typed = (Task)task!;
        Assert.True(typed.IsCompleted);
        PropertyInfo result = typed.GetType().GetProperty("Result")!;
        return (T)result.GetValue(typed)!;
    }

    public void Dispose() => _fixture.Dispose();
}
