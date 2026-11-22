using Orleans.Testing.Simulation.Instrumentation.Diagnostics;
using Orleans.Testing.Simulation.Instrumentation.Rewriting;
using Orleans.Testing.Simulation.Instrumentation.Rules;
using Orleans.Testing.Simulation.Instrumentation.Rules.BuiltIn;
using Orleans.Testing.Simulation.Instrumentation.Tests.Infrastructure;
using Mono.Cecil;

namespace Orleans.Testing.Simulation.Instrumentation.Tests.Golden;

/// <summary>
/// Golden tests for the configured async disposable and enumerable substitutions, and for the rule set without them,
/// whose configured value-task awaitable substitutions leave BCL members that cannot bind and so fail the rewrite.
/// </summary>
public sealed class ConfiguredAsyncStreamRuleGoldenTests
{
    private const string Fixture = """
        using System;
        using System.Collections.Generic;
        using System.Runtime.CompilerServices;
        using System.Threading;
        using System.Threading.Tasks;

        namespace Fx
        {
            public static class Streams
            {
                public static async Task Using(IAsyncDisposable resource)
                {
                    await using (resource.ConfigureAwait(false))
                    {
                    }
                }

                public static async Task<int> Sum(IAsyncEnumerable<int> source, CancellationToken cancellationToken)
                {
                    int sum = 0;
                    await foreach (int item in source.ConfigureAwait(false))
                    {
                        sum += item;
                    }

                    await foreach (int item in source.WithCancellation(cancellationToken))
                    {
                        sum += item;
                    }

                    await foreach (int item in source.WithCancellation(cancellationToken).ConfigureAwait(true))
                    {
                        sum += item;
                    }

                    await foreach (int item in source.ConfigureAwait(false).WithCancellation(cancellationToken))
                    {
                        sum += item;
                    }

                    return sum;
                }

                public static async IAsyncEnumerable<int> Produce([EnumeratorCancellation] CancellationToken cancellationToken = default)
                {
                    await Task.Yield();
                    yield return 1;
                }
            }
        }
        """;

    private static readonly string[] ConfiguredRules =
    [
        "clockwork.tasks.configured.asyncdisposable",
        "clockwork.tasks.configured.asyncenumerable",
        "clockwork.tasks.configured.asyncenumerator",
        "clockwork.tasks.asyncdisposable.configureawait",
        "clockwork.tasks.asyncenumerable.configureawait",
        "clockwork.tasks.asyncenumerable.withcancellation",
    ];

    private static string RuntimeAssemblyPath =>
        typeof(ControlledConfiguredAsyncDisposable).Assembly.Location;

    [Fact]
    public void ConfiguredAsyncStreamsAreRetargetedAndEveryReferenceBinds()
    {
        using var context = RewriteTestContext.Create();
        RewriteResult result = Rewrite(context, "Fx.ConfiguredStreams", BuiltInRuleSets.BuildControlledTasks(BuiltInRuleSets.AllFamilies));

        // The validator resolves every reference whose binding depends on a substitution, so success means they bind.
        result.EnsureSuccess();

        using ModuleDefinition module = context.LoadModule(Path.Combine(context.Directory, "Fx.ConfiguredStreams.rewritten.dll"));
        MemberReference[] members = ReferencedMembers(module);
        string[] targets = [.. members.Select(member => member.FullName)];

        Assert.DoesNotContain(targets, target => target.Contains("System.Runtime.CompilerServices.Configured", StringComparison.Ordinal));
        Assert.DoesNotContain(targets, target => target.Contains("System.Threading.Tasks.TaskAsyncEnumerableExtensions", StringComparison.Ordinal));
        Assert.Contains(targets, target => target.Contains(
            "ControlledTaskAsyncEnumerableExtensions::ConfigureAwait(System.IAsyncDisposable,System.Boolean)", StringComparison.Ordinal));
        Assert.Contains(targets, target => target.Contains(
            "ControlledTaskAsyncEnumerableExtensions::ConfigureAwait<System.Int32>(", StringComparison.Ordinal));
        Assert.Contains(targets, target => target.Contains(
            "ControlledTaskAsyncEnumerableExtensions::WithCancellation<System.Int32>(", StringComparison.Ordinal));
        Assert.Contains(targets, target => target.Contains(
            "ControlledConfiguredAsyncDisposable::DisposeAsync()", StringComparison.Ordinal));
        Assert.Contains(targets, target => target.Contains(
            "ControlledConfiguredCancelableAsyncEnumerable`1<System.Int32>::ConfigureAwait(System.Boolean)", StringComparison.Ordinal));
        Assert.Contains(targets, target => target.Contains(
            "ControlledConfiguredCancelableAsyncEnumerable`1<System.Int32>::WithCancellation(System.Threading.CancellationToken)", StringComparison.Ordinal));
        Assert.Contains(targets, target => target.Contains(
            "ControlledConfiguredCancelableAsyncEnumerable`1/Enumerator<System.Int32>::MoveNextAsync()", StringComparison.Ordinal));
        Assert.Contains(targets, target => target.Contains(
            "ControlledConfiguredCancelableAsyncEnumerable`1/Enumerator<System.Int32>::DisposeAsync()", StringComparison.Ordinal));

        string[] applied = [.. result.Manifest.Transformations.Select(transformation => transformation.RuleId).Distinct(StringComparer.Ordinal)];
        Assert.All(ConfiguredRules, rule => Assert.Contains(rule, applied));
    }

    [Fact]
    public void SubstitutionWhoseFrameworkMemberCannotBindFailsTheRewrite()
    {
        // The configured value-task awaitables without the configured disposable and enumerable is the rule set
        // that produced ConfiguredAsyncDisposable.DisposeAsync() returning a controlled awaitable.
        RewriteRuleSet broken = new(
            "clockwork.tasks.controlled.broken",
            "1.0",
            BuiltInRuleSets.BuildControlledTasks(BuiltInRuleSets.AllFamilies).Rules
                .Where(rule => !ConfiguredRules.Contains(rule.Id, StringComparer.Ordinal)));
        using var context = RewriteTestContext.Create();

        RewriteResult result = Rewrite(context, "Fx.BrokenStreams", broken);

        Assert.False(result.Succeeded);
        Assert.False(result.WasWritten);
        AssertUnbound(result, "System.Runtime.CompilerServices.ConfiguredAsyncDisposable::DisposeAsync()");
        AssertUnbound(result, "System.Runtime.CompilerServices.ConfiguredCancelableAsyncEnumerable`1/Enumerator<System.Int32>::MoveNextAsync()");
        AssertUnbound(result, "System.Runtime.CompilerServices.ConfiguredCancelableAsyncEnumerable`1/Enumerator<System.Int32>::DisposeAsync()");
    }

    private static RewriteResult Rewrite(RewriteTestContext context, string assemblyName, RewriteRuleSet ruleSet)
    {
        string fixturePath = context.CompileFixture(assemblyName, Fixture);
        string outputPath = Path.Combine(context.Directory, assemblyName + ".rewritten.dll");
        var options = new RewriteOptions
        {
            TargetRuntime = RewriteTestContext.TargetRuntime,
            ReplacementAssemblyPaths = [RuntimeAssemblyPath],
            ReferenceSearchDirectories = [context.Directory, Path.GetDirectoryName(RuntimeAssemblyPath)!],
        };

        return RewriteEngine.Rewrite(new RewriteRequest(fixturePath, outputPath, ruleSet, options));
    }

    private static void AssertUnbound(RewriteResult result, string member) =>
        Assert.Contains(
            result.Errors,
            diagnostic => diagnostic.Id == RewriteDiagnosticIds.UnresolvedReplacement
                && diagnostic.Message.Contains("'clockwork.tasks.configured.valuetask.awaitable", StringComparison.Ordinal)
                && diagnostic.Message.Contains(member, StringComparison.Ordinal));

    private static MemberReference[] ReferencedMembers(ModuleDefinition module) =>
        [
            .. module.GetTypes()
                .SelectMany(type => type.Methods)
                .Where(method => method.HasBody)
                .SelectMany(method => method.Body.Instructions)
                .Select(instruction => instruction.Operand)
                .OfType<MemberReference>()
                .Where(member => member is MethodReference or FieldReference),
        ];
}
