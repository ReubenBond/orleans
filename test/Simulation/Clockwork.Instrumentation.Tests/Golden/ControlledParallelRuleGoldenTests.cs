using System.Collections.Immutable;
using Orleans.Testing.Simulation.Instrumentation.Manifest;
using Orleans.Testing.Simulation.Instrumentation.Rewriting;
using Orleans.Testing.Simulation.Instrumentation.Rules;
using Orleans.Testing.Simulation.Instrumentation.Rules.BuiltIn;
using Orleans.Testing.Simulation.Instrumentation.Tests.Infrastructure;
using Orleans.Testing.Simulation.Runtime.Policy;
using Mono.Cecil;

namespace Orleans.Testing.Simulation.Instrumentation.Tests.Golden;

/// <summary>
/// End-to-end golden tests for the shipped controlled <see cref="System.Threading.Tasks.Parallel"/> rule
/// family: they rewrite real <c>Parallel.Invoke</c> / <c>For</c> / <c>ForEach</c> / <c>ForAsync</c> /
/// <c>ForEachAsync</c> call sites in a compiled fixture against the real <c>Clockwork</c> assembly and assert on the
/// rewritten IL and manifest. This proves the static signatures declared in <c>BuiltInRuleSets</c> line up with the
/// actual <see cref="Orleans.Testing.Simulation.Shims.System.Threading.Tasks.ControlledParallel"/> members, and that the overloads whose
/// body receives a <c>ParallelLoopState</c> (break/stop and thread-local <c>TLocal</c>) are rejected at the call site.
/// </summary>
public sealed class ControlledParallelRuleGoldenTests
{
    private const string Fixture = """
        using System.Collections.Concurrent;
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;

        namespace Fx
        {
            public static class ParallelUser
            {
                public static void Invoke() => Parallel.Invoke(() => { }, () => { });
                public static void InvokeOptions(ParallelOptions o) => Parallel.Invoke(o, () => { });
                public static ParallelLoopResult ForInt() => Parallel.For(0, 3, i => { });
                public static ParallelLoopResult ForIntOptions(ParallelOptions o) => Parallel.For(0, 3, o, i => { });
                public static ParallelLoopResult ForLong() => Parallel.For(0L, 3L, i => { });
                public static ParallelLoopResult ForLongOptions(ParallelOptions o) => Parallel.For(0L, 3L, o, i => { });
                public static ParallelLoopResult ForEach(IEnumerable<int> s) => Parallel.ForEach(s, i => { });
                public static ParallelLoopResult ForEachOptions(IEnumerable<int> s, ParallelOptions o) => Parallel.ForEach(s, o, i => { });
                public static ParallelLoopResult ForEachPartitioner(IEnumerable<int> s) => Parallel.ForEach(Partitioner.Create(s), i => { });
                public static ParallelLoopResult ForEachPartitionerOptions(IEnumerable<int> s, ParallelOptions o) => Parallel.ForEach(Partitioner.Create(s), o, i => { });
                public static Task ForAsync() => Parallel.ForAsync(0, 3, (i, ct) => default);
                public static Task ForAsyncToken(CancellationToken t) => Parallel.ForAsync(0L, 3L, t, (i, ct) => default);
                public static Task ForAsyncOptions(ParallelOptions o) => Parallel.ForAsync(0, 3, o, (i, ct) => default);
                public static Task ForEachAsync(IEnumerable<int> s) => Parallel.ForEachAsync(s, (i, ct) => default);
                public static Task ForEachAsyncToken(IEnumerable<int> s, CancellationToken t) => Parallel.ForEachAsync(s, t, (i, ct) => default);
                public static Task ForEachAsyncOptions(IEnumerable<int> s, ParallelOptions o) => Parallel.ForEachAsync(s, o, (i, ct) => default);
                public static Task ForEachAsyncStream(IAsyncEnumerable<int> s) => Parallel.ForEachAsync(s, (i, ct) => default);
                public static Task ForEachAsyncStreamToken(IAsyncEnumerable<int> s, CancellationToken t) => Parallel.ForEachAsync(s, t, (i, ct) => default);
                public static Task ForEachAsyncStreamOptions(IAsyncEnumerable<int> s, ParallelOptions o) => Parallel.ForEachAsync(s, o, (i, ct) => default);

                public static ParallelLoopResult ForLoopState() => Parallel.For(0, 3, (i, state) => state.Break());
                public static ParallelLoopResult ForEachLoopState(IEnumerable<int> s) => Parallel.ForEach(s, (i, state) => state.Break());
                public static ParallelLoopResult ForEachLoopStateIndex(IEnumerable<int> s) => Parallel.ForEach(s, (i, state, idx) => state.Break());
                public static ParallelLoopResult ForEachLoopStateOptions(IEnumerable<int> s, ParallelOptions o) => Parallel.ForEach(s, o, (i, state) => state.Break());
                public static ParallelLoopResult ForLocal() => Parallel.For(0, 3, () => 0, (i, state, local) => local + i, local => { });
                public static ParallelLoopResult ForEachLocal(IEnumerable<int> s) => Parallel.ForEach(s, () => 0, (i, state, local) => local + i, local => { });
                public static ParallelLoopResult ForEachPartitionerLoopState(IEnumerable<int> s) => Parallel.ForEach(Partitioner.Create(s), (i, state) => state.Stop());
                public static ParallelLoopResult ForEachOrderablePartitioner(IList<int> s) => Parallel.ForEach(Partitioner.Create(s, true), (i, state, idx) => { });
            }
        }
        """;

    private static string RuntimeAssemblyPath =>
        typeof(Orleans.Testing.Simulation.Shims.System.Threading.Tasks.ControlledParallel).Assembly.Location;

    private static RewriteResult RewriteFixture(RewriteTestContext context, string assemblyName)
    {
        string fixturePath = context.CompileFixture(assemblyName, Fixture);
        string outputPath = Path.Combine(
            context.Directory, Path.GetFileNameWithoutExtension(fixturePath) + ".rewritten.dll");

        var options = new RewriteOptions
        {
            TargetRuntime = RewriteTestContext.TargetRuntime,
            ReplacementAssemblyPaths = [RuntimeAssemblyPath],
            ReferenceSearchDirectories = [context.Directory, Path.GetDirectoryName(RuntimeAssemblyPath)!],
        };

        RewriteRuleSet ruleSet = BuiltInRuleSets.BuildControlledTasks(BuiltInRuleSets.AllFamilies);
        var result = RewriteEngine.Rewrite(new RewriteRequest(fixturePath, outputPath, ruleSet, options));
        result.EnsureSuccess();
        return result;
    }

    [Fact]
    public void SimpleBodyOverloadsAreRedirectedToControlledShim()
    {
        using var context = RewriteTestContext.Create();
        var result = RewriteFixture(context, "Fx.ParallelControlled");

        using ModuleDefinition module = context.LoadModule(
            Path.Combine(context.Directory, "Fx.ParallelControlled.rewritten.dll"));

        var expected = new (string Method, string Shim)[]
        {
            ("Invoke", "ControlledParallel::Invoke"),
            ("InvokeOptions", "ControlledParallel::Invoke"),
            ("ForInt", "ControlledParallel::For"),
            ("ForIntOptions", "ControlledParallel::For"),
            ("ForLong", "ControlledParallel::For"),
            ("ForLongOptions", "ControlledParallel::For"),
            ("ForEach", "ControlledParallel::ForEach"),
            ("ForEachOptions", "ControlledParallel::ForEach"),
            ("ForEachPartitioner", "ControlledParallel::ForEach"),
            ("ForEachPartitionerOptions", "ControlledParallel::ForEach"),
            ("ForAsync", "ControlledParallel::ForAsync"),
            ("ForAsyncToken", "ControlledParallel::ForAsync"),
            ("ForAsyncOptions", "ControlledParallel::ForAsync"),
            ("ForEachAsync", "ControlledParallel::ForEachAsync"),
            ("ForEachAsyncToken", "ControlledParallel::ForEachAsync"),
            ("ForEachAsyncOptions", "ControlledParallel::ForEachAsync"),
            ("ForEachAsyncStream", "ControlledParallel::ForEachAsync"),
            ("ForEachAsyncStreamToken", "ControlledParallel::ForEachAsync"),
            ("ForEachAsyncStreamOptions", "ControlledParallel::ForEachAsync"),
        };

        foreach (var (method, shim) in expected)
        {
            MethodDefinition definition = CecilInspect.GetMethod(module, "Fx.ParallelUser", method);
            Assert.True(CecilInspect.CallsAnyContaining(definition, shim), $"{method} should call {shim}");
            Assert.False(CecilInspect.CallsAnyContaining(definition, "Tasks.Parallel::"), $"{method} should not call the real Parallel");
        }

        ImmutableArray<ManifestTransformation> transformations = result.Manifest.Transformations;
        Assert.Contains(transformations, t =>
            t.RuleId == "clockwork.parallel.foreach" && t.Policy == SimulationApiPolicy.Controlled);
        Assert.Contains(transformations, t =>
            t.RuleId == "clockwork.parallel.for.int64" && t.Policy == SimulationApiPolicy.Controlled);
        Assert.Contains(transformations, t =>
            t.RuleId == "clockwork.parallel.foreach.partitioner" && t.Policy == SimulationApiPolicy.Controlled);
        Assert.Contains(transformations, t =>
            t.RuleId == "clockwork.parallel.forasync.cancellationtoken" && t.Policy == SimulationApiPolicy.Controlled);
        Assert.Contains(transformations, t =>
            t.RuleId == "clockwork.parallel.foreachasync.asyncenumerable.options" && t.Policy == SimulationApiPolicy.Controlled);
    }

    [Fact]
    public void LoopStateAndThreadLocalOverloadsAreRejectedAtTheCallSite()
    {
        using var context = RewriteTestContext.Create();
        var result = RewriteFixture(context, "Fx.ParallelRejected");

        using ModuleDefinition module = context.LoadModule(
            Path.Combine(context.Directory, "Fx.ParallelRejected.rewritten.dll"));

        foreach (var method in new[]
        {
            "ForLoopState", "ForEachLoopState", "ForEachLoopStateIndex", "ForEachLoopStateOptions", "ForLocal", "ForEachLocal",
            "ForEachPartitionerLoopState", "ForEachOrderablePartitioner",
        })
        {
            MethodDefinition definition = CecilInspect.GetMethod(module, "Fx.ParallelUser", method);
            Assert.True(CecilInspect.CallsAnyContaining(definition, "ControlledParallel::RejectUnsupported"), $"{method} should be rejected");
        }

        ImmutableArray<ManifestTransformation> transformations = result.Manifest.Transformations;
        Assert.Contains(transformations, t =>
            t.RuleId == "clockwork.parallel.for.int32.loopstate" && t.Policy == SimulationApiPolicy.Rejected);
        Assert.Contains(transformations, t =>
            t.RuleId == "clockwork.parallel.foreach.loopstate.index" && t.Policy == SimulationApiPolicy.Rejected);
        Assert.Contains(transformations, t =>
            t.RuleId == "clockwork.parallel.for.int32.local" && t.Policy == SimulationApiPolicy.Rejected);
        Assert.Contains(transformations, t =>
            t.RuleId == "clockwork.parallel.foreach.local" && t.Policy == SimulationApiPolicy.Rejected);
        Assert.Contains(transformations, t =>
            t.RuleId == "clockwork.parallel.foreach.loopstate.options" && t.Policy == SimulationApiPolicy.Rejected);
        Assert.Contains(transformations, t =>
            t.RuleId == "clockwork.parallel.foreach.partitioner.loopstate" && t.Policy == SimulationApiPolicy.Rejected);
        Assert.Contains(transformations, t =>
            t.RuleId == "clockwork.parallel.foreach.orderablepartitioner.loopstate.index" && t.Policy == SimulationApiPolicy.Rejected);
    }
}
