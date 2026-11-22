using System.Reflection;
using System.Threading.Tasks;

namespace Orleans.Testing.Simulation.Conformance.Tests;

/// <summary>
/// End-to-end conformance for the controlled <see cref="System.Threading.Lock"/> surface. The
/// rule set substitutes <see cref="System.Threading.Lock"/> and its nested <c>Scope</c> ref struct with the
/// controlled equivalents, so <c>new Lock()</c>, the C# <c>lock (Lock)</c> statement (lowered to
/// <c>Lock.Scope scope = obj.EnterScope(); try { ... } finally { scope.Dispose(); }</c>), and the explicit
/// <c>Enter</c>/<c>Exit</c>/<c>TryEnter</c>/<c>IsHeldByCurrentThread</c> members all resolve onto the
/// controlled monitor kernel. Staged in both Release and Debug codegen because the <c>lock (Lock)</c>
/// scope lowering differs between them.
/// </summary>
public sealed class LockConformanceTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string Source = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        namespace Conf { public static class LockProbe {
            // A C# lock over System.Threading.Lock guards a section shared with a controlled thread.
            public static Task<int> LockStatementGuardsCriticalSection()
            {
                var gate = new Lock();
                int counter = 0;
                var t = new Thread(() => { lock (gate) { counter += 5; } });
                t.Start();
                lock (gate) { counter += 1; }
                t.Join();
                return Task.FromResult(counter);
            }

            public static void EnterActionOnly(int[] sink)
            {
                lock (new Lock()) { sink[0] = 42; }
            }

            // Explicit Enter/IsHeldByCurrentThread/Exit round-trips.
            public static Task<bool> ExplicitEnterExit()
            {
                var gate = new Lock();
                gate.Enter();
                bool held = gate.IsHeldByCurrentThread;
                gate.Exit();
                return Task.FromResult(held && !gate.IsHeldByCurrentThread);
            }

            // EnterScope acquires and the scope's disposal releases (the lock-statement primitive).
            public static Task<bool> EnterScopeReleases()
            {
                var gate = new Lock();
                bool held;
                using (gate.EnterScope())
                {
                    held = gate.IsHeldByCurrentThread;
                }
                return Task.FromResult(held && !gate.IsHeldByCurrentThread);
            }

            // TryEnter acquires an uncontended lock.
            public static Task<bool> TryEnterAcquires()
            {
                var gate = new Lock();
                bool got = gate.TryEnter();
                if (got) gate.Exit();
                bool zero = gate.TryEnter(0);
                if (zero) gate.Exit();
                return Task.FromResult(got && zero);
            }

            // Reentrant lock statements on the same Lock acquire by recursion count.
            public static Task<int> ReentrantLock()
            {
                var gate = new Lock();
                int value = 0;
                lock (gate)
                {
                    lock (gate)
                    {
                        value = gate.IsHeldByCurrentThread ? 9 : -1;
                    }
                    if (!gate.IsHeldByCurrentThread) value = -2;
                }
                if (gate.IsHeldByCurrentThread) value = -3;
                return Task.FromResult(value);
            }
        } }
        """;

    private readonly RewriteFixture _fixture = new();
    private readonly Lazy<StagedProbe> _release;
    private readonly Lazy<StagedProbe> _debug;

    public LockConformanceTests()
    {
        _release = new Lazy<StagedProbe>(() =>
            _fixture.StageControlledTasks("Conf.LockRel", "Conf.LockProbe", Source, optimize: true));
        _debug = new Lazy<StagedProbe>(() =>
            _fixture.StageControlledTasks("Conf.LockDbg", "Conf.LockProbe", Source, optimize: false));
    }

    public static TheoryData<bool> Optimize => new() { true, false };

    [Theory]
    [MemberData(nameof(Optimize))]
    public void LockStatementGuardsCriticalSection(bool optimize)
    {
        using var host = new SimulationHost(Start);
        var task = (Task<int>)host.Invoke(Method("LockStatementGuardsCriticalSection", optimize))!;
        Assert.Equal(6, Result<int>(task));
    }

    [Theory]
    [MemberData(nameof(Optimize))]
    public void ExplicitEnterExit(bool optimize)
    {
        using var host = new SimulationHost(Start);
        var task = (Task<bool>)host.Invoke(Method("ExplicitEnterExit", optimize))!;
        Assert.True(Result<bool>(task));
    }

    [Theory]
    [MemberData(nameof(Optimize))]
    public void EnterScopeReleases(bool optimize)
    {
        using var host = new SimulationHost(Start);
        var task = (Task<bool>)host.Invoke(Method("EnterScopeReleases", optimize))!;
        Assert.True(Result<bool>(task));
    }

    [Theory]
    [MemberData(nameof(Optimize))]
    public void TryEnterAcquires(bool optimize)
    {
        using var host = new SimulationHost(Start);
        var task = (Task<bool>)host.Invoke(Method("TryEnterAcquires", optimize))!;
        Assert.True(Result<bool>(task));
    }

    [Theory]
    [MemberData(nameof(Optimize))]
    public void ReentrantLock(bool optimize)
    {
        using var host = new SimulationHost(Start);
        var task = (Task<int>)host.Invoke(Method("ReentrantLock", optimize))!;
        Assert.Equal(9, Result<int>(task));
    }

    [Fact]
    public async Task OnlyRewrittenLockRequiresActiveSimulationWithoutEnteringItsAction()
    {
        UninstrumentedProbe uninstrumented = _fixture.CompileUninstrumented(
            "Conf.UninstrumentedLock",
            "Conf.LockProbe",
            Source);
        var task = (Task<int>)uninstrumented.Method("LockStatementGuardsCriticalSection").Invoke(null, null)!;
        Assert.Equal(6, await task);

        int[] sink = [0];
        SimulationNotActiveExceptionAssert.Throws(Method("EnterActionOnly", optimize: true), sink);
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
