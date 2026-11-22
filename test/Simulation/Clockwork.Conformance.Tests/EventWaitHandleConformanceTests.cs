using System.Reflection;
using System.Threading.Tasks;

namespace Orleans.Testing.Simulation.Conformance.Tests;

/// <summary>
/// End-to-end conformance for the controlled event / wait-handle surface. The rule set redirects
/// <c>new AutoResetEvent</c> / <c>new ManualResetEvent</c> / <c>new EventWaitHandle</c> to controlled Create
/// factories, the inherited <see cref="System.Threading.WaitHandle.WaitOne()"/> overloads and
/// <c>Dispose</c>/<c>Close</c> to the controlled wait kernel, and <c>Set</c>/<c>Reset</c> to controlled
/// signalling. A <c>WaitOne</c> with no signal pumps the deterministic loop until a controlled thread
/// <c>Set</c>s the event; an auto-reset event wakes exactly one waiter while a manual-reset event releases
/// all; finite timeouts consume only virtual time; named / cross-process APIs are rejected. Staged in both
/// Release and Debug codegen.
/// </summary>
public sealed class EventWaitHandleConformanceTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string Source = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        namespace Conf { public static class EventProbe {
            // A blocked WaitOne resumes when a controlled thread Sets the auto-reset event.
            public static Task<bool> AutoResetSetWakesWaiter()
            {
                var evt = new AutoResetEvent(false);
                bool woke = false;
                var t = new Thread(() => { evt.WaitOne(); woke = true; });
                t.Start();
                evt.Set();
                t.Join();
                return Task.FromResult(woke);
            }

            // An auto-reset Set releases exactly one of two waiters; a second Set releases the other.
            public static Task<int> AutoResetSetWakesExactlyOne()
            {
                var evt = new AutoResetEvent(false);
                int woke = 0;
                var a = new Thread(() => { evt.WaitOne(); Interlocked.Increment(ref woke); });
                var b = new Thread(() => { evt.WaitOne(); Interlocked.Increment(ref woke); });
                a.Start();
                b.Start();
                evt.Set();
                a.Join();
                int afterFirst = woke;
                evt.Set();
                b.Join();
                // Encode both observations: exactly one after the first Set, two after the second.
                return Task.FromResult((afterFirst == 1 && woke == 2) ? 42 : -1);
            }

            // A manual-reset Set releases all waiters and stays signalled.
            public static Task<bool> ManualResetReleasesAllAndStaysSet()
            {
                var evt = new ManualResetEvent(false);
                int woke = 0;
                var a = new Thread(() => { evt.WaitOne(); Interlocked.Increment(ref woke); });
                var b = new Thread(() => { evt.WaitOne(); Interlocked.Increment(ref woke); });
                a.Start();
                b.Start();
                evt.Set();
                a.Join();
                b.Join();
                // Still signalled after releasing both, so a further zero-timeout wait succeeds.
                bool stillSet = evt.WaitOne(0);
                return Task.FromResult(woke == 2 && stillSet);
            }

            // Reset clears a manual-reset event so a subsequent zero-timeout wait fails.
            public static Task<bool> ManualResetResetClearsSignal()
            {
                var evt = new ManualResetEvent(true);
                bool before = evt.WaitOne(0);
                evt.Reset();
                bool after = evt.WaitOne(0);
                return Task.FromResult(before && !after);
            }

            // A finite WaitOne times out via the virtual deadline when the event is never set.
            public static Task<bool> WaitFiniteTimesOut()
            {
                var evt = new AutoResetEvent(false);
                return Task.FromResult(evt.WaitOne(50));
            }

            // A finite WaitOne succeeds when a controlled thread Sets before the virtual deadline.
            public static Task<bool> WaitFiniteSucceedsBeforeDeadline()
            {
                var evt = new AutoResetEvent(false);
                var t = new Thread(() => { evt.Set(); });
                t.Start();
                bool got = evt.WaitOne(1000000);
                t.Join();
                return Task.FromResult(got);
            }

            // An EventWaitHandle constructed with an explicit reset mode behaves as an auto-reset event.
            public static Task<bool> EventWaitHandleAutoResetMode()
            {
                var evt = new EventWaitHandle(false, EventResetMode.AutoReset);
                bool woke = false;
                var t = new Thread(() => { evt.WaitOne(); woke = true; });
                t.Start();
                evt.Set();
                t.Join();
                bool consumed = !evt.WaitOne(0); // Auto-reset consumed the signal.
                return Task.FromResult(woke && consumed);
            }

            // A named event constructor is rejected precisely inside a simulation.
            public static Task<bool> NamedEventRejected()
            {
                try
                {
                    var evt = new EventWaitHandle(false, EventResetMode.AutoReset, "clockwork-conf-named");
                    return Task.FromResult(false);
                }
                catch (Exception ex)
                {
                    return Task.FromResult(ex.GetType().FullName == "Orleans.Testing.Simulation.Runtime.SimulationApiException");
                }
            }

            // WaitAny returns the lowest-index signalled handle.
            public static Task<int> WaitAnyReturnsLowestIndex()
            {
                var a = new AutoResetEvent(false);
                var b = new ManualResetEvent(true);
                var c = new AutoResetEvent(true);
                return Task.FromResult(WaitHandle.WaitAny(new WaitHandle[] { a, b, c }));
            }

            // WaitAny blocks until a controlled thread signals one of the handles.
            public static Task<int> WaitAnyWakesOnSignal()
            {
                var a = new AutoResetEvent(false);
                var b = new AutoResetEvent(false);
                var t = new Thread(() => { b.Set(); });
                t.Start();
                int index = WaitHandle.WaitAny(new WaitHandle[] { a, b });
                t.Join();
                return Task.FromResult(index);
            }

            // WaitAny with a finite timeout returns WaitTimeout via the virtual deadline.
            public static Task<bool> WaitAnyTimesOut()
            {
                var a = new AutoResetEvent(false);
                var b = new AutoResetEvent(false);
                int index = WaitHandle.WaitAny(new WaitHandle[] { a, b }, 50);
                return Task.FromResult(index == WaitHandle.WaitTimeout);
            }

            // WaitAll succeeds only when all handles are simultaneously signalled and consumes atomically.
            public static Task<bool> WaitAllAtomicConsume()
            {
                var a = new AutoResetEvent(false);
                var b = new AutoResetEvent(false);
                bool beforeAll = WaitHandle.WaitAll(new WaitHandle[] { a, b }, 0);
                a.Set();
                b.Set();
                bool all = WaitHandle.WaitAll(new WaitHandle[] { a, b });
                // Both auto-reset handles consumed atomically, so neither remains signalled.
                bool aConsumed = !a.WaitOne(0);
                bool bConsumed = !b.WaitOne(0);
                return Task.FromResult(!beforeAll && all && aConsumed && bConsumed);
            }

            // WaitAll rejects duplicate handles with DuplicateWaitObjectException.
            public static Task<bool> WaitAllRejectsDuplicates()
            {
                var a = new AutoResetEvent(false);
                try
                {
                    WaitHandle.WaitAll(new WaitHandle[] { a, a });
                    return Task.FromResult(false);
                }
                catch (DuplicateWaitObjectException)
                {
                    return Task.FromResult(true);
                }
            }

            // SignalAndWait atomically signals the first handle then waits on the second.
            public static Task<bool> SignalAndWaitSignalsThenWaits()
            {
                var gate = new ManualResetEvent(false);
                var proceed = new AutoResetEvent(false);
                var partner = new Thread(() => { gate.WaitOne(); proceed.Set(); });
                partner.Start();
                bool got = WaitHandle.SignalAndWait(gate, proceed);
                partner.Join();
                return Task.FromResult(got);
            }
        } }
        """;

    private readonly RewriteFixture _fixture = new();
    private readonly Lazy<StagedProbe> _release;
    private readonly Lazy<StagedProbe> _debug;

    public EventWaitHandleConformanceTests()
    {
        _release = new Lazy<StagedProbe>(() =>
            _fixture.StageControlledTasks("Conf.EventRel", "Conf.EventProbe", Source, optimize: true));
        _debug = new Lazy<StagedProbe>(() =>
            _fixture.StageControlledTasks("Conf.EventDbg", "Conf.EventProbe", Source, optimize: false));
    }

    public static TheoryData<bool> Optimize => new() { true, false };

    [Theory]
    [MemberData(nameof(Optimize))]
    public void AutoResetSetWakesWaiter(bool optimize)
    {
        using var host = new SimulationHost(Start);
        var task = (Task<bool>)host.Invoke(Method("AutoResetSetWakesWaiter", optimize))!;
        Assert.True(Result<bool>(task));
    }

    [Theory]
    [MemberData(nameof(Optimize))]
    public void AutoResetSetWakesExactlyOne(bool optimize)
    {
        using var host = new SimulationHost(Start);
        var task = (Task<int>)host.Invoke(Method("AutoResetSetWakesExactlyOne", optimize))!;
        Assert.Equal(42, Result<int>(task));
    }

    [Theory]
    [MemberData(nameof(Optimize))]
    public void ManualResetReleasesAllAndStaysSet(bool optimize)
    {
        using var host = new SimulationHost(Start);
        var task = (Task<bool>)host.Invoke(Method("ManualResetReleasesAllAndStaysSet", optimize))!;
        Assert.True(Result<bool>(task));
    }

    [Theory]
    [MemberData(nameof(Optimize))]
    public void ManualResetResetClearsSignal(bool optimize)
    {
        using var host = new SimulationHost(Start);
        var task = (Task<bool>)host.Invoke(Method("ManualResetResetClearsSignal", optimize))!;
        Assert.True(Result<bool>(task));
    }

    [Theory]
    [MemberData(nameof(Optimize))]
    public void WaitFiniteTimesOut(bool optimize)
    {
        using var host = new SimulationHost(Start);
        var task = (Task<bool>)host.Invoke(Method("WaitFiniteTimesOut", optimize))!;
        Assert.False(Result<bool>(task));
    }

    [Theory]
    [MemberData(nameof(Optimize))]
    public void WaitFiniteSucceedsBeforeDeadline(bool optimize)
    {
        using var host = new SimulationHost(Start);
        var task = (Task<bool>)host.Invoke(Method("WaitFiniteSucceedsBeforeDeadline", optimize))!;
        Assert.True(Result<bool>(task));
    }

    [Theory]
    [MemberData(nameof(Optimize))]
    public void EventWaitHandleAutoResetMode(bool optimize)
    {
        using var host = new SimulationHost(Start);
        var task = (Task<bool>)host.Invoke(Method("EventWaitHandleAutoResetMode", optimize))!;
        Assert.True(Result<bool>(task));
    }

    [Theory]
    [MemberData(nameof(Optimize))]
    public void NamedEventRejected(bool optimize)
    {
        using var host = new SimulationHost(Start);
        var task = (Task<bool>)host.Invoke(Method("NamedEventRejected", optimize))!;
        Assert.True(Result<bool>(task));
    }

    [Theory]
    [MemberData(nameof(Optimize))]
    public void WaitAnyReturnsLowestIndex(bool optimize)
    {
        using var host = new SimulationHost(Start);
        var task = (Task<int>)host.Invoke(Method("WaitAnyReturnsLowestIndex", optimize))!;
        Assert.Equal(1, Result<int>(task));
    }

    [Theory]
    [MemberData(nameof(Optimize))]
    public void WaitAnyWakesOnSignal(bool optimize)
    {
        using var host = new SimulationHost(Start);
        var task = (Task<int>)host.Invoke(Method("WaitAnyWakesOnSignal", optimize))!;
        Assert.Equal(1, Result<int>(task));
    }

    [Theory]
    [MemberData(nameof(Optimize))]
    public void WaitAnyTimesOut(bool optimize)
    {
        using var host = new SimulationHost(Start);
        var task = (Task<bool>)host.Invoke(Method("WaitAnyTimesOut", optimize))!;
        Assert.True(Result<bool>(task));
    }

    [Theory]
    [MemberData(nameof(Optimize))]
    public void WaitAllAtomicConsume(bool optimize)
    {
        using var host = new SimulationHost(Start);
        var task = (Task<bool>)host.Invoke(Method("WaitAllAtomicConsume", optimize))!;
        Assert.True(Result<bool>(task));
    }

    [Theory]
    [MemberData(nameof(Optimize))]
    public void WaitAllRejectsDuplicates(bool optimize)
    {
        using var host = new SimulationHost(Start);
        var task = (Task<bool>)host.Invoke(Method("WaitAllRejectsDuplicates", optimize))!;
        Assert.True(Result<bool>(task));
    }

    [Theory]
    [MemberData(nameof(Optimize))]
    public void SignalAndWaitSignalsThenWaits(bool optimize)
    {
        using var host = new SimulationHost(Start);
        var task = (Task<bool>)host.Invoke(Method("SignalAndWaitSignalsThenWaits", optimize))!;
        Assert.True(Result<bool>(task));
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
