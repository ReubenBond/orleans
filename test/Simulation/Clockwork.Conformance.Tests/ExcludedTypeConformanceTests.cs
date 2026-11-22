using System.Reflection;

namespace Orleans.Testing.Simulation.Conformance.Tests;

/// <summary>
/// A type marked <c>[DoNotRewrite]</c> is excluded from rewriting together with the compiler-generated types nested in
/// it: its async and iterator state machines and its lambda closures, including an async lambda's state machine nested
/// in its closure. Its methods therefore run with the BCL's behaviour, uncontrolled and without an active simulation,
/// while the rest of the assembly is rewritten. Each probe runs from the rewritten assembly and from an uninstrumented
/// build, and the two must produce the same result.
/// </summary>
public sealed class ExcludedTypeConformanceTests : IDisposable
{
    private const string Source = """
        using System;
        using System.Collections.Generic;
        using System.Runtime.CompilerServices;
        using System.Threading;
        using System.Threading.Tasks;
        namespace Orleans.Testing.Simulation.Instrumentation.Attributes
        {
            [AttributeUsage(AttributeTargets.Class)]
            public sealed class DoNotRewriteAttribute(string reason) : Attribute
            {
                public string Reason { get; } = reason;
            }
        }
        namespace Conf {
            [Orleans.Testing.Simulation.Instrumentation.Attributes.DoNotRewrite("Runs outside the simulation.")]
            public static class LegacyProbe
            {
                // The BCL awaiters resume on thread-pool threads, and Task.Delay waits on a real timer.
                public static async Task<string> AwaitAsync()
                {
                    var log = new List<string>();
                    await Task.Yield();
                    log.Add("yield pool=" + Thread.CurrentThread.IsThreadPoolThread);
                    await Task.Delay(1).ConfigureAwait(false);
                    log.Add("delay pool=" + Thread.CurrentThread.IsThreadPoolThread);
                    return string.Join("|", log);
                }

                public static Task<string> Iterate() => Task.FromResult(string.Join("|", Numbers(3)));

                public static async Task<string> IterateAsync()
                {
                    var log = new List<string>();
                    await foreach (int item in NumbersAsync(3).ConfigureAwait(false))
                    {
                        log.Add(item + " pool=" + Thread.CurrentThread.IsThreadPoolThread);
                    }

                    return string.Join("|", log);
                }

                public static async Task<string> Lambda()
                {
                    int offset = 40;
                    Func<int, int> add = value => value + offset;
                    Func<int, int> twice = static value => value * 2;
                    Func<Task<int>> later = async () =>
                    {
                        await Task.Yield();
                        return add(1);
                    };
                    return twice(await later()).ToString();
                }

                private static IEnumerable<int> Numbers(int count)
                {
                    for (int i = 0; i < count; i++)
                    {
                        yield return i;
                    }
                }

                private static async IAsyncEnumerable<int> NumbersAsync(int count)
                {
                    for (int i = 0; i < count; i++)
                    {
                        await Task.Yield();
                        yield return i;
                    }
                }
            }

            public static class RewrittenProbe
            {
                public static Task Delay() => Task.Delay(1);
            }
        }
        """;

    private readonly RewriteFixture _fixture = new();
    private readonly Lazy<StagedProbe> _release;
    private readonly Lazy<StagedProbe> _debug;
    private readonly Lazy<UninstrumentedProbe> _bclRelease;
    private readonly Lazy<UninstrumentedProbe> _bclDebug;

    public ExcludedTypeConformanceTests()
    {
        _release = new(() => _fixture.StageControlledTasks("Conf.ExcludedRel", "Conf.LegacyProbe", Source, optimize: true));
        _debug = new(() => _fixture.StageControlledTasks("Conf.ExcludedDbg", "Conf.LegacyProbe", Source, optimize: false));
        _bclRelease = new(() => _fixture.CompileUninstrumented("Conf.ExcludedBclRel", "Conf.LegacyProbe", Source, optimize: true));
        _bclDebug = new(() => _fixture.CompileUninstrumented("Conf.ExcludedBclDbg", "Conf.LegacyProbe", Source, optimize: false));
    }

    public static TheoryData<bool, string, string> Probes
    {
        get
        {
            var data = new TheoryData<bool, string, string>();
            foreach (bool optimize in new[] { true, false })
            {
                data.Add(optimize, "AwaitAsync", "yield pool=True|delay pool=True");
                data.Add(optimize, "Iterate", "0|1|2");
                data.Add(optimize, "IterateAsync", "0 pool=True|1 pool=True|2 pool=True");
                data.Add(optimize, "Lambda", "82");
            }

            return data;
        }
    }

    public static TheoryData<bool> Optimizations => new() { true, false };

    [Theory(Timeout = 30_000)]
    [MemberData(nameof(Probes))]
    public async Task ExcludedTypeRunsWithBclBehaviourOutsideTheSimulation(bool optimize, string probe, string expected)
    {
        // Each probe starts on a thread-pool thread with no synchronization context, as no simulation is active.
        MethodInfo bclMethod = (optimize ? _bclRelease : _bclDebug).Value.Method(probe);
        MethodInfo stagedMethod = (optimize ? _release : _debug).Value.Method(probe);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        string bcl = await Task.Run(() => (Task<string>)bclMethod.Invoke(null, null)!, cancellationToken);
        string actual = await Task.Run(() => (Task<string>)stagedMethod.Invoke(null, null)!, cancellationToken);

        Assert.Equal(expected, bcl);
        Assert.Equal(bcl, actual);
    }

    [Theory]
    [MemberData(nameof(Optimizations))]
    public void ExcludedTypeAndItsCompilerGeneratedTypesAreRecordedWhileTheRestIsRewritten(bool optimize)
    {
        StagedProbe probe = (optimize ? _release : _debug).Value;
        string[] excluded = [.. probe.Result.Manifest.Exclusions.Select(exclusion => exclusion.TypeFullName)];

        Assert.Contains("Conf.LegacyProbe", excluded);
        Assert.Contains(excluded, name => name.StartsWith("Conf.LegacyProbe/<AwaitAsync>", StringComparison.Ordinal));
        Assert.Contains(excluded, name => name.StartsWith("Conf.LegacyProbe/<Numbers>", StringComparison.Ordinal));
        Assert.Contains(excluded, name => name.StartsWith("Conf.LegacyProbe/<NumbersAsync>", StringComparison.Ordinal));
        Assert.Contains(excluded, name => name.StartsWith("Conf.LegacyProbe/<>c__DisplayClass", StringComparison.Ordinal)
            && name.Contains("/<<Lambda>", StringComparison.Ordinal));
        Assert.All(excluded, name => Assert.StartsWith("Conf.LegacyProbe", name, StringComparison.Ordinal));
        MethodInfo rewritten = probe.Type.Assembly.GetType("Conf.RewrittenProbe", throwOnError: true)!.GetMethod("Delay")!;
        SimulationNotActiveExceptionAssert.Throws(rewritten);
    }

    public void Dispose() => _fixture.Dispose();
}
