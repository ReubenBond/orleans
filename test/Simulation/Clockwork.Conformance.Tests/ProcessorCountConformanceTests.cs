using System.Reflection;
using System.Threading.Tasks;

namespace Orleans.Testing.Simulation.Conformance.Tests;

/// <summary>
/// End-to-end conformance for the simulated processor count: under simulation <see cref="Environment.ProcessorCount"/>
/// returns the cluster's count (4 unless configured), the <see cref="Parallel"/> loops without a limit run that many
/// workers, and <c>Partitioner.Create(fromInclusive, toExclusive)</c> sizes its ranges from it with the BCL's formula,
/// whatever the host's core count. Probes rewritten without the rules show the escapes the rules close: the host's
/// count, and ranges sized by it. A BCL probe pins the facts the rules rely on: the range formula is the BCL's, and the
/// <see cref="IEnumerable{T}"/> partitioner, which stays a BCL call, reads its source only for the chunks it hands out.
/// </summary>
public sealed class ProcessorCountConformanceTests : IDisposable
{
    private const string ProbeType = "Conf.ProcessorCountProbe";
    private const string ProcessorCountRule = "clockwork.environment.processorcount";
    private const string PartitionerRulePrefix = "clockwork.parallel.partitioner.create";

    private static readonly DateTimeOffset Start = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string Source = """
        using System;
        using System.Collections.Concurrent;
        using System.Collections.Generic;
        using System.Linq;
        using System.Threading;
        using System.Threading.Tasks;

        namespace Conf
        {
            public static class ProcessorCountProbe
            {
                public static int ProcessorCount() => Environment.ProcessorCount;

                // The ranges of the int and long range partitioners over [0, count), as "from-to" pairs.
                public static string[] Ranges(int count, bool wide)
                {
                    IEnumerable<string> ranges = wide
                        ? Partitioner.Create(0L, (long)count).GetDynamicPartitions().Select(range => range.Item1 + "-" + range.Item2)
                        : Partitioner.Create(0, count).GetDynamicPartitions().Select(range => range.Item1 + "-" + range.Item2);
                    return ranges.ToArray();
                }

                // The most bodies in flight at once in a loop sized by the processor count.
                public static Task<int> MaxInFlight(string loop, int count)
                {
                    int inFlight = 0;
                    int max = 0;
                    Action<long> body = item =>
                    {
                        inFlight++;
                        max = Math.Max(max, inFlight);
                        Thread.Sleep(5);
                        inFlight--;
                    };
                    switch (loop)
                    {
                        case "For": Parallel.For(0, count, i => body(i)); break;
                        case "For(MaxDegreeOfParallelism = -1)":
                            Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = -1 }, i => body(i));
                            break;
                        case "For(MaxDegreeOfParallelism = Environment.ProcessorCount)":
                            Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, i => body(i));
                            break;
                        case "ForEachAsync":
                            return RunAsync(count, body, () => max);
                        default: throw new ArgumentOutOfRangeException(nameof(loop), loop, null);
                    }

                    return Task.FromResult(max);
                }

                // Two dynamic partitions of an enumerable partitioner take turns; each entry is the key taken and the
                // number of source elements read so far.
                public static string[] EnumerableReads(int steps)
                {
                    int reads = 0;
                    IEnumerable<int> Source()
                    {
                        for (int i = 0; i < 10_000; i++)
                        {
                            reads++;
                            yield return i;
                        }
                    }

                    IEnumerable<KeyValuePair<long, int>> partitions = Partitioner.Create(Source()).GetOrderableDynamicPartitions();
                    IEnumerator<KeyValuePair<long, int>>[] enumerators = { partitions.GetEnumerator(), partitions.GetEnumerator() };
                    var log = new List<string>();
                    for (int step = 0; step < steps; step++)
                    {
                        IEnumerator<KeyValuePair<long, int>> partition = enumerators[step % 2];
                        if (!partition.MoveNext()) break;
                        log.Add(partition.Current.Key + ":" + reads);
                    }

                    return log.ToArray();
                }

                private static async Task<int> RunAsync(int count, Action<long> body, Func<int> max)
                {
                    await Parallel.ForEachAsync(Enumerable.Range(0, count), async (item, ct) =>
                    {
                        body(item);
                        await Task.Yield();
                    });
                    return max();
                }
            }
        }
        """;

    private readonly RewriteFixture _fixture = new();
    private readonly Lazy<StagedProbe> _controlled;
    private readonly Lazy<UninstrumentedProbe> _bcl;

    public ProcessorCountConformanceTests()
    {
        _controlled = new(() => _fixture.StageControlledTasks("Conf.ProcessorCount", ProbeType, Source, optimize: true));
        _bcl = new(() => _fixture.CompileUninstrumented("Conf.ProcessorCountBcl", ProbeType, Source));
    }

    [Theory]
    [InlineData(null, 4)]
    [InlineData(7, 7)]
    [InlineData(1, 1)]
    public void ProcessorCountIsTheSimulatedCount(int? configured, int expected)
    {
        using var host = new SimulationHost(Start, processorCount: configured);

        Assert.Equal(expected, host.Invoke(_controlled.Value.Method("ProcessorCount")));
    }

    [Theory]
    [InlineData("For", null, 4)]
    [InlineData("For", 2, 2)]
    [InlineData("For", 6, 6)]
    [InlineData("For(MaxDegreeOfParallelism = -1)", 6, 6)]
    [InlineData("For(MaxDegreeOfParallelism = Environment.ProcessorCount)", 6, 6)]
    [InlineData("ForEachAsync", null, 4)]
    [InlineData("ForEachAsync", 3, 3)]
    public void LoopsSizedByTheProcessorCountRunTheSimulatedCountOfWorkers(string loop, int? configured, int expected)
    {
        using var host = new SimulationHost(Start, processorCount: configured);

        object? maxInFlight = host.Invoke(_controlled.Value.Method("MaxInFlight"), loop, 12);

        Assert.Equal(expected, Completed<int>(maxInFlight));
    }

    [Theory]
    [InlineData(null, 4, false)]
    [InlineData(null, 4, true)]
    [InlineData(7, 7, false)]
    [InlineData(7, 7, true)]
    public void RangePartitionerSizesItsRangesByTheSimulatedCount(int? configured, int processorCount, bool wide)
    {
        using var host = new SimulationHost(Start, processorCount: configured);

        var ranges = (string[])host.Invoke(_controlled.Value.Method("Ranges"), 100, wide)!;

        Assert.Equal(BclRanges(100, processorCount), ranges);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheRangeOracleIsTheBclsFormula(bool wide)
    {
        int count = RangeCountDistinguishing(Environment.ProcessorCount);

        var ranges = (string[])_bcl.Value.Method("Ranges").Invoke(null, [count, wide])!;

        Assert.Equal(BclRanges(count, Environment.ProcessorCount), ranges);
    }

    [Fact]
    public void WithoutTheRuleProcessorCountReadsTheHost()
    {
        StagedProbe escaping = _fixture.StageControlledTasksWithout(
            "Conf.ProcessorCountEscape", ProbeType, Source, rule => rule.Id == ProcessorCountRule);
        int simulated = Environment.ProcessorCount + 1;
        using var host = new SimulationHost(Start, processorCount: simulated);

        Assert.Equal(Environment.ProcessorCount, host.Invoke(escaping.Method("ProcessorCount")));
        Assert.Equal(simulated, host.Invoke(_controlled.Value.Method("ProcessorCount")));
    }

    [Fact]
    public void WithoutThePartitionerRulesRangesFollowTheHost()
    {
        StagedProbe escaping = _fixture.StageControlledTasksWithout(
            "Conf.PartitionerEscape",
            ProbeType,
            Source,
            rule => rule.Id.StartsWith(PartitionerRulePrefix, StringComparison.Ordinal));
        int simulated = Environment.ProcessorCount + 1;
        int count = RangeCountDistinguishing(Environment.ProcessorCount);
        using var host = new SimulationHost(Start, processorCount: simulated);

        Assert.Equal(BclRanges(count, Environment.ProcessorCount), (string[])host.Invoke(escaping.Method("Ranges"), count, false)!);
        Assert.Equal(BclRanges(count, simulated), (string[])host.Invoke(_controlled.Value.Method("Ranges"), count, false)!);
    }

    // The BCL sizes Create(IEnumerable)'s fill buffer by the processor count but never fills it, which is why the
    // factory stays a BCL call: each partition reads the source only for the chunk it takes, with chunk sizes that
    // depend on nothing but the number of grabs. This holds on this host whatever its core count.
    [Fact]
    public void EnumerablePartitionerReadsTheSourceOnlyForTheChunksItTakes()
    {
        const int steps = 400;

        var log = (string[])_bcl.Value.Method("EnumerableReads").Invoke(null, [steps])!;

        Assert.Equal(EnumerableReadsWithoutReadAhead(steps), log);
    }

    // The BCL's range partitioner: Create(from, to) divides [from, to) into ranges of (to - from) / (processors * 3),
    // or 1 if that is 0.
    private static string[] BclRanges(int count, int processorCount)
    {
        int rangeSize = Math.Max(1, (int)((long)count / (processorCount * 3L)));
        var ranges = new List<string>();
        for (int from = 0; from < count; from += rangeSize)
        {
            ranges.Add(from + "-" + Math.Min(from + rangeSize, count));
        }

        return [.. ranges];
    }

    // A range wide enough that processorCount and processorCount + 1 give different range sizes.
    private static int RangeCountDistinguishing(int processorCount) => 3 * processorCount * (processorCount + 1) * 2;

    // The BCL's dynamic-partition chunking with no read-ahead: each partition's chunks are 1, then the same size for
    // three grabs before doubling, capped at the default chunk size for a value type (128), and a grab reads exactly
    // its chunk from the shared source.
    private static string[] EnumerableReadsWithoutReadAhead(int steps)
    {
        const int maxChunkSize = 128;
        int reads = 0;
        var partitions = new (long Next, int Remaining, int ChunkSize, int Countdown)[2];
        var log = new List<string>();
        for (int step = 0; step < steps; step++)
        {
            ref var partition = ref partitions[step % 2];
            if (partition.Remaining == 0)
            {
                int size = partition.ChunkSize == 0 ? 1
                    : partition.Countdown > 0 ? partition.ChunkSize
                    : Math.Min(partition.ChunkSize * 2, maxChunkSize);
                partition.Countdown = partition.ChunkSize == 0 || partition.Countdown == 0 ? 2 : partition.Countdown - 1;
                partition.ChunkSize = size;
                partition.Next = reads;
                partition.Remaining = size;
                reads += size;
            }

            log.Add(partition.Next + ":" + reads);
            partition.Next++;
            partition.Remaining--;
        }

        return [.. log];
    }

    private static T Completed<T>(object? task)
    {
        var typed = Assert.IsAssignableFrom<Task<T>>(task);
        Assert.Equal(TaskStatus.RanToCompletion, typed.Status);
        return typed.Result;
    }

    public void Dispose() => _fixture.Dispose();
}
