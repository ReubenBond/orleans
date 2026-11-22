using System.Collections;
using System.Reflection;
using System.Runtime.Loader;
using Orleans.Testing.Simulation.Instrumentation.Tests.Infrastructure;

namespace Orleans.Testing.Simulation.Instrumentation.Tests.Golden;

/// <summary>
/// Focused in-process execution tests that load a rewritten fixture into a collectible
/// <see cref="AssemblyLoadContext"/> and invoke its methods, proving the redirected/rejected sites
/// actually dispatch to the shim at runtime. This is a test mechanism only - the engine itself never
/// loads or executes rewritten code (load-time hooks are out of scope for the rewrite engine).
/// </summary>
public sealed class ExecutionGoldenTests
{
    private const string Fixture = """
        using ClockworkFixtures.Api;

        namespace Fx
        {
            public static class Exec
            {
                public static long Ticks() => RealClock.UtcNowTicks();
                public static int Value() { var s = new Service(3); return s.GetValue(); }
                public static int Made() { var w = new Widget(5); return w.X; }
                public static void Danger() => Forbidden.DangerousWrite("x");
            }
        }
        """;

    [Fact]
    public void RewrittenMethodsDispatchToShimAtRuntime()
    {
        using var context = RewriteTestContext.Create();
        string fixturePath = context.CompileFixture("Fx.Exec", Fixture);
        string outputPath = Path.Combine(context.Directory, "Fx.Exec.rewritten.dll");
        context.Rewrite(fixturePath, outputPath, RewriteTestContext.StandardRuleSet()).EnsureSuccess();

        var alc = new DirectoryLoadContext(context.Directory);
        try
        {
            Assembly rewritten = alc.LoadFromAssemblyPath(outputPath);
            Type exec = rewritten.GetType("Fx.Exec", throwOnError: true)!;

            // The shim replaces the real implementations, returning its own values.
            Assert.Equal(999L, exec.GetMethod("Ticks")!.Invoke(null, null));
            Assert.Equal(7, exec.GetMethod("Value")!.Invoke(null, null));
            Assert.Equal(1005, exec.GetMethod("Made")!.Invoke(null, null));

            // The rejection shim throws deterministically.
            TargetInvocationException thrown = Assert.Throws<TargetInvocationException>(
                () => exec.GetMethod("Danger")!.Invoke(null, null));
            Assert.IsType<InvalidOperationException>(thrown.InnerException);

            List<string> events = ReadRecorderEvents(alc);
            Assert.Contains("UtcNowTicks", events);
            Assert.Contains("GetValue", events);
            Assert.Contains("CreateWidget", events);
            Assert.Contains(events, e => e.StartsWith("Reject:", StringComparison.Ordinal));
        }
        finally
        {
            alc.Unload();
        }
    }

    private static List<string> ReadRecorderEvents(AssemblyLoadContext alc)
    {
        Assembly shims = alc.LoadFromAssemblyName(new AssemblyName(FixtureSources.ShimAssemblyName));
        Type recorder = shims.GetType("ClockworkFixtures.Shims.Recorder", throwOnError: true)!;
        object value = recorder.GetField("Events", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
        return ((IEnumerable)value).Cast<string>().ToList();
    }

    private sealed class DirectoryLoadContext(string directory) : AssemblyLoadContext(isCollectible: true)
    {
        private readonly string _directory = directory;

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name is null)
            {
                return null;
            }

            string candidate = Path.Combine(_directory, assemblyName.Name + ".dll");
            return File.Exists(candidate) ? LoadFromAssemblyPath(candidate) : null;
        }
    }
}
