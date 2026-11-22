using Orleans.Testing.Simulation.Instrumentation.Configuration;
using Orleans.Testing.Simulation.Instrumentation.Diagnostics;
using Orleans.Testing.Simulation.Instrumentation.Rewriting;
using Orleans.Testing.Simulation.Instrumentation.Rules;
using Orleans.Testing.Simulation.Instrumentation.Rules.BuiltIn;
using Orleans.Testing.Simulation.Instrumentation.Tests.Infrastructure;
using Mono.Cecil;

namespace Orleans.Testing.Simulation.Instrumentation.Tests.Golden;

/// <summary>Proves malformed JSON replacement contracts fail before invalid IL is emitted.</summary>
public sealed class ReplacementContractGoldenTests
{
    private const string Fixture = """
        using ClockworkFixtures.Api;

        namespace Fx
        {
            public static class Contracts
            {
                public static long Ticks() => RealClock.UtcNowTicks();
                public static int Instance() => new Service(1).GetValue();
                public static Widget Create() => new Widget(1);
                public static int Generic() => GenericOps.Echo(1);
                public static int Wrap() => new Meterable().Measure();
                public static void Reject() => Forbidden.DangerousWrite("x");
            }
        }
        """;

    public static TheoryData<string, RewriteRule, string> MalformedRules => new()
    {
        {
            "instance replacement",
            RewriteRule.RedirectCall(
                "instance",
                MemberSignature.Method("ClockworkFixtures.Api.RealClock", "UtcNowTicks"),
                RewriteReplacement.Method(FixtureSources.ShimAssemblyName, "ClockworkFixtures.Shims.InvalidShim", "InstanceTicks")),
            RewriteDiagnosticIds.ReplacementContractMismatch
        },
        {
            "receiver mismatch",
            RewriteRule.RedirectCall(
                "receiver",
                MemberSignature.Method("ClockworkFixtures.Api.Service", "GetValue"),
                RewriteReplacement.Method(
                    FixtureSources.ShimAssemblyName,
                    "ClockworkFixtures.Shims.InvalidShim",
                    "WrongReceiver",
                    "System.String")),
            RewriteDiagnosticIds.ReplacementContractMismatch
        },
        {
            "base-call replacement of a non-virtual member",
            RewriteRule.RedirectCall(
                "basecall",
                MemberSignature.Method("ClockworkFixtures.Api.Service", "GetValue"),
                RewriteReplacement.Method(
                    FixtureSources.ShimAssemblyName,
                    "ClockworkFixtures.Shims.ClockShim",
                    "GetValue",
                    "ClockworkFixtures.Api.Service")) with
            {
                BaseCallReplacement = RewriteReplacement.Method(
                    FixtureSources.ShimAssemblyName,
                    "ClockworkFixtures.Shims.ClockShim",
                    "GetValue",
                    "ClockworkFixtures.Api.Service"),
            },
            RewriteDiagnosticIds.ReplacementContractMismatch
        },        {
            "return mismatch",
            RewriteRule.RedirectCall(
                "return",
                MemberSignature.Method("ClockworkFixtures.Api.RealClock", "UtcNowTicks"),
                RewriteReplacement.Method(FixtureSources.ShimAssemblyName, "ClockworkFixtures.Shims.InvalidShim", "WrongReturn")),
            RewriteDiagnosticIds.ReplacementContractMismatch
        },
        {
            "constructor return mismatch",
            RewriteRule.RedirectNewObj(
                "factory",
                MemberSignature.Constructor("ClockworkFixtures.Api.Widget", "System.Int32"),
                RewriteReplacement.Method(
                    FixtureSources.ShimAssemblyName,
                    "ClockworkFixtures.Shims.InvalidShim",
                    "WrongFactory",
                    "System.Int32")),
            RewriteDiagnosticIds.ReplacementContractMismatch
        },
        {
            "generic arity mismatch",
            RewriteRule.RedirectCall(
                "generic",
                new MemberSignature("ClockworkFixtures.Api.GenericOps", "Echo"),
                RewriteReplacement.Method(
                    FixtureSources.ShimAssemblyName,
                    "ClockworkFixtures.Shims.InvalidShim",
                    "GenericMismatch")),
            RewriteDiagnosticIds.ReplacementContractMismatch
        },
        {
            "post-call mismatch",
            RewriteRule.WrapAfterCall(
                "wrapper",
                MemberSignature.Method("ClockworkFixtures.Api.Meterable", "Measure"),
                RewriteReplacement.Method(
                    FixtureSources.ShimAssemblyName,
                    "ClockworkFixtures.Shims.InvalidShim",
                    "WrongWrapper",
                    "System.String")),
            RewriteDiagnosticIds.ReplacementContractMismatch
        },
        {
            "rejection mismatch",
            RewriteRule.InjectRejection(
                "rejection",
                MemberSignature.Method("ClockworkFixtures.Api.Forbidden", "DangerousWrite", "System.String"),
                RewriteReplacement.Method(FixtureSources.ShimAssemblyName, "ClockworkFixtures.Shims.InvalidShim", "WrongReject")),
            RewriteDiagnosticIds.ReplacementContractMismatch
        },
        {
            "ambiguous same-name replacement",
            RewriteRule.RedirectCall(
                "ambiguous",
                MemberSignature.Method("ClockworkFixtures.Api.RealClock", "UtcNowTicks"),
                RewriteReplacement.Method(FixtureSources.ShimAssemblyName, "ClockworkFixtures.Shims.InvalidShim", "Ambiguous")),
            RewriteDiagnosticIds.UnresolvedReplacement
        },
    };

    [Theory]
    [MemberData(nameof(MalformedRules))]
    public void MalformedJsonRuleFailsWithoutWritingInvalidIl(
        string name,
        RewriteRule authoredRule,
        string expectedDiagnostic)
    {
        using var context = RewriteTestContext.Create();
        string fixturePath = context.CompileFixture("Fx.Contracts." + name.Replace(' ', '.'), Fixture);
        string outputPath = fixturePath + ".rewritten.dll";
        RewriteRuleSet parsed = RuleSetJson.Parse(RuleSetJson.Write(
            new RewriteRuleSet("clockwork.contracts", "1.0", [authoredRule])));

        RewriteResult result = context.Rewrite(fixturePath, outputPath, parsed);

        Assert.False(result.Succeeded);
        Assert.False(result.WasWritten);
        Assert.False(File.Exists(outputPath));
        Assert.Contains(result.Errors, diagnostic => diagnostic.Id == expectedDiagnostic);
    }

    [Fact]
    public void ValueTypeInstanceReceiverRequiresManagedPointerReplacement()
    {
        const string source = """
            using ClockworkFixtures.Api;
            namespace Fx
            {
                public static class ValueReceiver
                {
                    public static int Run()
                    {
                        var value = new StructProbe { N = 3 };
                        return value.Probe();
                    }
                }
            }
            """;
        using var context = RewriteTestContext.Create();
        string fixturePath = context.CompileFixture("Fx.ValueReceiver", source);
        var ruleSet = new RewriteRuleSet(
            "clockwork.value-receiver",
            "1.0",
            [
                RewriteRule.RedirectCall(
                    "value-receiver",
                    MemberSignature.Method("ClockworkFixtures.Api.StructProbe", "Probe"),
                    RewriteReplacement.Method(
                        FixtureSources.ShimAssemblyName,
                        "ClockworkFixtures.Shims.ClockShim",
                        "GetProbe",
                        "ClockworkFixtures.Api.StructProbe&")),
            ]);

        RewriteResult result = context.Rewrite(fixturePath, ruleSet);

        result.EnsureSuccess();
        using Mono.Cecil.ModuleDefinition module = context.LoadModule(
            Path.Combine(context.Directory, "Fx.ValueReceiver.rewritten.dll"));
        Assert.True(CecilInspect.CallsAnyContaining(
            CecilInspect.GetMethod(module, "Fx.ValueReceiver", "Run"),
            "ClockShim::GetProbe"));
    }

    [Fact]
    public void NestedGenericShapeWithForeignParameterOwnerRewrites()
    {
        const string source = """
            using ClockworkFixtures.Api;
            namespace Fx
            {
                public static class RecursiveContract<T>
                {
                    public static RecursiveContainer<T>.Element Read(
                        GenericBox<RecursiveContainer<T>.Element> box) => box.Value;
                }
            }
            """;
        using var context = RewriteTestContext.Create();
        string fixturePath = context.CompileFixture("Fx.RecursiveContract", source);
        var ruleSet = RecursiveBoxRuleSet("ReadBox");

        RewriteResult result = context.Rewrite(fixturePath, ruleSet);

        result.EnsureSuccess();
        using Mono.Cecil.ModuleDefinition module = context.LoadModule(
            Path.Combine(context.Directory, "Fx.RecursiveContract.rewritten.dll"));
        Assert.True(CecilInspect.CallsAnyContaining(
            CecilInspect.GetMethod(module, "Fx.RecursiveContract`1", "Read"),
            "ClockShim::ReadBox"));
    }

    [Fact]
    public void IncompatibleNestedGenericShapeReportsContractMismatch()
    {
        const string source = """
            using ClockworkFixtures.Api;
            namespace Fx
            {
                public static class RecursiveContract<T>
                {
                    public static RecursiveContainer<T>.Element Read(
                        GenericBox<RecursiveContainer<T>.Element> box) => box.Value;
                }
            }
            """;
        using var context = RewriteTestContext.Create();
        string fixturePath = context.CompileFixture("Fx.RecursiveContract.Invalid", source);

        RewriteResult result = context.Rewrite(fixturePath, RecursiveBoxRuleSet("ReadBoxWrong"));

        Assert.False(result.Succeeded);
        Assert.False(result.WasWritten);
        RewriteDiagnostic diagnostic = Assert.Single(
            result.Errors,
            error => error.Id == RewriteDiagnosticIds.ReplacementContractMismatch);
        Assert.Contains("RecursiveContainer`1/Element<!0>", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("System.Collections.Generic.List`1", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RecursiveGenericOwnerShapesWorkWithMergedBuiltIns()
    {
        const string source = """
            using System;
            using System.Collections.Generic;
            using System.Threading.Tasks;

            namespace Fx
            {
                public static class MethodOwner
                {
                    public static Task<Task<List<T>>> Any<T>(
                        Task<List<T>> first,
                        Task<List<T>> second) =>
                        Task.WhenAny(first, second);
                }

                public sealed class TypeOwner<T>
                {
                    public static List<T> Result(Task<List<T>> task) => task.Result;
                }

                public static class ClockProbe
                {
                    public static DateTime Now() => DateTime.UtcNow;
                }
            }
            """;
        using var context = RewriteTestContext.Create();
        string fixturePath = context.CompileFixture("Fx.RecursiveBuiltIns", source);
        string runtimePath = typeof(Orleans.Testing.Simulation.Shims.System.Threading.Tasks.ControlledTask).Assembly.Location;
        var configuration = new InstrumentationConfiguration
        {
            BuiltInRuleSetIds =
            [
                BuiltInRuleSets.DeterministicBclId,
                BuiltInRuleSets.ControlledTasksId,
            ],
            BuiltInIncludeFamilies =
            [
                nameof(BuiltInRuleFamily.Clock),
                nameof(BuiltInRuleFamily.TaskCombinators),
                nameof(BuiltInRuleFamily.TaskSynchronization),
            ],
            TargetRuntime = BuiltInRuleSets.SupportedRuntime,
        };
        RewriteRuleSet rules = RuleSetMerge.LoadAndMerge(configuration).RuleSet;
        var options = new RewriteOptions
        {
            ReplacementAssemblyPaths = [runtimePath],
            ReferenceSearchDirectories = [context.Directory, Path.GetDirectoryName(runtimePath)!],
            TargetRuntime = BuiltInRuleSets.SupportedRuntime,
        };

        RewriteResult result = context.Rewrite(fixturePath, rules, options);

        result.EnsureSuccess();
        Assert.Equal("clockwork.merged", rules.Id);
        Assert.Contains(result.Manifest.Transformations, t => t.RuleId == "clockwork.tasks.whenany.generic.pair");
        Assert.Contains(result.Manifest.Transformations, t => t.RuleId == "clockwork.tasks.result.generic");
        Assert.Contains(result.Manifest.Transformations, t => t.RuleId == "clockwork.bcl.datetime.utcnow");
        using ModuleDefinition module = context.LoadModule(
            Path.Combine(context.Directory, "Fx.RecursiveBuiltIns.rewritten.dll"));
        Assert.True(CecilInspect.CallsAnyContaining(
            CecilInspect.GetMethod(module, "Fx.MethodOwner", "Any"),
            "ControlledTask::WhenAny"));
        Assert.True(CecilInspect.CallsAnyContaining(
            CecilInspect.GetMethod(module, "Fx.TypeOwner`1", "Result"),
            "ControlledTask::Result"));
        Assert.True(CecilInspect.CallsAnyContaining(
            CecilInspect.GetMethod(module, "Fx.ClockProbe", "Now"),
            "ControlledDateTime::GetUtcNow"));
    }

    [Fact]
    public void RecursiveSelfCallPreservesCallerGenericArgumentContext()
    {
        const string source = """
            using System.Collections.Generic;

            namespace Fx
            {
                public static class Recursive
                {
                    public static List<T> M<T>(List<T> value)
                    {
                        _ = M<List<T>>(new List<List<T>>());
                        return value;
                    }
                }
            }
            """;
        const string shimSource = """
            using System.Collections.Generic;

            namespace Fx
            {
                public static class RecursiveShim
                {
                    public static List<T> M<T>(List<T> value) => value;
                }
            }
            """;
        using var context = RewriteTestContext.Create();
        string fixturePath = context.CompileFixture("Fx.RecursiveSelfCall", source);
        string shimPath = FixtureCompiler.Compile(
            "Fx.RecursiveShim",
            shimSource,
            context.Directory,
            FixtureSymbols.PortableFile,
            optimize: false);
        var rules = new RewriteRuleSet(
            "clockwork.recursive-self-call",
            "1.0",
            [
                RewriteRule.RedirectCall(
                    "recursive-self-call",
                    new MemberSignature("Fx.Recursive", "M"),
                    RewriteReplacement.Method("Fx.RecursiveShim", "Fx.RecursiveShim", "M")),
            ]);
        var options = new RewriteOptions
        {
            TargetRuntime = RewriteTestContext.TargetRuntime,
            ReplacementAssemblyPaths = [shimPath],
            ReferenceSearchDirectories = [context.Directory],
        };

        RewriteResult result = context.Rewrite(fixturePath, rules, options);

        result.EnsureSuccess();
        using ModuleDefinition module = context.LoadModule(
            Path.Combine(context.Directory, "Fx.RecursiveSelfCall.rewritten.dll"));
        MethodDefinition method = CecilInspect.GetMethod(module, "Fx.Recursive", "M");
        Assert.True(CecilInspect.CallsAnyContaining(method, "RecursiveShim::M"));
        Assert.False(CecilInspect.CallsAnyContaining(method, "Recursive::M"));
    }

    private static RewriteRuleSet RecursiveBoxRuleSet(string replacementMethod) =>
        new(
            "clockwork.recursive-contract",
            "1.0",
            [
                RewriteRule.RedirectCall(
                    "recursive-box-value",
                    new MemberSignature("ClockworkFixtures.Api.GenericBox`1", "get_Value"),
                    RewriteReplacement.Method(
                        FixtureSources.ShimAssemblyName,
                        "ClockworkFixtures.Shims.ClockShim",
                        replacementMethod)),
            ]);
}
