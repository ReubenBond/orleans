using System.Reflection;
using System.Threading.Tasks;

namespace Orleans.Testing.Simulation.Conformance.Tests;

/// <summary>
/// End-to-end semantic conformance for the controlled <c>ValueTask</c>/<c>ValueTask&lt;T&gt;</c>
/// machinery. Ordinary <c>async ValueTask</c> source is compiled (Debug and Release, whose lowered state
/// machines differ), rewritten so the compiler-generated <c>AsyncValueTaskMethodBuilder</c>,
/// <c>ValueTaskAwaiter</c>, and <c>ConfiguredValueTaskAwaitable</c> types are retargeted onto Clockwork's
/// controlled equivalents, and executed inside a live single-logical-thread <see cref="SimulationHost"/>.
/// A probe that awaits an initially-incomplete value task can only progress through the deterministic
/// loop, proving the value-task state machine is controlled rather than escaping to the thread pool.
/// </summary>
public sealed class ValueTaskConformanceTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string Source = """
        using System.Threading.Tasks;
        namespace Conf { public static class ValueTaskProbe {
            // async ValueTask<T> awaiting a Task<int> antecedent through the ValueTask builder/awaiter.
            public static Task<int> AddValue(Task<int> a, Task<int> b) => AddValueImpl(a, b).AsTask();
            private static async ValueTask<int> AddValueImpl(Task<int> a, Task<int> b)
                => await new ValueTask<int>(a) + await new ValueTask<int>(b);

            // async ValueTask (non-generic) that completes a side channel once its antecedent resolves.
            public static Task RunValue(Task<int> a, int[] sink) => RunValueImpl(a, sink).AsTask();
            private static async ValueTask RunValueImpl(Task<int> a, int[] sink)
            {
                sink[0] = await new ValueTask<int>(a);
            }

            // ConfigureAwait(false) on a ValueTask<T> must stay controlled inside simulation.
            public static Task<int> ConfiguredValue(Task<int> a) => ConfiguredValueImpl(a).AsTask();
            private static async ValueTask<int> ConfiguredValueImpl(Task<int> a)
                => await new ValueTask<int>(a).ConfigureAwait(false) + 1;

            // synchronously completed value task runs straight through.
            public static Task<int> AddValueSync(int a, int b)
                => AddValueImpl(Task.FromResult(a), Task.FromResult(b)).AsTask();

            // fault propagation through the value-task awaiter.
            public static Task<int> FaultingValue(Task<int> a) => FaultingValueImpl(a).AsTask();
            private static async ValueTask<int> FaultingValueImpl(Task<int> a) => await new ValueTask<int>(a);
        } }
        """;

    private readonly RewriteFixture _fixture = new();

    public static TheoryData<bool> Optimizations => new() { false, true };

    [Theory]
    [MemberData(nameof(Optimizations))]
    public void SynchronouslyCompletedValueTaskRunsToCompletion(bool optimize)
    {
        StagedProbe probe = Stage(optimize);
        using var host = new SimulationHost(Start);

        object? task = host.Invoke(probe.Method("AddValueSync"), 10, 5);
        Assert.Equal(15, Result<int>(task));
    }

    [Theory]
    [MemberData(nameof(Optimizations))]
    public void AwaitingIncompleteValueTaskResumesThroughTheControlledLoop(bool optimize)
    {
        StagedProbe probe = Stage(optimize);
        using var host = new SimulationHost(Start);

        var a = new TaskCompletionSource<int>();
        var b = new TaskCompletionSource<int>();

        object? task = host.InvokeWithWork(
            probe.Method("AddValue"),
            [a.Task, b.Task],
            () => a.SetResult(3),
            () => b.SetResult(4));

        Assert.Equal(7, Result<int>(task));
    }

    [Theory]
    [MemberData(nameof(Optimizations))]
    public void NonGenericValueTaskResumesThroughTheControlledLoop(bool optimize)
    {
        StagedProbe probe = Stage(optimize);
        using var host = new SimulationHost(Start);

        var a = new TaskCompletionSource<int>();
        var sink = new int[1];

        object? task = host.InvokeWithWork(
            probe.Method("RunValue"),
            [a.Task, sink],
            () => a.SetResult(99));

        Assert.Equal(TaskStatus.RanToCompletion, ((Task)task!).Status);
        Assert.Equal(99, sink[0]);
    }

    [Theory]
    [MemberData(nameof(Optimizations))]
    public void ConfigureAwaitFalseOnValueTaskStaysControlled(bool optimize)
    {
        StagedProbe probe = Stage(optimize);
        using var host = new SimulationHost(Start);

        var a = new TaskCompletionSource<int>();

        // The only way this resumes is through the coordinator's loop. Had ConfigureAwait(false) escaped
        // to the thread pool, the single-threaded cluster pump alone would never complete it.
        object? task = host.InvokeWithWork(
            probe.Method("ConfiguredValue"),
            [a.Task],
            () => a.SetResult(41));

        Assert.Equal(42, Result<int>(task));
    }

    [Theory]
    [MemberData(nameof(Optimizations))]
    public void FaultedValueTaskPropagatesTheOriginalException(bool optimize)
    {
        StagedProbe probe = Stage(optimize);
        using var host = new SimulationHost(Start);

        var a = new TaskCompletionSource<int>();
        var boom = new InvalidTimeZoneException("boom");

        object? task = host.InvokeWithWork(
            probe.Method("FaultingValue"),
            [a.Task],
            () => a.SetException(boom));

        var faulted = (Task)task!;
        Assert.Equal(TaskStatus.Faulted, faulted.Status);
        Assert.Same(boom, faulted.Exception!.InnerException);
    }

    private StagedProbe Stage(bool optimize) =>
        _fixture.StageControlledTasks(
            $"Conf.ValueTask.{(optimize ? "Rel" : "Dbg")}", "Conf.ValueTaskProbe", Source, optimize);

    private static T Result<T>(object? task)
    {
        var typed = (Task)task!;
        Assert.True(typed.IsCompleted);
        PropertyInfo result = typed.GetType().GetProperty("Result")!;
        return (T)result.GetValue(typed)!;
    }

    public void Dispose() => _fixture.Dispose();
}
