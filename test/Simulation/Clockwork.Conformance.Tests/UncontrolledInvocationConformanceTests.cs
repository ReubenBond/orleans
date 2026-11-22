using System.Reflection;

namespace Orleans.Testing.Simulation.Conformance.Tests;

/// <summary>
/// End-to-end conformance for the uncontrolled-invocation rejection surface. Once a
/// fixture is rewritten with the controlled-task rule set, real <see cref="System.Diagnostics.Process"/>
/// and <see cref="System.Environment"/> control call sites throw a
/// <see cref="Orleans.Testing.Simulation.Runtime.UncontrolledInvocationException"/> that names the exact API - the rewriter
/// injected a throwing guard before the original call, so a rewritten assembly can never launch, kill, wait
/// on, or terminate a real OS process. Unlike the controlled shims, the rejection is unconditional (these
/// APIs cannot be modelled at all), so it fires whether or not a simulation is active.
/// </summary>
public sealed class UncontrolledInvocationConformanceTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string Source = """
        using System;
        using System.Diagnostics;
        namespace Conf { public static class UncontrolledProbe {
            public static int StartProcess()
            {
                Process.Start("app.exe");
                return 0;
            }

            public static int ExitHost()
            {
                Environment.Exit(3);
                return 0;
            }

            public static int FailFastHost()
            {
                Environment.FailFast("boom");
                return 0;
            }
        } }
        """;

    private readonly RewriteFixture _fixture = new();
    private readonly Lazy<StagedProbe> _probe;

    public UncontrolledInvocationConformanceTests() =>
        _probe = new Lazy<StagedProbe>(() =>
            _fixture.StageControlledTasks("Conf.Uncontrolled", "Conf.UncontrolledProbe", Source, optimize: true));

    [Fact]
    public void ProcessStartIsRejectedUnderSimulation()
    {
        using var host = new SimulationHost(Start);
        var ex = Assert.ThrowsAny<Exception>(() => host.Invoke(Method("StartProcess")));
        AssertUncontrolled(ex);
    }

    [Fact]
    public void EnvironmentExitIsRejectedUnderSimulation()
    {
        using var host = new SimulationHost(Start);
        var ex = Assert.ThrowsAny<Exception>(() => host.Invoke(Method("ExitHost")));
        AssertUncontrolled(ex);
    }

    [Fact]
    public void EnvironmentFailFastIsRejectedUnderSimulation()
    {
        using var host = new SimulationHost(Start);
        var ex = Assert.ThrowsAny<Exception>(() => host.Invoke(Method("FailFastHost")));
        AssertUncontrolled(ex);
    }

    [Fact]
    public void RejectionIsUnconditionalOutsideAnySimulation()
    {
        // Uncontrolled APIs cannot be modelled, so a rewritten assembly is never allowed to reach them.
        var ex = Assert.ThrowsAny<Exception>(() => Method("StartProcess").Invoke(null, null));
        AssertUncontrolled(ex);
    }

    private MethodInfo Method(string name) => _probe.Value.Method(name);

    private static void AssertUncontrolled(Exception ex)
    {
        Exception unwrapped = Unwrap(ex);
        Assert.Equal(
            "Orleans.Testing.Simulation.Runtime.UncontrolledInvocationException",
            unwrapped.GetType().FullName);
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
