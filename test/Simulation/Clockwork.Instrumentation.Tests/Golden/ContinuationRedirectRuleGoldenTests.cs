using Orleans.Testing.Simulation.Instrumentation.Rewriting;
using Orleans.Testing.Simulation.Instrumentation.Rules;
using Orleans.Testing.Simulation.Instrumentation.Rules.BuiltIn;
using Orleans.Testing.Simulation.Instrumentation.Tests.Infrastructure;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Orleans.Testing.Simulation.Instrumentation.Tests.Golden;

/// <summary>
/// Golden tests for the redirects which keep Clockwork from registering BCL continuations: every .NET 11
/// <c>Task</c>/<c>Task&lt;TResult&gt;</c> <c>ContinueWith</c> overload, the <c>ValueTask</c> conversions, and the
/// <c>TaskCompletionSource</c> completion members. Each call site in the fixture is retargeted onto its shim, every
/// rule of the three families applies, and every rewritten member reference binds. The completion sources themselves
/// keep their BCL type.
/// </summary>
public sealed class ContinuationRedirectRuleGoldenTests
{
    private const string Fixture = """
        using System;
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;
        using System.Threading.Tasks.Sources;

        namespace Fx
        {
            public static class Continuations
            {
                public static void OfTask(Task t, CancellationToken c, TaskScheduler s, TaskContinuationOptions o)
                {
                    t.ContinueWith(_ => { });
                    t.ContinueWith(_ => { }, c);
                    t.ContinueWith(_ => { }, s);
                    t.ContinueWith(_ => { }, o);
                    t.ContinueWith(_ => { }, c, o, s);
                    t.ContinueWith((_, state) => { }, 1);
                    t.ContinueWith((_, state) => { }, 1, c);
                    t.ContinueWith((_, state) => { }, 1, s);
                    t.ContinueWith((_, state) => { }, 1, o);
                    t.ContinueWith((_, state) => { }, 1, c, o, s);
                    t.ContinueWith(_ => 1);
                    t.ContinueWith(_ => 1, c);
                    t.ContinueWith(_ => 1, s);
                    t.ContinueWith(_ => 1, o);
                    t.ContinueWith(_ => 1, c, o, s);
                    t.ContinueWith((_, state) => "x", 1);
                    t.ContinueWith((_, state) => "x", 1, c);
                    t.ContinueWith((_, state) => "x", 1, s);
                    t.ContinueWith((_, state) => "x", 1, o);
                    t.ContinueWith((_, state) => "x", 1, c, o, s);
                }

                public static void OfGenericTask(Task<int> t, CancellationToken c, TaskScheduler s, TaskContinuationOptions o)
                {
                    t.ContinueWith(_ => { });
                    t.ContinueWith(_ => { }, c);
                    t.ContinueWith(_ => { }, s);
                    t.ContinueWith(_ => { }, o);
                    t.ContinueWith(_ => { }, c, o, s);
                    t.ContinueWith((_, state) => { }, 1);
                    t.ContinueWith((_, state) => { }, 1, c);
                    t.ContinueWith((_, state) => { }, 1, s);
                    t.ContinueWith((_, state) => { }, 1, o);
                    t.ContinueWith((_, state) => { }, 1, c, o, s);
                    t.ContinueWith(_ => "x");
                    t.ContinueWith(_ => "x", c);
                    t.ContinueWith(_ => "x", s);
                    t.ContinueWith(_ => "x", o);
                    t.ContinueWith(_ => "x", c, o, s);
                    t.ContinueWith((_, state) => 2L, 1);
                    t.ContinueWith((_, state) => 2L, 1, c);
                    t.ContinueWith((_, state) => 2L, 1, s);
                    t.ContinueWith((_, state) => 2L, 1, o);
                    t.ContinueWith((_, state) => 2L, 1, c, o, s);
                }
            }

            public static class Conversions
            {
                public static Task AsTask(ValueTask value) => value.AsTask();
                public static ValueTask Preserve(ValueTask value) => value.Preserve();
                public static Task<int> AsTaskT(ValueTask<int> value) => value.AsTask();
                public static ValueTask<int> PreserveT(ValueTask<int> value) => value.Preserve();
            }

            public static class Completions
            {
                public static void Complete(TaskCompletionSource s, Task t, CancellationToken c, Exception e, IEnumerable<Exception> es)
                {
                    s.SetResult();
                    s.TrySetResult();
                    s.SetException(e);
                    s.SetException(es);
                    s.TrySetException(e);
                    s.TrySetException(es);
                    s.SetCanceled();
                    s.SetCanceled(c);
                    s.TrySetCanceled();
                    s.TrySetCanceled(c);
                    s.SetFromTask(t);
                    s.TrySetFromTask(t);
                }

                public static void CompleteGeneric(TaskCompletionSource<string> s, Task<string> t, CancellationToken c, Exception e, IEnumerable<Exception> es)
                {
                    s.SetResult("x");
                    s.TrySetResult("x");
                    s.SetException(e);
                    s.SetException(es);
                    s.TrySetException(e);
                    s.TrySetException(es);
                    s.SetCanceled();
                    s.SetCanceled(c);
                    s.TrySetCanceled();
                    s.TrySetCanceled(c);
                    s.SetFromTask(t);
                    s.TrySetFromTask(t);
                }

                public static TaskCompletionSource<int> Create() => new(TaskCreationOptions.RunContinuationsAsynchronously);

                public static TaskCompletionSource<int> CreateWithState() => new("state", TaskCreationOptions.None);

                public static TaskCompletionSource CreateNonGeneric() => new(TaskCreationOptions.RunContinuationsAsynchronously);

                public static TaskCompletionSource CreateNonGenericWithState() => new("state", TaskCreationOptions.None);
            }

            public sealed class DerivedSource : TaskCompletionSource<int>
            {
                public DerivedSource()
                    : base(TaskCreationOptions.RunContinuationsAsynchronously)
                {
                }

                public void Resolve() => SetResult(1);
            }

            // A value-task source completes its core, a field, in place.
            public sealed class CoreSource
            {
                private ManualResetValueTaskSourceCore<int> _core;

                public void Resolve() => _core.SetResult(1);

                public void Fail(Exception e) => _core.SetException(e);
            }
        }
        """;

    private static readonly BuiltInRuleFamily[] Families =
    [
        BuiltInRuleFamily.TaskContinuations,
        BuiltInRuleFamily.TaskCompletionSource,
        BuiltInRuleFamily.ValueTaskConversions,
    ];

    private static string RuntimeAssemblyPath =>
        typeof(Orleans.Testing.Simulation.Shims.System.Threading.Tasks.ControlledTask).Assembly.Location;

    [Fact]
    public void EveryRedirectAppliesAndBinds()
    {
        using var context = RewriteTestContext.Create();
        string fixturePath = context.CompileFixture("Fx.ContinuationRedirects", Fixture);
        string outputPath = Path.Combine(context.Directory, "Fx.ContinuationRedirects.rewritten.dll");
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
                .Where(entry => Families.Contains(entry.Family))
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
        Assert.Equal(74, expectedRules.Length);
        Assert.Equal(expectedRules, appliedRules);

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

        Assert.DoesNotContain(calls, call => call.Name == "ContinueWith" && IsBcl(call));
        Assert.DoesNotContain(calls, call => call.Name is "AsTask" or "Preserve" && IsBcl(call));
        Assert.DoesNotContain(calls, call => call.Name.Contains("Set", StringComparison.Ordinal)
            && call.DeclaringType.FullName.StartsWith("System.Threading.Tasks.TaskCompletionSource", StringComparison.Ordinal));
        Assert.All(calls, call => Assert.True(call.Resolve() is not null, $"Rewritten reference '{call.FullName}' does not bind."));

        // The completion sources keep their BCL type: a derived source's base constructor call, its base type, and the
        // signatures are unchanged, and the constructors which take options are built by the shim's factories.
        Assert.Contains(calls, call => call.FullName ==
            "System.Void System.Threading.Tasks.TaskCompletionSource`1<System.Int32>::.ctor(System.Threading.Tasks.TaskCreationOptions)");
        Assert.True(CecilInspect.CallsAnyContaining(
            CecilInspect.GetMethod(module, "Fx.Completions", "Create"),
            "Orleans.Testing.Simulation.Shims.System.Threading.Tasks.ControlledTaskCompletionSource::CreateGeneric<System.Int32>"));
        Assert.Equal(
            "System.Threading.Tasks.TaskCompletionSource`1<System.Int32>",
            module.GetType("Fx.DerivedSource").BaseType.FullName);
        Assert.DoesNotContain(calls, call => call.Name is "SetResult" or "SetException"
            && call.DeclaringType.FullName.StartsWith("System.Threading.Tasks.Sources.ManualResetValueTaskSourceCore", StringComparison.Ordinal));
        Assert.True(CecilInspect.CallsAnyContaining(
            CecilInspect.GetMethod(module, "Fx.CoreSource", "Resolve"),
            "Orleans.Testing.Simulation.Shims.System.Threading.Tasks.Sources.ControlledManualResetValueTaskSourceCore::SetResult<System.Int32>"));
        Assert.True(CecilInspect.CallsAnyContaining(
            CecilInspect.GetMethod(module, "Fx.DerivedSource", "Resolve"),
            "Orleans.Testing.Simulation.Shims.System.Threading.Tasks.ControlledTaskCompletionSource::SetResult<System.Int32>"));
    }

    private static bool IsBcl(MethodReference call) =>
        call.DeclaringType.FullName.StartsWith("System.Threading.Tasks.", StringComparison.Ordinal);
}
