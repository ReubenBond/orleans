using Orleans.Testing.Simulation.Instrumentation.Manifest;
using Orleans.Testing.Simulation.Instrumentation.Rewriting;
using Orleans.Testing.Simulation.Instrumentation.Rules.BuiltIn;
using Orleans.Testing.Simulation.Instrumentation.Tests.Infrastructure;
using Orleans.Testing.Simulation.Runtime.Policy;
using Mono.Cecil;

namespace Orleans.Testing.Simulation.Instrumentation.Tests.Golden;

/// <summary>
/// Golden test for the runtime-async <c>AsyncHelpers.Await</c> intrinsics that take a configured awaitable. Their
/// parameter is a substituted type, so without a rule the rewrite fails with an unbindable-reference <c>CWR0001</c>;
/// instead each redirects to a shim that takes the substitute and rejects the call by name.
/// </summary>
public sealed class AsyncHelpersRuleGoldenTests
{
    private const string Fixture = """
        #pragma warning disable SYSLIB5007
        using System.Runtime.CompilerServices;
        using System.Threading.Tasks;

        namespace Fx
        {
            public static class RuntimeAsync
            {
                public static void AwaitTask(Task task) => AsyncHelpers.Await(task.ConfigureAwait(false));

                public static int AwaitTaskOfT(Task<int> task) => AsyncHelpers.Await(task.ConfigureAwait(false));

                public static void AwaitValueTask(ValueTask task) => AsyncHelpers.Await(task.ConfigureAwait(false));

                public static int AwaitValueTaskOfT(ValueTask<int> task) => AsyncHelpers.Await(task.ConfigureAwait(false));
            }
        }
        """;

    public static TheoryData<string, string, string> Overloads => new()
    {
        { "AwaitTask", "clockwork.tasks.asynchelpers.await.configured", "ControlledAsyncHelpers::Await(Orleans.Testing.Simulation.Shims.System.Runtime.CompilerServices.ControlledConfiguredTaskAwaitable)" },
        { "AwaitTaskOfT", "clockwork.tasks.asynchelpers.await.configured.generic", "ControlledAsyncHelpers::Await<System.Int32>(" },
        { "AwaitValueTask", "clockwork.tasks.asynchelpers.await.configured.valuetask", "ControlledAsyncHelpers::Await(Orleans.Testing.Simulation.Shims.System.Runtime.CompilerServices.ControlledConfiguredValueTaskAwaitable)" },
        { "AwaitValueTaskOfT", "clockwork.tasks.asynchelpers.await.configured.valuetask.generic", "ControlledAsyncHelpers::Await<System.Int32>(" },
    };

    [Theory]
    [MemberData(nameof(Overloads))]
    public void ConfiguredAwaitIntrinsicRedirectsToARejectingShim(string method, string ruleId, string target)
    {
        using var context = RewriteTestContext.Create();
        string fixturePath = context.CompileFixture("Fx.RuntimeAsync", Fixture);
        string runtimePath = typeof(ControlledAsyncHelpers).Assembly.Location;
        var options = new RewriteOptions
        {
            TargetRuntime = RewriteTestContext.TargetRuntime,
            ReplacementAssemblyPaths = [runtimePath],
            ReferenceSearchDirectories = [context.Directory, Path.GetDirectoryName(runtimePath)!],
        };

        RewriteResult result = context.Rewrite(
            fixturePath,
            BuiltInRuleSets.BuildControlledTasks(BuiltInRuleSets.AllFamilies),
            options);

        result.EnsureSuccess();
        using ModuleDefinition module = context.LoadModule(Path.Combine(context.Directory, "Fx.RuntimeAsync.rewritten.dll"));
        List<string> targets = CecilInspect.CallTargets(CecilInspect.GetMethod(module, "Fx.RuntimeAsync", method));
        Assert.Contains(targets, call => call.Contains(target, StringComparison.Ordinal));
        Assert.DoesNotContain(targets, call => call.Contains("AsyncHelpers::Await", StringComparison.Ordinal)
            && !call.Contains("ControlledAsyncHelpers", StringComparison.Ordinal));
        ManifestTransformation transformation = Assert.Single(
            result.Manifest.Transformations,
            transformation => transformation.RuleId == ruleId);
        Assert.Equal(SimulationApiPolicy.Rejected, transformation.Policy);
        Assert.Equal($"Fx.RuntimeAsync.{method}", transformation.Method);
    }
}
