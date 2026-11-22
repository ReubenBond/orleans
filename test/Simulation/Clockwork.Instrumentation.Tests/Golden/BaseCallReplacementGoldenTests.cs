using System.Collections.Immutable;
using Orleans.Testing.Simulation.Instrumentation.Manifest;
using Orleans.Testing.Simulation.Instrumentation.Rewriting;
using Orleans.Testing.Simulation.Instrumentation.Rules;
using Orleans.Testing.Simulation.Instrumentation.Rules.BuiltIn;
using Orleans.Testing.Simulation.Instrumentation.Tests.Infrastructure;
using Mono.Cecil;

namespace Orleans.Testing.Simulation.Instrumentation.Tests.Golden;

/// <summary>
/// Golden tests for <see cref="RewriteRule.BaseCallReplacement"/>: a rule over a virtual member redirects a virtual
/// <c>callvirt</c> to its dispatching replacement and an override's non-virtual base <c>call</c> to its base-call
/// replacement, so the base call runs the base algorithm instead of dispatching back into the override. The manifest
/// records the replacement each site was rewritten to.
/// </summary>
public sealed class BaseCallReplacementGoldenTests
{
    private const string Fixture = """
        using System.Threading;
        using System.Threading.Channels;
        using System.Threading.Tasks;

        namespace Fx
        {
            public sealed class CountingReader : ChannelReader<int>
            {
                public override bool TryRead(out int item)
                {
                    item = 0;
                    return false;
                }

                public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default) => new(false);

                public override ValueTask<int> ReadAsync(CancellationToken cancellationToken = default) => base.ReadAsync(cancellationToken);
            }

            public static class ReaderUser
            {
                public static ValueTask<int> Read(ChannelReader<int> reader) => reader.ReadAsync();
            }
        }
        """;

    private const string ReadAsyncRule = "clockwork.channels.reader.readasync";

    private static string RuntimeAssemblyPath =>
        typeof(Orleans.Testing.Simulation.Shims.System.Threading.Channels.ControlledChannelReader).Assembly.Location;

    [Fact]
    public void BaseCallsAndVirtualCallsAreRedirectedToTheirOwnReplacements()
    {
        using var context = RewriteTestContext.Create();
        string fixturePath = context.CompileFixture("Fx.BaseCall", Fixture);
        string outputPath = Path.Combine(context.Directory, "Fx.BaseCall.rewritten.dll");
        var options = new RewriteOptions
        {
            TargetRuntime = RewriteTestContext.TargetRuntime,
            ReplacementAssemblyPaths = [RuntimeAssemblyPath],
            ReferenceSearchDirectories = [context.Directory, Path.GetDirectoryName(RuntimeAssemblyPath)!],
        };
        RewriteRuleSet ruleSet = BuiltInRuleSets.BuildControlledTasks(BuiltInRuleSets.AllFamilies);
        RewriteResult result = RewriteEngine.Rewrite(new RewriteRequest(fixturePath, outputPath, ruleSet, options));
        result.EnsureSuccess();

        using ModuleDefinition module = context.LoadModule(outputPath);
        MethodDefinition @override = CecilInspect.GetMethod(module, "Fx.CountingReader", "ReadAsync");
        MethodDefinition caller = CecilInspect.GetMethod(module, "Fx.ReaderUser", "Read");
        Assert.True(CecilInspect.CallsAnyContaining(@override, "ControlledChannelReader::BaseReadAsync"));
        Assert.False(CecilInspect.CallsAnyContaining(@override, "ControlledChannelReader::ReadAsync"));
        Assert.True(CecilInspect.CallsAnyContaining(caller, "ControlledChannelReader::ReadAsync"));
        Assert.False(CecilInspect.CallsAnyContaining(caller, "ControlledChannelReader::BaseReadAsync"));

        RewriteRule rule = Assert.Single(ruleSet.Rules, rule => rule.Id == ReadAsyncRule);
        ImmutableArray<ManifestTransformation> transformations = result.Manifest.Transformations;
        Assert.Contains(transformations, t =>
            t.RuleId == ReadAsyncRule && t.Method.Contains("CountingReader", StringComparison.Ordinal)
            && t.Replacement == rule.BaseCallReplacement!.Value.ToCanonicalString());
        Assert.Contains(transformations, t =>
            t.RuleId == ReadAsyncRule && t.Method.Contains("ReaderUser", StringComparison.Ordinal)
            && t.Replacement == rule.Replacement.ToCanonicalString());
    }

    [Fact]
    public void TheBaseCallReplacementIsPartOfTheRulesSignature()
    {
        RewriteRule rule = Assert.Single(
            BuiltInRuleSets.BuildControlledTasks(BuiltInRuleSets.AllFamilies).Rules,
            rule => rule.Id == ReadAsyncRule);

        Assert.NotEqual(rule.ToCanonicalString(), (rule with { BaseCallReplacement = null }).ToCanonicalString());
        Assert.Contains(nameof(RewriteRule.BaseCallReplacement), rule.ToCanonicalString(), StringComparison.Ordinal);
    }
}
