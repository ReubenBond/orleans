using Orleans.Testing.Simulation.Instrumentation.Diagnostics;
using Orleans.Testing.Simulation.Instrumentation.Rewriting;
using Orleans.Testing.Simulation.Instrumentation.Tests.Infrastructure;

namespace Orleans.Testing.Simulation.Instrumentation.Tests.Golden;

/// <summary>
/// End-to-end golden tests for the cross-assembly task-detection pass. A consumer assembly is
/// rewritten while it calls into a separate <em>uncontrolled</em> dependency assembly (neither rewritten,
/// nor BCL, nor the shim). Every call whose return type is a <see cref="System.Threading.Tasks.Task"/>,
/// <see cref="System.Threading.Tasks.ValueTask"/>, their generic forms, or a custom awaitable must produce a
/// precise <see cref="RewriteDiagnosticIds.UncontrolledTaskReturn"/> warning naming the callee and the
/// call-site method - so a task whose continuation could escape the scheduler is never silently accepted -
/// while calls returning ordinary values and calls into the BCL are left unflagged.
/// </summary>
public sealed class CrossAssemblyTaskDetectionGoldenTests
{
    private const string DependencySource = """
        using System.Runtime.CompilerServices;
        using System.Threading.Tasks;

        namespace Dep
        {
            public sealed class CustomAwaiter : INotifyCompletion
            {
                public bool IsCompleted => true;
                public void OnCompleted(System.Action continuation) => continuation();
                public int GetResult() => 0;
            }

            public sealed class CustomAwaitable
            {
                public CustomAwaiter GetAwaiter() => new CustomAwaiter();
            }

            public static class Uncontrolled
            {
                public static Task ReturnsTask() => Task.CompletedTask;
                public static Task<int> ReturnsTaskOfInt() => Task.FromResult(1);
                public static ValueTask ReturnsValueTask() => default;
                public static ValueTask<int> ReturnsValueTaskOfInt() => new ValueTask<int>(2);
                public static CustomAwaitable ReturnsCustomAwaitable() => new CustomAwaitable();
                public static int ReturnsPlainValue() => 3;
            }
        }
        """;

    private const string ConsumerSource = """
        using System.Threading.Tasks;
        using Dep;

        namespace Fx
        {
            public static class Consumer
            {
                public static void CallsTask() => Uncontrolled.ReturnsTask();
                public static void CallsTaskOfInt() { var t = Uncontrolled.ReturnsTaskOfInt(); }
                public static void CallsValueTask() { var v = Uncontrolled.ReturnsValueTask(); }
                public static void CallsValueTaskOfInt() { var v = Uncontrolled.ReturnsValueTaskOfInt(); }
                public static void CallsCustomAwaitable() { var a = Uncontrolled.ReturnsCustomAwaitable(); }
                public static void CallsPlainValue() { var n = Uncontrolled.ReturnsPlainValue(); }
                public static Task<int> CallsControlledBcl() => Task.FromResult(5);
            }
        }
        """;

    [Fact]
    public void UncontrolledAwaitableReturningCallsAreDiagnosed()
    {
        using var context = RewriteTestContext.Create();

        string dependencyPath = FixtureCompiler.Compile(
            "Dep.Uncontrolled", DependencySource, context.Directory, FixtureSymbols.PortableFile, optimize: false);
        string consumerPath = FixtureCompiler.Compile(
            "Fx.Consumer", ConsumerSource, context.Directory, FixtureSymbols.PortableFile, optimize: false,
            additionalReferencePaths: [dependencyPath]);

        string outputPath = Path.Combine(context.Directory, "Fx.Consumer.rewritten.dll");
        var options = new RewriteOptions
        {
            TargetRuntime = RewriteTestContext.TargetRuntime,
            DetectUncontrolledTasks = true,
            ReplacementAssemblyPaths = [context.ShimPath],
            ReferenceSearchDirectories = [context.Directory],
        };

        var result = RewriteEngine.Rewrite(
            new RewriteRequest(consumerPath, outputPath, RewriteTestContext.StandardRuleSet(), options));
        result.EnsureSuccess();

        var warnings = result.Diagnostics
            .Where(d => d.Id == RewriteDiagnosticIds.UncontrolledTaskReturn)
            .ToList();

        foreach (string callee in new[]
        {
            "ReturnsTask", "ReturnsTaskOfInt", "ReturnsValueTask", "ReturnsValueTaskOfInt", "ReturnsCustomAwaitable",
        })
        {
            Assert.True(
                warnings.Exists(w => w.Message.Contains(callee, StringComparison.Ordinal)),
                $"expected a cross-assembly warning for '{callee}'");
        }

        // Exactly the five awaitable-returning uncontrolled calls are flagged.
        Assert.Equal(5, warnings.Count);

        // A plain value-returning uncontrolled call is not an escape.
        Assert.DoesNotContain(warnings, w => w.Message.Contains("ReturnsPlainValue", StringComparison.Ordinal));

        // The BCL is controlled through explicit rules / benign primitives, so System.* is never flagged.
        Assert.DoesNotContain(warnings, w => w.Message.Contains("System.Threading.Tasks.Task::FromResult", StringComparison.Ordinal));

        // The diagnostics carry a precise call-site (the containing consumer method).
        Assert.All(warnings, w => Assert.Contains("Fx.Consumer", w.Method!, StringComparison.Ordinal));
    }

    [Fact]
    public void UnresolvableCustomAwaitableReturnTypeIsDiagnosed()
    {
        using var context = RewriteTestContext.Create();
        string dependencyPath = FixtureCompiler.Compile(
            "Dep.Uncontrolled", DependencySource, context.Directory, FixtureSymbols.PortableFile, optimize: false);
        string consumerPath = FixtureCompiler.Compile(
            "Fx.Consumer.MissingDep", ConsumerSource, context.Directory, FixtureSymbols.PortableFile, optimize: false,
            additionalReferencePaths: [dependencyPath]);
        File.Delete(dependencyPath);
        File.Delete(Path.ChangeExtension(dependencyPath, "pdb"));

        var options = new RewriteOptions
        {
            TargetRuntime = RewriteTestContext.TargetRuntime,
            DetectUncontrolledTasks = true,
            ReplacementAssemblyPaths = [context.ShimPath],
            ReferenceSearchDirectories = [context.Directory],
        };
        RewriteResult result = RewriteEngine.Rewrite(new RewriteRequest(
            consumerPath,
            Path.Combine(context.Directory, "Fx.Consumer.MissingDep.rewritten.dll"),
            RewriteTestContext.StandardRuleSet(),
            options));

        result.EnsureSuccess();
        RewriteDiagnostic diagnostic = Assert.Single(
            result.Diagnostics,
            candidate => candidate.Id == RewriteDiagnosticIds.AwaitableResolutionFailed);
        Assert.Contains("Dep.CustomAwaitable", diagnostic.Message);
        Assert.Contains("ReturnsCustomAwaitable", diagnostic.Message);
    }
}
