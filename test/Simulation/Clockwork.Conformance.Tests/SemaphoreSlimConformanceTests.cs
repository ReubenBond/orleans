using System.Reflection;
using System.Threading.Tasks;

namespace Orleans.Testing.Simulation.Conformance.Tests;

/// <summary>
/// End-to-end conformance for the controlled <see cref="System.Threading.SemaphoreSlim"/> surface. The
/// rule set redirects the constructors to the controlled <c>Create</c> factories and every
/// instance member (<c>CurrentCount</c>, the synchronous <c>Wait</c> overloads, the asynchronous
/// <c>WaitAsync</c> overloads, <c>Release</c>, <c>Dispose</c>) to receiver-first controlled shims whose
/// permit count and waiter set live on the single logical thread. <c>AvailableWaitHandle</c> is bridged
/// to a controlled manual-reset handle that tracks count &gt; 0.
/// </summary>
public sealed class SemaphoreSlimConformanceTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string Source = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        namespace Conf { public static class SemaphoreProbe {
            // A synchronous Wait blocks until a controlled thread releases a permit.
            public static Task<int> WaitBlocksUntilRelease()
            {
                var s = new SemaphoreSlim(0, 2);
                var releaser = new Thread(() => s.Release());
                releaser.Start();
                s.Wait();               // pumps the loop; the releaser runs and hands over a permit
                releaser.Join();
                return Task.FromResult(s.CurrentCount);
            }

            // The zero-timeout Wait is a faithful non-blocking try.
            public static Task<bool> ZeroTimeoutTry()
            {
                var s = new SemaphoreSlim(1, 1);
                bool first = s.Wait(0);   // succeeds, count 1 -> 0
                bool second = s.Wait(0);  // fails, count 0
                return Task.FromResult(first && !second);
            }

            public static void CreateOnly(int[] sink)
            {
                using var s = new SemaphoreSlim(1, 1);
                sink[0] = s.CurrentCount;
            }

            // WaitAsync completes once a controlled thread releases a permit.
            public static async Task<int> WaitAsyncCompletesOnRelease()
            {
                var s = new SemaphoreSlim(0, 2);
                var releaser = new Thread(() => s.Release());
                releaser.Start();
                await s.WaitAsync();
                releaser.Join();
                return s.CurrentCount;
            }

            // WaitAsync with an available permit completes synchronously.
            public static async Task<bool> WaitAsyncImmediate()
            {
                var s = new SemaphoreSlim(1, 1);
                await s.WaitAsync();
                bool zero = s.CurrentCount == 0;
                s.Release();
                return zero;
            }

            // Releasing beyond the maximum count throws SemaphoreFullException.
            public static Task<bool> ReleaseBeyondMaxThrows()
            {
                var s = new SemaphoreSlim(1, 1);
                try { s.Release(); return Task.FromResult(false); }
                catch (SemaphoreFullException) { return Task.FromResult(true); }
            }

            // AvailableWaitHandle is signalled while a permit is available and clears when drained.
            public static Task<bool> AvailableWaitHandleTracksCount()
            {
                var s = new SemaphoreSlim(1, 2);
                WaitHandle h = s.AvailableWaitHandle;
                bool availableInitially = h.WaitOne(0); // Signalled: a permit is available.
                s.Wait();                               // Drain the only permit.
                bool clearedWhenEmpty = !h.WaitOne(0);  // Cleared: no permit.
                s.Release();                            // Refill.
                bool reSignalled = h.WaitOne(0);        // Signalled again on the same handle.
                return Task.FromResult(availableInitially && clearedWhenEmpty && reSignalled);
            }

            // A thread waiting on AvailableWaitHandle wakes when another thread releases a permit.
            public static Task<bool> AvailableWaitHandleWakesOnRelease()
            {
                var s = new SemaphoreSlim(0, 1);
                WaitHandle h = s.AvailableWaitHandle;
                bool woke = false;
                var t = new Thread(() => { woke = h.WaitOne(); });
                t.Start();
                s.Release();
                t.Join();
                // Observing the handle did not consume the permit.
                return Task.FromResult(woke && s.CurrentCount == 1);
            }

            // A finite synchronous Wait with no permit returns false once the simulated deadline elapses,
            // advancing modelled time through the cluster clock (no wall-clock wait).
            public static Task<bool> WaitFiniteTimesOut()
            {
                var s = new SemaphoreSlim(0, 1);
                bool got = s.Wait(50);
                return Task.FromResult(!got);
            }

            // The asynchronous WaitAsync finite overload likewise completes with false on the deadline.
            public static async Task<bool> WaitAsyncFiniteTimesOut()
            {
                var s = new SemaphoreSlim(0, 1);
                bool got = await s.WaitAsync(50);
                return !got;
            }

            // A release before the deadline completes the finite WaitAsync with true (the release beats the
            // timeout because modelled time only advances when nothing else can run).
            public static async Task<bool> WaitAsyncFiniteCompletesOnRelease()
            {
                var s = new SemaphoreSlim(0, 1);
                var releaser = new Thread(() => s.Release());
                releaser.Start();
                bool got = await s.WaitAsync(10_000);
                releaser.Join();
                return got;
            }

            // Cancellation before the deadline throws OperationCanceledException rather than timing out.
            public static async Task<bool> WaitAsyncFiniteCancelThrows()
            {
                var s = new SemaphoreSlim(0, 1);
                var cts = new CancellationTokenSource();
                var canceller = new Thread(() => cts.Cancel());
                canceller.Start();
                try
                {
                    await s.WaitAsync(10_000, cts.Token);
                    return false;
                }
                catch (OperationCanceledException)
                {
                    canceller.Join();
                    return true;
                }
            }
        } }
        """;

    private readonly RewriteFixture _fixture = new();
    private readonly Lazy<StagedProbe> _release;
    private readonly Lazy<StagedProbe> _debug;

    public SemaphoreSlimConformanceTests()
    {
        _release = new Lazy<StagedProbe>(() =>
            _fixture.StageControlledTasks("Conf.SemaphoreRel", "Conf.SemaphoreProbe", Source, optimize: true));
        _debug = new Lazy<StagedProbe>(() =>
            _fixture.StageControlledTasks("Conf.SemaphoreDbg", "Conf.SemaphoreProbe", Source, optimize: false));
    }

    public static TheoryData<bool> Optimize => new() { true, false };

    [Theory]
    [MemberData(nameof(Optimize))]
    public void SemaphoreLoweringIsEquivalentInDebugAndRelease(bool optimize)
    {
        using var host = new SimulationHost(Start);
        Assert.Equal(0, Result<int>((Task<int>)host.Invoke(Method("WaitBlocksUntilRelease", optimize))!));
        Assert.Equal(0, Result<int>((Task<int>)host.Invoke(Method("WaitAsyncCompletesOnRelease", optimize))!));
        Assert.True(Result<bool>((Task<bool>)host.Invoke(Method("WaitFiniteTimesOut", optimize))!));
        Assert.True(Result<bool>((Task<bool>)host.Invoke(Method("AvailableWaitHandleTracksCount", optimize))!));
    }

    [Fact]
    public void WaitBlocksUntilRelease()
    {
        using var host = new SimulationHost(Start);
        var task = (Task<int>)host.Invoke(Method("WaitBlocksUntilRelease"))!;
        Assert.Equal(0, Result<int>(task));
    }

    [Fact]
    public void ZeroTimeoutTry()
    {
        using var host = new SimulationHost(Start);
        var task = (Task<bool>)host.Invoke(Method("ZeroTimeoutTry"))!;
        Assert.True(Result<bool>(task));
    }

    [Fact]
    public void WaitAsyncCompletesOnRelease()
    {
        using var host = new SimulationHost(Start);
        var task = (Task<int>)host.Invoke(Method("WaitAsyncCompletesOnRelease"))!;
        Assert.Equal(0, Result<int>(task));
    }

    [Fact]
    public void WaitAsyncImmediate()
    {
        using var host = new SimulationHost(Start);
        var task = (Task<bool>)host.Invoke(Method("WaitAsyncImmediate"))!;
        Assert.True(Result<bool>(task));
    }

    [Fact]
    public void ReleaseBeyondMaxThrows()
    {
        using var host = new SimulationHost(Start);
        var task = (Task<bool>)host.Invoke(Method("ReleaseBeyondMaxThrows"))!;
        Assert.True(Result<bool>(task));
    }

    [Fact]
    public void AvailableWaitHandleTracksCount()
    {
        using var host = new SimulationHost(Start);
        var task = (Task<bool>)host.Invoke(Method("AvailableWaitHandleTracksCount"))!;
        Assert.True(Result<bool>(task));
    }

    [Fact]
    public void AvailableWaitHandleWakesOnRelease()
    {
        using var host = new SimulationHost(Start);
        var task = (Task<bool>)host.Invoke(Method("AvailableWaitHandleWakesOnRelease"))!;
        Assert.True(Result<bool>(task));
    }

    [Fact]
    public void WaitFiniteTimesOut()
    {
        using var host = new SimulationHost(Start);
        var task = (Task<bool>)host.Invoke(Method("WaitFiniteTimesOut"))!;
        Assert.True(Result<bool>(task));
    }

    [Fact]
    public void WaitAsyncFiniteTimesOut()
    {
        using var host = new SimulationHost(Start);
        var task = (Task<bool>)host.Invoke(Method("WaitAsyncFiniteTimesOut"))!;
        Assert.True(Result<bool>(task));
    }

    [Fact]
    public void WaitAsyncFiniteCompletesOnRelease()
    {
        using var host = new SimulationHost(Start);
        var task = (Task<bool>)host.Invoke(Method("WaitAsyncFiniteCompletesOnRelease"))!;
        Assert.True(Result<bool>(task));
    }

    [Fact]
    public void WaitAsyncFiniteCancelThrows()
    {
        using var host = new SimulationHost(Start);
        var task = (Task<bool>)host.Invoke(Method("WaitAsyncFiniteCancelThrows"))!;
        Assert.True(Result<bool>(task));
    }

    [Fact]
    public async Task OnlyRewrittenSemaphoreRequiresActiveSimulationWithoutMutatingState()
    {
        UninstrumentedProbe uninstrumented = _fixture.CompileUninstrumented(
            "Conf.UninstrumentedSemaphore",
            "Conf.SemaphoreProbe",
            Source);
        var task = (Task<bool>)uninstrumented.Method("ZeroTimeoutTry").Invoke(null, null)!;
        Assert.True(await task);

        int[] sink = [0];
        SimulationNotActiveExceptionAssert.Throws(Method("CreateOnly"), sink);
        Assert.Equal(0, sink[0]);
    }

    private MethodInfo Method(string name) => _release.Value.Method(name);

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
