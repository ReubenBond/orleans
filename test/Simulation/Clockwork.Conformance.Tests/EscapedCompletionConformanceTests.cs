using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Orleans.Testing.Simulation.Runtime.Replay;

namespace Orleans.Testing.Simulation.Conformance.Tests;

/// <summary>
/// End-to-end conformance for the producers whose completions used to escape the coordinator. A task or value-task
/// source which runs its continuations asynchronously (a <see cref="TaskCompletionSource"/> created with
/// <see cref="TaskCreationOptions.RunContinuationsAsynchronously"/>, a controlled timer's <c>DisposeAsync</c>, a
/// channel's completion and pending reads, and a <see cref="System.Threading.Tasks.Sources.ManualResetValueTaskSourceCore{TResult}"/>
/// with <c>RunContinuationsAsynchronously</c>) posts the continuations the BCL registered on it to the real thread pool.
/// Each probe consumes one producer through a combinator (including <c>Task.WhenEach</c>), a <c>ContinueWith</c>, a
/// <c>TaskFactory.ContinueWhenAll</c>/<c>ContinueWhenAny</c>, or a <c>ValueTask</c> conversion while
/// workers interleave; recording the same seed repeatedly must produce the same schedule, trace, and replay artifact,
/// and every consumer must resume as a controlled operation. The probes are staged in both Release and Debug codegen.
/// </summary>
public sealed class EscapedCompletionConformanceTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    internal const string Source = """
        using System;
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Channels;
        using System.Threading.Tasks;
        using System.Threading.Tasks.Sources;

        namespace Conf { public static class EscapeProbe {
            public static async Task<string> Race(string producer, int rounds, int workers, List<string> trace, Action<string> observe)
            {
                for (int round = 0; round < rounds; round++)
                {
                    var work = new List<Task>();
                    for (int i = 0; i < workers; i++)
                    {
                        work.Add(Worker(round, i, trace));
                    }

                    work.Add(Round(producer, round, trace, observe));
                    await Task.WhenAll(work);
                }

                return string.Join("|", trace);
            }

            private static Task Round(string producer, int round, List<string> trace, Action<string> observe) => producer switch
            {
                "whenany" => WhenAnyRound(round, trace, observe),
                "whenall" => WhenAllRound(round, trace, observe),
                "unwrap" => UnwrapRound(round, trace, observe),
                "continuewith" => ContinueWithRound(round, trace, observe),
                "timer" => TimerRound(round, trace, observe),
                "channelcompletion" => ChannelCompletionRound(round, trace, observe),
                "channelread" => ChannelReadRound(round, trace, observe),
                "valuetasksource" => ValueTaskSourceRound(round, trace, observe),
                "preserve" => PreserveRound(round, trace, observe),
                "wheneach" => WhenEachRound(round, trace, observe),
                "continuewhenall" => ContinueWhenAllRound(round, trace, observe),
                "continuewhenany" => ContinueWhenAnyRound(round, trace, observe),
                _ => throw new ArgumentOutOfRangeException(nameof(producer)),
            };

            private static async Task Worker(int round, int id, List<string> trace)
            {
                for (int step = 0; step < 3; step++)
                {
                    await Task.Yield();
                    trace.Add(round + ":w" + id + "." + step);
                }
            }

            private static async Task WhenAnyRound(int round, List<string> trace, Action<string> observe)
            {
                var first = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                var second = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                Task<Task<int>> winner = Task.WhenAny(first.Task, second.Task);
                Task completions = Task.WhenAll(Complete(round, first, 1, trace), Complete(round, second, 2, trace));
                Task<int> won = await winner;
                observe("whenany");
                trace.Add(round + ":winner " + won.Result);
                await completions;
            }

            private static async Task WhenAllRound(int round, List<string> trace, Action<string> observe)
            {
                var first = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                var second = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                Task<int[]> all = Task.WhenAll(first.Task, second.Task);
                Task completions = Task.WhenAll(Complete(round, first, 1, trace), Complete(round, second, 2, trace));
                int[] values = await all;
                observe("whenall");
                trace.Add(round + ":all " + string.Join(",", values));
                await completions;
            }

            private static async Task UnwrapRound(int round, List<string> trace, Action<string> observe)
            {
                var outer = new TaskCompletionSource<Task<int>>(TaskCreationOptions.RunContinuationsAsynchronously);
                var inner = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                Task<int> unwrapped = outer.Task.Unwrap();
                Task completions = Task.WhenAll(Complete(round, outer, inner.Task, trace), Complete(round, inner, 3, trace));
                int value = await unwrapped;
                observe("unwrap");
                trace.Add(round + ":unwrapped " + value);
                await completions;
            }

            private static async Task ContinueWithRound(int round, List<string> trace, Action<string> observe)
            {
                var source = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                Task continued = source.Task.ContinueWith(
                    completed =>
                    {
                        observe("continuewith");
                        trace.Add(round + ":continuation " + completed.Result);
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                Task completion = Complete(round, source, 4, trace);
                await continued;
                observe("continued");
                trace.Add(round + ":continued");
                await completion;
            }

            private static async Task TimerRound(int round, List<string> trace, Action<string> observe)
            {
                var timer = new Timer(
                    _ =>
                    {
                        trace.Add(round + ":tick");
                        Thread.Sleep(5);
                        trace.Add(round + ":tock");
                    },
                    null,
                    1,
                    Timeout.Infinite);
                await Task.Delay(2);
                Task disposed = timer.DisposeAsync().AsTask();
                Task first = await Task.WhenAny(disposed, Task.Delay(1000));
                observe("timer");
                trace.Add(round + ":disposed " + (first == disposed));
            }

            private static async Task ChannelCompletionRound(int round, List<string> trace, Action<string> observe)
            {
                var channel = Channel.CreateUnbounded<int>();
                Task completer = CompleteWriter(round, channel.Writer, trace);
                Task first = await Task.WhenAny(channel.Reader.Completion, Task.Delay(1000));
                observe("channelcompletion");
                trace.Add(round + ":completed " + (first == channel.Reader.Completion));
                await completer;
            }

            private static async Task ChannelReadRound(int round, List<string> trace, Action<string> observe)
            {
                var channel = Channel.CreateUnbounded<int>();
                Task<int> read = channel.Reader.ReadAsync().AsTask();
                Task writer = Write(round, channel.Writer, 5, trace);
                Task first = await Task.WhenAny(read, Task.Delay(1000));
                observe("channelread");
                trace.Add(round + ":read " + (first == read ? read.Result : -1));
                await writer;
            }

            private static async Task ValueTaskSourceRound(int round, List<string> trace, Action<string> observe)
            {
                var source = new AsynchronousSource();
                Task<int> converted = source.Next().AsTask();
                Task completion = Complete(round, source, 6, trace);
                int value = await converted;
                observe("valuetasksource");
                trace.Add(round + ":converted " + value);
                await completion;
            }

            private static async Task PreserveRound(int round, List<string> trace, Action<string> observe)
            {
                var source = new AsynchronousSource();
                ValueTask<int> preserved = source.Next().Preserve();
                Task completion = Complete(round, source, 7, trace);
                int first = await preserved;
                int second = await preserved;
                observe("preserve");
                trace.Add(round + ":preserved " + first + "," + second);
                await completion;
            }

            private static async Task WhenEachRound(int round, List<string> trace, Action<string> observe)
            {
                var first = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                var second = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                IAsyncEnumerable<Task<int>> each = Task.WhenEach(first.Task, second.Task);
                Task completions = Task.WhenAll(Complete(round, first, 1, trace), Complete(round, second, 2, trace));
                await foreach (Task<int> completed in each)
                {
                    observe("wheneach");
                    trace.Add(round + ":each " + completed.Result);
                }

                await completions;
            }

            private static async Task ContinueWhenAllRound(int round, List<string> trace, Action<string> observe)
            {
                var first = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                var second = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                Task continued = Task.Factory.ContinueWhenAll(
                    new[] { first.Task, second.Task },
                    all =>
                    {
                        observe("continuewhenall");
                        trace.Add(round + ":continuewhenall " + all[0].Result + "," + all[1].Result);
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                Task completions = Task.WhenAll(Complete(round, first, 1, trace), Complete(round, second, 2, trace));
                await continued;
                await completions;
            }

            private static async Task ContinueWhenAnyRound(int round, List<string> trace, Action<string> observe)
            {
                var first = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                var second = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                Task<int> continued = Task<int>.Factory.ContinueWhenAny(
                    new Task[] { first.Task, second.Task },
                    winner =>
                    {
                        observe("continuewhenany");
                        int value = ((Task<int>)winner).Result;
                        trace.Add(round + ":continuewhenany " + value);
                        return value;
                    });
                Task completions = Task.WhenAll(Complete(round, first, 1, trace), Complete(round, second, 2, trace));
                await continued;
                await completions;
            }

            private static async Task Complete<T>(int round, TaskCompletionSource<T> source, T value, List<string> trace)
            {
                await Task.Yield();
                trace.Add(round + ":set " + value);
                source.SetResult(value);
                trace.Add(round + ":after set " + value);
            }

            private static async Task Complete(int round, AsynchronousSource source, int value, List<string> trace)
            {
                await Task.Yield();
                trace.Add(round + ":set " + value);
                source.SetResult(value);
                trace.Add(round + ":after set " + value);
            }

            private static async Task CompleteWriter(int round, ChannelWriter<int> writer, List<string> trace)
            {
                await Task.Yield();
                trace.Add(round + ":complete");
                writer.Complete();
                trace.Add(round + ":after complete");
            }

            private static async Task Write(int round, ChannelWriter<int> writer, int value, List<string> trace)
            {
                await Task.Yield();
                trace.Add(round + ":write " + value);
                writer.TryWrite(value);
                trace.Add(round + ":after write " + value);
            }

            // Logs whether any continuation of a completed task runs inside the completing call: a continuation which
            // runs inline does so on the completing thread, inside the window the thread-static flag marks.
            [ThreadStatic]
            private static bool t_completing;

            public static async Task<string> CompletionOrder(bool runContinuationsAsynchronously)
            {
                var log = new List<string>();
                var source = new TaskCompletionSource<int>(
                    runContinuationsAsynchronously ? TaskCreationOptions.RunContinuationsAsynchronously : TaskCreationOptions.None);
                var pending = new TaskCompletionSource<int>();
                var outer = new TaskCompletionSource<Task<int>>();
                Task<int> unwrapped = outer.Task.Unwrap();
                outer.SetResult(source.Task);
                var asynchronous = new AsynchronousSource();
                Task<int> converted = asynchronous.Next().AsTask();
                var observed = new List<Task>
                {
                    Logged(source.Task, "await", log),
                    Logged(Task.WhenAny(source.Task, pending.Task), "whenany", log),
                    Logged(Task.WhenAll(source.Task, unwrapped), "whenall", log),
                    Logged(unwrapped, "unwrap", log),
                    source.Task.ContinueWith(_ => Add(log, "continuewith")),
                    source.Task.ContinueWith(
                        _ => Add(log, "executesynchronously"),
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default),
                    Logged(converted, "astask", log),
                };

                Add(log, "before set");
                t_completing = true;
                source.SetResult(1);
                asynchronous.SetResult(2);
                t_completing = false;
                Add(log, "after set");
                await Task.WhenAll(observed);
                lock (log)
                {
                    return string.Join("|", log);
                }
            }

            private static async Task Logged(Task task, string entry, List<string> log)
            {
                await task;
                Add(log, entry);
            }

            private static void Add(List<string> log, string entry)
            {
                lock (log)
                {
                    log.Add(t_completing ? entry + " inside set" : entry);
                }
            }

            public static string CreationOptions()
            {
                var lines = new List<string>();
                object state = "state";
                lines.Add("tcs " + new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task.CreationOptions);
                lines.Add("tcs<int> " + new TaskCompletionSource<int>(state, TaskCreationOptions.RunContinuationsAsynchronously).Task.CreationOptions);
                lines.Add("tcs none " + new TaskCompletionSource<int>().Task.CreationOptions);
                var antecedent = new TaskCompletionSource();
                Task continuation = antecedent.Task.ContinueWith(
                    _ => { },
                    CancellationToken.None,
                    TaskContinuationOptions.DenyChildAttach | TaskContinuationOptions.HideScheduler |
                        TaskContinuationOptions.PreferFairness | TaskContinuationOptions.RunContinuationsAsynchronously |
                        TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.LazyCancellation,
                    TaskScheduler.Default);
                lines.Add("continuation " + continuation.CreationOptions + " " + continuation.Status);
                Task<int> stateful = antecedent.Task.ContinueWith((_, value) => 1, state, TaskContinuationOptions.DenyChildAttach);
                lines.Add("stateful " + stateful.CreationOptions + " " + stateful.AsyncState);
                antecedent.SetResult();
                return string.Join(Environment.NewLine, lines);
            }

            // A value-task source which, like a channel's pending operation, runs its continuations asynchronously.
            private sealed class AsynchronousSource : IValueTaskSource<int>
            {
                private ManualResetValueTaskSourceCore<int> _core = new() { RunContinuationsAsynchronously = true };

                public ValueTask<int> Next() => new(this, _core.Version);

                public void SetResult(int value) => _core.SetResult(value);

                public int GetResult(short token) => _core.GetResult(token);

                public ValueTaskSourceStatus GetStatus(short token) => _core.GetStatus(token);

                public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) =>
                    _core.OnCompleted(continuation, state, token, flags);
            }
        } }
        """;

    private const string ProbeType = "Conf.EscapeProbe";
    private const int SimulationSeed = 4242;
    private const int ScheduleSeed = 17;
    private const int Rounds = 3;
    private const int Workers = 6;
    private const int Recordings = 5;

    private static readonly string[] Producers =
    [
        "whenany",
        "whenall",
        "unwrap",
        "continuewith",
        "timer",
        "channelcompletion",
        "channelread",
        "valuetasksource",
        "preserve",
        "wheneach",
        "continuewhenall",
        "continuewhenany",
    ];

    private readonly RewriteFixture _fixture = new();
    private readonly Lazy<StagedProbe> _release;
    private readonly Lazy<StagedProbe> _debug;

    public EscapedCompletionConformanceTests()
    {
        _release = new Lazy<StagedProbe>(() =>
            _fixture.StageControlledTasks("Conf.EscapeRel", ProbeType, Source, optimize: true));
        _debug = new Lazy<StagedProbe>(() =>
            _fixture.StageControlledTasks("Conf.EscapeDbg", ProbeType, Source, optimize: false));
    }

    public static TheoryData<bool, string> ProducerCases
    {
        get
        {
            var data = new TheoryData<bool, string>();
            foreach (bool optimize in new[] { true, false })
            {
                foreach (string producer in Producers)
                {
                    data.Add(optimize, producer);
                }
            }

            return data;
        }
    }

    public static TheoryData<bool, bool> OrderCases => new()
    {
        { true, true },
        { true, false },
        { false, true },
        { false, false },
    };

    public static TheoryData<bool> Optimizations => new() { true, false };

    [Theory]
    [MemberData(nameof(ProducerCases))]
    public void SameSeedRecordsTheSameScheduleAndReplays(bool optimize, string producer)
    {
        MethodInfo race = Method("Race", optimize);
        var recordings = new List<(ReplayExecutionResult Result, string Trace)>();
        for (int i = 0; i < Recordings; i++)
        {
            var probe = new StrongBox<Task<string>?>();
            ReplayExecutionResult recorded = ReplayRunner.RecordCluster(
                SeededRandom(),
                cluster => ScheduleRace(cluster, race, producer, probe),
                TestContext.Current.CancellationToken);
            Assert.Equal(ReplayTerminationKind.Completed, recorded.Artifact.Outcome.Kind);
            recordings.Add((recorded, CompletedTrace(probe)));
        }

        (ReplayExecutionResult first, string firstTrace) = recordings[0];
        for (int round = 0; round < Rounds; round++)
        {
            Assert.Contains($"|{round}:w0.2", firstTrace, StringComparison.Ordinal);
        }

        Assert.All(recordings, recording =>
        {
            Assert.Equal(firstTrace, recording.Trace);
            Assert.Equal(first.Steps, recording.Result.Steps);
            Assert.Equal(first.ArtifactId, recording.Result.ArtifactId);
        });

        var replayedProbe = new StrongBox<Task<string>?>();
        ReplayExecutionResult replayed = ReplayRunner.ReplayCluster(
            first.Artifact,
            ReplayCompatibilityRequirements.Current(),
            cluster => ScheduleRace(cluster, race, producer, replayedProbe),
            maxSteps: 100_000,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(replayed.Reproduced);
        Assert.Equal(firstTrace, CompletedTrace(replayedProbe));
    }

    [Theory]
    [MemberData(nameof(ProducerCases))]
    public void ConsumersResumeAsControlledOperations(bool optimize, string producer)
    {
        using var host = new SimulationHost(Start);
        var observed = new ConcurrentQueue<(string Point, bool Controlled)>();

        var task = (Task<string>)host.Invoke(
            Method("Race", optimize),
            producer,
            Rounds,
            Workers,
            new List<string>(),
            new Action<string>(point => observed.Enqueue((point, host.Scheduler.CurrentOperation is not null))))!;

        Assert.Equal(TaskStatus.RanToCompletion, task.Status);
        Assert.Equal(Rounds * (producer is "continuewith" or "wheneach" ? 2 : 1), observed.Count);
        Assert.All(observed, entry => Assert.True(entry.Controlled, $"'{entry.Point}' ran outside a controlled operation."));
        string expected = producer switch
        {
            "whenany" => "winner 1",
            "whenall" => "all 1,2",
            "unwrap" => "unwrapped 3",
            "continuewith" => "continuation 4",
            "timer" => "disposed True",
            "channelcompletion" => "completed True",
            "channelread" => "read 5",
            "valuetasksource" => "converted 6",
            "wheneach" => "each 2",
            "continuewhenall" => "continuewhenall 1,2",
            "continuewhenany" => "continuewhenany 1",
            _ => "preserved 7,7",
        };
        for (int round = 0; round < Rounds; round++)
        {
            Assert.Contains($"{round}:{expected}", Completed(task), StringComparison.Ordinal);
        }
    }

    [Theory]
    [MemberData(nameof(OrderCases))]
    public void NoContinuationRunsInsideTheCompletingCall(bool optimize, bool runContinuationsAsynchronously)
    {
        using var host = new SimulationHost(Start);

        var task = (Task<string>)host.Invoke(Method("CompletionOrder", optimize), runContinuationsAsynchronously)!;

        // Whatever the source's options, Clockwork's continuations never run inside the completing call, and their
        // order is deterministic. (The BCL runs the await and ExecuteSynchronously continuations of a source created
        // without RunContinuationsAsynchronously inside SetResult; ExecuteSynchronously is a hint.)
        Assert.Equal(TaskStatus.RanToCompletion, task.Status);
        Assert.Equal(
            "before set|after set|await|whenany|whenall|unwrap|continuewith|executesynchronously|astask",
            Completed(task));
    }

    [Theory]
    [MemberData(nameof(Optimizations))]
    public async Task BclRunsNothingInsideTheCompletingCallOfAnAsynchronousSource(bool optimize)
    {
        UninstrumentedProbe bcl = _fixture.CompileUninstrumented(
            optimize ? "Conf.EscapeBclOrderRel" : "Conf.EscapeBclOrderDbg", ProbeType, Source, optimize);

        var task = (Task<string>)bcl.Method("CompletionOrder").Invoke(null, [true])!;
        string[] entries = (await task).Split('|');

        Assert.Equal(
            ["after set", "astask", "await", "before set", "continuewith", "executesynchronously", "unwrap", "whenall", "whenany"],
            entries.Order(StringComparer.Ordinal));

        // Without the flag, the BCL runs the await and ExecuteSynchronously continuations inside SetResult, which
        // Clockwork does not: there ExecuteSynchronously is a hint.
        var synchronous = (Task<string>)bcl.Method("CompletionOrder").Invoke(null, [false])!;
        string[] inline = (await synchronous).Split('|');
        Assert.Contains("await inside set", inline);
        Assert.Contains("executesynchronously inside set", inline);
    }

    [Theory]
    [MemberData(nameof(Optimizations))]
    public void CreationOptionsAreTheBcls(bool optimize)
    {
        UninstrumentedProbe bcl = _fixture.CompileUninstrumented(
            optimize ? "Conf.EscapeBclOptionsRel" : "Conf.EscapeBclOptionsDbg", ProbeType, Source, optimize);
        string expected = (string)bcl.Method("CreationOptions").Invoke(null, null)!;
        using var host = new SimulationHost(Start);

        string actual = (string)host.Invoke(Method("CreationOptions", optimize))!;

        Assert.Equal(expected, actual);
        Assert.Contains("tcs RunContinuationsAsynchronously", actual, StringComparison.Ordinal);
    }

    private MethodInfo Method(string name, bool optimize) =>
        (optimize ? _release : _debug).Value.Method(name);

    private static ReplayRecordingOptions SeededRandom() => new()
    {
        SimulationSeed = SimulationSeed,
        SchedulingPolicy = ReplaySchedulingPolicy.SeededRandom,
        ScheduleSeed = ScheduleSeed,
        MaxSteps = 100_000,
    };

    private static void ScheduleRace(SimulationCluster cluster, MethodInfo race, string producer, StrongBox<Task<string>?> probe)
    {
        SimulationNode node = cluster.AddNode("node");
        node.Context.SchedulerLane.Enqueue(() =>
            probe.Value = (Task<string>)race.Invoke(
                null,
                [producer, Rounds, Workers, new List<string>(), new Action<string>(static _ => { })])!);
    }

    private static string CompletedTrace(StrongBox<Task<string>?> probe)
    {
        Assert.NotNull(probe.Value);
        return Completed(probe.Value);
    }

    private static string Completed(Task<string> task)
    {
        Assert.Equal(TaskStatus.RanToCompletion, task.Status);
        return task.GetAwaiter().GetResult();
    }

    public void Dispose() => _fixture.Dispose();
}
