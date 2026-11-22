using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks.Sources;
using Orleans.Testing.Simulation.Shims.System.Threading.Tasks.Sources;
using Orleans.Testing.Simulation.Runtime;
using Orleans.Testing.Simulation.Runtime.Scheduling;
using Orleans.Testing.Simulation.Runtime.Tasks;

namespace Orleans.Testing.Simulation.Tests;

// Completions which Clockwork performs for simulation code run as on a thread-pool thread: a bare continuation of the
// task they complete (a framework await with ConfigureAwait(false)) runs inline, on the simulation's logical thread, as
// the BCL runs it on a thread-pool thread, never on the real thread pool. Each continuation records separately whether it
// ran inside the completing call (inline) and whether it ran on the logical thread (no escape).
public sealed class SimulationCompletionTests
{
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(30);

    /// <summary>Where simulation code completes a task.</summary>
    public enum CompletionSite
    {
        /// <summary>Before the first await of <c>RunToCompletion</c>'s root task, on the cluster's scheduler and context.</summary>
        Root,

        /// <summary>In the cluster lane's work, with the lane's context current.</summary>
        Lane,
    }

    /// <summary>How a <c>SemaphoreSlim.WaitAsync</c> waiter is resolved.</summary>
    public enum WaiterOutcome
    {
        /// <summary>The controlled <c>Release</c>.</summary>
        Release,

        /// <summary>The BCL's <c>CancellationTokenSource.Cancel</c>, called by code which is not rewritten.</summary>
        UninstrumentedCancel,

        /// <summary>The controlled <c>Dispose</c>, which faults the waiter.</summary>
        Dispose,
    }

    /// <summary>A completion which Clockwork makes, other than the <c>TaskCompletionSource</c> shims.</summary>
    public enum CompletionRoute
    {
        /// <summary>The controlled <c>ManualResetValueTaskSourceCore.SetResult</c>, observed through <c>AsTask</c>.</summary>
        ValueTaskSourceCoreSetResult,

        /// <summary>The controlled <c>ManualResetValueTaskSourceCore.SetException</c>, observed through <c>AsTask</c>.</summary>
        ValueTaskSourceCoreSetException,

        /// <summary><c>PeriodicTimer.Dispose</c>, which completes a pending <c>WaitForNextTickAsync</c>.</summary>
        PeriodicTimerDispose,

        /// <summary><see cref="SimulationGate.Open"/>, which releases a waiter in the gate's lane.</summary>
        GateOpen,

        /// <summary>A gate waiter which the BCL's <c>Cancel</c>, called by code which is not rewritten, cancels.</summary>
        GateWaitCanceled,

        /// <summary><see cref="SimulationLatch.Signal"/>, which releases a waiter in the latch's lane.</summary>
        LatchSignal,

        /// <summary>The last arrival at a <see cref="SimulationBarrier"/>, which releases the others in its lane.</summary>
        BarrierArrive,
    }

    /// <summary>A completion which the BCL rejects.</summary>
    public enum CompletionError
    {
        /// <summary><c>SetResult</c> on a completed source.</summary>
        SetResultTwice,

        /// <summary><c>Cancel(true)</c> with a callback which throws.</summary>
        CancelWithAThrowingCallback,

        /// <summary><c>SetException</c> on a completed source.</summary>
        SetExceptionOnACompletedSource,
    }

    /// <summary>A completion made where too little stack remains for the BCL to inline its continuations.</summary>
    public enum DeepCompletion
    {
        /// <summary>The controlled <c>TaskCompletionSource.SetResult</c>.</summary>
        SetResult,

        /// <summary>The controlled <c>CancellationTokenSource.Cancel</c> of a source whose <c>CancelAfter</c> is armed.</summary>
        Cancel,

        /// <summary>The release of a primitive's waiter which has a deadline.</summary>
        Waiter,
    }

    public static TheoryData<string, CompletionSite> CompletionMembers
    {
        get
        {
            var data = new TheoryData<string, CompletionSite>();
            foreach (MethodInfo member in CompletionShims())
            {
                foreach (CompletionSite site in Enum.GetValues<CompletionSite>())
                {
                    data.Add(member.ToString()!, site);
                }
            }

            return data;
        }
    }

    // Every completion member of the TaskCompletionSource shims, generic and not.
    [Theory]
    [MemberData(nameof(CompletionMembers))]
    public async Task EveryCompletionShimRunsBareContinuationsInline(string member, CompletionSite site)
    {
        MethodInfo shim = CompletionShims().Single(method => method.ToString() == member);
        await using var cluster = new SimulationCluster(seed: 1, DateTimeOffset.UnixEpoch);
        var probe = new ContinuationProbe(() => cluster.Scheduler.IsSimulationThread);

        await RunIn(cluster, site, () =>
        {
            object?[] arguments = CompletionArguments(shim);
            probe.Watch(SourceTask(arguments[0]!));
            probe.Complete(() => shim.Invoke(null, arguments));
        });

        Assert.Equal(new Placement(Inline: true, OnLogicalThread: true), probe.Observed);
        Assert.Equal(new Placement(Inline: true, OnLogicalThread: true), await OnAThreadPoolThread(onLogicalThread =>
        {
            var bcl = new ContinuationProbe(onLogicalThread);
            object?[] arguments = CompletionArguments(shim);
            MethodInfo member = arguments[0]!.GetType().GetMethod(
                shim.Name,
                [.. shim.GetParameters().Skip(1).Select(parameter => parameter.ParameterType)])!;
            bcl.Watch(SourceTask(arguments[0]!));
            bcl.Complete(() => member.Invoke(arguments[0], arguments[1..]));
            return bcl.Observed;
        }));
    }

    // Every other route by which Clockwork completes a task runs a bare continuation on the logical thread: inline in the
    // completing call where Clockwork completes it there, and otherwise in the lane's work which releases the waiter.
    [Theory]
    [InlineData(CompletionRoute.ValueTaskSourceCoreSetResult, CompletionSite.Root)]
    [InlineData(CompletionRoute.ValueTaskSourceCoreSetResult, CompletionSite.Lane)]
    [InlineData(CompletionRoute.ValueTaskSourceCoreSetException, CompletionSite.Root)]
    [InlineData(CompletionRoute.ValueTaskSourceCoreSetException, CompletionSite.Lane)]
    [InlineData(CompletionRoute.PeriodicTimerDispose, CompletionSite.Root)]
    [InlineData(CompletionRoute.PeriodicTimerDispose, CompletionSite.Lane)]
    [InlineData(CompletionRoute.GateOpen, CompletionSite.Root)]
    [InlineData(CompletionRoute.GateOpen, CompletionSite.Lane)]
    [InlineData(CompletionRoute.GateWaitCanceled, CompletionSite.Root)]
    [InlineData(CompletionRoute.GateWaitCanceled, CompletionSite.Lane)]
    [InlineData(CompletionRoute.LatchSignal, CompletionSite.Root)]
    [InlineData(CompletionRoute.LatchSignal, CompletionSite.Lane)]
    [InlineData(CompletionRoute.BarrierArrive, CompletionSite.Root)]
    [InlineData(CompletionRoute.BarrierArrive, CompletionSite.Lane)]
    public async Task EveryCompletionRouteRunsBareContinuationsOnTheLogicalThread(CompletionRoute route, CompletionSite site)
    {
        await using var cluster = new SimulationCluster(seed: 1, DateTimeOffset.UnixEpoch);
        var probe = new ContinuationProbe(() => cluster.Scheduler.IsSimulationThread);
        SimulationSchedulerLane lane = cluster.SchedulerLane;

        await RunIn(cluster, site, () =>
        {
            switch (route)
            {
                case CompletionRoute.ValueTaskSourceCoreSetResult:
                case CompletionRoute.ValueTaskSourceCoreSetException:
                    var source = new ValueTaskSource();
                    probe.Watch(source.Task.AsTask());
                    probe.Complete(() =>
                    {
                        if (route == CompletionRoute.ValueTaskSourceCoreSetResult)
                        {
                            ControlledManualResetValueTaskSourceCore.SetResult(ref source.Core, 1);
                        }
                        else
                        {
                            ControlledManualResetValueTaskSourceCore.SetException(ref source.Core, new InvalidOperationException());
                        }
                    });
                    break;
                case CompletionRoute.PeriodicTimerDispose:
                    var timer = new ControlledPeriodicTimer(TimeSpan.FromHours(1));
                    probe.Watch(timer.WaitForNextTickAsync().AsTask());
                    probe.Complete(timer.Dispose);
                    break;
                case CompletionRoute.GateOpen:
                    var gate = new SimulationGate(lane);
                    probe.Watch(gate.WaitAsync());
                    gate.Open();
                    break;
                case CompletionRoute.GateWaitCanceled:
                    var canceled = new SimulationGate(lane);
                    var cancellation = new CancellationTokenSource();
                    probe.Watch(canceled.WaitAsync(cancellation.Token));
                    probe.Complete(cancellation.Cancel);
                    break;
                case CompletionRoute.LatchSignal:
                    var latch = new SimulationLatch(lane, initialCount: 1);
                    probe.Watch(latch.WaitAsync());
                    latch.Signal();
                    break;
                case CompletionRoute.BarrierArrive:
                    var barrier = new SimulationBarrier(lane, participantCount: 2);
                    probe.Watch(barrier.ArriveAndWaitAsync());
                    _ = barrier.ArriveAndWaitAsync();
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(route), route, null);
            }
        });

        bool completesInTheCall = route is CompletionRoute.ValueTaskSourceCoreSetResult or CompletionRoute.ValueTaskSourceCoreSetException
            or CompletionRoute.PeriodicTimerDispose or CompletionRoute.GateWaitCanceled;
        Placement observed = Assert.NotNull(probe.Observed);
        Assert.True(observed.OnLogicalThread);
        if (completesInTheCall)
        {
            Assert.True(observed.Inline);
        }
    }

    // The BCL's own ManualResetValueTaskSourceCore, on a thread-pool thread, runs AsTask's bare continuations inline.
    [Fact]
    public async Task OnAThreadPoolThreadTheBclRunsAValueTaskSourceCoresContinuationsInline() =>
        Assert.Equal(new Placement(Inline: true, OnLogicalThread: true), await OnAThreadPoolThread(onLogicalThread =>
        {
            var bcl = new ContinuationProbe(onLogicalThread);
            var source = new ValueTaskSource();
            bcl.Watch(source.Task.AsTask());
            bcl.Complete(() => source.Core.SetResult(1));
            return bcl.Observed;
        }));

    // CancellationTokenSource runs a callback registered with useSynchronizationContext through the context's Send, which
    // runs it inline on the lane which owns it, inside the completion as outside it.
    [Theory]
    [InlineData(CompletionSite.Root)]
    [InlineData(CompletionSite.Lane)]
    public async Task CancelRunsACallbackRegisteredOnTheContextInline(CompletionSite site)
    {
        await using var cluster = new SimulationCluster(seed: 1, DateTimeOffset.UnixEpoch);
        var controlled = new ContinuationProbe(() => cluster.Scheduler.IsSimulationThread);
        var bcl = new ContinuationProbe(() => cluster.Scheduler.IsSimulationThread);

        await RunIn(cluster, site, () =>
        {
            using var controlledSource = new CancellationTokenSource();
            using var bclSource = new CancellationTokenSource();
            using CancellationTokenRegistration controlledRegistration = controlled.WatchOnTheContext(controlledSource.Token);
            using CancellationTokenRegistration bclRegistration = bcl.WatchOnTheContext(bclSource.Token);
            controlled.Complete(() => ControlledCancellationTokenSource.Cancel(controlledSource));
            bcl.Complete(bclSource.Cancel);
        });

        Assert.Equal(new Placement(Inline: true, OnLogicalThread: true), controlled.Observed);
        Assert.Equal(bcl.Observed, controlled.Observed);
    }

    // Inside a completion the thread still executes the lane's work, but with no context current; installing the lane's
    // context there makes it current until the scope ends.
    [Fact]
    public async Task InstallingTheLanesContextInsideACompletionMakesItCurrent()
    {
        await using var cluster = new SimulationCluster(seed: 1, DateTimeOffset.UnixEpoch);
        SimulationSynchronizationContext context = cluster.SchedulerLane.SynchronizationContext;
        var observed = new List<SynchronizationContext?>();
        cluster.SchedulerLane.Enqueue(() => SimulationCompletion.Run(
            observed,
            completion =>
            {
                completion.Add(SynchronizationContext.Current);
                using (context.Install())
                {
                    completion.Add(SynchronizationContext.Current);
                }

                completion.Add(SynchronizationContext.Current);
            },
            "test"));

        await Drive(() => cluster.RunUntilIdle(TestContext.Current.CancellationToken));

        Assert.Equal([null, context, null], observed);
    }

    // A Send to another lane's context, from simulation work, cannot pump that lane: the pump fails the call and the run
    // at once rather than retry.
    [Fact]
    public async Task SendToAnotherLanesContextFailsTheRun()
    {
        await using var cluster = new SimulationCluster(seed: 1, DateTimeOffset.UnixEpoch);
        SimulationNode sender = cluster.AddNode("sender");
        SimulationNode receiver = cluster.AddNode("receiver");
        Exception? thrown = null;
        bool sent = false;
        sender.Context.SchedulerLane.Enqueue(() =>
        {
            try
            {
                receiver.Context.SynchronizationContext.Send(_ => sent = true, null);
            }
            catch (SimulationSchedulerException exception)
            {
                thrown = exception;
            }
        });

        await Assert.ThrowsAnyAsync<SimulationSchedulerException>(() => Drive(() => cluster.RunUntilIdle(TestContext.Current.CancellationToken)));
        Assert.NotNull(thrown);
        Assert.False(sent);
    }

    // The BCL's SemaphoreSlim completes a WaitAsync task with RunContinuationsAsynchronously, so no continuation runs
    // inside the call which resolves it; Clockwork delivers it in a controlled operation of its own, on the logical thread.
    [Theory]
    [InlineData(WaiterOutcome.Release, CompletionSite.Root)]
    [InlineData(WaiterOutcome.Release, CompletionSite.Lane)]
    [InlineData(WaiterOutcome.UninstrumentedCancel, CompletionSite.Root)]
    [InlineData(WaiterOutcome.UninstrumentedCancel, CompletionSite.Lane)]
    [InlineData(WaiterOutcome.Dispose, CompletionSite.Root)]
    [InlineData(WaiterOutcome.Dispose, CompletionSite.Lane)]
    public async Task AWaitAsyncWaiterContinuesAsynchronouslyOnTheLogicalThread(WaiterOutcome outcome, CompletionSite site)
    {
        await using var cluster = new SimulationCluster(seed: 1, DateTimeOffset.UnixEpoch);
        var probe = new ContinuationProbe(() => cluster.Scheduler.IsSimulationThread);

        await RunIn(cluster, site, () =>
        {
            SemaphoreSlim semaphore = ControlledSemaphoreSlim.Create(0);
            var source = new CancellationTokenSource();
            probe.Watch(ControlledSemaphoreSlim.WaitAsync(semaphore, source.Token));
            probe.Complete(() => Resolve(outcome, semaphore, source, controlled: true));
        });

        Assert.Equal(new Placement(Inline: false, OnLogicalThread: true), probe.Observed);
        if (outcome != WaiterOutcome.Dispose)
        {
            // The BCL's Dispose leaves a WaitAsync waiter pending; otherwise its continuation runs later, on another thread.
            Placement? bcl = await OnAThreadPoolThread(onLogicalThread =>
            {
                var bclProbe = new ContinuationProbe(onLogicalThread);
                var semaphore = new SemaphoreSlim(0);
                var source = new CancellationTokenSource();
                bclProbe.Watch(semaphore.WaitAsync(source.Token));
                bclProbe.Complete(() => Resolve(outcome, semaphore, source, controlled: false));
                SpinWait.SpinUntil(() => bclProbe.Observed is not null, Watchdog);
                return bclProbe.Observed;
            });
            Assert.False(Assert.NotNull(bcl).Inline);
        }
    }

    // On the path which hides the root's scheduler, a rejected completion throws what the BCL throws, unwrapped.
    [Theory]
    [InlineData(CompletionError.SetResultTwice)]
    [InlineData(CompletionError.CancelWithAThrowingCallback)]
    [InlineData(CompletionError.SetExceptionOnACompletedSource)]
    public async Task ARejectedCompletionInTheRootThrowsWhatTheBclThrows(CompletionError error)
    {
        await using var cluster = new SimulationCluster(seed: 1, DateTimeOffset.UnixEpoch);
        Rejection? controlled = null;

        await RunIn(cluster, CompletionSite.Root, () =>
        {
            Assert.NotEqual(TaskScheduler.Default, TaskScheduler.Current);
            controlled = Reject(error, controlled: true);
        });
        Rejection bcl = await Task.Run(() => Reject(error, controlled: false), TestContext.Current.CancellationToken);

        Assert.NotNull(controlled);
        Assert.Equal(bcl.Thrown.GetType(), controlled.Thrown.GetType());
        Assert.Equal(bcl.Thrown.Message, controlled.Thrown.Message);
        Assert.Equal(ReferenceEquals(bcl.Thrown, bcl.Raised), ReferenceEquals(controlled.Thrown, controlled.Raised));
    }

    // Where less than the completion's headroom remains beyond the BCL's margin, though the BCL's own check still passes,
    // a completion fails the run before it does anything (the token's timer and the waiter's deadline stay armed), even
    // when simulation code catches the exception.
    [Theory]
    [InlineData(DeepCompletion.SetResult, CompletionSite.Root)]
    [InlineData(DeepCompletion.SetResult, CompletionSite.Lane)]
    [InlineData(DeepCompletion.Cancel, CompletionSite.Root)]
    [InlineData(DeepCompletion.Cancel, CompletionSite.Lane)]
    [InlineData(DeepCompletion.Waiter, CompletionSite.Root)]
    [InlineData(DeepCompletion.Waiter, CompletionSite.Lane)]
    public async Task ACompletionOnAnExhaustedStackFailsTheRunWithoutSideEffects(DeepCompletion completion, CompletionSite site)
    {
        await using var cluster = new SimulationCluster(seed: 1, DateTimeOffset.UnixEpoch);
        var source = new TaskCompletionSource();
        using var cancellation = new CancellationTokenSource();
        var waiter = new SimulationWaiter();
        SimulationApiException? thrown = null;
        int timersBefore = 0;
        int timersAfter = 0;
        Action body = () =>
        {
            Action complete;
            switch (completion)
            {
                case DeepCompletion.SetResult:
                    complete = () => ControlledTaskCompletionSource.SetResult(source);
                    break;
                case DeepCompletion.Cancel:
                    ControlledCancellationTokenSource.CancelAfter(cancellation, TimeSpan.FromHours(1));
                    complete = () => ControlledCancellationTokenSource.Cancel(cancellation);
                    break;
                case DeepCompletion.Waiter:
                    SimulationWaiter.AttachDeadline(waiter, TimeSpan.FromHours(1), "test", static _ => { });
                    Assert.True(waiter.TryClaim(out SimulationWaiter.Claim claim));
                    complete = () => claim.Complete(true);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(completion), completion, null);
            }

            timersBefore = cluster.Scheduler.CapturePendingTimers().Count;
            thrown = Assert.Throws<SimulationApiException>(() => WithinTheCompletionHeadroom(complete));
            timersAfter = cluster.Scheduler.CapturePendingTimers().Count;
        };

        await Assert.ThrowsAsync<SimulationApiException>(() => RunIn(cluster, site, body));
        Assert.NotNull(thrown);
        Assert.False(source.Task.IsCompleted);
        Assert.False(cancellation.IsCancellationRequested);
        Assert.False(waiter.Task.IsCompleted);
        Assert.Equal(timersBefore, timersAfter);
        Assert.Equal(completion is DeepCompletion.SetResult ? 0 : 1, timersBefore);
    }

    private static IEnumerable<MethodInfo> CompletionShims() =>
        typeof(ControlledTaskCompletionSource)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.Name.StartsWith("Set", StringComparison.Ordinal) || method.Name.StartsWith("TrySet", StringComparison.Ordinal))
            .Select(method => method.IsGenericMethodDefinition ? method.MakeGenericMethod(typeof(int)) : method)
            .OrderBy(method => method.ToString(), StringComparer.Ordinal);

    private static object?[] CompletionArguments(MethodInfo shim) =>
        [.. shim.GetParameters().Select(object? (parameter) => parameter.ParameterType switch
        {
            Type type when type == typeof(TaskCompletionSource) => new TaskCompletionSource(),
            Type type when type == typeof(TaskCompletionSource<int>) => new TaskCompletionSource<int>(),
            Type type when type == typeof(int) => 1,
            Type type when type == typeof(Exception) => new InvalidOperationException("completion"),
            Type type when type == typeof(IEnumerable<Exception>) => new[] { new InvalidOperationException("completion") },
            Type type when type == typeof(CancellationToken) => new CancellationToken(canceled: true),
            Type type when type == typeof(Task) => Task.CompletedTask,
            Type type when type == typeof(Task<int>) => Task.FromResult(1),
            Type type => throw new InvalidOperationException($"No argument for {type} in {shim}."),
        })];

    private static Task SourceTask(object source) =>
        (Task)source.GetType().GetProperty(nameof(TaskCompletionSource.Task))!.GetValue(source)!;

    private static void Resolve(WaiterOutcome outcome, SemaphoreSlim semaphore, CancellationTokenSource source, bool controlled)
    {
        switch (outcome)
        {
            case WaiterOutcome.Release when controlled:
                ControlledSemaphoreSlim.Release(semaphore);
                break;
            case WaiterOutcome.Release:
                semaphore.Release();
                break;
            case WaiterOutcome.UninstrumentedCancel:
                source.Cancel();
                break;
            case WaiterOutcome.Dispose:
                ControlledSemaphoreSlim.Dispose(semaphore);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null);
        }
    }

    private static Rejection Reject(CompletionError error, bool controlled)
    {
        var source = new TaskCompletionSource();
        var raised = new InvalidOperationException("callback");
        using var cancellation = new CancellationTokenSource();
        using CancellationTokenRegistration registration = cancellation.Token.Register(() => throw raised);
        source.SetResult();
        Action reject = (error, controlled) switch
        {
            (CompletionError.SetResultTwice, true) => () => ControlledTaskCompletionSource.SetResult(source),
            (CompletionError.SetResultTwice, false) => source.SetResult,
            (CompletionError.CancelWithAThrowingCallback, true) => () => ControlledCancellationTokenSource.Cancel(cancellation, throwOnFirstException: true),
            (CompletionError.CancelWithAThrowingCallback, false) => () => cancellation.Cancel(throwOnFirstException: true),
            (CompletionError.SetExceptionOnACompletedSource, true) => () => ControlledTaskCompletionSource.SetException(source, new InvalidOperationException()),
            (CompletionError.SetExceptionOnACompletedSource, false) => () => source.SetException(new InvalidOperationException()),
            _ => throw new ArgumentOutOfRangeException(nameof(error), error, null),
        };

        try
        {
            reject();
        }
        catch (Exception exception)
        {
            return new Rejection(exception, raised);
        }

        throw new InvalidOperationException($"{error} completed.");
    }

    // Recurses until less than half a completion's headroom remains beyond the BCL's margin, where the BCL's own check
    // still passes, then runs the action.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void WithinTheCompletionHeadroom(Action action)
    {
        if (HasStackPast(SimulationCompletion.CompletionStackHeadroom / 2))
        {
            WithinTheCompletionHeadroom(action);
            return;
        }

        Assert.True(RuntimeHelpers.TryEnsureSufficientExecutionStack());
        action();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool HasStackPast(int bytes)
    {
        Span<byte> reserved = stackalloc byte[bytes];
        reserved[0] = 1;
        return RuntimeHelpers.TryEnsureSufficientExecutionStack() && reserved[0] == 1;
    }

    private static Task RunIn(SimulationCluster cluster, CompletionSite site, Action body) =>
        Drive(() =>
        {
            if (site == CompletionSite.Root)
            {
                cluster.RunToCompletion(
                    () =>
                    {
                        body();
                        return Task.CompletedTask;
                    },
                    TestContext.Current.CancellationToken);
            }
            else
            {
                cluster.SchedulerLane.Enqueue(body);
            }

            cluster.RunUntilIdle(TestContext.Current.CancellationToken);
        });

    // Drives the simulation on a thread of its own under a watchdog, so a hang fails the test.
    private static Task Drive(Action drive) =>
        Task.Factory.StartNew(drive, TestContext.Current.CancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default)
            .WaitAsync(Watchdog, TestContext.Current.CancellationToken);

    // Runs the BCL's version on a thread-pool thread, whose own thread it reports as the logical one.
    private static Task<Placement?> OnAThreadPoolThread(Func<Func<bool>, Placement?> run) =>
        Task.Run(
                () =>
                {
                    int runner = Environment.CurrentManagedThreadId;
                    return run(() => Environment.CurrentManagedThreadId == runner);
                },
                TestContext.Current.CancellationToken)
            .WaitAsync(Watchdog, TestContext.Current.CancellationToken);

    private sealed record Rejection(Exception Thrown, Exception Raised);

    private sealed class ValueTaskSource : IValueTaskSource<int>
    {
        public ManualResetValueTaskSourceCore<int> Core;

        public ValueTask<int> Task => new(this, Core.Version);

        public int GetResult(short token) => Core.GetResult(token);

        public ValueTaskSourceStatus GetStatus(short token) => Core.GetStatus(token);

        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) =>
            Core.OnCompleted(continuation, state, token, flags);
    }

    private readonly record struct Placement(bool Inline, bool OnLogicalThread);

    // Records where a continuation runs: inside the completing call, on the thread which made it, or not; and on the
    // logical thread or not.
    private sealed class ContinuationProbe(Func<bool> onLogicalThread)
    {
        private int _completingThread;

        public Placement? Observed { get; private set; }

        // A bare continuation, as a framework await with ConfigureAwait(false) registers.
        public void Watch(Task task) =>
            task.ConfigureAwait(false).GetAwaiter().UnsafeOnCompleted(Observe);

        // A callback which CancellationTokenSource runs through the current context's Send.
        public CancellationTokenRegistration WatchOnTheContext(CancellationToken token) =>
            token.Register(Observe, useSynchronizationContext: true);

        public void Complete(Action complete)
        {
            _completingThread = Environment.CurrentManagedThreadId;
            try
            {
                complete();
            }
            finally
            {
                _completingThread = 0;
            }
        }

        private void Observe() =>
            Observed = new Placement(_completingThread == Environment.CurrentManagedThreadId, onLogicalThread());
    }
}
