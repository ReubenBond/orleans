using System.Reflection;
using System.Threading.Tasks;

namespace Orleans.Testing.Simulation.Conformance.Tests;

/// <summary>
/// Focused stress conformance for the controlled async machinery: many controlled async operations
/// fan out and interleave through the scheduler. Two invariants are asserted deterministically
/// (no timing, no sleeps): every continuation observes the same controlled managed-thread identity -
/// proving work never escapes the logical simulation thread - and the
/// interleaving order is identical across independent runs under the same seed, proving the schedule is
/// reproducible rather than a thread-pool race.
/// </summary>
public sealed class AsyncStressConformanceTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // Workers is the fan-out; Rounds is the number of yield points per worker. Each resumption records
    // its worker index (to capture the interleaving) and its controlled managed-thread identity.
    private const int Workers = 8;
    private const int Rounds = 5;
    private const int Steps = Workers * Rounds;

    private const string Source = """
        using System.Threading.Tasks;
        namespace Conf { public static class StressProbe {
            public static Task Interleave(int workers, int rounds, int[] order, int[] threadIds, int[] cursor)
                => Impl(workers, rounds, order, threadIds, cursor, forceYielding: false);

            public static Task InterleaveForceYielding(int workers, int rounds, int[] order, int[] threadIds, int[] cursor)
                => Impl(workers, rounds, order, threadIds, cursor, forceYielding: true);

            private static async Task Impl(int workers, int rounds, int[] order, int[] threadIds, int[] cursor, bool forceYielding)
            {
                var tasks = new Task[workers];
                for (int i = 0; i < workers; i++) tasks[i] = Worker(i, rounds, order, threadIds, cursor, forceYielding);
                await Task.WhenAll(tasks);
            }

            private static async Task Worker(int index, int rounds, int[] order, int[] threadIds, int[] cursor, bool forceYielding)
            {
                for (int k = 0; k < rounds; k++)
                {
                    if (forceYielding) await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
                    else await Task.Yield();
                    int c = cursor[0]++;
                    order[c] = index;
                    threadIds[c] = System.Environment.CurrentManagedThreadId;
                }
            }
        } }
        """;

    private readonly RewriteFixture _fixture = new();
    private readonly Lazy<StagedProbe> _probe;

    public AsyncStressConformanceTests() =>
        _probe = new Lazy<StagedProbe>(() =>
            _fixture.StageControlledTasks("Conf.AsyncStress", "Conf.StressProbe", Source, optimize: true));

    [Fact]
    public void EveryContinuationRunsOnTheSameLogicalThread()
    {
        (int[] _, int[] threadIds) = RunInterleave(seed: 1);

        int expected = threadIds[0];
        Assert.All(threadIds, id => Assert.Equal(expected, id));
    }

    [Theory]
    [InlineData("Interleave")]
    [InlineData("InterleaveForceYielding")]
    public void InterleavingIsReproducibleUnderTheSameSeed(string probe)
    {
        (int[] first, int[] _) = RunInterleave(seed: 7, probe);
        (int[] second, int[] _) = RunInterleave(seed: 7, probe);

        Assert.Equal(first, second);
    }

    private (int[] Order, int[] ThreadIds) RunInterleave(int seed, string probe = "Interleave")
    {
        using var host = new SimulationHost(Start, seed);
        var order = new int[Steps];
        var threadIds = new int[Steps];
        var cursor = new int[1];

        var task = (Task)host.Invoke(_probe.Value.Method(probe), Workers, Rounds, order, threadIds, cursor)!;

        Assert.Equal(TaskStatus.RanToCompletion, task.Status);
        Assert.Equal(Steps, cursor[0]);
        return (order, threadIds);
    }

    public void Dispose() => _fixture.Dispose();
}
