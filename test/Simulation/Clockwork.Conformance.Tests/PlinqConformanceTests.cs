using System.Collections.Concurrent;
using System.Reflection;
using Orleans.Testing.Simulation.Runtime;

namespace Orleans.Testing.Simulation.Conformance.Tests;

/// <summary>
/// End-to-end conformance for the rejected PLINQ entry points. PLINQ runs a query on worker tasks of
/// <c>TaskScheduler.Default</c> and blocks its caller until they finish, so every <c>ParallelEnumerable.AsParallel</c>
/// overload and <c>ParallelEnumerable.Range</c>, <c>Repeat</c>, and <c>Empty</c> - the only ways to create a
/// <c>ParallelQuery&lt;T&gt;</c> - throw a <see cref="SimulationApiException"/> naming the API before the call runs.
/// Rewritten without the rules, a query runs its body on threads outside the simulation.
/// </summary>
public sealed class PlinqConformanceTests : IDisposable
{
    private const string ProbeType = "Conf.PlinqProbe";

    private static readonly DateTimeOffset Start = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string Source = """
        using System;
        using System.Collections;
        using System.Collections.Concurrent;
        using System.Collections.Generic;
        using System.Linq;

        namespace Conf
        {
            public static class PlinqProbe
            {
                public static int Create(string entryPoint, Action report)
                {
                    int[] items = Enumerable.Range(0, 64).ToArray();
                    ParallelQuery<int> query = entryPoint switch
                    {
                        "AsParallel" => items.AsParallel(),
                        "AsParallelNonGeneric" => ((IEnumerable)items).AsParallel().Cast<int>(),
                        "AsParallelPartitioner" => Partitioner.Create(items).AsParallel(),
                        "Range" => ParallelEnumerable.Range(0, 64),
                        "Repeat" => ParallelEnumerable.Repeat(1, 64),
                        "Empty" => ParallelEnumerable.Empty<int>(),
                        _ => throw new ArgumentOutOfRangeException(nameof(entryPoint)),
                    };

                    return query.WithDegreeOfParallelism(4).Select(item => { report(); return item; }).Count();
                }
            }
        }
        """;

    private readonly RewriteFixture _fixture = new();
    private readonly Lazy<StagedProbe> _probe;

    public PlinqConformanceTests() =>
        _probe = new(() => _fixture.StageControlledTasks("Conf.Plinq", ProbeType, Source, optimize: true));

    public static TheoryData<string, string> EntryPoints => new()
    {
        { "AsParallel", "AsParallel" },
        { "AsParallelNonGeneric", "AsParallel" },
        { "AsParallelPartitioner", "AsParallel" },
        { "Range", "Range" },
        { "Repeat", "Repeat" },
        { "Empty", "Empty" },
    };

    [Theory]
    [MemberData(nameof(EntryPoints))]
    public void EveryEntryPointIsRejectedBeforeTheQueryRuns(string entryPoint, string member)
    {
        using var host = new SimulationHost(Start);
        int reports = 0;

        var exception = Assert.Throws<SimulationApiException>(
            () => host.Invoke(_probe.Value.Method("Create"), entryPoint, (Action)(() => Interlocked.Increment(ref reports))));

        Assert.Equal(SimulationApiCategory.Parallel, exception.Category);
        Assert.Equal("System.Linq.ParallelEnumerable." + member, exception.ApiName);
        Assert.Equal(0, reports);
    }

    [Fact]
    public void WithoutThePlinqRulesAQueryRunsItsBodyOutsideTheSimulation()
    {
        StagedProbe escaping = _fixture.StageControlledTasksWithout(
            "Conf.PlinqEscape",
            ProbeType,
            Source,
            rule => rule.Id.StartsWith("clockwork.plinq.", StringComparison.Ordinal));
        // The guard is off: the escape this test pins runs the query on the thread pool.
        using var host = new SimulationHost(Start, allowThreadPoolEscapes: true);
        var observed = new ConcurrentQueue<bool>();

        // An array is range-partitioned, so the worker tasks of three of the four partitions each run their own range.
        object? count = host.Invoke(
            escaping.Method("Create"),
            "AsParallel",
            (Action)(() => observed.Enqueue(host.Scheduler.IsSimulationThread)));

        Assert.Equal(64, count);
        Assert.Contains(false, observed);
    }

    public void Dispose() => _fixture.Dispose();
}
