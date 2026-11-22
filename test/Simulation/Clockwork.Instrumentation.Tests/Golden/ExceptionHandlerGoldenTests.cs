using Orleans.Testing.Simulation.Instrumentation.Diagnostics;
using Orleans.Testing.Simulation.Instrumentation.Tests.Infrastructure;
using Mono.Cecil;

namespace Orleans.Testing.Simulation.Instrumentation.Tests.Golden;

/// <summary>
/// Golden tests proving that rewriting inside protected regions (try/catch/filter/finally) preserves
/// exception-handler boundaries and passes the engine's read-back validation, for both call
/// redirection and rejection injection - the two operations that mutate instruction ranges.
/// </summary>
public sealed class ExceptionHandlerGoldenTests
{
    private const string Fixture = """
        using ClockworkFixtures.Api;

        namespace Fx
        {
            public static class Guarded
            {
                public static long TryCatchFilterFinally()
                {
                    try
                    {
                        return RealClock.UtcNowTicks();
                    }
                    catch (System.Exception) when (RealClock.UtcNowTicks() > 0)
                    {
                        return -1;
                    }
                    finally
                    {
                        System.GC.KeepAlive(null);
                    }
                }

                public static void RejectInsideTry()
                {
                    try
                    {
                        Forbidden.DangerousWrite("guarded");
                    }
                    catch (System.InvalidOperationException)
                    {
                    }
                }
            }
        }
        """;

    [Fact]
    public void RedirectInsideProtectedRegionsValidates()
    {
        using var context = RewriteTestContext.Create();
        string fixturePath = context.CompileFixture("Fx.Guarded", Fixture);

        var result = context.Rewrite(fixturePath, RewriteTestContext.StandardRuleSet());
        result.EnsureSuccess();

        // Read-back validation (CWR0009) must not have fired.
        Assert.DoesNotContain(result.Diagnostics, d => d.Id == RewriteDiagnosticIds.ValidationFailed);

        using ModuleDefinition module = context.LoadModule(
            Path.Combine(context.Directory, "Fx.Guarded.rewritten.dll"));
        MethodDefinition guarded = CecilInspect.GetMethod(module, "Fx.Guarded", "TryCatchFilterFinally");

        Assert.True(guarded.Body.HasExceptionHandlers);
        // The original catch/filter/finally structure is preserved (a filtered handler and a finally).
        Assert.Contains(guarded.Body.ExceptionHandlers, h => h.HandlerType == Mono.Cecil.Cil.ExceptionHandlerType.Filter);
        Assert.Contains(guarded.Body.ExceptionHandlers, h => h.HandlerType == Mono.Cecil.Cil.ExceptionHandlerType.Finally);
        Assert.True(CecilInspect.CallsAnyContaining(guarded, "ClockShim::UtcNowTicks"));
    }

    [Fact]
    public void RejectionInsideTryKeepsHandlerBoundaries()
    {
        using var context = RewriteTestContext.Create();
        string fixturePath = context.CompileFixture("Fx.GuardedReject", Fixture);

        var result = context.Rewrite(fixturePath, RewriteTestContext.StandardRuleSet());
        result.EnsureSuccess();

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == RewriteDiagnosticIds.ValidationFailed);

        using ModuleDefinition module = context.LoadModule(
            Path.Combine(context.Directory, "Fx.GuardedReject.rewritten.dll"));
        MethodDefinition method = CecilInspect.GetMethod(module, "Fx.Guarded", "RejectInsideTry");

        Assert.True(method.Body.HasExceptionHandlers);
        Assert.True(CecilInspect.CallsAnyContaining(method, "ClockShim::Reject"));
        Assert.True(CecilInspect.CallsAnyContaining(method, "Forbidden::DangerousWrite"));

        // Every handler must still reference instructions that live in the rewritten body.
        var bodyInstructions = new HashSet<Mono.Cecil.Cil.Instruction>(method.Body.Instructions);
        foreach (Mono.Cecil.Cil.ExceptionHandler handler in method.Body.ExceptionHandlers)
        {
            Assert.Contains(handler.TryStart, bodyInstructions);
            Assert.Contains(handler.HandlerStart, bodyInstructions);
        }
    }
}
