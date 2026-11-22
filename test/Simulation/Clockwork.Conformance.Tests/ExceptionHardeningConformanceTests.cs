using System.Reflection;

namespace Orleans.Testing.Simulation.Conformance.Tests;

/// <summary>
/// End-to-end conformance for exception-handler hardening. Every fixture in this suite is
/// rewritten with <c>HardenExceptionHandlers</c> enabled, so a guard is injected at the start of each broad
/// <c>catch</c> / exception <c>filter</c>. These tests prove the guard is transparent to ordinary
/// application exception handling: a rewritten broad <c>catch (Exception)</c>, a filtered catch, a
/// try/finally, and a nested handler all still observe, filter, run cleanup for, and recover from normal
/// exceptions exactly as written - the hardening only intercepts the scheduler's internal control signal,
/// which never reaches user code under the cooperative cluster drive.
/// </summary>
public sealed class ExceptionHardeningConformanceTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string Source = """
        using System;
        using System.Threading.Tasks;
        namespace Conf { public static class HardenProbe {
            private static void Boom() => throw new InvalidOperationException("boom");

            public static Task<int> BroadCatchHandlesNormalException()
            {
                try { Boom(); return Task.FromResult(-1); }
                catch (Exception) { return Task.FromResult(7); }
            }

            public static Task<string> BroadCatchObservesTheException()
            {
                try { Boom(); return Task.FromResult("unreached"); }
                catch (Exception e) { return Task.FromResult(e.Message); }
            }

            public static Task<int> FilterSelectsMatchingException()
            {
                try { Boom(); return Task.FromResult(-1); }
                catch (InvalidOperationException e) when (e.Message == "boom") { return Task.FromResult(11); }
                catch (Exception) { return Task.FromResult(-2); }
            }

            public static Task<int> FinallyStillRuns()
            {
                int n = 0;
                try { Boom(); }
                catch (Exception) { n += 1; }
                finally { n += 10; }
                return Task.FromResult(n);
            }

            public static Task<int> NestedHandlersUnwindNormally()
            {
                int n = 0;
                try
                {
                    try { Boom(); }
                    catch (Exception) { n += 1; throw new FormatException("re"); }
                }
                catch (FormatException) { n += 100; }
                return Task.FromResult(n);
            }

            public static Task<bool> UncaughtExceptionStillPropagates()
            {
                try { Boom(); return Task.FromResult(false); }
                catch (FormatException) { return Task.FromResult(false); }
            }
        } }
        """;

    private readonly RewriteFixture _fixture = new();
    private readonly Lazy<StagedProbe> _probe;

    public ExceptionHardeningConformanceTests() =>
        _probe = new Lazy<StagedProbe>(() =>
            _fixture.StageControlledTasks("Conf.Harden", "Conf.HardenProbe", Source, optimize: true));

    [Fact]
    public void BroadCatchStillHandlesANormalException()
    {
        using var host = new SimulationHost(Start);
        var task = (Task<int>)host.Invoke(Method("BroadCatchHandlesNormalException"))!;
        Assert.Equal(7, Result<int>(task));
    }

    [Fact]
    public void BroadCatchStillObservesTheCaughtException()
    {
        using var host = new SimulationHost(Start);
        var task = (Task<string>)host.Invoke(Method("BroadCatchObservesTheException"))!;
        Assert.Equal("boom", Result<string>(task));
    }

    [Fact]
    public void ExceptionFilterStillSelectsTheMatchingHandler()
    {
        using var host = new SimulationHost(Start);
        var task = (Task<int>)host.Invoke(Method("FilterSelectsMatchingException"))!;
        Assert.Equal(11, Result<int>(task));
    }

    [Fact]
    public void FinallyBlockStillRunsAfterAHandledException()
    {
        using var host = new SimulationHost(Start);
        var task = (Task<int>)host.Invoke(Method("FinallyStillRuns"))!;
        Assert.Equal(11, Result<int>(task));
    }

    [Fact]
    public void NestedHandlersStillUnwindNormally()
    {
        using var host = new SimulationHost(Start);
        var task = (Task<int>)host.Invoke(Method("NestedHandlersUnwindNormally"))!;
        Assert.Equal(101, Result<int>(task));
    }

    [Fact]
    public void UnmatchedExceptionStillPropagatesOutOfARewrittenHandler()
    {
        using var host = new SimulationHost(Start);
        var ex = Assert.ThrowsAny<Exception>(() => host.Invoke(Method("UncaughtExceptionStillPropagates")));
        Assert.Equal("boom", Unwrap(ex).Message);
    }

    private MethodInfo Method(string name) => _probe.Value.Method(name);

    private static T Result<T>(object task)
    {
        var typed = (Task)task;
        Assert.True(typed.IsCompleted);
        PropertyInfo result = typed.GetType().GetProperty("Result")!;
        return (T)result.GetValue(typed)!;
    }

    private static Exception Unwrap(Exception ex)
    {
        while (ex is TargetInvocationException or AggregateException && ex.InnerException is not null)
        {
            ex = ex.InnerException;
        }

        return ex;
    }

    public void Dispose() => _fixture.Dispose();
}
