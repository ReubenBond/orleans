using System.Collections.Immutable;
using System.Linq;
using Orleans.Testing.Simulation.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Orleans.Testing.Simulation.Analyzers.Tests;

public sealed class CompletionAuthorityAnalyzerTests
{
    // The authority and the callback attribute, as the Clockwork assembly defines them.
    private const string Authority = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using System.Threading.Tasks.Sources;

        namespace Orleans.Testing.Simulation.Runtime.Tasks
        {
            [AttributeUsage(AttributeTargets.Parameter)]
            internal sealed class CompletionCallbackAttribute : Attribute;

            internal static class SimulationCompletion
            {
                public static void Run<TState>(TState state, [CompletionCallback] Action<TState> complete) => complete(state);

                public static void Run([CompletionCallback] Action complete) => complete();

                public static void Cancel(CancellationTokenSource source) => source.Cancel();
            }
        }

        namespace Other
        {
            [AttributeUsage(AttributeTargets.Parameter)]
            internal sealed class CompletionCallbackAttribute : Attribute;
        }

        """;

    [Theory]
    [InlineData("new TaskCompletionSource().SetResult();")]
    [InlineData("new TaskCompletionSource<int>().TrySetException(new Exception());")]
    [InlineData("var core = new ManualResetValueTaskSourceCore<int>(); core.SetResult(1);")]
    [InlineData("new CancellationTokenSource().Cancel();")]
    [InlineData("_ = new CancellationTokenSource().CancelAsync();")]
    [InlineData("new CancellationTokenSource().CancelAfter(1);")]
    public async Task ACompletionCallOutsideTheAuthorityIsReported(string statement) =>
        Assert.Single(await AnalyzeAsync(Probe(statement)));

    [Theory]
    [InlineData("Action complete = new TaskCompletionSource().SetResult;")]
    [InlineData("var source = new CancellationTokenSource(); new CancellationTokenSource().Token.Register(source.Cancel);")]
    [InlineData("Func<Task> cancel = new CancellationTokenSource().CancelAsync;")]
    public async Task AMethodGroupOfACompletionMemberOutsideTheAuthorityIsReported(string statement) =>
        Assert.Single(await AnalyzeAsync(Probe(statement)));

    [Theory]
    [InlineData("SimulationCompletion.Run(new TaskCompletionSource(), static source => source.SetResult());")]
    [InlineData("SimulationCompletion.Run(new CancellationTokenSource().Cancel);")]
    [InlineData("SimulationCompletion.Cancel(new CancellationTokenSource());")]
    public async Task ACompletionThroughTheAuthorityIsAllowed(string statement) =>
        Assert.Empty(await AnalyzeAsync(Probe(statement)));

    // A completion member inside a lambda which is itself nested in a completion callback runs wherever the inner lambda
    // is invoked, so it is reported.
    [Fact]
    public async Task ACompletionInALambdaNestedInACallbackIsReported() =>
        Assert.Single(await AnalyzeAsync(Probe(
            "SimulationCompletion.Run(new TaskCompletionSource(), static source => { Action later = () => source.SetResult(); later(); });")));

    // Everything after an async callback's first await, or a local function's body wherever it escapes to, runs outside
    // the completion.
    [Theory]
    [InlineData("SimulationCompletion.Run(new TaskCompletionSource(), static async source => { await Task.Yield(); source.SetResult(); });")]
    [InlineData("SimulationCompletion.Run(new TaskCompletionSource(), static source => { async Task Later() { await Task.Yield(); source.SetResult(); } _ = Later(); });")]
    [InlineData("Action? escaped = null; SimulationCompletion.Run(new TaskCompletionSource(), source => { void Complete() => source.SetResult(); escaped = Complete; }); escaped!();")]
    public async Task ACompletionInAnAsyncCallbackOrALocalFunctionIsReported(string statement) =>
        Assert.Single(await AnalyzeAsync(Probe(statement)));

    [Fact]
    public async Task AnAttributeOfTheSameSimpleNameInAnotherNamespaceAuthorizesNothing() =>
        Assert.Single(await AnalyzeAsync(Authority + """
            namespace Probe
            {
                using System;
                using System.Threading.Tasks;

                internal static class Probe
                {
                    private static void Complete([Other.CompletionCallback] Action complete) => complete();

                    private static void M() => Complete(() => new TaskCompletionSource().SetResult());
                }
            }
            """));

    [Fact]
    public async Task ACallbackParameterPassedToAnotherCallbackParameterIsAllowed() =>
        Assert.Empty(await AnalyzeAsync(Authority + """
            namespace Probe
            {
                using System;
                using Orleans.Testing.Simulation.Runtime.Tasks;

                internal static class Probe
                {
                    private static void Complete<TState>(TState state, [CompletionCallback] Action<TState> complete) =>
                        SimulationCompletion.Run(state, complete);
                }
            }
            """));

    [Theory]
    [InlineData("complete(state);")]
    [InlineData("Action<TState> copy = complete; copy(state);")]
    [InlineData("SimulationCompletion.Run((state, complete), static pair => pair.complete(pair.state));")]
    public async Task ACallbackParameterWhichFlowsElsewhereIsReported(string body)
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(Authority + $$"""
            namespace Probe
            {
                using System;
                using Orleans.Testing.Simulation.Runtime.Tasks;

                internal static class Probe
                {
                    private static void Complete<TState>(TState state, [CompletionCallback] Action<TState> complete)
                    {
                        {{body}}
                    }
                }
            }
            """);

        Assert.Contains("complete", Assert.Single(diagnostics).GetMessage(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheRuleIsInactiveInAnAssemblyWhichDoesNotDefineTheAuthority() =>
        Assert.Empty(await AnalyzeAsync("""
            using System.Threading.Tasks;

            internal static class Probe
            {
                private static void M() => new TaskCompletionSource().SetResult();
            }
            """));

    private static string Probe(string statement) => Authority + $$"""
        namespace Probe
        {
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using System.Threading.Tasks.Sources;
            using Orleans.Testing.Simulation.Runtime.Tasks;

            internal static class Probe
            {
                private static void M()
                {
                    {{statement}}
                }
            }
        }
        """;

    private static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string source)
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest));
        CSharpCompilation compilation = CSharpCompilation.Create(
            "CompletionAuthorityProbe",
            [tree],
            ReferenceAssemblies,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.Empty(compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        CompilationWithAnalyzers withAnalyzers = compilation.WithAnalyzers([new CompletionAuthorityAnalyzer()]);
        ImmutableArray<Diagnostic> diagnostics = await withAnalyzers.GetAnalyzerDiagnosticsAsync(TestContext.Current.CancellationToken);
        return [.. diagnostics.Where(diagnostic => diagnostic.Id == "CW1004")];
    }

    private static readonly MetadataReference[] ReferenceAssemblies =
        ((string)(AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty))
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToArray();
}
