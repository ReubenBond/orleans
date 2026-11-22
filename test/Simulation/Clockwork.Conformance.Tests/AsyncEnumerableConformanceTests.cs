using System.Collections.Concurrent;

namespace Orleans.Testing.Simulation.Conformance.Tests;

/// <summary>
/// Conformance for the framework's <c>System.Linq.AsyncEnumerable</c> operators over rewritten sources, which Clockwork
/// does not rewrite. The operators are async methods and iterators compiled into the framework, which await the source's
/// <c>MoveNextAsync</c> with BCL awaiters. A controlled operation runs with no synchronization context and the default
/// task scheduler, so such an await captures no context and its continuation runs inline in the controlled operation
/// which completes the source's value task: a rewritten async iterator completes it as the iterator resumes on the
/// coordinator, and a task-backed source as a controlled operation completes its task. Inside a task started with
/// <c>Task.Start</c>, an await which captures a context captures the coordinator's task scheduler, which queues the
/// continuation as a controlled operation. Every step of the sources and of the user callbacks therefore runs on the
/// logical thread, and each query returns what it returns uninstrumented.
/// </summary>
public sealed class AsyncEnumerableConformanceTests : IDisposable
{
    private const string ProbeType = "Conf.AsyncEnumerableProbe";

    private static readonly DateTimeOffset Start = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string Source = """
        using System;
        using System.Collections.Generic;
        using System.Linq;
        using System.Runtime.CompilerServices;
        using System.Threading;
        using System.Threading.Tasks;

        namespace Conf
        {
            // A source whose MoveNextAsync is an async method, so its value task is backed by a task.
            public sealed class TaskBackedSequence : IAsyncEnumerable<int>
            {
                private readonly Action _report;

                public TaskBackedSequence(Action report) => _report = report;

                public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default) => new Enumerator(_report);

                private sealed class Enumerator : IAsyncEnumerator<int>
                {
                    private readonly Action _report;
                    private int _current = -1;

                    public Enumerator(Action report) => _report = report;

                    public int Current => _current;

                    public async ValueTask<bool> MoveNextAsync()
                    {
                        await Task.Delay(1);
                        _report();
                        return ++_current < 4;
                    }

                    public ValueTask DisposeAsync() => default;
                }
            }

            public static class AsyncEnumerableProbe
            {
                private static async IAsyncEnumerable<int> Iterate(Action report, [EnumeratorCancellation] CancellationToken cancellationToken = default)
                {
                    for (int i = 0; i < 5; i++)
                    {
                        await Task.Delay(1, cancellationToken);
                        report();
                        yield return i;
                    }
                }

                public static async Task<string> Query(Action report)
                {
                    List<int> all = await Iterate(report).ToListAsync();
                    int[] filtered = await Iterate(report)
                        .Where(x => { report(); return x % 2 == 0; })
                        .Select(async (x, ct) => { await Task.Delay(1, ct); report(); return x * 10; })
                        .ToArrayAsync();
                    int sum = await new TaskBackedSequence(report).Append(100).Prepend(-1).SumAsync();
                    bool any = await Iterate(report).AnyAsync(x => x == 3);
                    int count = await new TaskBackedSequence(report).CountAsync();
                    return string.Join(",", all) + "|" + string.Join(",", filtered) + "|" + sum + "|" + any + "|" + count;
                }

                // Inside a started task an await captures the coordinator's TaskScheduler as its context.
                public static Task<string> QueryInStartedTask(Action report)
                {
                    var outer = new Task<Task<string>>(() => Query(report));
                    outer.Start();
                    return outer.Unwrap();
                }
            }
        }
        """;

    private readonly RewriteFixture _fixture = new();
    private readonly Lazy<StagedProbe> _probe;
    private readonly Lazy<UninstrumentedProbe> _bcl;

    public AsyncEnumerableConformanceTests()
    {
        _probe = new(() => _fixture.StageControlledTasks("Conf.AsyncEnumerable", ProbeType, Source, optimize: true));
        _bcl = new(() => _fixture.CompileUninstrumented("Conf.AsyncEnumerableBcl", ProbeType, Source));
    }

    [Theory]
    [InlineData("Query")]
    [InlineData("QueryInStartedTask")]
    public async Task OperatorsRunEveryStepOnTheLogicalThread(string scenario)
    {
        string expected = await (Task<string>)_bcl.Value.Method("Query").Invoke(null, [(Action)(() => { })])!;
        using var host = new SimulationHost(Start);
        var observed = new ConcurrentQueue<bool>();

        object? task = host.Invoke(_probe.Value.Method(scenario), (Action)(() => observed.Enqueue(host.Scheduler.IsSimulationThread)));

        var typed = Assert.IsType<Task<string>>(task, exactMatch: false);
        Assert.Equal(TaskStatus.RanToCompletion, typed.Status);
        Assert.Equal(expected, await typed);
        Assert.NotEmpty(observed);
        Assert.All(observed, Assert.True);
    }

    public void Dispose() => _fixture.Dispose();
}
