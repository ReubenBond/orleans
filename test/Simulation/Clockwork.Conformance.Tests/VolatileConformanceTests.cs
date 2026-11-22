using System.Reflection;
using System.Threading.Tasks;

namespace Orleans.Testing.Simulation.Conformance.Tests;

/// <summary>
/// End-to-end conformance for the controlled <see cref="System.Threading.Volatile"/> surface.
/// The rule set redirects every <c>Read</c>/<c>Write</c> overload (and the barriers) to a controlled shim
/// with the identical <c>ref</c>-first signature. A volatile access is indivisible on the cooperative
/// logical thread; outside simulation, rewritten calls fail before mutating ref state.
/// </summary>
public sealed class VolatileConformanceTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string Source = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        namespace Conf { public static class VolatileProbe {
            // A volatile write on a controlled thread is observed by a volatile read on the main thread
            // after a deterministic Join handoff (Join pumps the loop until the writer completes).
            public static Task<int> WritePublishesToReader()
            {
                int shared = 0;
                bool ready = false;
                var writer = new Thread(() => { shared = 99; Volatile.Write(ref ready, true); });
                writer.Start();
                writer.Join();
                bool published = Volatile.Read(ref ready);
                return Task.FromResult(published ? shared : -1);
            }

            // Read returns exactly the written value across representative primitive overloads.
            public static Task<bool> ReadWriteRoundTrip()
            {
                int i = 0; Volatile.Write(ref i, 42);
                long l = 0; Volatile.Write(ref l, 43);
                double d = 0; Volatile.Write(ref d, 2.5);
                bool b = false; Volatile.Write(ref b, true);
                bool ok = Volatile.Read(ref i) == 42 && Volatile.Read(ref l) == 43L
                       && Volatile.Read(ref d) == 2.5 && Volatile.Read(ref b);
                return Task.FromResult(ok);
            }

            public static void WriteRef(ref int location) => Volatile.Write(ref location, 42);

            // The generic reference overloads publish and acquire an object by identity.
            public static Task<bool> GenericReferenceRoundTrip()
            {
                var value = new object();
                object slot = null;
                Volatile.Write(ref slot, value);
                bool ok = ReferenceEquals(Volatile.Read(ref slot), value);
                return Task.FromResult(ok);
            }

            // The acquire/release barriers are controlled and do not throw.
            public static Task<bool> BarriersRun()
            {
                Volatile.ReadBarrier();
                Volatile.WriteBarrier();
                return Task.FromResult(true);
            }
        } }
        """;

    private readonly RewriteFixture _fixture = new();
    private readonly Lazy<StagedProbe> _probe;

    public VolatileConformanceTests() =>
        _probe = new Lazy<StagedProbe>(() =>
            _fixture.StageControlledTasks("Conf.Volatile", "Conf.VolatileProbe", Source, optimize: true));

    [Fact]
    public void WritePublishesToReader()
    {
        using var host = new SimulationHost(Start);
        var task = (Task<int>)host.Invoke(Method("WritePublishesToReader"))!;
        Assert.Equal(99, Result<int>(task));
    }

    [Fact]
    public void ReadWriteRoundTrip()
    {
        using var host = new SimulationHost(Start);
        var task = (Task<bool>)host.Invoke(Method("ReadWriteRoundTrip"))!;
        Assert.True(Result<bool>(task));
    }

    [Fact]
    public void GenericReferenceRoundTrip()
    {
        using var host = new SimulationHost(Start);
        var task = (Task<bool>)host.Invoke(Method("GenericReferenceRoundTrip"))!;
        Assert.True(Result<bool>(task));
    }

    [Fact]
    public void BarriersRun()
    {
        using var host = new SimulationHost(Start);
        var task = (Task<bool>)host.Invoke(Method("BarriersRun"))!;
        Assert.True(Result<bool>(task));
    }

    [Fact]
    public async Task OnlyRewrittenVolatileRequiresActiveSimulationWithoutMutatingRefState()
    {
        UninstrumentedProbe uninstrumented = _fixture.CompileUninstrumented(
            "Conf.UninstrumentedVolatile",
            "Conf.VolatileProbe",
            Source);
        var task = (Task<bool>)uninstrumented.Method("ReadWriteRoundTrip").Invoke(null, null)!;
        Assert.True(await task);

        object?[] args = [7];
        SimulationNotActiveExceptionAssert.Throws(Method("WriteRef"), args);
        Assert.Equal(7, args[0]);
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
}
