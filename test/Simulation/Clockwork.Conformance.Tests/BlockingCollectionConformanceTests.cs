using System.Collections.Concurrent;
using System.Reflection;

namespace Orleans.Testing.Simulation.Conformance.Tests;

/// <summary>
/// End-to-end conformance for the controlled <c>BlockingCollection&lt;T&gt;</c>. The BCL blocks a taker on an empty
/// collection, and an adder on a full bounded one, on framework semaphores and wait handles; rewritten, the caller parks
/// on the coordinator instead, so the other controlled work which unblocks it runs. Each deterministic scenario returns
/// what the same probe returns uninstrumented, on real threads. Rewritten without the rules, a timed take blocks the
/// logical thread for its whole timeout, so the producer which would satisfy it cannot run.
/// </summary>
public sealed class BlockingCollectionConformanceTests : IDisposable
{
    private const string ProbeType = "Conf.BlockingCollectionProbe";

    private static readonly DateTimeOffset Start = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string Source = """
        using System;
        using System.Collections.Concurrent;
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;

        namespace Conf
        {
            public static class BlockingCollectionProbe
            {
                // A bounded producer blocks on a full collection while a consumer drains it.
                public static async Task<string> ProducerConsumer(int capacity)
                {
                    using var collection = new BlockingCollection<int>(capacity);
                    Task producer = Task.Run(() =>
                    {
                        for (int i = 0; i < 5; i++) collection.Add(i);
                        collection.CompleteAdding();
                    });
                    var read = new List<int>();
                    await Task.Run(() =>
                    {
                        foreach (int item in collection.GetConsumingEnumerable()) read.Add(item);
                    });
                    await producer;
                    return string.Join(",", read) + "|" + collection.IsCompleted;
                }

                // The producer's zero-timeout add waits for the consumer's first take, so the collection has room for it
                // under the BCL as under simulation, whatever the threads' real-time margins.
                public static async Task<string> TakeAndTryTake()
                {
                    using var collection = new BlockingCollection<int>(3);
                    var firstTaken = new TaskCompletionSource();
                    Task<string> producer = Task.Run(async () =>
                    {
                        await Task.Delay(1);
                        collection.Add(1);
                        await Task.Delay(1);
                        bool addedSecond = collection.TryAdd(2, Timeout.Infinite);
                        bool addedThird = collection.TryAdd(3, TimeSpan.FromMilliseconds(-1));
                        await firstTaken.Task;
                        bool addedFourth = collection.TryAdd(4, 0);
                        return $"{addedSecond},{addedThird},{addedFourth}";
                    });
                    int first = collection.Take();
                    firstTaken.SetResult();
                    bool second = collection.TryTake(out int secondItem, -1);
                    string added = await producer;
                    bool third = collection.TryTake(out int thirdItem);
                    bool fourth = collection.TryTake(out int fourthItem, TimeSpan.Zero);
                    return $"{first}|{second}:{secondItem}|{third}:{thirdItem}|{fourth}:{fourthItem}|added:{added}";
                }

                // A zero-timeout add to a full bounded collection fails at once, and succeeds once a take makes room.
                public static Task<string> ZeroTimeoutAdd()
                {
                    using var collection = new BlockingCollection<int>(1);
                    collection.Add(1);
                    bool full = collection.TryAdd(2, 0);
                    bool fullTimeSpan = collection.TryAdd(2, TimeSpan.Zero);
                    bool fullUntimed = collection.TryAdd(2);
                    int taken = collection.Take();
                    bool room = collection.TryAdd(3, 0);
                    return Task.FromResult($"{full},{fullTimeSpan},{fullUntimed}|{taken}|{room}:{string.Join(",", collection.ToArray())}");
                }

                public static async Task<string> CancelTake()
                {
                    using var collection = new BlockingCollection<int>();
                    using var source = new CancellationTokenSource();
                    Task canceller = Task.Run(async () =>
                    {
                        await Task.Delay(1);
                        source.Cancel();
                    });
                    try
                    {
                        collection.Take(source.Token);
                        return "taken";
                    }
                    catch (OperationCanceledException exception) when (exception.CancellationToken == source.Token)
                    {
                        await canceller;
                        return "canceled";
                    }
                }

                // CompleteAdding wakes a waiting taker, which then finds the collection completed.
                public static async Task<string> CompleteAddingWakesTakers()
                {
                    using var collection = new BlockingCollection<int>();
                    Task completer = Task.Run(async () =>
                    {
                        await Task.Delay(1);
                        collection.CompleteAdding();
                    });
                    bool taken = collection.TryTake(out _, Timeout.Infinite);
                    string take;
                    try
                    {
                        collection.Take();
                        take = "taken";
                    }
                    catch (InvalidOperationException exception)
                    {
                        take = exception.Message;
                    }

                    await completer;
                    int count = 0;
                    foreach (int item in collection.GetConsumingEnumerable()) count++;
                    return taken + "|" + take + "|" + count;
                }

                public static async Task<string> CompleteAddingWakesAdder()
                {
                    using var collection = new BlockingCollection<int>(1);
                    collection.Add(0);
                    Task completer = Task.Run(async () =>
                    {
                        await Task.Delay(1);
                        collection.CompleteAdding();
                    });
                    try
                    {
                        collection.Add(1);
                        return "added";
                    }
                    catch (InvalidOperationException exception)
                    {
                        await completer;
                        return exception.GetType().Name + ":" + exception.Message;
                    }
                }

                public static async Task<string> AnyOfSeveral()
                {
                    var empty = new BlockingCollection<int>();
                    var later = new BlockingCollection<int>();
                    Task producer = Task.Run(async () =>
                    {
                        await Task.Delay(1);
                        later.Add(5);
                    });
                    int takenFrom = BlockingCollection<int>.TakeFromAny(new[] { empty, later }, out int taken);
                    await producer;

                    var full = new BlockingCollection<int>(1);
                    var drained = new BlockingCollection<int>(1);
                    full.Add(0);
                    drained.Add(0);
                    Task consumer = Task.Run(async () =>
                    {
                        await Task.Delay(1);
                        drained.Take();
                    });
                    int addedTo = BlockingCollection<int>.AddToAny(new[] { full, drained }, 9);
                    await consumer;

                    empty.CompleteAdding();
                    later.CompleteAdding();
                    string takeFromCompleted;
                    try
                    {
                        BlockingCollection<int>.TakeFromAny(new[] { empty, later }, out _);
                        takeFromCompleted = "taken";
                    }
                    catch (ArgumentException exception)
                    {
                        takeFromCompleted = exception.ParamName + ":" + exception.Message;
                    }

                    int tryFromCompleted = BlockingCollection<int>.TryTakeFromAny(new[] { empty, later }, out _, Timeout.Infinite);
                    return $"{takenFrom}:{taken}|{addedTo}:{string.Join(",", drained.ToArray())}|{takeFromCompleted}|{tryFromCompleted}";
                }

                // Invalid arguments and states throw what the BCL throws, before any wait.
                public static Task<string> Failures()
                {
                    var results = new List<string>();
                    void Record(Action action)
                    {
                        try
                        {
                            action();
                            results.Add("none");
                        }
                        catch (Exception exception)
                        {
                            results.Add(exception.GetType().Name + ":" + exception.Message);
                        }
                    }

                    var collection = new BlockingCollection<int>(1);
                    Record(() => collection.TryTake(out _, -2));
                    Record(() => collection.TryAdd(1, TimeSpan.FromMilliseconds(-2)));
                    Record(() => BlockingCollection<int>.TryTakeFromAny(new BlockingCollection<int>[0], out _, 1));
                    Record(() => BlockingCollection<int>.AddToAny(null, 1));
                    Record(() => collection.Add(1, new CancellationToken(true)));
                    collection.CompleteAdding();
                    Record(() => collection.Add(1));
                    Record(() => BlockingCollection<int>.TryAddToAny(new[] { collection }, 1, 1));
                    collection.Dispose();
                    Record(() => collection.Take());
                    Record(() => collection.TryTake(out _, 1));
                    return Task.FromResult(string.Join("\n", results));
                }

                // A take which waits up to `timeout` milliseconds for a producer which adds after `delay`.
                public static async Task<string> TimedTake(int delay, int timeout)
                {
                    using var collection = new BlockingCollection<int>();
                    Task producer = Task.Run(async () =>
                    {
                        await Task.Delay(delay);
                        collection.Add(7);
                    });
                    bool taken = collection.TryTake(out int item, timeout);
                    await producer;
                    return taken + ":" + item;
                }
            }
        }
        """;

    private readonly RewriteFixture _fixture = new();
    private readonly Lazy<StagedProbe> _probe;
    private readonly Lazy<UninstrumentedProbe> _bcl;

    public BlockingCollectionConformanceTests()
    {
        _probe = new(() => _fixture.StageControlledTasks("Conf.BlockingCollection", ProbeType, Source, optimize: true));
        _bcl = new(() => _fixture.CompileUninstrumented("Conf.BlockingCollectionBcl", ProbeType, Source));
    }

    public static TheoryData<string, object[]> DeterministicScenarios => new()
    {
        { "ProducerConsumer", [1] },
        { "ProducerConsumer", [3] },
        { "CancelTake", [] },
        { "CompleteAddingWakesTakers", [] },
        { "AnyOfSeveral", [] },
        { "Failures", [] },
    };

    [Theory]
    [MemberData(nameof(DeterministicScenarios))]
    public async Task ScenarioReturnsWhatTheBclReturns(string scenario, object[] args)
    {
        string expected = await (Task<string>)_bcl.Value.Method(scenario).Invoke(null, args)!;
        using var host = new SimulationHost(Start);

        string actual = Completed(host.Invoke(_probe.Value.Method(scenario), args));

        Assert.Equal(expected, actual);
    }

    // The scenarios whose outcome does not depend on real-time margins, pinned so a run of either side which differs fails
    // here with the value it should have produced.
    [Theory]
    [InlineData("TakeAndTryTake", "1|True:2|True:3|True:4|added:True,True,True")]
    [InlineData("ZeroTimeoutAdd", "False,False,False|1|True:3")]
    public async Task ScenarioReturnsItsDeterministicOutcome(string scenario, string expected)
    {
        Assert.Equal(expected, await (Task<string>)_bcl.Value.Method(scenario).Invoke(null, null)!);
        using var host = new SimulationHost(Start);

        Assert.Equal(expected, Completed(host.Invoke(_probe.Value.Method(scenario))));
    }

    [Fact]
    public void CompleteAddingFailsAWaitingAdderWithTheBclMessage()
    {
        // The BCL reports CompleteAdding during a blocked Add with this resource; which of its two messages a real run
        // reports depends on whether the adder was already waiting, which it always is here.
        string message = (string)typeof(BlockingCollection<int>).Assembly.GetType("System.SR")!
            .GetProperty("BlockingCollection_Add_ConcurrentCompleteAdd", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;
        using var host = new SimulationHost(Start);

        string actual = Completed(host.Invoke(_probe.Value.Method("CompleteAddingWakesAdder")));

        Assert.Equal("InvalidOperationException:" + message, actual);
    }

    [Theory]
    [InlineData(50, 100, "True:7")]
    [InlineData(100, 50, "False:0")]
    public void TimedWaitsElapseInVirtualTime(int delay, int timeout, string expected)
    {
        using var host = new SimulationHost(Start);

        Assert.Equal(expected, Completed(host.Invoke(_probe.Value.Method("TimedTake"), delay, timeout)));
    }

    [Fact]
    public void WithoutTheRulesATimedTakeBlocksTheLogicalThread()
    {
        StagedProbe escaping = _fixture.StageControlledTasksWithout(
            "Conf.BlockingCollectionEscape",
            ProbeType,
            Source,
            rule => rule.Id.StartsWith("clockwork.blockingcollection.", StringComparison.Ordinal));
        using var host = new SimulationHost(Start);

        // The BCL's TryTake blocks the logical thread for its real-time timeout, so the producer cannot run first.
        Assert.Equal("False:0", Completed(host.Invoke(escaping.Method("TimedTake"), 1, 100)));
    }

    private static string Completed(object? task)
    {
        var typed = Assert.IsType<Task<string>>(task, exactMatch: false);
        Assert.Equal(TaskStatus.RanToCompletion, typed.Status);
        return typed.GetAwaiter().GetResult();
    }

    public void Dispose() => _fixture.Dispose();
}
