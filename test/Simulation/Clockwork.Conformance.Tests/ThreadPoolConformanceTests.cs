using System.Reflection;
using System.Threading.Tasks;

namespace Orleans.Testing.Simulation.Conformance.Tests;

/// <summary>
/// End-to-end conformance for the controlled <see cref="System.Threading.ThreadPool"/> queueing surface
/// (beyond Coyote). Once a fixture is rewritten with the controlled-task rule set,
/// <c>ThreadPool.QueueUserWorkItem</c> / <c>UnsafeQueueUserWorkItem</c> queue their callback as a fresh
/// controlled operation that runs deterministically on the single logical thread under the cluster drive,
/// callbacks run in FIFO order with exactly one running at a time, and the safe-vs-unsafe
/// <c>ExecutionContext</c> flow distinction is observable at real call sites. wait-handle and atomic control additionally
/// controls the registered-wait factories (<c>RegisterWaitForSingleObject</c>/<c>Unsafe…</c>): the
/// callback fires with <c>timedOut:false</c> on a signal and <c>timedOut:true</c> on the virtual-time
/// deadline, honours <c>executeOnlyOnce</c>/re-arm, and stops on <c>Unregister</c>.
/// </summary>
public sealed class ThreadPoolConformanceTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string Source = """
        using System.Threading;
        using System.Threading.Tasks;
        namespace Conf { public static class PoolProbe {
            // QueueUserWorkItem runs its callback under the cluster drive and completes the awaited task.
            public static async Task<int> QueueRunsCallback()
            {
                var tcs = new TaskCompletionSource<int>();
                ThreadPool.QueueUserWorkItem(_ => tcs.SetResult(42));
                return await tcs.Task;
            }

            public static bool QueueActionOnly(int[] sink) =>
                ThreadPool.QueueUserWorkItem(_ => sink[0] = 42);

            // Generic QueueUserWorkItem<TState> passes strongly-typed state.
            public static async Task<int> GenericQueuePassesState()
            {
                var tcs = new TaskCompletionSource<int>();
                ThreadPool.QueueUserWorkItem(s => tcs.SetResult(s), 7, false);
                return await tcs.Task;
            }

            // UnsafeQueueUserWorkItem(IThreadPoolWorkItem) executes the work item.
            public static async Task<bool> UnsafeWorkItemExecutes()
            {
                var tcs = new TaskCompletionSource<bool>();
                ThreadPool.UnsafeQueueUserWorkItem(new Item(tcs), false);
                return await tcs.Task;
            }

            private sealed class Item : IThreadPoolWorkItem
            {
                private readonly TaskCompletionSource<bool> _tcs;
                public Item(TaskCompletionSource<bool> tcs) => _tcs = tcs;
                public void Execute() => _tcs.SetResult(true);
            }

            // Callbacks run in FIFO order on the single logical thread (exactly one running at a time).
            public static async Task<string> CallbacksRunInOrder()
            {
                var sb = new System.Text.StringBuilder();
                var tcs = new TaskCompletionSource();
                ThreadPool.QueueUserWorkItem(_ => sb.Append('a'));
                ThreadPool.QueueUserWorkItem(_ => sb.Append('b'));
                ThreadPool.QueueUserWorkItem(_ => { sb.Append('c'); tcs.SetResult(); });
                await tcs.Task;
                return sb.ToString();
            }

            // Safe QueueUserWorkItem flows the caller's ExecutionContext (snapshot at enqueue time).
            public static async Task<int> SafeFlowsContext()
            {
                var ambient = new AsyncLocal<int>();
                ambient.Value = 5;
                var tcs = new TaskCompletionSource<int>();
                ThreadPool.QueueUserWorkItem(_ => tcs.SetResult(ambient.Value));
                ambient.Value = 9;
                return await tcs.Task;
            }

            // Unsafe variant does not flow the caller's ExecutionContext, so the callback observes the
            // drive loop's ambient context where the caller's AsyncLocal is unset (default 0).
            public static async Task<int> UnsafeDoesNotFlowContext()
            {
                var ambient = new AsyncLocal<int>();
                ambient.Value = 5;
                var tcs = new TaskCompletionSource<int>();
                ThreadPool.UnsafeQueueUserWorkItem(_ => tcs.SetResult(ambient.Value), null);
                ambient.Value = 9;
                return await tcs.Task;
            }

            // A registered wait fires its callback with timedOut=false when the handle is signalled.
            public static async Task<bool> RegisterWaitFiresOnSignal()
            {
                using var evt = new AutoResetEvent(false);
                var tcs = new TaskCompletionSource<bool>();
                var reg = ThreadPool.RegisterWaitForSingleObject(
                    evt, (_, timedOut) => tcs.SetResult(timedOut), null, Timeout.Infinite, true);
                evt.Set();
                bool timedOut = await tcs.Task;
                reg.Unregister(null);
                return !timedOut;
            }

            // A registered wait fires its callback with timedOut=true when its virtual deadline elapses.
            public static async Task<bool> RegisterWaitFiresOnTimeout()
            {
                using var evt = new AutoResetEvent(false);
                var tcs = new TaskCompletionSource<bool>();
                ThreadPool.RegisterWaitForSingleObject(
                    evt, (_, timedOut) => tcs.SetResult(timedOut), null, 50, true);
                return await tcs.Task;
            }

            // A repeating registration (executeOnlyOnce:false) fires once per signal until unregistered.
            public static async Task<int> RepeatingRegisterWaitFiresPerSignal()
            {
                using var evt = new AutoResetEvent(false);
                int count = 0;
                var gate = new[] { new TaskCompletionSource(), new TaskCompletionSource() };
                var reg = ThreadPool.RegisterWaitForSingleObject(
                    evt, (_, _) => { int n = ++count; gate[n - 1].SetResult(); }, null, Timeout.Infinite, false);
                evt.Set();
                await gate[0].Task;
                evt.Set();
                await gate[1].Task;
                reg.Unregister(null);
                return count;
            }

            // Unregister stops a repeating registration: a later signal does not fire the callback again.
            public static async Task<int> UnregisterStopsTheWait()
            {
                using var evt = new AutoResetEvent(false);
                int count = 0;
                var first = new TaskCompletionSource();
                var reg = ThreadPool.RegisterWaitForSingleObject(
                    evt, (_, _) => { count++; first.TrySetResult(); }, null, Timeout.Infinite, false);
                evt.Set();
                await first.Task;
                reg.Unregister(null);
                evt.Set();
                await Task.Yield();
                await Task.Yield();
                return count;
            }

            public static async Task<long[]> IdentityAndTrace(System.Func<long> strand)
            {
                long[] values = new long[9];
                values[0] = System.Environment.CurrentManagedThreadId;
                values[1] = strand();
                var trace = new System.Collections.Generic.List<int>();
                var completed = new TaskCompletionSource();
                int remaining = 3;
                for (int i = 0; i < remaining; i++)
                {
                    ThreadPool.QueueUserWorkItem(state =>
                    {
                        int index = (int)state!;
                        values[2 + index * 2] = System.Environment.CurrentManagedThreadId;
                        values[3 + index * 2] = strand();
                        trace.Add(index + 1);
                        if (--remaining == 0) completed.SetResult();
                    }, i);
                }

                await completed.Task;
                values[8] = trace[0] * 100 + trace[1] * 10 + trace[2];
                return values;
            }
        } }
        """;

    private readonly RewriteFixture _fixture = new();
    private readonly Lazy<StagedProbe> _release;
    private readonly Lazy<StagedProbe> _debug;

    public ThreadPoolConformanceTests()
    {
        _release = new Lazy<StagedProbe>(() =>
            _fixture.StageControlledTasks("Conf.ThreadPoolRel", "Conf.PoolProbe", Source, optimize: true));
        _debug = new Lazy<StagedProbe>(() =>
            _fixture.StageControlledTasks("Conf.ThreadPoolDbg", "Conf.PoolProbe", Source, optimize: false));
    }

    public static TheoryData<bool> Optimize => new() { true, false };

    [Theory]
    [MemberData(nameof(Optimize))]
    public void QueueUsesFreshControlledStrandsWithRepeatableTrace(bool optimize)
    {
        long[] Run()
        {
            using var host = new SimulationHost(Start);
            return Result<long[]>((Task<long[]>)host.Invoke(
                Method("IdentityAndTrace", optimize),
                (Func<long>)(() => Orleans.Testing.Simulation.Runtime.Threading.SimulationSynchronizationFlow.CurrentId))!);
        }

        long[] first = Run();
        long[] second = Run();
        Assert.NotEqual(Orleans.Testing.Simulation.Runtime.Threading.SimulationSynchronizationFlow.None, first[1]);
        Assert.Equal(123, first[8]);
        Assert.Equal(first[8], second[8]);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(first[0], first[2 + i * 2]);
            Assert.NotEqual(Orleans.Testing.Simulation.Runtime.Threading.SimulationSynchronizationFlow.None, first[3 + i * 2]);
            Assert.NotEqual(first[1], first[3 + i * 2]);
        }
    }

    [Fact]
    public void QueueUserWorkItemRunsCallbackUnderTheClusterDrive()
    {
        using var host = new SimulationHost(Start);
        var task = (Task<int>)host.Invoke(Method("QueueRunsCallback"))!;
        Assert.Equal(42, Result<int>(task));
    }

    [Fact]
    public void GenericQueueUserWorkItemPassesTypedState()
    {
        using var host = new SimulationHost(Start);
        var task = (Task<int>)host.Invoke(Method("GenericQueuePassesState"))!;
        Assert.Equal(7, Result<int>(task));
    }

    [Fact]
    public void UnsafeWorkItemExecutesUnderTheClusterDrive()
    {
        using var host = new SimulationHost(Start);
        var task = (Task<bool>)host.Invoke(Method("UnsafeWorkItemExecutes"))!;
        Assert.True(Result<bool>(task));
    }

    [Fact]
    public void CallbacksRunInFifoOrderOnTheSingleLogicalThread()
    {
        using var host = new SimulationHost(Start);
        var task = (Task<string>)host.Invoke(Method("CallbacksRunInOrder"))!;
        Assert.Equal("abc", Result<string>(task));
    }

    [Fact]
    public void SafeQueueFlowsExecutionContext()
    {
        using var host = new SimulationHost(Start);
        var task = (Task<int>)host.Invoke(Method("SafeFlowsContext"))!;
        Assert.Equal(5, Result<int>(task));
    }

    [Fact]
    public void UnsafeQueueDoesNotFlowExecutionContext()
    {
        using var host = new SimulationHost(Start);
        var task = (Task<int>)host.Invoke(Method("UnsafeDoesNotFlowContext"))!;
        Assert.Equal(0, Result<int>(task));
    }

    [Fact]
    public async Task OnlyRewrittenQueueRequiresActiveSimulationWithoutRunningItsCallback()
    {
        UninstrumentedProbe uninstrumented = _fixture.CompileUninstrumented(
            "Conf.UninstrumentedThreadPool",
            "Conf.PoolProbe",
            Source);
        var task = (Task<int>)uninstrumented.Method("QueueRunsCallback").Invoke(null, null)!;
        Assert.Equal(42, await task);

        int[] sink = [0];
        SimulationNotActiveExceptionAssert.Throws(Method("QueueActionOnly"), sink);
        Assert.Equal(0, sink[0]);
    }

    [Fact]
    public void RegisterWaitFiresCallbackOnSignalWithTimedOutFalse()
    {
        using var host = new SimulationHost(Start);
        var task = (Task<bool>)host.Invoke(Method("RegisterWaitFiresOnSignal"))!;
        Assert.True(Result<bool>(task));
    }

    [Fact]
    public void RegisterWaitFiresCallbackOnTimeoutWithTimedOutTrue()
    {
        using var host = new SimulationHost(Start);
        var task = (Task<bool>)host.Invoke(Method("RegisterWaitFiresOnTimeout"))!;
        Assert.True(Result<bool>(task));
    }

    [Fact]
    public void RepeatingRegisterWaitFiresOncePerSignal()
    {
        using var host = new SimulationHost(Start);
        var task = (Task<int>)host.Invoke(Method("RepeatingRegisterWaitFiresPerSignal"))!;
        Assert.Equal(2, Result<int>(task));
    }

    [Fact]
    public void UnregisterStopsARepeatingRegisteredWait()
    {
        using var host = new SimulationHost(Start);
        var task = (Task<int>)host.Invoke(Method("UnregisterStopsTheWait"))!;
        Assert.Equal(1, Result<int>(task));
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
