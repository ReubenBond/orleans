using Orleans.Testing.Simulation.Instrumentation.Rewriting;
using Orleans.Testing.Simulation.Instrumentation.Rules;
using Orleans.Testing.Simulation.Instrumentation.Rules.BuiltIn;
using Orleans.Testing.Simulation.Instrumentation.Tests.Infrastructure;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Orleans.Testing.Simulation.Instrumentation.Tests.Golden;

/// <summary>
/// End-to-end golden tests for the exception-hardening pass. They rewrite a compiled
/// fixture with several handler shapes against the real <c>Clockwork</c> assembly and assert that only
/// broad <c>catch (Exception)</c> / <c>catch</c> blocks and exception <c>filter</c>s receive the injected
/// <c>dup; call SimulationExceptionGuard.ThrowIfControlSignal(object)</c> guard, while narrow typed catches,
/// finally blocks, and rethrow-only handlers are left untouched - and that the rewritten module still
/// verifies (round-trips through <see cref="RewriteResult.EnsureSuccess"/> and reloads).
/// </summary>
public sealed class ExceptionHardeningRuleGoldenTests
{
    private const string GuardCall = "SimulationExceptionGuard::ThrowIfControlSignal";

    private const string Fixture = """
        using System;
        using System.IO;

        namespace Fx
        {
            public static class HandlerUser
            {
                private static int _n;
                private static void Work() => _n++;

                public static int BroadCatch()
                {
                    try { Work(); return 0; }
                    catch (Exception) { return -1; }
                }

                public static int BroadCatchWithVariable()
                {
                    try { Work(); return 0; }
                    catch (Exception e) { return e.Message.Length; }
                }

                public static int FilteredCatch()
                {
                    try { Work(); return 0; }
                    catch (Exception e) when (e.Message.Length > 0) { return -3; }
                }

                public static int TypedCatch()
                {
                    try { Work(); return 0; }
                    catch (IOException) { return -2; }
                }

                public static int FinallyOnly()
                {
                    try { Work(); return 0; }
                    finally { Work(); }
                }

                public static int RethrowOnly()
                {
                    try { Work(); return 0; }
                    catch (Exception) { throw; }
                }
            }
        }
        """;

    private static string RuntimeAssemblyPath =>
        typeof(Orleans.Testing.Simulation.Runtime.SimulationExceptionGuard).Assembly.Location;

    private static ModuleDefinition RewriteAndLoad(RewriteTestContext context, string assemblyName)
    {
        string fixturePath = context.CompileFixture(assemblyName, Fixture);
        string outputPath = Path.Combine(
            context.Directory, Path.GetFileNameWithoutExtension(fixturePath) + ".rewritten.dll");

        var options = new RewriteOptions
        {
            TargetRuntime = RewriteTestContext.TargetRuntime,
            HardenExceptionHandlers = true,
            ReplacementAssemblyPaths = [RuntimeAssemblyPath],
            ReferenceSearchDirectories = [context.Directory, Path.GetDirectoryName(RuntimeAssemblyPath)!],
        };

        RewriteRuleSet ruleSet = BuiltInRuleSets.BuildControlledTasks(BuiltInRuleSets.AllFamilies);
        var result = RewriteEngine.Rewrite(new RewriteRequest(fixturePath, outputPath, ruleSet, options));

        // EnsureSuccess runs the RewriteValidator read-back, proving the injected regions produce valid IL.
        result.EnsureSuccess();
        return context.LoadModule(outputPath);
    }

    [Fact]
    public void OnlyBroadHandlersAndFiltersAreHardened()
    {
        using var context = RewriteTestContext.Create();
        using ModuleDefinition module = RewriteAndLoad(context, "Fx.Hardening");

        foreach (string method in new[] { "BroadCatch", "BroadCatchWithVariable", "FilteredCatch" })
        {
            MethodDefinition definition = CecilInspect.GetMethod(module, "Fx.HandlerUser", method);
            Assert.True(
                CecilInspect.CallsAnyContaining(definition, GuardCall),
                $"{method} should be hardened");
        }

        foreach (string method in new[] { "TypedCatch", "FinallyOnly", "RethrowOnly" })
        {
            MethodDefinition definition = CecilInspect.GetMethod(module, "Fx.HandlerUser", method);
            Assert.False(
                CecilInspect.CallsAnyContaining(definition, GuardCall),
                $"{method} should NOT be hardened");
        }
    }

    [Fact]
    public void GuardIsInjectedAtTheHandlerStartAfterADup()
    {
        using var context = RewriteTestContext.Create();
        using ModuleDefinition module = RewriteAndLoad(context, "Fx.HardeningShape");

        MethodDefinition definition = CecilInspect.GetMethod(module, "Fx.HandlerUser", "BroadCatch");
        ExceptionHandler handler = Assert.Single(definition.Body.ExceptionHandlers);

        // The pass repoints HandlerStart to the injected `dup`, immediately followed by the guard call.
        Instruction start = handler.HandlerStart;
        Assert.Equal(Code.Dup, start.OpCode.Code);
        Assert.Equal(Code.Call, start.Next.OpCode.Code);
        Assert.True(
            start.Next.Operand is MethodReference mr && mr.FullName.Contains(GuardCall, StringComparison.Ordinal),
            "the instruction after the injected dup should call the guard");
    }
}
