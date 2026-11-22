using System.Reflection;
using System.Threading.Tasks;

namespace Orleans.Testing.Simulation.Conformance.Tests;

/// <summary>
/// End-to-end conformance for the controlled <see cref="System.Threading.Thread"/> surface.
/// Once a fixture is rewritten with the controlled-task rule set, <c>new Thread(...)</c> becomes a real
/// thread object whose body is queued as a fresh controlled operation, <c>Start</c> schedules it on the
/// single logical thread, and <c>Join</c> pumps the deterministic cluster drive until it terminates —
/// proving the constructor/instance/static rule shapes resolve at real call sites and run deterministically.
/// </summary>
public sealed class ThreadConformanceTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string Source = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        namespace Conf { public static class ThreadProbe {
            // A single controlled thread mutates shared state; Join observes its termination.
            public static Task<int> StartAndJoin()
            {
                int result = 0;
                var t = new Thread(() => result = 42);
                t.Start();
                t.Join();
                return Task.FromResult(result);
            }

            public static void StartActionOnly(int[] sink)
            {
                var t = new Thread(() => sink[0] = 42);
                t.Start();
            }

            // Several controlled threads run cooperatively on the single logical thread; all are joined.
            public static Task<int> ManyThreads()
            {
                int total = 0;
                var threads = new Thread[4];
                for (int i = 0; i < threads.Length; i++)
                {
                    int local = i + 1;
                    threads[i] = new Thread(() => total += local);
                }
                foreach (var t in threads) t.Start();
                foreach (var t in threads) t.Join();
                return Task.FromResult(total);
            }

            // A parameterized thread receives its start argument.
            public static Task<string> Parameterized()
            {
                string captured = null;
                var t = new Thread(o => captured = (string)o);
                t.Start("payload");
                t.Join();
                return Task.FromResult(captured);
            }

            // Joining the same thread repeatedly observes the single termination without re-running the body.
            public static Task<int> RepeatedJoins()
            {
                int count = 0;
                var t = new Thread(() => count++);
                t.Start();
                t.Join();
                t.Join();
                t.Join();
                return Task.FromResult(count);
            }

            // A faulting thread body is an unhandled exception that faults its controlled operation;
            // Join observes the terminated thread and neither throws nor hangs.
            public static Task<bool> FaultingThreadJoins()
            {
                bool reached = false;
                var t = new Thread(() => { reached = true; throw new InvalidOperationException("boom"); });
                t.Start();
                t.Join();
                return Task.FromResult(reached);
            }

            // Thread.Sleep is a cooperative no-op inside a simulation (never blocks or uses real time).
            public static Task<int> SleepDoesNotBlock()
            {
                int result = 0;
                var t = new Thread(() => { Thread.Sleep(60_000); result = 5; });
                t.Start();
                t.Join();
                return Task.FromResult(result);
            }

            // OS thread priority is rejected precisely inside a simulation.
            public static Task<bool> RejectsPriority()
            {
                var t = new Thread(() => { });
                try
                {
                    t.Priority = ThreadPriority.Highest;
                    return Task.FromResult(false);
                }
                catch (Exception ex) when (ex.GetType().Name == "SimulationApiException")
                {
                    return Task.FromResult(true);
                }
            }

            public static Task UnstartedJoin()
            {
                var t = new Thread(() => { });
                t.Join();
                return Task.CompletedTask;
            }

            public static Task<int> JoinZeroSnapshot()
            {
                bool ran = false;
                var t = new Thread(() => ran = true);
                t.Start();
                bool joined = t.Join(0);
                int snapshot = (joined ? 2 : 0) | (ran ? 1 : 0);
                return Task.FromResult(snapshot);
            }

            public static Task JoinIntegerValidation(int millisecondsTimeout)
            {
                var t = new Thread(() => { });
                t.Start();
                t.Join();
                _ = t.Join(millisecondsTimeout);
                return Task.CompletedTask;
            }

            public static Task JoinTimeSpanValidation(long millisecondsTimeout)
            {
                var t = new Thread(() => { });
                t.Start();
                t.Join();
                _ = t.Join(TimeSpan.FromMilliseconds(millisecondsTimeout));
                return Task.CompletedTask;
            }

            public static Task SleepIntegerValidation(int millisecondsTimeout)
            {
                Thread.Sleep(millisecondsTimeout);
                return Task.CompletedTask;
            }

            public static Task SleepTimeSpanValidation(long millisecondsTimeout)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(millisecondsTimeout));
                return Task.CompletedTask;
            }

            public static Task ThreadStartParameterMismatch()
            {
                var t = new Thread(new ThreadStart(() => { }));
                t.Start(new object());
                return Task.CompletedTask;
            }
        } }
        """;

    private readonly RewriteFixture _fixture = new();
    private readonly Lazy<StagedProbe> _probe;

    public ThreadConformanceTests() =>
        _probe = new Lazy<StagedProbe>(() =>
            _fixture.StageControlledTasks("Conf.Thread", "Conf.ThreadProbe", Source, optimize: true));

    [Fact]
    public void SingleThreadStartsAndJoinsUnderTheClusterDrive()
    {
        using var host = new SimulationHost(Start);
        var task = (Task<int>)host.Invoke(Method("StartAndJoin"))!;
        Assert.Equal(42, Result<int>(task));
    }

    [Fact]
    public void ManyThreadsAllRunAndJoinDeterministically()
    {
        using var host = new SimulationHost(Start);
        var task = (Task<int>)host.Invoke(Method("ManyThreads"))!;
        Assert.Equal(10, Result<int>(task));
    }

    [Fact]
    public void ParameterizedThreadReceivesArgument()
    {
        using var host = new SimulationHost(Start);
        var task = (Task<string>)host.Invoke(Method("Parameterized"))!;
        Assert.Equal("payload", Result<string>(task));
    }

    [Fact]
    public void RepeatedJoinsObserveSingleTermination()
    {
        using var host = new SimulationHost(Start);
        var task = (Task<int>)host.Invoke(Method("RepeatedJoins"))!;
        Assert.Equal(1, Result<int>(task));
    }

    [Fact]
    public void FaultingThreadJoinsWithoutTearingDownTheHost()
    {
        using var host = new SimulationHost(Start);
        var task = (Task<bool>)host.Invoke(Method("FaultingThreadJoins"))!;
        Assert.True(Result<bool>(task));
    }

    [Fact]
    public void SleepInsideAThreadDoesNotBlockTheDrive()
    {
        using var host = new SimulationHost(Start);
        var task = (Task<int>)host.Invoke(Method("SleepDoesNotBlock"))!;
        Assert.Equal(5, Result<int>(task));
    }

    [Fact]
    public void PriorityIsRejectedInsideSimulation()
    {
        using var host = new SimulationHost(Start);
        var task = (Task<bool>)host.Invoke(Method("RejectsPriority"))!;
        Assert.True(Result<bool>(task));
    }

    [Fact]
    public async Task OnlyRewrittenThreadRequiresActiveSimulationWithoutStartingItsAction()
    {
        UninstrumentedProbe uninstrumented = _fixture.CompileUninstrumented(
            "Conf.UninstrumentedThread",
            "Conf.ThreadProbe",
            Source);
        var task = (Task<int>)uninstrumented.Method("StartAndJoin").Invoke(null, null)!;
        Assert.Equal(42, await task);

        int[] sink = [0];
        SimulationNotActiveExceptionAssert.Throws(Method("StartActionOnly"), sink);
        Assert.Equal(0, sink[0]);
    }

    [Fact]
    public void UnstartedJoinThrowsThreadStateException()
    {
        using var host = new SimulationHost(Start);

        var exception = Assert.Throws<ThreadStateException>(() => host.Invoke(Method("UnstartedJoin")));

        Assert.IsType<ThreadStateException>(exception);
    }

    [Fact]
    public void JoinZeroReturnsFalseBeforeTheQueuedBodyRuns()
    {
        using var host = new SimulationHost(Start);

        var task = (Task<int>)host.Invoke(Method("JoinZeroSnapshot"))!;

        Assert.Equal(0, Result<int>(task));
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(int.MinValue)]
    public void JoinIntegerValidationMatchesBcl(int millisecondsTimeout)
    {
        using var host = new SimulationHost(Start);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => { _ = host.Invoke(Method("JoinIntegerValidation"), millisecondsTimeout); });

        Assert.IsType<ArgumentOutOfRangeException>(exception);
        Assert.Equal("millisecondsTimeout", exception.ParamName);
    }

    [Theory]
    [InlineData(-2L)]
    [InlineData((long)int.MaxValue + 1)]
    public void JoinTimeSpanValidationMatchesBcl(long millisecondsTimeout)
    {
        using var host = new SimulationHost(Start);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => { _ = host.Invoke(Method("JoinTimeSpanValidation"), millisecondsTimeout); });

        Assert.IsType<ArgumentOutOfRangeException>(exception);
        Assert.Equal("timeout", exception.ParamName);
    }

    [Theory]
    [InlineData("integer", -2L, "millisecondsTimeout")]
    [InlineData("integer", int.MinValue, "millisecondsTimeout")]
    [InlineData("timespan", -2L, "timeout")]
    [InlineData("timespan", (long)int.MaxValue + 1, "timeout")]
    public void SleepValidationMatchesBcl(string overload, long millisecondsTimeout, string expectedParamName)
    {
        using var host = new SimulationHost(Start);
        MethodInfo method;
        object argument;
        if (overload == "integer")
        {
            method = Method("SleepIntegerValidation");
            argument = checked((int)millisecondsTimeout);
        }
        else
        {
            method = Method("SleepTimeSpanValidation");
            argument = millisecondsTimeout;
        }

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => { _ = host.Invoke(method, argument); });

        Assert.IsType<ArgumentOutOfRangeException>(exception);
        Assert.Equal(expectedParamName, exception.ParamName);
    }

    [Fact]
    public void ThreadStartParameterMismatchThrowsInvalidOperationException()
    {
        using var host = new SimulationHost(Start);

        var exception = Assert.Throws<InvalidOperationException>(
            () => { _ = host.Invoke(Method("ThreadStartParameterMismatch")); });

        Assert.IsType<InvalidOperationException>(exception);
    }

    private MethodInfo Method(string name) => _probe.Value.Method(name);

    private static T Result<T>(object task)
    {
        var typed = (Task)task;
        Assert.True(typed.IsCompleted);
        PropertyInfo result = typed.GetType().GetProperty("Result")!;
        return (T)result.GetValue(typed)!;
    }

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void JoinZeroSnapshotPinsPrePumpAndPostDriveState()
    {
        using var host = new SimulationHost(Start);

        var task = (Task<int[]>)host.Invoke(JoinZeroDepthMethod("JoinZeroDetailedSnapshot"))!;
        int[] observation = Result<int[]>(task);

        Assert.Equal(0x5A00, observation[0]);
        Assert.Equal(0, observation[0] & 0x2);
        Assert.Equal(0, observation[0] & 0x1);
        Assert.Equal(0x2A, observation[1]);
    }

    private const string JoinZeroDepthSource = """
        using System.Threading;
        using System.Threading.Tasks;
        namespace Conf { public static class ThreadJoinZeroDepthProbe {
            public static Task<int[]> JoinZeroDetailedSnapshot()
            {
                const int Sentinel = 0x5A00;
                var observation = new[] { -1, -1 };
                var thread = new Thread(() => observation[1] = 0x2A);
                thread.Start();
                bool joined = thread.Join(0);
                observation[0] =
                    Sentinel |
                    (joined ? 0x2 : 0) |
                    (observation[1] == 0x2A ? 0x1 : 0);
                return Task.FromResult(observation);
            }
        } }
        """;

    private StagedProbe? _joinZeroDepthProbe;

    private MethodInfo JoinZeroDepthMethod(string name) =>
        (_joinZeroDepthProbe ??= _fixture.StageControlledTasks(
            "Conf.ThreadJoinZeroDepth",
            "Conf.ThreadJoinZeroDepthProbe",
            JoinZeroDepthSource,
            optimize: true)).Method(name);
}
