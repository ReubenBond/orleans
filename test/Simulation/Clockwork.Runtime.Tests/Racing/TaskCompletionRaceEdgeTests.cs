using Orleans.Testing.Simulation;
using System.Threading.Tasks;
using Orleans.Testing.Simulation.Runtime.Racing;
using Orleans.Testing.Simulation.Runtime.Scheduling;
using Orleans.Testing.Simulation.Runtime.Tests.Scheduling;
using Orleans.Testing.Simulation.Runtime.Tests.Tasks;
using Orleans.Testing.Simulation.Runtime.Threading;
using Orleans.Testing.Simulation.Shims.System.Runtime.CompilerServices;

namespace Orleans.Testing.Simulation.Runtime.Tests.Racing;

/// <summary>
/// Race-detection edges through task completion. A producer writes a location and then completes a real
/// <see cref="TaskCompletionSource{TResult}"/> through a redirected completion member, which publishes the release edge;
/// a consumer observes the task through an await, a synchronous wait, a controlled combinator, a continuation, or a
/// value-task conversion, which acquires it, and then reads the location. Each pair is checked with the consumer
/// starting both after the completion and before it (so the combinators take both their synchronous and their
/// coordinator-completed paths). A write after the completion is not ordered before the read, which the detector
/// reports. A controlled timer's <c>DisposeAsync</c> publishes the same edge from the callbacks it waited for.
/// </summary>
public sealed class TaskCompletionRaceEdgeTests
{
    private const string Location = "Fx.State::Value";

    private static readonly string[] Producers = ["SetResult", "TrySetResult", "SetException", "TrySetCanceled", "SetFromTask"];

    private static readonly string[] Consumers =
        ["await", "wait", "result", "whenall", "whenany", "unwrap", "continuewith", "continuewith-await", "astask", "wheneach",
            "continuewhenall", "continuewhenany"];

    public static TheoryData<string, string, bool> Pairs
    {
        get
        {
            var data = new TheoryData<string, string, bool>();
            foreach (string producer in Producers)
            {
                foreach (string consumer in Consumers)
                {
                    data.Add(producer, consumer, false);
                    data.Add(producer, consumer, true);
                }
            }

            return data;
        }
    }

    public static TheoryData<string, bool> ConsumerOrders
    {
        get
        {
            var data = new TheoryData<string, bool>();
            foreach (string consumer in Consumers)
            {
                data.Add(consumer, false);
                data.Add(consumer, true);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Pairs))]
    public void CompletionThenObservationOrdersTheAccesses(string producer, string consumer, bool consumerFirst)
    {
        using SimulationScheduler scheduler = Run(producer, consumer, consumerFirst, writeAfterCompletion: false);

        Assert.Null(scheduler.FirstRace);
    }

    [Theory]
    [MemberData(nameof(ConsumerOrders))]
    public void WriteAfterCompletionRacesWithTheObservingRead(string consumer, bool consumerFirst)
    {
        using SimulationScheduler scheduler = Run("SetResult", consumer, consumerFirst, writeAfterCompletion: true);

        RaceReport race = Assert.IsType<RaceReport>(scheduler.FirstRace);
        Assert.Equal(Location, race.FirstAccess.Location.Member);
    }

    // Unwrapping an outer task which has already completed successfully returns the inner task itself, which carries
    // only its own edges. The inner task here completed without publishing one, so only the outer task's edge, which
    // the unwrapping operation acquires as it observes the outer task complete, orders the write before the read.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnwrapOfACompletedOuterTaskAcquiresTheOuterEdge(bool generic)
    {
        Task<int> inner = Task.FromResult(1);
        var outer = new TaskCompletionSource<Task>();
        var genericOuter = new TaskCompletionSource<Task<int>>();
        using SimulationScheduler scheduler = SchedulerTestHarness.NewScheduler();
        scheduler.Schedule("producer", () =>
        {
            RaceInstrumentation.WriteStatic(Location, "producer", 1, null, -1);
            if (generic)
            {
                ControlledTaskCompletionSource.SetResult(genericOuter, inner);
            }
            else
            {
                ControlledTaskCompletionSource.SetResult(outer, inner);
            }
        });
        scheduler.Drain(TestContext.Current.CancellationToken);
        scheduler.Schedule("consumer", () =>
        {
            Task unwrapped = generic ? ControlledTask.Unwrap(genericOuter.Task) : ControlledTask.Unwrap(outer.Task);
            Assert.Same(inner, unwrapped);
            new ControlledTaskAwaiter(unwrapped).GetResult();
            Read();
        });
        scheduler.Drain(TestContext.Current.CancellationToken);

        Assert.Null(scheduler.FirstRace);
    }

    // SetFromTask acquires the completed task's edges before it completes the source, so a framework continuation which
    // runs inline inside the completion is ordered after the writes which preceded that task's completion.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SetFromTaskOrdersAnInlineFrameworkContinuationAfterTheCompletedTask(bool tryForm)
    {
        var antecedent = new TaskCompletionSource<int>();
        var source = new TaskCompletionSource<int>();
        bool read = false;
        using SimulationScheduler scheduler = SchedulerTestHarness.NewScheduler();
        scheduler.Schedule("producer", () =>
        {
            RaceInstrumentation.WriteStatic(Location, "producer", 1, null, -1);
            ControlledTaskCompletionSource.SetResult(antecedent, 1);
        });
        scheduler.Drain(TestContext.Current.CancellationToken);
        scheduler.Schedule("forwarder", () =>
        {
            source.Task.ConfigureAwait(false).GetAwaiter().UnsafeOnCompleted(() =>
            {
                Read();
                read = true;
            });
            if (tryForm)
            {
                Assert.True(ControlledTaskCompletionSource.TrySetFromTask(source, antecedent.Task));
            }
            else
            {
                ControlledTaskCompletionSource.SetFromTask(source, antecedent.Task);
            }

            Assert.True(read);
        });
        scheduler.Drain(TestContext.Current.CancellationToken);

        Assert.Null(scheduler.FirstRace);
    }

    // A documented limitation: a task which uninstrumented code completes publishes no release edge. In a scheduler lane's
    // work, a framework method awaits a task which rewritten code completes, and completes its own task inline inside that
    // completion; rewritten code which awaits the framework method's task is not ordered after the completer's writes, so
    // the detector reports a race that is not one.
    [Fact]
    public void ATaskWhichAFrameworkAwaitCompletesInALanesWorkPublishesNoEdge()
    {
        var antecedent = new TaskCompletionSource<int>();
        Task? forwarded = null;
        using SimulationScheduler scheduler = SchedulerTestHarness.NewScheduler();
        var lane = new SimulationSchedulerLane(scheduler, new SingleThreadedGuard());
        lane.Enqueue(() =>
        {
            Assert.IsType<SimulationSynchronizationContext>(SynchronizationContext.Current);
            forwarded = ForwardAsync(antecedent.Task);
            RaceInstrumentation.WriteStatic(Location, "producer", 1, null, -1);
            ControlledTaskCompletionSource.SetResult(antecedent, 1);
            Assert.True(forwarded.IsCompleted);
        });
        scheduler.Drain(TestContext.Current.CancellationToken);
        scheduler.Schedule("consumer", () =>
        {
            new ControlledTaskAwaiter(forwarded!).GetResult();
            Read();
        });
        scheduler.Drain(TestContext.Current.CancellationToken);

        RaceReport race = Assert.IsType<RaceReport>(scheduler.FirstRace);
        Assert.Equal(Location, race.FirstAccess.Location.Member);

        static async Task ForwardAsync(Task task) => await task.ConfigureAwait(false);
    }

    [Fact]
    public void TimerCallbackHappensBeforeTheAwaitOfDisposeAsync()
    {
        using var host = new SimulationSchedulerTestHost();
        var started = new TaskCompletionSource();
        var gate = new TaskCompletionSource();
        var steps = new List<string>();

        TaskTestHarness.RunInSimulation(host, () =>
        {
            var timer = new ControlledTimer(
                _ =>
                {
                    // The write follows the callback's last release edge, so only disposal can order it before the read.
                    ControlledTaskCompletionSource.SetResult(started);
                    RaceInstrumentation.WriteStatic(Location, "callback", 1, null, -1);
                    ControlledTask.Wait(gate.Task);
                    steps.Add("callback");
                },
                null,
                0,
                Timeout.Infinite);
            host.Scheduler.Schedule("consumer", () =>
            {
                ControlledTask.Wait(started.Task);
                Task disposal = ControlledValueTask.AsTask(timer.DisposeAsync());
                steps.Add("disposing " + disposal.IsCompleted);
                ControlledTaskCompletionSource.SetResult(gate);
                new ControlledTaskAwaiter(disposal).GetResult();
                steps.Add("disposed");
                Read();
            });
            host.Scheduler.RunUntilIdle(TestContext.Current.CancellationToken);
        });

        Assert.Equal(["disposing False", "callback", "disposed"], steps);
        Assert.Null(host.Scheduler.FirstRace);
    }

    private static SimulationScheduler Run(string producer, string consumer, bool consumerFirst, bool writeAfterCompletion)
    {
        var source = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        SimulationScheduler scheduler = SchedulerTestHarness.NewScheduler();
        Action produce = () =>
        {
            if (!writeAfterCompletion)
            {
                RaceInstrumentation.WriteStatic(Location, "producer", 1, null, -1);
            }

            Complete(producer, source);
            if (writeAfterCompletion)
            {
                RaceInstrumentation.WriteStatic(Location, "producer", 2, null, -1);
            }
        };
        Action consume = () => Observe(consumer, source.Task);

        if (consumerFirst)
        {
            scheduler.Schedule("consumer", consume);
            scheduler.Schedule("producer", produce);
        }
        else
        {
            scheduler.Schedule("producer", produce);
            scheduler.Drain(TestContext.Current.CancellationToken);
            scheduler.Schedule("consumer", consume);
        }

        scheduler.Drain(TestContext.Current.CancellationToken);
        Assert.True(source.Task.IsCompleted);
        return scheduler;
    }

    private static void Complete(string producer, TaskCompletionSource<int> source)
    {
        switch (producer)
        {
            case "SetResult":
                ControlledTaskCompletionSource.SetResult(source, 1);
                break;
            case "TrySetResult":
                Assert.True(ControlledTaskCompletionSource.TrySetResult(source, 1));
                break;
            case "SetException":
                ControlledTaskCompletionSource.SetException(source, new InvalidOperationException("boom"));
                break;
            case "TrySetCanceled":
                Assert.True(ControlledTaskCompletionSource.TrySetCanceled(source));
                break;
            default:
                ControlledTaskCompletionSource.SetFromTask(source, Task.FromResult(1));
                break;
        }
    }

    private static void Observe(string consumer, Task<int> task)
    {
        switch (consumer)
        {
            case "await":
                Ignore(() => new ControlledTaskAwaiter<int>(task).GetResult());
                Read();
                break;
            case "wait":
                Ignore(() => ControlledTask.Wait(task));
                Read();
                break;
            case "result":
                Ignore(() => ControlledTask.Result(task));
                Read();
                break;
            case "whenall":
                Ignore(() => new ControlledTaskAwaiter(ControlledTask.WhenAll(task, Task.CompletedTask)).GetResult());
                Read();
                break;
            case "whenany":
                Task<Task<int>> any = ControlledTask.WhenAny(task, new TaskCompletionSource<int>().Task);
                Assert.Same(task, new ControlledTaskAwaiter<Task<int>>(any).GetResult());
                Read();
                break;
            case "unwrap":
                var outer = new TaskCompletionSource<Task<int>>();
                Task<int> unwrapped = ControlledTask.Unwrap(outer.Task);
                ControlledTaskCompletionSource.SetResult(outer, task);
                Ignore(() => new ControlledTaskAwaiter<int>(unwrapped).GetResult());
                Read();
                break;
            case "continuewith":
                _ = ControlledTask.ContinueWith(task, _ => Read());
                break;
            case "continuewith-await":
                Task continuation = ControlledTask.ContinueWith(task, _ => { });
                new ControlledTaskAwaiter(continuation).GetResult();
                Read();
                break;
            case "wheneach":
                IAsyncEnumerator<Task<int>> each = ControlledTask.WhenEach(task).GetAsyncEnumerator();
                Assert.True(new ControlledValueTaskAwaiter<bool>(each.MoveNextAsync()).GetResult());
                Assert.Same(task, each.Current);
                Read();
                break;
            case "continuewhenall":
                _ = ControlledTaskFactory.ContinueWhenAll(Task.Factory, new Task[] { task }, _ => Read());
                break;
            case "continuewhenany":
                _ = ControlledTaskFactory.ContinueWhenAny(Task.Factory, new Task[] { task, new TaskCompletionSource().Task }, _ => Read());
                break;
            default:
                Task<int> converted = ControlledValueTask.AsTask(new ValueTask<int>(task));
                Assert.Same(task, converted);
                Ignore(() => new ControlledTaskAwaiter<int>(converted).GetResult());
                Read();
                break;
        }
    }

    private static void Read() => RaceInstrumentation.ReadStatic(Location, "consumer", 3, null, -1);

    private static void Ignore(Action observe)
    {
        try
        {
            observe();
        }
        catch (Exception exception) when (exception is InvalidOperationException or OperationCanceledException or AggregateException)
        {
        }
    }
}
