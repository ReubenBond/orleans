using Orleans.Testing.Simulation.Instrumentation.Diagnostics;
using Orleans.Testing.Simulation.Instrumentation.Rules;
using Orleans.Testing.Simulation.Instrumentation.Rewriting;
using Orleans.Testing.Simulation.Instrumentation.Tests.Infrastructure;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Orleans.Testing.Simulation.Instrumentation.Tests.Golden;

/// <summary>
/// Verifies member substitution selects methods, and the constructors that replace awaiter-source calls, by
/// complete signature shape.
/// </summary>
public sealed class MemberSubstitutionShapeGoldenTests
{
    private const string Fixture = """
        using System.Collections.Generic;
        using ClockworkFixtures.Api;

        namespace Fx
        {
            public static class ShapeCalls
            {
                public static int Run()
                {
                    var value = new LegacyShapes();
                    int number = 1;
                    string text = "x";
                    long wide = 2;
                    int sum = value.Choose(number);
                    sum += value.Ref(ref number);
                    sum += value.In(in wide);
                    sum += value.Out(out text);
                    sum += value.Generic(new List<int> { number });
                    sum += value.NestedShape(new LegacyShapes.Nested());
                    return sum;
                }
            }
        }
        """;

    private const string AwaiterSourceFixture = """
        using System.Threading.Tasks;

        namespace Fx
        {
            public static class AwaiterSources
            {
                public static void TaskSource(Task task) { _ = task.GetAwaiter(); }
                public static void GenericTaskSource(Task<string> task) { _ = task.GetAwaiter(); }
                public static void ValueTaskSource(ValueTask valueTask) { _ = valueTask.GetAwaiter(); }
            }
        }
        """;

    private const string ConfigureAwaitFixture = """
        using System.Threading.Tasks;

        namespace Fx
        {
            public static class ConfigureAwaitSources
            {
                public static void Bool(Task task) { _ = task.ConfigureAwait(false); }
                public static void Options(Task task) { _ = task.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing); }
            }
        }
        """;

    [Fact]
    public void SameArityOverloadsMapByRefGenericAndNestedShape()
    {
        using var context = RewriteTestContext.Create();
        string fixturePath = context.CompileFixture("Fx.MemberShapes", Fixture);
        var rules = new RewriteRuleSet(
            "clockwork.member-shapes",
            "1.0",
            [
                RewriteRule.SubstituteType(
                    "shape-type",
                    "ClockworkFixtures.Api.LegacyShapes",
                    RewriteReplacement.Type(FixtureSources.ShimAssemblyName, "ClockworkFixtures.Shims.ModernShapes")),
                RewriteRule.SubstituteType(
                    "shape-nested",
                    "ClockworkFixtures.Api.LegacyShapes/Nested",
                    RewriteReplacement.Type(FixtureSources.ShimAssemblyName, "ClockworkFixtures.Shims.ModernShapes/Nested")),
            ]);

        RewriteResult result = context.Rewrite(fixturePath, rules);
        result.EnsureSuccess();

        using ModuleDefinition module = context.LoadModule(
            Path.Combine(context.Directory, "Fx.MemberShapes.rewritten.dll"));
        MethodDefinition run = CecilInspect.GetMethod(module, "Fx.ShapeCalls", "Run");
        MethodReference[] calls = run.Body.Instructions
            .Select(instruction => instruction.Operand)
            .OfType<MethodReference>()
            .Where(method => method.DeclaringType.FullName.Contains("ModernShapes", StringComparison.Ordinal))
            .ToArray();

        Assert.Contains(calls, method => HasParameters(method, "System.Int32"));
        Assert.Contains(calls, method => HasParameters(method, "System.Int32&"));
        Assert.Contains(calls, method => HasParameters(method, "System.Int64&"));
        Assert.Contains(calls, method => HasParameters(method, "System.String&"));
        Assert.Contains(
            calls,
            method => method is GenericInstanceMethod generic
                && generic.GenericArguments.Single().FullName == "System.Int32"
                && HasParameters(generic.ElementMethod, "System.Collections.Generic.List`1<!!0>"));
        Assert.Contains(calls, method => HasParameters(method, "ClockworkFixtures.Shims.ModernShapes/Nested"));
        Assert.DoesNotContain(calls, method => HasParameters(method, "System.String") && method.Name == "Choose");
        Assert.DoesNotContain(calls, method => HasParameters(method, "System.Int32&") && method.Name == "Out");
        Assert.All(result.Manifest.Transformations, transformation =>
        {
            Assert.True(transformation.ILOffset > 0);
            Assert.NotNull(transformation.SourceFile);
            Assert.True(transformation.SourceLine > 0);
        });
    }

    [Fact]
    public void AwaiterSourceConstructorTakesTheReceiverAsTheCallPushesIt()
    {
        using var context = RewriteTestContext.Create();
        string fixturePath = context.CompileFixture("Fx.AwaiterSources", AwaiterSourceFixture);
        var rules = new RewriteRuleSet(
            "clockwork.awaiter-sources",
            "1.0",
            [
                AwaiterSubstitution("decoy-task-awaiter", "TaskAwaiter", "DecoyTaskAwaiter"),
                AwaiterSubstitution("decoy-task-awaiter-generic", "TaskAwaiter`1", "DecoyTaskAwaiter`1"),
                AwaiterSubstitution("decoy-valuetask-awaiter", "ValueTaskAwaiter", "DecoyValueTaskAwaiter"),
            ]);

        RewriteResult result = context.Rewrite(fixturePath, rules);
        result.EnsureSuccess();

        using ModuleDefinition module = context.LoadModule(
            Path.Combine(context.Directory, "Fx.AwaiterSources.rewritten.dll"));

        // A Task is pushed by value and a ValueTask by address; the generic receiver is the awaiter's own
        // type parameter rather than a closed instantiation.
        Assert.Equal("System.Threading.Tasks.Task", ConstructedReceiver(module, "TaskSource"));
        Assert.Equal("System.Threading.Tasks.Task`1<!0>", ConstructedReceiver(module, "GenericTaskSource"));
        Assert.Equal("System.Threading.Tasks.ValueTask&", ConstructedReceiver(module, "ValueTaskSource"));
    }

    [Fact]
    public void AwaiterSourceOverloadWithoutAMatchingConstructorFailsClosed()
    {
        using var context = RewriteTestContext.Create();
        string fixturePath = context.CompileFixture("Fx.ConfigureAwaitSources", ConfigureAwaitFixture);
        var rules = new RewriteRuleSet(
            "clockwork.configure-await-sources",
            "1.0",
            [AwaiterSubstitution("bool-only-configured-awaitable", "ConfiguredTaskAwaitable", "BoolOnlyConfiguredTaskAwaitable")]);

        RewriteResult result = context.Rewrite(fixturePath, rules);

        Assert.False(result.Succeeded);
        Assert.False(result.WasWritten);
        RewriteDiagnostic error = Assert.Single(result.Errors);
        Assert.Equal(RewriteDiagnosticIds.UnresolvedReplacement, error.Id);
        Assert.Equal("Fx.ConfigureAwaitSources.Options", error.Method);
        Assert.Contains(
            "System.Threading.Tasks.Task::ConfigureAwait(System.Threading.Tasks.ConfigureAwaitOptions)",
            error.Message,
            StringComparison.Ordinal);
    }

    private static bool HasParameters(MethodReference method, params string[] parameterTypes) =>
        method.Parameters.Select(parameter => parameter.ParameterType.FullName).SequenceEqual(parameterTypes);

    private static RewriteRule AwaiterSubstitution(string id, string bclType, string shimType) =>
        RewriteRule.SubstituteType(
            id,
            "System.Runtime.CompilerServices." + bclType,
            RewriteReplacement.Type(FixtureSources.ShimAssemblyName, "ClockworkFixtures.Shims." + shimType));

    private static string ConstructedReceiver(ModuleDefinition module, string methodName)
    {
        MethodDefinition method = CecilInspect.GetMethod(module, "Fx.AwaiterSources", methodName);
        MethodReference constructor = Assert.Single(
            method.Body.Instructions.Where(instruction => instruction.OpCode == OpCodes.Newobj)
                .Select(instruction => (MethodReference)instruction.Operand));
        Assert.StartsWith("ClockworkFixtures.Shims.Decoy", constructor.DeclaringType.FullName, StringComparison.Ordinal);
        return Assert.Single(constructor.Parameters).ParameterType.FullName;
    }
}
