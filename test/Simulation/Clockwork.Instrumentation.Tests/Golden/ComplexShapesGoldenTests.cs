using Orleans.Testing.Simulation.Instrumentation.Diagnostics;
using Orleans.Testing.Simulation.Instrumentation.Tests.Infrastructure;
using Mono.Cecil;

namespace Orleans.Testing.Simulation.Instrumentation.Tests.Golden;

/// <summary>
/// Robustness tests proving the engine correctly rewrites a redirected call embedded in a wide range
/// of IL shapes - by-ref parameters, arrays, constrained interface calls, delegates, async state
/// machines, iterators, and nested types - while leaving the surrounding shapes intact and passing
/// read-back validation. These exercise the traversal and offset-fixing logic across compiler-
/// generated members, not new redirection semantics.
/// </summary>
public sealed class ComplexShapesGoldenTests
{
    private const string Fixture = """
        using System.Collections.Generic;
        using System.Threading.Tasks;
        using ClockworkFixtures.Api;

        namespace Fx
        {
            public static class Shapes
            {
                public static long ByRef(ref long acc)
                {
                    acc += RealClock.UtcNowTicks();
                    return acc;
                }

                public static long Arrays()
                {
                    var values = new long[] { RealClock.UtcNowTicks(), 2, 3 };
                    return values[0];
                }

                public static int Constrained<T>(T probe) where T : IProbe
                {
                    // A constrained.callvirt to IProbe.Probe alongside a redirected call.
                    long ticks = RealClock.UtcNowTicks();
                    return probe.Probe() + (int)ticks;
                }

                public static long Delegated()
                {
                    System.Func<long> f = () => 1L;
                    return f() + RealClock.UtcNowTicks();
                }

                public static async Task<long> AsyncTicks()
                {
                    await Task.Yield();
                    return RealClock.UtcNowTicks();
                }

                public static IEnumerable<long> IteratorTicks()
                {
                    yield return RealClock.UtcNowTicks();
                    yield return 2L;
                }

                public sealed class Nested
                {
                    public long Inner() => RealClock.UtcNowTicks();
                }
            }
        }
        """;

    [Theory]
    [InlineData("ByRef")]
    [InlineData("Arrays")]
    [InlineData("Constrained")]
    [InlineData("Delegated")]
    public void DirectMethodShapesAreRewritten(string methodName)
    {
        using RewriteTestContext context = Build(out ModuleDefinition module, methodName);
        using (module)
        {
            MethodDefinition method = CecilInspect.GetMethod(module, "Fx.Shapes", methodName);
            Assert.True(CecilInspect.CallsAnyContaining(method, "ClockShim::UtcNowTicks"));
            Assert.False(CecilInspect.CallsAnyContaining(method, "RealClock::UtcNowTicks"));
        }
    }

    [Fact]
    public void ConstrainedInterfaceCallIsPreserved()
    {
        using RewriteTestContext context = Build(out ModuleDefinition module, "Constrained");
        using (module)
        {
            MethodDefinition method = CecilInspect.GetMethod(module, "Fx.Shapes", "Constrained");
            Assert.Contains(method.Body.Instructions, i => i.OpCode.Code == Mono.Cecil.Cil.Code.Constrained);
            Assert.True(CecilInspect.CallsAnyContaining(method, "IProbe::Probe"));
        }
    }

    [Fact]
    public void NestedTypeMethodIsRewritten()
    {
        using RewriteTestContext context = Build(out ModuleDefinition module, "Nested");
        using (module)
        {
            MethodDefinition inner = CecilInspect.GetMethod(module, "Fx.Shapes/Nested", "Inner");
            Assert.True(CecilInspect.CallsAnyContaining(inner, "ClockShim::UtcNowTicks"));
        }
    }

    [Theory]
    [InlineData("AsyncTicks")]
    [InlineData("IteratorTicks")]
    public void CompilerGeneratedStateMachinesAreRewritten(string flavor)
    {
        using RewriteTestContext context = Build(out ModuleDefinition module, flavor);
        using (module)
        {
            // The redirected call lives in a compiler-generated MoveNext, not the user method.
            Assert.True(CecilInspect.AnyMethodCallsContaining(module, "ClockShim::UtcNowTicks"));
            Assert.False(CecilInspect.AnyMethodCallsContaining(module, "RealClock::UtcNowTicks"));
        }
    }

    private static RewriteTestContext Build(out ModuleDefinition module, string tag)
    {
        var context = RewriteTestContext.Create();
        string fixturePath = context.CompileFixture("Fx.Shapes." + tag, Fixture);
        string outputPath = Path.Combine(context.Directory, "Fx.Shapes." + tag + ".rewritten.dll");

        var result = context.Rewrite(fixturePath, outputPath, RewriteTestContext.StandardRuleSet());
        result.EnsureSuccess();
        Assert.DoesNotContain(result.Diagnostics, d => d.Id == RewriteDiagnosticIds.ValidationFailed);

        module = context.LoadModule(outputPath);
        return context;
    }
}
