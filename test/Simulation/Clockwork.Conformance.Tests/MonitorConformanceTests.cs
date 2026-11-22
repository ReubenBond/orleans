using System.Reflection;
using System.Threading.Tasks;

namespace Orleans.Testing.Simulation.Conformance.Tests;

/// <summary>
/// End-to-end conformance for the controlled <see cref="System.Threading.Monitor"/> surface and therefore
/// the C# <c>lock (object)</c> statement. Once a fixture is rewritten with the controlled-task
/// rule set, every <c>lock</c> (which the compiler lowers to <c>Monitor.Enter(obj, ref bool)</c> +
/// <c>finally Monitor.Exit(obj)</c>) and every explicit <c>Monitor</c> call resolves onto the controlled
/// monitor kernel, which models ownership, reentrancy, and condition waits on the single logical thread.
/// The suite is staged twice - Release and Debug codegen - because the compiler lowers <c>lock</c> to
/// materially different IL (extra locals and sequence points) in each, and both must be redirected.
/// </summary>
public sealed class MonitorConformanceTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string Source = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        namespace Conf { public static class MonitorProbe {
            // A C# lock guards a critical section shared between the root strand and a controlled thread.
            public static Task<int> LockGuardsCriticalSection()
            {
                object gate = new object();
                int counter = 0;
                var t = new Thread(() => { lock (gate) { counter += 5; } });
                t.Start();
                lock (gate) { counter += 1; }
                t.Join();
                return Task.FromResult(counter);
            }

            public static void EnterActionOnly(int[] sink)
            {
                lock (new object()) { sink[0] = 42; }
            }

            // A reentrant (recursive) lock on the same object acquires and releases by recursion count.
            public static Task<int> ReentrantLock()
            {
                object gate = new object();
                int value = 0;
                lock (gate)
                {
                    lock (gate)
                    {
                        value = Monitor.IsEntered(gate) ? 7 : -1;
                    }
                    // Still held after the inner scope releases one recursion level.
                    if (!Monitor.IsEntered(gate)) value = -2;
                }
                if (Monitor.IsEntered(gate)) value = -3;
                return Task.FromResult(value);
            }

            // An exception thrown inside a lock releases it via the compiler's finally.
            public static Task<bool> LockReleasesOnException()
            {
                object gate = new object();
                try { lock (gate) { throw new InvalidOperationException("boom"); } }
                catch (InvalidOperationException) { }
                bool released = !Monitor.IsEntered(gate);
                lock (gate) { } // re-acquire proves the monitor is free again
                return Task.FromResult(released && !Monitor.IsEntered(gate));
            }

            // Explicit Enter/IsEntered/Exit round-trips.
            public static Task<bool> ExplicitEnterExit()
            {
                object gate = new object();
                Monitor.Enter(gate);
                bool entered = Monitor.IsEntered(gate);
                Monitor.Exit(gate);
                return Task.FromResult(entered && !Monitor.IsEntered(gate));
            }

            // TryEnter with the non-blocking and zero-timeout overloads acquires an uncontended monitor.
            public static Task<bool> TryEnterAcquires()
            {
                object gate = new object();
                bool got = Monitor.TryEnter(gate);
                if (got) Monitor.Exit(gate);
                bool zero = Monitor.TryEnter(gate, 0);
                if (zero) Monitor.Exit(gate);
                bool taken = false;
                Monitor.TryEnter(gate, 0, ref taken);
                if (taken) Monitor.Exit(gate);
                return Task.FromResult(got && zero && taken);
            }

            // Exit without ownership throws SynchronizationLockException.
            public static Task<bool> ExitWithoutOwnershipThrows()
            {
                object gate = new object();
                try { Monitor.Exit(gate); return Task.FromResult(false); }
                catch (SynchronizationLockException) { return Task.FromResult(true); }
            }

            // Wait/Pulse hand off a value from a producer strand to a waiting consumer strand.
            public static Task<int> WaitPulseHandoff()
            {
                object gate = new object();
                int data = 0; bool ready = false; int observed = -1;
                var consumer = new Thread(() =>
                {
                    lock (gate) { while (!ready) Monitor.Wait(gate); observed = data; }
                });
                consumer.Start();
                var producer = new Thread(() =>
                {
                    lock (gate) { data = 42; ready = true; Monitor.Pulse(gate); }
                });
                producer.Start();
                consumer.Join();
                producer.Join();
                return Task.FromResult(observed);
            }

            // PulseAll wakes every waiter on the monitor.
            public static Task<int> PulseAllWakesEveryWaiter()
            {
                object gate = new object();
                int woken = 0; bool go = false;
                var t1 = new Thread(() => { lock (gate) { while (!go) Monitor.Wait(gate); woken++; } });
                var t2 = new Thread(() => { lock (gate) { while (!go) Monitor.Wait(gate); woken++; } });
                t1.Start(); t2.Start();
                var signal = new Thread(() => { lock (gate) { go = true; Monitor.PulseAll(gate); } });
                signal.Start();
                t1.Join(); t2.Join(); signal.Join();
                return Task.FromResult(woken);
            }

            // A finite TryEnter on a monitor another strand owns waits until the simulated deadline and
            // then reports failure - driven end-to-end by the cluster clock, not wall time. The finite
            // wait is PausedUntilTime, so it is NOT reported as a deadlock cycle.
            public static Task<bool> TryEnterFiniteTimesOut()
            {
                object gate = new object();
                bool timedOut = false;
                Monitor.Enter(gate); // root strand owns the monitor for the whole test
                var contender = new Thread(() => { timedOut = !Monitor.TryEnter(gate, 50); });
                contender.Start();
                contender.Join(); // contender cannot acquire; its deadline fires at virtual t=50
                Monitor.Exit(gate);
                return Task.FromResult(timedOut);
            }

            // A finite Monitor.Wait that is never pulsed reacquires the monitor and returns false once the
            // simulated deadline elapses (advancing modelled time through the cluster clock).
            public static Task<bool> WaitFiniteTimesOut()
            {
                object gate = new object();
                bool signalled = true; bool reentered = false;
                var waiter = new Thread(() =>
                {
                    lock (gate)
                    {
                        signalled = Monitor.Wait(gate, 50);      // never pulsed -> false at t=50
                        reentered = Monitor.IsEntered(gate);     // monitor reacquired before return
                    }
                });
                waiter.Start();
                waiter.Join();
                return Task.FromResult(!signalled && reentered);
            }

            // A finite Monitor.Wait signalled before its deadline returns true (the pulse beats the timeout,
            // because modelled time only advances when nothing else can run).
            public static Task<bool> WaitFiniteCompletesOnPulse()
            {
                object gate = new object();
                bool signalled = false; bool ready = false;
                var waiter = new Thread(() =>
                {
                    lock (gate) { while (!ready) signalled = Monitor.Wait(gate, 10_000); }
                });
                waiter.Start();
                var pulser = new Thread(() => { lock (gate) { ready = true; Monitor.Pulse(gate); } });
                pulser.Start();
                waiter.Join();
                pulser.Join();
                return Task.FromResult(signalled);
            }

            // A never-satisfiable wait on the root strand surfaces as the loop-model deadlock diagnostic.
            // Explicit Enter (rather than a C# lock) is used so no compiler-generated finally runs Exit on
            // the strand that already released the monitor to wait.
            public static Task<bool> UnsatisfiableWaitDeadlocks()
            {
                object gate = new object();
                Monitor.Enter(gate);
                try
                {
                    Monitor.Wait(gate); // nobody will ever pulse
                    return Task.FromResult(false);
                }
                catch (Exception ex) when (ex.GetType().Name.Contains("Deadlock"))
                {
                    return Task.FromResult(true);
                }
            }

            public static Task<bool> RejectsLockContentionCount()
            {
                try
                {
                    _ = Monitor.LockContentionCount;
                    return Task.FromResult(false);
                }
                catch (Exception ex) when (ex.GetType().Name == "SimulationApiException")
                {
                    return Task.FromResult(true);
                }
            }
        } }
        """;

    private readonly RewriteFixture _fixture = new();
    private readonly Lazy<StagedProbe> _release;
    private readonly Lazy<StagedProbe> _debug;

    public MonitorConformanceTests()
    {
        _release = new Lazy<StagedProbe>(() =>
            _fixture.StageControlledTasks("Conf.MonitorRel", "Conf.MonitorProbe", Source, optimize: true));
        _debug = new Lazy<StagedProbe>(() =>
            _fixture.StageControlledTasks("Conf.MonitorDbg", "Conf.MonitorProbe", Source, optimize: false));
    }

    public static TheoryData<bool> Optimize => new() { true, false };

    [Theory]
    [MemberData(nameof(Optimize))]
    public void LockGuardsCriticalSection(bool optimize)
    {
        using var host = new SimulationHost(Start);
        var task = (Task<int>)host.Invoke(Method("LockGuardsCriticalSection", optimize))!;
        Assert.Equal(6, Result<int>(task));
    }

    [Theory]
    [MemberData(nameof(Optimize))]
    public void ReentrantLock(bool optimize)
    {
        using var host = new SimulationHost(Start);
        var task = (Task<int>)host.Invoke(Method("ReentrantLock", optimize))!;
        Assert.Equal(7, Result<int>(task));
    }

    [Theory]
    [MemberData(nameof(Optimize))]
    public void LockReleasesOnException(bool optimize)
    {
        using var host = new SimulationHost(Start);
        var task = (Task<bool>)host.Invoke(Method("LockReleasesOnException", optimize))!;
        Assert.True(Result<bool>(task));
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
    public void TryEnterAcquires(bool optimize)
    {
        using var host = new SimulationHost(Start);
        var task = (Task<bool>)host.Invoke(Method("TryEnterAcquires", optimize))!;
        Assert.True(Result<bool>(task));
    }

    [Theory]
    [MemberData(nameof(Optimize))]
    public void ExitWithoutOwnershipThrows(bool optimize)
    {
        using var host = new SimulationHost(Start);
        var task = (Task<bool>)host.Invoke(Method("ExitWithoutOwnershipThrows", optimize))!;
        Assert.True(Result<bool>(task));
    }

    [Theory]
    [MemberData(nameof(Optimize))]
    public void LockContentionCountIsClassifiedAndRejected(bool optimize)
    {
        using var host = new SimulationHost(Start);
        var task = (Task<bool>)host.Invoke(Method("RejectsLockContentionCount", optimize))!;
        Assert.True(Result<bool>(task));
    }

    [Theory]
    [MemberData(nameof(Optimize))]
    public void WaitPulseHandoff(bool optimize)
    {
        using var host = new SimulationHost(Start);
        var task = (Task<int>)host.Invoke(Method("WaitPulseHandoff", optimize))!;
        Assert.Equal(42, Result<int>(task));
    }

    [Theory]
    [MemberData(nameof(Optimize))]
    public void PulseAllWakesEveryWaiter(bool optimize)
    {
        using var host = new SimulationHost(Start);
        var task = (Task<int>)host.Invoke(Method("PulseAllWakesEveryWaiter", optimize))!;
        Assert.Equal(2, Result<int>(task));
    }

    [Fact]
    public void UnsatisfiableWaitDeadlocks()
    {
        using var host = new SimulationHost(Start);
        var task = (Task<bool>)host.Invoke(Method("UnsatisfiableWaitDeadlocks", optimize: true))!;
        Assert.True(Result<bool>(task));
    }

    [Theory]
    [MemberData(nameof(Optimize))]
    public void TryEnterFiniteTimesOut(bool optimize)
    {
        using var host = new SimulationHost(Start);
        var task = (Task<bool>)host.Invoke(Method("TryEnterFiniteTimesOut", optimize))!;
        Assert.True(Result<bool>(task));
    }

    [Theory]
    [MemberData(nameof(Optimize))]
    public void WaitFiniteTimesOut(bool optimize)
    {
        using var host = new SimulationHost(Start);
        var task = (Task<bool>)host.Invoke(Method("WaitFiniteTimesOut", optimize))!;
        Assert.True(Result<bool>(task));
    }

    [Theory]
    [MemberData(nameof(Optimize))]
    public void WaitFiniteCompletesOnPulse(bool optimize)
    {
        using var host = new SimulationHost(Start);
        var task = (Task<bool>)host.Invoke(Method("WaitFiniteCompletesOnPulse", optimize))!;
        Assert.True(Result<bool>(task));
    }

    [Fact]
    public async Task OnlyRewrittenMonitorRequiresActiveSimulationWithoutEnteringItsAction()
    {
        UninstrumentedProbe uninstrumented = _fixture.CompileUninstrumented(
            "Conf.UninstrumentedMonitor",
            "Conf.MonitorProbe",
            Source);
        var task = (Task<int>)uninstrumented.Method("LockGuardsCriticalSection").Invoke(null, null)!;
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
