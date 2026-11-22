using Orleans.Testing.Simulation.Instrumentation.Manifest;
using Orleans.Testing.Simulation.Instrumentation.Rewriting;
using Orleans.Testing.Simulation.Instrumentation.Rules;
using Orleans.Testing.Simulation.Instrumentation.Rules.BuiltIn;
using Orleans.Testing.Simulation.Instrumentation.Tests.Infrastructure;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Orleans.Testing.Simulation.Instrumentation.Tests.Golden;

/// <summary>
/// Golden tests for the multi-task surfaces: every .NET 11 <c>Task.WhenEach</c> overload and every <c>TaskFactory</c> and
/// <c>TaskFactory&lt;TResult&gt;</c> <c>ContinueWhenAll</c>/<c>ContinueWhenAny</c> overload redirect onto their shims, with
/// the type arguments bound declaring-type-first, and every <c>FromAsync</c> call site is preceded by the rejection.
/// Every rule of the three surfaces applies, and every rewritten member reference binds.
/// </summary>
public sealed class MultiTaskRedirectRuleGoldenTests
{
    private const string Fixture = """
        using System;
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;

        namespace Fx
        {
            public static class Each
            {
                public static void All(Task a, Task<int> b, List<Task> c, List<Task<int>> d)
                {
                    Task.WhenEach(new[] { a });
                    Task.WhenEach(a, a);
                    Task.WhenEach(c);
                    Task.WhenEach(new[] { b });
                    Task.WhenEach(b, b);
                    Task.WhenEach(d);
                }
            }

            public static class WhenAll
            {
                public static void OfFactory(TaskFactory f, Task[] t, Task<int>[] g, CancellationToken c, TaskContinuationOptions o, TaskScheduler s)
                {
                    f.ContinueWhenAll(t, _ => { });
                    f.ContinueWhenAll(t, _ => { }, c);
                    f.ContinueWhenAll(t, _ => { }, o);
                    f.ContinueWhenAll(t, _ => { }, c, o, s);
                    f.ContinueWhenAll(g, _ => { });
                    f.ContinueWhenAll(g, _ => { }, c);
                    f.ContinueWhenAll(g, _ => { }, o);
                    f.ContinueWhenAll(g, _ => { }, c, o, s);
                    f.ContinueWhenAll(t, _ => "x");
                    f.ContinueWhenAll(t, _ => "x", c);
                    f.ContinueWhenAll(t, _ => "x", o);
                    f.ContinueWhenAll(t, _ => "x", c, o, s);
                    f.ContinueWhenAll(g, _ => 2L);
                    f.ContinueWhenAll(g, _ => 2L, c);
                    f.ContinueWhenAll(g, _ => 2L, o);
                    f.ContinueWhenAll(g, _ => 2L, c, o, s);
                }

                public static void OfGenericFactory(TaskFactory<string> f, Task[] t, Task<int>[] g, CancellationToken c, TaskContinuationOptions o, TaskScheduler s)
                {
                    f.ContinueWhenAll(t, _ => "x");
                    f.ContinueWhenAll(t, _ => "x", c);
                    f.ContinueWhenAll(t, _ => "x", o);
                    f.ContinueWhenAll(t, _ => "x", c, o, s);
                    f.ContinueWhenAll(g, _ => "x");
                    f.ContinueWhenAll(g, _ => "x", c);
                    f.ContinueWhenAll(g, _ => "x", o);
                    f.ContinueWhenAll(g, _ => "x", c, o, s);
                }
            }

            public static class WhenAny
            {
                public static void OfFactory(TaskFactory f, Task[] t, Task<int>[] g, CancellationToken c, TaskContinuationOptions o, TaskScheduler s)
                {
                    f.ContinueWhenAny(t, _ => { });
                    f.ContinueWhenAny(t, _ => { }, c);
                    f.ContinueWhenAny(t, _ => { }, o);
                    f.ContinueWhenAny(t, _ => { }, c, o, s);
                    f.ContinueWhenAny(g, _ => { });
                    f.ContinueWhenAny(g, _ => { }, c);
                    f.ContinueWhenAny(g, _ => { }, o);
                    f.ContinueWhenAny(g, _ => { }, c, o, s);
                    f.ContinueWhenAny(t, _ => "x");
                    f.ContinueWhenAny(t, _ => "x", c);
                    f.ContinueWhenAny(t, _ => "x", o);
                    f.ContinueWhenAny(t, _ => "x", c, o, s);
                    f.ContinueWhenAny(g, _ => 2L);
                    f.ContinueWhenAny(g, _ => 2L, c);
                    f.ContinueWhenAny(g, _ => 2L, o);
                    f.ContinueWhenAny(g, _ => 2L, c, o, s);
                }

                public static void OfGenericFactory(TaskFactory<string> f, Task[] t, Task<int>[] g, CancellationToken c, TaskContinuationOptions o, TaskScheduler s)
                {
                    f.ContinueWhenAny(t, _ => "x");
                    f.ContinueWhenAny(t, _ => "x", c);
                    f.ContinueWhenAny(t, _ => "x", o);
                    f.ContinueWhenAny(t, _ => "x", c, o, s);
                    f.ContinueWhenAny(g, _ => "x");
                    f.ContinueWhenAny(g, _ => "x", c);
                    f.ContinueWhenAny(g, _ => "x", o);
                    f.ContinueWhenAny(g, _ => "x", c, o, s);
                }
            }

            public static class FromAsync
            {
                public static Task Begin(TaskFactory f, IAsyncResult r) => f.FromAsync(r, _ => { });

                public static Task<int> BeginGeneric(TaskFactory<int> f, IAsyncResult r) => f.FromAsync(r, _ => 1);

                public static Task<string> BeginWithArgument(TaskFactory f, Func<int, AsyncCallback, object?, IAsyncResult> begin) =>
                    f.FromAsync(begin, _ => "x", 1, null);
            }
        }
        """;

    private const string FactoryShim = "Orleans.Testing.Simulation.Shims.System.Threading.Tasks.ControlledTaskFactory";

    private static string RuntimeAssemblyPath =>
        typeof(Orleans.Testing.Simulation.Shims.System.Threading.Tasks.ControlledTask).Assembly.Location;

    [Fact]
    public void EveryRedirectAndRejectionAppliesAndBinds()
    {
        using var context = RewriteTestContext.Create();
        string fixturePath = context.CompileFixture("Fx.MultiTaskRedirects", Fixture);
        string outputPath = Path.Combine(context.Directory, "Fx.MultiTaskRedirects.rewritten.dll");
        var options = new RewriteOptions
        {
            TargetRuntime = RewriteTestContext.TargetRuntime,
            ReplacementAssemblyPaths = [RuntimeAssemblyPath],
            ReferenceSearchDirectories = [context.Directory, Path.GetDirectoryName(RuntimeAssemblyPath)!],
        };
        RewriteRuleSet ruleSet = BuiltInRuleSets.BuildControlledTasks(BuiltInRuleSets.AllFamilies);

        RewriteResult result = RewriteEngine.Rewrite(new RewriteRequest(fixturePath, outputPath, ruleSet, options));
        result.EnsureSuccess();

        string[] expectedRules =
        [
            .. BuiltInRuleSets.ControlledTasksInventory
                .Where(entry => entry.Family is BuiltInRuleFamily.TaskFactoryContinuations or BuiltInRuleFamily.TaskFactoryFromAsync
                    || entry.Rule.Target.MemberName == "WhenEach")
                .Select(entry => entry.Rule.Id)
                .Order(StringComparer.Ordinal),
        ];
        string[] appliedRules =
        [
            .. result.Manifest.Transformations
                .Select(transformation => transformation.RuleId)
                .Where(expectedRules.Contains)
                .Distinct()
                .Order(StringComparer.Ordinal),
        ];
        Assert.Equal(6 + 48 + 2, expectedRules.Length);
        Assert.Equal(expectedRules, appliedRules);
        Assert.Equal(
            3,
            result.Manifest.Transformations.Count(transformation => transformation.RuleId.Contains(".fromasync", StringComparison.Ordinal)
                && transformation.Outcome == TransformationOutcome.Rejected));

        using ModuleDefinition module = context.LoadModule(outputPath);
        MethodReference[] calls =
        [
            .. module.GetTypes()
                .SelectMany(type => type.Methods)
                .Where(method => method.HasBody)
                .SelectMany(method => method.Body.Instructions)
                .Where(instruction => instruction.OpCode.Code is Code.Call or Code.Callvirt or Code.Newobj)
                .Select(instruction => (MethodReference)instruction.Operand),
        ];

        Assert.DoesNotContain(calls, call => call.Name is "WhenEach" or "ContinueWhenAll" or "ContinueWhenAny" && IsBcl(call));
        Assert.All(calls, call => Assert.True(call.Resolve() is not null, $"Rewritten reference '{call.FullName}' does not bind."));

        // A TaskFactory<TResult> member binds the shim's TResult from the receiver, then its own TAntecedentResult.
        Assert.True(CecilInspect.CallsAnyContaining(
            CecilInspect.GetMethod(module, "Fx.WhenAll", "OfGenericFactory"),
            FactoryShim + "::ContinueWhenAll<System.String,System.Int32>"));
        Assert.True(CecilInspect.CallsAnyContaining(
            CecilInspect.GetMethod(module, "Fx.WhenAny", "OfFactory"),
            FactoryShim + "::ContinueWhenAny<System.Int32,System.Int64>"));

        // FromAsync stays in place, after a call that rejects it, so the evaluation stack stays balanced.
        MethodDefinition begin = CecilInspect.GetMethod(module, "Fx.FromAsync", "BeginWithArgument");
        Instruction fromAsync = begin.Body.Instructions.Single(
            instruction => instruction.Operand is MethodReference { Name: "FromAsync" } call && IsBcl(call));
        Assert.Equal(FactoryShim + "::RejectFromAsync", Callee(fromAsync.Previous));
        Assert.Equal("System.Threading.Tasks.TaskFactory.FromAsync", fromAsync.Previous.Previous.Operand);
    }

    private static string Callee(Instruction instruction)
    {
        var method = (MethodReference)instruction.Operand;
        return method.DeclaringType.FullName + "::" + method.Name;
    }

    private static bool IsBcl(MethodReference call) =>
        call.DeclaringType.FullName.StartsWith("System.Threading.Tasks.", StringComparison.Ordinal);
}
