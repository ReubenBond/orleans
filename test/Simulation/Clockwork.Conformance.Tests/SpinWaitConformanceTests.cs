using System.Reflection;
using System.Threading.Tasks;

namespace Orleans.Testing.Simulation.Conformance.Tests;

/// <summary>
/// End-to-end conformance for the controlled <see cref="System.Threading.SpinWait"/> value type.
/// The rule set substitutes <see cref="System.Threading.SpinWait"/> wholesale, so <c>new SpinWait()</c>,
/// locals typed <c>SpinWait</c>, the instance members (<c>Count</c>/<c>NextSpinWillYield</c>/<c>Reset</c>/
/// <c>SpinOnce</c>) and the static <c>SpinUntil</c> overloads all resolve onto the controlled struct. A
/// controlled spin never busy-waits: it yields to the deterministic loop, and finite <c>SpinUntil</c> uses a
/// virtual-time deadline. Staged in both Release and Debug codegen.
/// </summary>
public sealed class SpinWaitConformanceTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string Source = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        namespace Conf { public static class SpinWaitProbe {
            // SpinUntil pumps the deterministic loop until a controlled thread satisfies the predicate.
            public static Task<bool> SpinUntilWaitsForControlledSignal()
            {
                bool ready = false;
                var t = new Thread(() => { ready = true; });
                t.Start();
                SpinWait.SpinUntil(() => ready);
                t.Join();
                return Task.FromResult(ready);
            }

            // Finite SpinUntil succeeds when the predicate is satisfied before the virtual deadline.
            public static Task<bool> SpinUntilFiniteSucceeds()
            {
                bool ready = false;
                var t = new Thread(() => { ready = true; });
                t.Start();
                bool met = SpinWait.SpinUntil(() => ready, 1000000);
                t.Join();
                return Task.FromResult(met && ready);
            }

            // Finite SpinUntil returns false via the virtual deadline when the predicate never holds.
            public static Task<bool> SpinUntilFiniteTimesOut()
            {
                bool met = SpinWait.SpinUntil(() => false, 50);
                return Task.FromResult(met);
            }

            // TimeSpan overload succeeds before its virtual deadline.
            public static Task<bool> SpinUntilTimeSpanSucceeds()
            {
                bool ready = false;
                var t = new Thread(() => { ready = true; });
                t.Start();
                bool met = SpinWait.SpinUntil(() => ready, TimeSpan.FromSeconds(1000));
                t.Join();
                return Task.FromResult(met);
            }

            // Zero timeout is a non-blocking check.
            public static Task<bool> SpinUntilZeroTimeout()
            {
                return Task.FromResult(SpinWait.SpinUntil(() => false, 0));
            }

            // SpinOnce advances the observable count; Reset clears it.
            public static Task<int> SpinOnceAdvancesCount()
            {
                var sw = new SpinWait();
                int before = sw.Count;
                sw.SpinOnce();
                sw.SpinOnce();
                sw.SpinOnce(20);
                int after = sw.Count;
                sw.Reset();
                int reset = sw.Count;
                return Task.FromResult((before == 0 && after == 3 && reset == 0) ? 42 : -1);
            }

            public static void SpinUntilActionOnly(int[] sink) =>
                SpinWait.SpinUntil(() => { sink[0]++; return true; });

            // NextSpinWillYield becomes true once the spin count passes the yield threshold.
            public static Task<bool> NextSpinWillYieldEventually()
            {
                var sw = new SpinWait();
                bool early = sw.NextSpinWillYield;
                for (int i = 0; i < 12; i++) sw.SpinOnce();
                bool late = sw.NextSpinWillYield;
                return Task.FromResult(!early && late);
            }

            // A SpinWait local passed by ref keeps its identity through substitution.
            public static Task<int> SpinWaitByRefKeepsCount()
            {
                var sw = new SpinWait();
                Advance(ref sw);
                Advance(ref sw);
                return Task.FromResult(sw.Count);
            }

            private static void Advance(ref SpinWait sw) => sw.SpinOnce();
        } }
        """;

    private readonly RewriteFixture _fixture = new();
    private readonly Lazy<StagedProbe> _release;
    private readonly Lazy<StagedProbe> _debug;

    public SpinWaitConformanceTests()
    {
        _release = new Lazy<StagedProbe>(() =>
            _fixture.StageControlledTasks("Conf.SpinWaitRel", "Conf.SpinWaitProbe", Source, optimize: true));
        _debug = new Lazy<StagedProbe>(() =>
            _fixture.StageControlledTasks("Conf.SpinWaitDbg", "Conf.SpinWaitProbe", Source, optimize: false));
    }

    public static TheoryData<bool> Optimize => new() { true, false };

    [Theory]
    [MemberData(nameof(Optimize))]
    public void SpinUntilWaitsForControlledSignal(bool optimize)
    {
        using var host = new SimulationHost(Start);
        var task = (Task<bool>)host.Invoke(Method("SpinUntilWaitsForControlledSignal", optimize))!;
        Assert.True(Result<bool>(task));
    }

    [Theory]
    [MemberData(nameof(Optimize))]
    public void SpinUntilFiniteSucceeds(bool optimize)
    {
        using var host = new SimulationHost(Start);
        var task = (Task<bool>)host.Invoke(Method("SpinUntilFiniteSucceeds", optimize))!;
        Assert.True(Result<bool>(task));
    }

    [Theory]
    [MemberData(nameof(Optimize))]
    public void SpinUntilFiniteTimesOut(bool optimize)
    {
        using var host = new SimulationHost(Start);
        var task = (Task<bool>)host.Invoke(Method("SpinUntilFiniteTimesOut", optimize))!;
        Assert.False(Result<bool>(task));
    }

    [Theory]
    [MemberData(nameof(Optimize))]
    public void SpinUntilTimeSpanSucceeds(bool optimize)
    {
        using var host = new SimulationHost(Start);
        var task = (Task<bool>)host.Invoke(Method("SpinUntilTimeSpanSucceeds", optimize))!;
        Assert.True(Result<bool>(task));
    }

    [Theory]
    [MemberData(nameof(Optimize))]
    public void SpinUntilZeroTimeout(bool optimize)
    {
        using var host = new SimulationHost(Start);
        var task = (Task<bool>)host.Invoke(Method("SpinUntilZeroTimeout", optimize))!;
        Assert.False(Result<bool>(task));
    }

    [Theory]
    [MemberData(nameof(Optimize))]
    public void SpinOnceAdvancesCount(bool optimize)
    {
        using var host = new SimulationHost(Start);
        var task = (Task<int>)host.Invoke(Method("SpinOnceAdvancesCount", optimize))!;
        Assert.Equal(42, Result<int>(task));
    }

    [Theory]
    [MemberData(nameof(Optimize))]
    public void NextSpinWillYieldEventually(bool optimize)
    {
        using var host = new SimulationHost(Start);
        var task = (Task<bool>)host.Invoke(Method("NextSpinWillYieldEventually", optimize))!;
        Assert.True(Result<bool>(task));
    }

    [Theory]
    [MemberData(nameof(Optimize))]
    public void SpinWaitByRefKeepsCount(bool optimize)
    {
        using var host = new SimulationHost(Start);
        var task = (Task<int>)host.Invoke(Method("SpinWaitByRefKeepsCount", optimize))!;
        Assert.Equal(2, Result<int>(task));
    }

    [Fact]
    public async Task OnlyRewrittenSpinWaitRequiresActiveSimulationWithoutRunningItsPredicate()
    {
        UninstrumentedProbe uninstrumented = _fixture.CompileUninstrumented(
            "Conf.UninstrumentedSpinWait",
            "Conf.SpinWaitProbe",
            Source);
        var task = (Task<int>)uninstrumented.Method("SpinOnceAdvancesCount").Invoke(null, null)!;
        Assert.Equal(42, await task);

        int[] sink = [0];
        SimulationNotActiveExceptionAssert.Throws(Method("SpinUntilActionOnly", optimize: true), sink);
        Assert.Equal(0, sink[0]);
    }

    private MethodInfo Method(string name, bool optimize) =>
        (optimize ? _release : _debug).Value.Method(name);

    private static T Result<T>(object task)
    {
        var typed = (Task)task;
        Assert.True(typed.IsCompleted);
        PropertyInfo result = typed.GetType().GetProperty("Result")!;
        return (T)result.GetValue(typed)!;
    }

    public void Dispose() => _fixture.Dispose();
}
