using System.Collections.Concurrent;
using System.Reflection;
using System.Threading.Tasks;
using Orleans.Testing.Simulation.Runtime;

namespace Orleans.Testing.Simulation.Conformance.Tests;

/// <summary>
/// End-to-end conformance for the task and thread entry points which used to leave the coordinator: <c>Task.Start</c>
/// and <c>Task.RunSynchronously</c> (which queue to <see cref="TaskScheduler.Current"/>, the thread pool),
/// <c>Thread.UnsafeStart</c> (which starts an OS thread), and the timed and cancellable <c>Task.Wait</c>,
/// <c>WaitAll</c>, and <c>WaitAny</c> overloads (which block the physical thread, stalling the simulation that would
/// complete the tasks). Rewritten, each runs on the simulation's logical thread and its timeouts elapse in virtual time,
/// with the BCL's results and exceptions; rewritten without its rules, each escapes or stalls.
/// </summary>
public sealed class TaskEntryPointConformanceTests : IDisposable
{
    private const string ProbeType = "Conf.TaskEntryProbe";

    private static readonly DateTimeOffset Start = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string Source = """
        using System;
        using System.Collections.Generic;
        using System.Linq;
        using System.Threading;
        using System.Threading.Tasks;

        namespace Conf
        {
            public static class TaskEntryProbe
            {
                private static readonly AsyncLocal<string> Flow = new AsyncLocal<string>();

                // A task which a timer completes after `milliseconds`.
                private static Task After(int milliseconds, Action<TaskCompletionSource> complete)
                {
                    var source = new TaskCompletionSource();
                    Timer timer = null;
                    timer = new Timer(
                        _ =>
                        {
                            timer.Dispose();
                            complete(source);
                        },
                        null,
                        milliseconds,
                        Timeout.Infinite);
                    return source.Task;
                }

                private static Task Completes(int milliseconds) => After(milliseconds, source => source.TrySetResult());

                private static Task Faults(int milliseconds) =>
                    After(milliseconds, source => source.TrySetException(new InvalidOperationException("boom")));

                private static CancellationToken CancelledAfter(int milliseconds)
                {
                    var cancellation = new CancellationTokenSource();
                    cancellation.CancelAfter(milliseconds);
                    return cancellation.Token;
                }

                private static Task Pending() => new TaskCompletionSource().Task;

                private static Task Faulted() => Task.FromException(new InvalidOperationException("boom"));

                private static CancellationToken Cancelled() => new CancellationToken(true);

                // A token which concurrent work cancels from a completion callback. A wait observes the cancellation
                // whether the callback runs before the wait starts or while it blocks, so the outcome does not depend
                // on timing.
                private static CancellationToken CancelledByCallback()
                {
                    var cancellation = new CancellationTokenSource();
                    Task.Run(() => { }).ContinueWith(_ => cancellation.Cancel());
                    return cancellation.Token;
                }

                private static TimeSpan AboveIntMaxValue => TimeSpan.FromMilliseconds(int.MaxValue) + TimeSpan.FromMilliseconds(1);

                private static string Wait(string wait)
                {
                    switch (wait)
                    {
                        // Timer-driven waits, whose outcome depends on virtual time.
                        case "Wait(int) times out": return Completes(500).Wait(20).ToString();
                        case "Wait(int) completes": return Completes(100).Wait(5000).ToString();
                        case "Wait(int) completes before a short timeout": return Completes(50).Wait(1000).ToString();
                        case "Wait(int) observes a fault": return Faults(100).Wait(5000).ToString();
                        case "Wait(Timeout.Infinite) completes": return Completes(100).Wait(Timeout.Infinite).ToString();
                        case "Wait(TimeSpan) times out": return Completes(500).Wait(TimeSpan.FromMilliseconds(20)).ToString();
                        case "Wait(InfiniteTimeSpan) completes": return Completes(100).Wait(Timeout.InfiniteTimeSpan).ToString();
                        case "Wait(TimeSpan, CancellationToken) completes": return Completes(100).Wait(TimeSpan.FromSeconds(5), CancellationToken.None).ToString();
                        case "Wait(CancellationToken) is cancelled": Completes(500).Wait(CancelledAfter(20)); return "void";
                        case "Wait(Timeout.Infinite, CancellationToken) is cancelled": return Completes(500).Wait(Timeout.Infinite, CancelledAfter(20)).ToString();
                        case "Wait(int, CancellationToken) observes a cancelled task":
                        {
                            var cancellation = new CancellationTokenSource();
                            Task task = After(100, source => { cancellation.Cancel(); source.TrySetCanceled(cancellation.Token); });
                            return task.Wait(5000, cancellation.Token).ToString();
                        }
                        case "Wait(int) observes a cancelled task":
                            return After(100, source => source.TrySetCanceled(new CancellationToken(true))).Wait(5000).ToString();
                        case "WaitAll(Task[], int) times out": return Task.WaitAll(new[] { Completes(20), Completes(500) }, 100).ToString();
                        case "WaitAll(Task[], int) completes": return Task.WaitAll(new[] { Completes(50), Completes(100) }, 5000).ToString();
                        case "WaitAll(Task[], TimeSpan) observes a fault": return Task.WaitAll(new[] { Completes(50), Faults(100) }, TimeSpan.FromSeconds(5)).ToString();
                        case "WaitAll(Task[], InfiniteTimeSpan) completes": return Task.WaitAll(new[] { Completes(50), Completes(100) }, Timeout.InfiniteTimeSpan).ToString();
                        case "WaitAll(Task[], int, CancellationToken) is cancelled": return Task.WaitAll(new[] { Completes(20), Completes(500) }, 5000, CancelledAfter(100)).ToString();
                        case "WaitAll(Task[], CancellationToken) completes": Task.WaitAll(new[] { Completes(50), Completes(100) }, CancellationToken.None); return "void";
                        case "WaitAll(IEnumerable, CancellationToken) completes": Task.WaitAll(new List<Task> { Completes(50), Completes(100) }, CancellationToken.None); return "void";
                        case "WaitAll(ReadOnlySpan) completes": Task.WaitAll(Completes(50), Completes(100)); return "void";
                        case "WaitAny(Task[], int) times out": return Task.WaitAny(new[] { Completes(500), Completes(400) }, 20).ToString();
                        case "WaitAny(Task[], int) completes": return Task.WaitAny(new[] { Completes(500), Completes(50) }, 5000).ToString();
                        case "WaitAny(Task[], Timeout.Infinite) completes": return Task.WaitAny(new[] { Completes(500), Completes(50) }, Timeout.Infinite).ToString();
                        case "WaitAny(Task[], int) returns the lowest index of tasks completed at one instant":
                        {
                            var first = new TaskCompletionSource();
                            var second = new TaskCompletionSource();
                            After(50, _ => { second.TrySetResult(); first.TrySetResult(); });
                            return Task.WaitAny(new[] { Completes(500), first.Task, second.Task }, 5000).ToString();
                        }
                        case "WaitAny(Task[], TimeSpan) completes": return Task.WaitAny(new[] { Completes(500), Completes(50) }, TimeSpan.FromSeconds(5)).ToString();
                        case "WaitAny(Task[], CancellationToken) is cancelled": return Task.WaitAny(new[] { Completes(500), Completes(400) }, CancelledAfter(20)).ToString();
                        case "WaitAny(Task[], int, CancellationToken) completes": return Task.WaitAny(new[] { Completes(500), Completes(50) }, 5000, CancellationToken.None).ToString();

                        // Waits whose outcome does not depend on timing: their tasks never complete or have already
                        // completed, and their tokens are cancelled before the call or by concurrent work.
                        case "Wait(int) on a pending task times out": return Pending().Wait(20).ToString();
                        case "Wait(int) on a completed task": return Task.CompletedTask.Wait(5000).ToString();
                        case "Wait(int) on a faulted task": return Faulted().Wait(5000).ToString();
                        case "Wait(int) on a cancelled task": return Task.FromCanceled(new CancellationToken(true)).Wait(5000).ToString();
                        case "Wait(-2)": return Pending().Wait(-2).ToString();
                        case "Wait(Timeout.Infinite) on a task completed by concurrent work": return Task.Run(() => { }).Wait(Timeout.Infinite).ToString();
                        case "Wait(int, CancellationToken) on a task cancelled by that token":
                        {
                            var cancellation = new CancellationTokenSource();
                            cancellation.Cancel();
                            return Task.FromCanceled(cancellation.Token).Wait(5000, cancellation.Token).ToString();
                        }
                        case "Wait(int, CancellationToken) on a completed task ignores a cancelled token": return Task.CompletedTask.Wait(5000, Cancelled()).ToString();
                        case "Wait(int, CancellationToken) observes a cancelled token": return Pending().Wait(5000, Cancelled()).ToString();
                        case "Wait(0, CancellationToken) does not observe a cancelled token": return Pending().Wait(0, Cancelled()).ToString();
                        case "Wait(-2, CancellationToken) validates before observing the token": return Pending().Wait(-2, Cancelled()).ToString();
                        case "Wait(Timeout.Infinite, CancellationToken) is cancelled by a completion callback": return Pending().Wait(Timeout.Infinite, CancelledByCallback()).ToString();
                        case "Wait(CancellationToken) observes a cancelled token": Pending().Wait(Cancelled()); return "void";
                        case "Wait(CancellationToken) is cancelled by a completion callback": Pending().Wait(CancelledByCallback()); return "void";
                        case "Wait(TimeSpan) on a pending task times out": return Pending().Wait(TimeSpan.FromMilliseconds(20)).ToString();
                        case "Wait(InfiniteTimeSpan) on a completed task": return Task.CompletedTask.Wait(Timeout.InfiniteTimeSpan).ToString();
                        case "Wait(negative TimeSpan)": return Pending().Wait(TimeSpan.FromMilliseconds(-2)).ToString();
                        case "Wait(TimeSpan above int.MaxValue)": return Pending().Wait(AboveIntMaxValue).ToString();
                        case "Wait(TimeSpan, CancellationToken) observes a cancelled token first": return Task.CompletedTask.Wait(TimeSpan.FromSeconds(5), Cancelled()).ToString();
                        case "Wait(TimeSpan.Zero, CancellationToken) observes a cancelled token": return Pending().Wait(TimeSpan.Zero, Cancelled()).ToString();
                        case "Wait(negative TimeSpan, CancellationToken) validates before observing the token": return Pending().Wait(TimeSpan.FromMilliseconds(-2), Cancelled()).ToString();
                        case "Wait(InfiniteTimeSpan, CancellationToken) is cancelled by a completion callback": return Pending().Wait(Timeout.InfiniteTimeSpan, CancelledByCallback()).ToString();
                        case "WaitAll(Task[], int) on a pending task times out": return Task.WaitAll(new[] { Task.CompletedTask, Pending() }, 20).ToString();
                        case "WaitAll(Task[], int) on completed tasks": return Task.WaitAll(new[] { Task.CompletedTask, Task.FromResult(1) }, 5000).ToString();
                        case "WaitAll(Task[], int) on an empty array": return Task.WaitAll(new Task[0], 5000).ToString();
                        case "WaitAll(Task[], int) with a null array": return Task.WaitAll((Task[])null, 5000).ToString();
                        case "WaitAll(Task[], int) with a null element": return Task.WaitAll(new[] { Task.CompletedTask, null }, 5000).ToString();
                        case "WaitAll(Task[], -2)": return Task.WaitAll(new[] { Pending() }, -2).ToString();
                        case "WaitAll(Task[], int, CancellationToken) with a zero timeout observes a cancelled token": return Task.WaitAll(new[] { Task.CompletedTask }, 0, Cancelled()).ToString();
                        case "WaitAll(Task[], int, CancellationToken) observes a cancelled token before a null element": return Task.WaitAll(new[] { Task.CompletedTask, null }, 5000, Cancelled()).ToString();
                        case "WaitAll(Task[], Timeout.Infinite, CancellationToken) is cancelled by a completion callback": return Task.WaitAll(new[] { Task.CompletedTask, Pending() }, Timeout.Infinite, CancelledByCallback()).ToString();
                        case "WaitAll(Task[], CancellationToken) is cancelled by a completion callback": Task.WaitAll(new[] { Pending() }, CancelledByCallback()); return "void";
                        case "WaitAll(Task[], TimeSpan) on a faulted task": return Task.WaitAll(new[] { Task.CompletedTask, Faulted() }, TimeSpan.FromSeconds(5)).ToString();
                        case "WaitAll(Task[], InfiniteTimeSpan) on completed tasks": return Task.WaitAll(new[] { Task.CompletedTask }, Timeout.InfiniteTimeSpan).ToString();
                        case "WaitAll(Task[], negative TimeSpan)": return Task.WaitAll(new[] { Pending() }, TimeSpan.FromMilliseconds(-2)).ToString();
                        case "WaitAll(Task[], TimeSpan above int.MaxValue)": return Task.WaitAll(new[] { Pending() }, AboveIntMaxValue).ToString();
                        case "WaitAll(IEnumerable, CancellationToken) on an empty sequence": Task.WaitAll(new List<Task>(), CancellationToken.None); return "void";
                        case "WaitAll(IEnumerable, CancellationToken) with a null element": Task.WaitAll(new List<Task> { Task.CompletedTask, null }, CancellationToken.None); return "void";
                        case "WaitAll(IEnumerable, CancellationToken) is cancelled by a completion callback": Task.WaitAll(new List<Task> { Pending() }, CancelledByCallback()); return "void";
                        case "WaitAll(ReadOnlySpan) on an empty span": Task.WaitAll(ReadOnlySpan<Task>.Empty); return "void";
                        case "WaitAny(Task[], int) on pending tasks times out": return Task.WaitAny(new[] { Pending(), Pending() }, 20).ToString();
                        case "WaitAny(Task[], int) returns the lowest index of completed tasks": return Task.WaitAny(new[] { Pending(), Faulted(), Task.CompletedTask }, 5000).ToString();
                        case "WaitAny(Task[], int) on an empty array": return Task.WaitAny(new Task[0], 5000).ToString();
                        case "WaitAny(Task[], int) with a null array": return Task.WaitAny((Task[])null, 5000).ToString();
                        case "WaitAny(Task[], int) with a null element": return Task.WaitAny(new[] { Task.CompletedTask, null }, 5000).ToString();
                        case "WaitAny(Task[], Timeout.Infinite) on a completed task": return Task.WaitAny(new[] { Pending(), Task.CompletedTask }, Timeout.Infinite).ToString();
                        case "WaitAny(Task[], -2)": return Task.WaitAny(new[] { Pending() }, -2).ToString();
                        case "WaitAny(Task[], int, CancellationToken) with a zero timeout observes a cancelled token": return Task.WaitAny(new[] { Task.CompletedTask }, 0, Cancelled()).ToString();
                        case "WaitAny(Task[], CancellationToken) on an empty array": return Task.WaitAny(new Task[0], CancellationToken.None).ToString();
                        case "WaitAny(Task[], CancellationToken) is cancelled by a completion callback": return Task.WaitAny(new[] { Pending(), Pending() }, CancelledByCallback()).ToString();
                        case "WaitAny(Task[], InfiniteTimeSpan) on a completed task": return Task.WaitAny(new[] { Pending(), Task.CompletedTask }, Timeout.InfiniteTimeSpan).ToString();
                        case "WaitAny(Task[], negative TimeSpan)": return Task.WaitAny(new[] { Pending() }, TimeSpan.FromMilliseconds(-2)).ToString();
                        case "WaitAny(Task[], TimeSpan above int.MaxValue)": return Task.WaitAny(new[] { Pending() }, AboveIntMaxValue).ToString();
                        default: throw new ArgumentOutOfRangeException(nameof(wait), wait, null);
                    }
                }

                // The waits of the escape probes. Controlled, each completes in virtual time. Escaped, it blocks the
                // simulation's thread, so the timer which would complete its task never fires, until its timeout
                // elapses in real time or the caller cancels `escape`.
                private static string EscapingWait(string wait, CancellationToken escape)
                {
                    switch (wait)
                    {
                        case "Wait(int)": return Completes(50).Wait(200).ToString();
                        case "Wait(int, CancellationToken)": return Completes(50).Wait(200, CancellationToken.None).ToString();
                        case "Wait(CancellationToken)": Completes(50).Wait(escape); return "void";
                        case "Wait(TimeSpan)": return Completes(50).Wait(TimeSpan.FromMilliseconds(200)).ToString();
                        case "Wait(TimeSpan, CancellationToken)": return Completes(50).Wait(TimeSpan.FromMilliseconds(200), CancellationToken.None).ToString();
                        case "WaitAll(Task[], int)": return Task.WaitAll(new[] { Completes(50) }, 200).ToString();
                        case "WaitAll(Task[], int, CancellationToken)": return Task.WaitAll(new[] { Completes(50) }, 200, CancellationToken.None).ToString();
                        case "WaitAll(Task[], CancellationToken)": Task.WaitAll(new[] { Completes(50) }, escape); return "void";
                        case "WaitAll(Task[], TimeSpan)": return Task.WaitAll(new[] { Completes(50) }, TimeSpan.FromMilliseconds(200)).ToString();
                        case "WaitAll(IEnumerable, CancellationToken)": Task.WaitAll(new List<Task> { Completes(50) }, escape); return "void";
                        case "WaitAny(Task[], int)": return Task.WaitAny(new[] { Completes(50) }, 200).ToString();
                        case "WaitAny(Task[], int, CancellationToken)": return Task.WaitAny(new[] { Completes(50) }, 200, CancellationToken.None).ToString();
                        case "WaitAny(Task[], CancellationToken)": return Task.WaitAny(new[] { Completes(50) }, escape).ToString();
                        case "WaitAny(Task[], TimeSpan)": return Task.WaitAny(new[] { Completes(50) }, TimeSpan.FromMilliseconds(200)).ToString();
                        default: throw new ArgumentOutOfRangeException(nameof(wait), wait, null);
                    }
                }

                // A wait's result, or its exception with the parameter it names.
                private static string Describe(Func<string> wait)
                {
                    try
                    {
                        return wait();
                    }
                    catch (AggregateException exception)
                    {
                        return "AggregateException(" + string.Join(",", exception.InnerExceptions.Select(inner => inner.GetType().Name)) + ")";
                    }
                    catch (OperationCanceledException exception)
                    {
                        return exception.GetType().Name;
                    }
                    catch (ArgumentException exception)
                    {
                        return exception.GetType().Name + "(" + exception.ParamName + ")";
                    }
                }

                // Runs a wait and returns its result or exception, and the time it took.
                public static Task<(string Outcome, double Milliseconds)> Outcome(string wait)
                {
                    DateTimeOffset start = TimeProvider.System.GetUtcNow();
                    string outcome = Describe(() => Wait(wait));
                    return Task.FromResult((outcome, (TimeProvider.System.GetUtcNow() - start).TotalMilliseconds));
                }

                public static string Escape(string wait, CancellationToken escape) => Describe(() => EscapingWait(wait, escape));

                public static async Task<string> StartAndRunSynchronously(Func<bool> onSimulationThread)
                {
                    var log = new List<string>();
                    var started = new Task(() => log.Add("started:" + onSimulationThread()));
                    started.Start();
                    log.Add("after start:" + started.Status);
                    await started;

                    var inline = new Task(() => log.Add("inline:" + onSimulationThread()));
                    inline.RunSynchronously();
                    log.Add("after inline:" + inline.Status);

                    try
                    {
                        started.Start();
                    }
                    catch (InvalidOperationException)
                    {
                        log.Add("restart rejected");
                    }

                    var withScheduler = new Task<int>(() => 42);
                    withScheduler.Start(TaskScheduler.Default);
                    log.Add("default scheduler:" + await withScheduler);

                    var inlineWithScheduler = new Task<int>(() => 7);
                    inlineWithScheduler.RunSynchronously(TaskScheduler.Default);
                    log.Add("inline default scheduler:" + inlineWithScheduler.Result);
                    return string.Join("|", log);
                }

                public static void StartOnCustomScheduler() =>
                    new Task(() => { }).Start(new ConcurrentExclusiveSchedulerPair().ExclusiveScheduler);

                public static string StartThreads(Func<bool> onSimulationThread)
                {
                    var log = new List<string>();
                    Flow.Value = "flowed";
                    var safe = new Thread(() => log.Add("start:" + (Flow.Value ?? "none") + ":" + onSimulationThread()));
                    safe.Start();
                    safe.Join();
                    var unsafeThread = new Thread(() => log.Add("unsafe start:" + (Flow.Value ?? "none") + ":" + onSimulationThread()));
                    unsafeThread.UnsafeStart();
                    unsafeThread.Join();
                    var parameterized = new Thread(state => log.Add("unsafe start(" + state + "):" + (Flow.Value ?? "none") + ":" + onSimulationThread()));
                    parameterized.UnsafeStart("p");
                    parameterized.Join();
                    Flow.Value = null;
                    return string.Join("|", log);
                }

                public static void ReportStart(Action report) => new Task(report).Start();

                public static void ReportUnsafeStart(Action report) => new Thread(() => report()).UnsafeStart();
            }
        }
        """;

    private readonly RewriteFixture _fixture = new();
    private readonly Lazy<StagedProbe> _release;
    private readonly Lazy<StagedProbe> _debug;
    private readonly Lazy<UninstrumentedProbe> _bcl;

    public TaskEntryPointConformanceTests()
    {
        _release = new(() => _fixture.StageControlledTasks("Conf.TaskEntryRel", ProbeType, Source, optimize: true));
        _debug = new(() => _fixture.StageControlledTasks("Conf.TaskEntryDbg", ProbeType, Source, optimize: false));
        _bcl = new(() => _fixture.CompileUninstrumented("Conf.TaskEntryBcl", ProbeType, Source));
    }

    [Theory]
    [InlineData("Wait(int) times out", "False", 20)]
    [InlineData("Wait(int) completes", "True", 100)]
    [InlineData("Wait(int) completes before a short timeout", "True", 50)]
    [InlineData("Wait(int) observes a fault", "AggregateException(InvalidOperationException)", 100)]
    [InlineData("Wait(Timeout.Infinite) completes", "True", 100)]
    [InlineData("Wait(TimeSpan) times out", "False", 20)]
    [InlineData("Wait(InfiniteTimeSpan) completes", "True", 100)]
    [InlineData("Wait(TimeSpan, CancellationToken) completes", "True", 100)]
    [InlineData("Wait(CancellationToken) is cancelled", "OperationCanceledException", 20)]
    [InlineData("Wait(Timeout.Infinite, CancellationToken) is cancelled", "OperationCanceledException", 20)]
    [InlineData("Wait(int, CancellationToken) observes a cancelled task", "OperationCanceledException", 100)]
    [InlineData("Wait(int) observes a cancelled task", "AggregateException(TaskCanceledException)", 100)]
    [InlineData("WaitAll(Task[], int) times out", "False", 100)]
    [InlineData("WaitAll(Task[], int) completes", "True", 100)]
    [InlineData("WaitAll(Task[], TimeSpan) observes a fault", "AggregateException(InvalidOperationException)", 100)]
    [InlineData("WaitAll(Task[], InfiniteTimeSpan) completes", "True", 100)]
    [InlineData("WaitAll(Task[], int, CancellationToken) is cancelled", "OperationCanceledException", 100)]
    [InlineData("WaitAll(Task[], CancellationToken) completes", "void", 100)]
    [InlineData("WaitAll(IEnumerable, CancellationToken) completes", "void", 100)]
    [InlineData("WaitAll(ReadOnlySpan) completes", "void", 100)]
    [InlineData("WaitAny(Task[], int) times out", "-1", 20)]
    [InlineData("WaitAny(Task[], int) completes", "1", 50)]
    [InlineData("WaitAny(Task[], Timeout.Infinite) completes", "1", 50)]
    [InlineData("WaitAny(Task[], int) returns the lowest index of tasks completed at one instant", "1", 50)]
    [InlineData("WaitAny(Task[], TimeSpan) completes", "1", 50)]
    [InlineData("WaitAny(Task[], CancellationToken) is cancelled", "OperationCanceledException", 20)]
    [InlineData("WaitAny(Task[], int, CancellationToken) completes", "1", 50)]
    public void TimedWaitElapsesInVirtualTime(string wait, string expected, double milliseconds)
    {
        using var host = new SimulationHost(Start);

        var (outcome, elapsed) = Completed<(string, double)>(host.Invoke(Method("Outcome"), wait));

        Assert.Equal(expected, outcome);
        Assert.Equal(milliseconds, elapsed);
    }

    // The BCL runs in real time, so the oracle's inputs are ones whose outcome does not depend on timing: tasks which
    // never complete or have already completed, and tokens cancelled before the call or by concurrent work.
    [Theory]
    [InlineData("Wait(int) on a pending task times out", "False")]
    [InlineData("Wait(int) on a completed task", "True")]
    [InlineData("Wait(int) on a faulted task", "AggregateException(InvalidOperationException)")]
    [InlineData("Wait(int) on a cancelled task", "AggregateException(TaskCanceledException)")]
    [InlineData("Wait(-2)", "ArgumentOutOfRangeException(millisecondsTimeout)")]
    [InlineData("Wait(Timeout.Infinite) on a task completed by concurrent work", "True")]
    [InlineData("Wait(int, CancellationToken) on a task cancelled by that token", "OperationCanceledException")]
    [InlineData("Wait(int, CancellationToken) on a completed task ignores a cancelled token", "True")]
    [InlineData("Wait(int, CancellationToken) observes a cancelled token", "OperationCanceledException")]
    [InlineData("Wait(0, CancellationToken) does not observe a cancelled token", "False")]
    [InlineData("Wait(-2, CancellationToken) validates before observing the token", "ArgumentOutOfRangeException(millisecondsTimeout)")]
    [InlineData("Wait(Timeout.Infinite, CancellationToken) is cancelled by a completion callback", "OperationCanceledException")]
    [InlineData("Wait(CancellationToken) observes a cancelled token", "OperationCanceledException")]
    [InlineData("Wait(CancellationToken) is cancelled by a completion callback", "OperationCanceledException")]
    [InlineData("Wait(TimeSpan) on a pending task times out", "False")]
    [InlineData("Wait(InfiniteTimeSpan) on a completed task", "True")]
    [InlineData("Wait(negative TimeSpan)", "ArgumentOutOfRangeException(timeout)")]
    [InlineData("Wait(TimeSpan above int.MaxValue)", "ArgumentOutOfRangeException(timeout)")]
    [InlineData("Wait(TimeSpan, CancellationToken) observes a cancelled token first", "OperationCanceledException")]
    [InlineData("Wait(TimeSpan.Zero, CancellationToken) observes a cancelled token", "OperationCanceledException")]
    [InlineData("Wait(negative TimeSpan, CancellationToken) validates before observing the token", "ArgumentOutOfRangeException(timeout)")]
    [InlineData("Wait(InfiniteTimeSpan, CancellationToken) is cancelled by a completion callback", "OperationCanceledException")]
    [InlineData("WaitAll(Task[], int) on a pending task times out", "False")]
    [InlineData("WaitAll(Task[], int) on completed tasks", "True")]
    [InlineData("WaitAll(Task[], int) on an empty array", "True")]
    [InlineData("WaitAll(Task[], int) with a null array", "ArgumentNullException(tasks)")]
    [InlineData("WaitAll(Task[], int) with a null element", "ArgumentException(tasks)")]
    [InlineData("WaitAll(Task[], -2)", "ArgumentOutOfRangeException(millisecondsTimeout)")]
    [InlineData("WaitAll(Task[], int, CancellationToken) with a zero timeout observes a cancelled token", "OperationCanceledException")]
    [InlineData("WaitAll(Task[], int, CancellationToken) observes a cancelled token before a null element", "OperationCanceledException")]
    [InlineData("WaitAll(Task[], Timeout.Infinite, CancellationToken) is cancelled by a completion callback", "OperationCanceledException")]
    [InlineData("WaitAll(Task[], CancellationToken) is cancelled by a completion callback", "OperationCanceledException")]
    [InlineData("WaitAll(Task[], TimeSpan) on a faulted task", "AggregateException(InvalidOperationException)")]
    [InlineData("WaitAll(Task[], InfiniteTimeSpan) on completed tasks", "True")]
    [InlineData("WaitAll(Task[], negative TimeSpan)", "ArgumentOutOfRangeException(timeout)")]
    [InlineData("WaitAll(Task[], TimeSpan above int.MaxValue)", "ArgumentOutOfRangeException(timeout)")]
    [InlineData("WaitAll(IEnumerable, CancellationToken) on an empty sequence", "void")]
    [InlineData("WaitAll(IEnumerable, CancellationToken) with a null element", "ArgumentException(tasks)")]
    [InlineData("WaitAll(IEnumerable, CancellationToken) is cancelled by a completion callback", "OperationCanceledException")]
    [InlineData("WaitAll(ReadOnlySpan) on an empty span", "void")]
    [InlineData("WaitAny(Task[], int) on pending tasks times out", "-1")]
    [InlineData("WaitAny(Task[], int) returns the lowest index of completed tasks", "1")]
    [InlineData("WaitAny(Task[], int) on an empty array", "-1")]
    [InlineData("WaitAny(Task[], int) with a null array", "ArgumentNullException(tasks)")]
    [InlineData("WaitAny(Task[], int) with a null element", "ArgumentException(tasks)")]
    [InlineData("WaitAny(Task[], Timeout.Infinite) on a completed task", "1")]
    [InlineData("WaitAny(Task[], -2)", "ArgumentOutOfRangeException(millisecondsTimeout)")]
    [InlineData("WaitAny(Task[], int, CancellationToken) with a zero timeout observes a cancelled token", "OperationCanceledException")]
    [InlineData("WaitAny(Task[], CancellationToken) on an empty array", "-1")]
    [InlineData("WaitAny(Task[], CancellationToken) is cancelled by a completion callback", "OperationCanceledException")]
    [InlineData("WaitAny(Task[], InfiniteTimeSpan) on a completed task", "1")]
    [InlineData("WaitAny(Task[], negative TimeSpan)", "ArgumentOutOfRangeException(timeout)")]
    [InlineData("WaitAny(Task[], TimeSpan above int.MaxValue)", "ArgumentOutOfRangeException(timeout)")]
    public async Task WaitReturnsAndThrowsWhatTheBclDoes(string wait, string expected)
    {
        using var host = new SimulationHost(Start);

        var (outcome, _) = Completed<(string, double)>(host.Invoke(Method("Outcome"), wait));
        var (bclOutcome, _) = await ((Task<(string, double)>)_bcl.Value.Method("Outcome").Invoke(null, [wait])!)
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(expected, bclOutcome);
        Assert.Equal(bclOutcome, outcome);
    }

    [Theory]
    [InlineData("Wait(int)", "clockwork.tasks.wait.milliseconds", "True", "False")]
    [InlineData("Wait(int, CancellationToken)", "clockwork.tasks.wait.milliseconds.cancellationtoken", "True", "False")]
    [InlineData("Wait(CancellationToken)", "clockwork.tasks.wait.cancellationtoken", "void", "OperationCanceledException")]
    [InlineData("Wait(TimeSpan)", "clockwork.tasks.wait.timespan", "True", "False")]
    [InlineData("Wait(TimeSpan, CancellationToken)", "clockwork.tasks.wait.timespan.cancellationtoken", "True", "False")]
    [InlineData("WaitAll(Task[], int)", "clockwork.tasks.waitall.array.milliseconds", "True", "False")]
    [InlineData("WaitAll(Task[], int, CancellationToken)", "clockwork.tasks.waitall.array.milliseconds.cancellationtoken", "True", "False")]
    [InlineData("WaitAll(Task[], CancellationToken)", "clockwork.tasks.waitall.array.cancellationtoken", "void", "OperationCanceledException")]
    [InlineData("WaitAll(Task[], TimeSpan)", "clockwork.tasks.waitall.array.timespan", "True", "False")]
    [InlineData("WaitAll(IEnumerable, CancellationToken)", "clockwork.tasks.waitall.enumerable.cancellationtoken", "void", "OperationCanceledException")]
    [InlineData("WaitAny(Task[], int)", "clockwork.tasks.waitany.array.milliseconds", "0", "-1")]
    [InlineData("WaitAny(Task[], int, CancellationToken)", "clockwork.tasks.waitany.array.milliseconds.cancellationtoken", "0", "-1")]
    [InlineData("WaitAny(Task[], CancellationToken)", "clockwork.tasks.waitany.array.cancellationtoken", "0", "OperationCanceledException")]
    [InlineData("WaitAny(Task[], TimeSpan)", "clockwork.tasks.waitany.array.timespan", "0", "-1")]
    public void WithoutItsRuleATimedOrCancellableWaitStallsTheSimulation(string wait, string ruleId, string controlled, string stalled)
    {
        StagedProbe stalling = _fixture.StageControlledTasksWithout(
            "Conf.TaskEntryStall." + ruleId,
            ProbeType,
            Source,
            rule => rule.Id == ruleId);

        Assert.Equal(controlled, RunEscapeProbe(Method("Escape"), wait, stallTimeout: null));
        Assert.Equal(stalled, RunEscapeProbe(stalling.Method("Escape"), wait, StallTimeout));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StartedAndInlineTasksRunOnTheLogicalThread(bool optimize)
    {
        using var host = new SimulationHost(Start);

        string log = Completed<string>(host.Invoke(Method("StartAndRunSynchronously", optimize), OnSimulationThread(host)));

        Assert.Equal(
            "after start:WaitingToRun|started:True|inline:True|after inline:RanToCompletion|restart rejected|" +
            "default scheduler:42|inline default scheduler:7",
            log);
    }

    [Fact]
    public void StartOnACustomSchedulerIsRejected()
    {
        using var host = new SimulationHost(Start);

        var exception = Assert.IsType<SimulationApiException>(Record.Exception(() => host.Invoke(Method("StartOnCustomScheduler"))));

        Assert.Equal("System.Threading.Tasks.Task.Start", exception.ApiName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UnsafeStartRunsTheThreadOnTheLogicalThreadWithoutTheCallersContext(bool optimize)
    {
        using var host = new SimulationHost(Start);

        var log = (string)host.Invoke(Method("StartThreads", optimize), OnSimulationThread(host))!;
        var bcl = (string)_bcl.Value.Method("StartThreads").Invoke(null, [new Func<bool>(() => true)])!;

        Assert.Equal("start:flowed:True|unsafe start:none:True|unsafe start(p):none:True", log);
        Assert.Equal(bcl, log);
    }

    [Theory]
    [InlineData("ReportStart", "clockwork.tasks.start")]
    [InlineData("ReportUnsafeStart", "clockwork.thread.unsafestart")]
    public void WithoutTheirRulesStartedWorkEscapesToAnotherThread(string probe, string ruleId)
    {
        StagedProbe escaping = _fixture.StageControlledTasksWithout(
            "Conf.TaskEntryEscape." + probe,
            ProbeType,
            Source,
            rule => rule.Id == ruleId);
        // The guard is off: the escape this test pins queues the work to the thread pool.
        using var host = new SimulationHost(Start, allowThreadPoolEscapes: true);
        var observed = new ConcurrentQueue<bool>();

        host.Invoke(escaping.Method(probe), new Action(() => observed.Enqueue(host.Scheduler.IsSimulationThread)));

        Assert.True(SpinWait.SpinUntil(() => !observed.IsEmpty, TimeSpan.FromSeconds(30)));
        Assert.False(Assert.Single(observed));
    }

    // How long an escape probe runs before the test treats it as stalled and cancels its escape token. An escaped wait
    // has the same outcome whether the cancellation arrives before or during the wait.
    private static readonly TimeSpan StallTimeout = TimeSpan.FromMilliseconds(200);

    // Runs an escape probe on a thread of its own, because an escaped wait blocks the simulation's thread. Once the run
    // has stalled for `stallTimeout`, the test cancels the probe's escape token, which is all that can release an
    // escaped wait without a timeout. The run is joined, with a bound, before the host is disposed.
    private static string RunEscapeProbe(MethodInfo probe, string wait, TimeSpan? stallTimeout)
    {
        using var escape = new CancellationTokenSource();
        using var host = new SimulationHost(Start);
        Task<object?> run = Task.Factory.StartNew(
            () => host.Invoke(probe, wait, escape.Token),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        if (stallTimeout is { } timeout && !run.Wait(timeout, TestContext.Current.CancellationToken))
        {
            escape.Cancel();
        }

        Assert.True(run.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken), "The probe's wait did not return.");
        return Assert.IsType<string>(run.Result);
    }

    private static Func<bool> OnSimulationThread(SimulationHost host) => () => host.Scheduler.IsSimulationThread;

    private static T Completed<T>(object? task)
    {
        var typed = Assert.IsAssignableFrom<Task<T>>(task);
        Assert.Equal(TaskStatus.RanToCompletion, typed.Status);
        return typed.GetAwaiter().GetResult();
    }

    private MethodInfo Method(string name, bool optimize = true) => (optimize ? _release : _debug).Value.Method(name);

    public void Dispose() => _fixture.Dispose();
}
