using System.Collections.Immutable;
using System.Linq;
using Orleans.Testing.Simulation.Analyzers;
using Orleans.Testing.Simulation.Instrumentation.Rules.BuiltIn;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Orleans.Testing.Simulation.Analyzers.Tests;

/// <summary>
/// Verifies that <see cref="NondeterministicApiAnalyzer"/> reports the same controlled
/// BCL surface that the built-in <c>clockwork.bcl.deterministic</c> rewrite rule set redirects, so
/// compile-time guidance stays aligned with rewrite-time behaviour.
/// </summary>
public sealed class NondeterministicApiAnalyzerTests
{
    [Theory]
    [InlineData("_ = System.DateTime.Now;", "CW1001")]
    [InlineData("_ = System.DateTime.UtcNow;", "CW1001")]
    [InlineData("_ = System.DateTime.Today;", "CW1001")]
    [InlineData("_ = System.DateTimeOffset.Now;", "CW1001")]
    [InlineData("_ = System.DateTimeOffset.UtcNow;", "CW1001")]
    [InlineData("_ = System.Diagnostics.Stopwatch.GetTimestamp();", "CW1001")]
    [InlineData("_ = System.Diagnostics.Stopwatch.GetElapsedTime(0L);", "CW1001")]
    [InlineData("_ = System.Environment.TickCount;", "CW1001")]
    [InlineData("_ = System.Environment.TickCount64;", "CW1001")]
    [InlineData("_ = System.Guid.NewGuid();", "CW1001")]
    [InlineData("_ = System.Guid.CreateVersion7();", "CW1001")]
    [InlineData("_ = System.Random.Shared;", "CW1001")]
    [InlineData("_ = new System.Random();", "CW1001")]
    [InlineData("_ = new System.Random(42);", "CW1001")]
    [InlineData("_ = System.Threading.Tasks.Task.Delay(1);", "CW1001")]
    [InlineData("_ = System.Threading.Tasks.Task.Delay(System.TimeSpan.Zero);", "CW1001")]
    [InlineData("_ = System.Threading.Tasks.Task.Delay(1, default);", "CW1001")]
    [InlineData("_ = System.Threading.Tasks.Task.Delay(System.TimeSpan.Zero, default(System.Threading.CancellationToken));", "CW1001")]
    [InlineData("_ = System.Threading.Tasks.Task.Delay(System.TimeSpan.Zero, System.TimeProvider.System);", "CW1001")]
    [InlineData("_ = System.Threading.Tasks.Task.Delay(System.TimeSpan.Zero, System.TimeProvider.System, default);", "CW1001")]
    [InlineData("using var timer = new System.Threading.Timer(_ => { });", "CW1001")]
    [InlineData("using var timer = new System.Threading.PeriodicTimer(System.TimeSpan.FromSeconds(1));", "CW1001")]
    [InlineData("using var source = new System.Threading.CancellationTokenSource(1);", "CW1001")]
    [InlineData("_ = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);", "CW1001")]
    [InlineData("_ = System.Security.Cryptography.RandomNumberGenerator.GetInt32(100);", "CW1001")]
    [InlineData("System.Security.Cryptography.RandomNumberGenerator.Fill(System.Span<byte>.Empty);", "CW1001")]
    [InlineData("using var r = System.Security.Cryptography.RandomNumberGenerator.Create();", "CW1001")]
    [InlineData("_ = System.Security.Cryptography.RandomNumberGenerator.GetItems<int>([1, 2], 1);", "CW1001")]
    [InlineData("System.Security.Cryptography.RandomNumberGenerator.Shuffle<int>(System.Span<int>.Empty);", "CW1001")]
    [InlineData("_ = System.Threading.Tasks.Task.Run(() => { });", "CW1001")]
    [InlineData("_ = System.Threading.Tasks.Task.CompletedTask.ContinueWith(_ => { });", "CW1001")]
    [InlineData("_ = default(System.Threading.Tasks.ValueTask).AsTask();", "CW1001")]
    [InlineData("_ = default(System.Threading.Tasks.ValueTask<int>).Preserve();", "CW1001")]
    [InlineData("new System.Threading.Tasks.TaskCompletionSource<int>().SetResult(1);", "CW1001")]
    [InlineData("_ = new System.Threading.Tasks.TaskCompletionSource(System.Threading.Tasks.TaskCreationOptions.None);", "CW1001")]
    [InlineData("_ = new System.Threading.Tasks.TaskCompletionSource<int>(null, System.Threading.Tasks.TaskCreationOptions.None);", "CW1001")]
    [InlineData("_ = System.Threading.Tasks.Task.WhenEach(System.Threading.Tasks.Task.CompletedTask);", "CW1001")]
    [InlineData("_ = System.Threading.Tasks.Task.Factory.ContinueWhenAll(new[] { System.Threading.Tasks.Task.CompletedTask }, _ => { });", "CW1001")]
    [InlineData("_ = System.Threading.Tasks.Task<int>.Factory.ContinueWhenAny(new[] { System.Threading.Tasks.Task.CompletedTask }, _ => 1);", "CW1001")]
    [InlineData("_ = System.Threading.Tasks.Task.Factory.FromAsync(System.Threading.Tasks.Task.CompletedTask, _ => { });", "CW1001")]
    [InlineData("System.Threading.Thread.Sleep(1);", "CW1001")]
    [InlineData("System.Threading.ThreadPool.QueueUserWorkItem(_ => { });", "CW1001")]
    [InlineData("System.Threading.Tasks.Parallel.Invoke(() => { });", "CW1001")]
    [InlineData("_ = System.Threading.Tasks.Parallel.ForEachAsync(new[] { 1 }, (_, _) => default);", "CW1001")]
    [InlineData("_ = System.Environment.ProcessorCount;", "CW1001")]
    [InlineData("_ = System.Collections.Concurrent.Partitioner.Create(0, 10);", "CW1001")]
    [InlineData("_ = System.Collections.Concurrent.Partitioner.Create(0L, 10L);", "CW1001")]
    [InlineData("_ = System.Threading.Tasks.Task.CompletedTask.Wait(1);", "CW1001")]
    [InlineData("System.Threading.Tasks.Task task = System.Threading.Tasks.Task.CompletedTask; task.Start();", "CW1001")]
    [InlineData("_ = new System.Threading.Tasks.Task(() => { });", "CW1001")]
    [InlineData("_ = new System.Threading.Tasks.Task<int>(() => 1);", "CW1001")]
    [InlineData("System.Threading.Tasks.ValueTask<int> value = default; _ = value.Result;", "CW1001")]
    [InlineData("_ = System.Threading.Channels.Channel.CreateUnbounded<int>().Reader.ReadAllAsync();", "CW1001")]
    [InlineData("_ = System.Threading.Channels.Channel.CreateUnbounded<int>().Reader.ReadAsync();", "CW1001")]
    [InlineData("_ = System.Threading.Channels.Channel.CreateUnbounded<int>().Writer.WriteAsync(1);", "CW1001")]
    [InlineData("_ = System.Linq.ParallelEnumerable.Range(0, 1);", "CW1001")]
    [InlineData("_ = new System.Collections.Concurrent.BlockingCollection<int>().Take();", "CW1001")]
    [InlineData("new System.ComponentModel.BackgroundWorker().RunWorkerAsync();", "CW1001")]
    [InlineData("_ = new System.Threading.Tasks.Dataflow.ActionBlock<int>(_ => { });", "CW1001")]
    [InlineData("_ = System.Net.WebSockets.WebSocket.CreateFromStream(System.IO.Stream.Null, new System.Net.WebSockets.WebSocketCreationOptions());", "CW1001")]
    [InlineData("_ = new System.Net.Http.HttpClient().GetStringAsync(\"http://example\");", "CW1001")]
    [InlineData("_ = System.Net.Http.Json.HttpClientJsonExtensions.GetFromJsonAsync<int>(new System.Net.Http.HttpClient(), \"http://example\");", "CW1001")]
    [InlineData("_ = System.IO.Stream.Null.FlushAsync();", "CW1001")]
    [InlineData("System.Threading.Monitor.Enter(new object());", "CW1001")]
    [InlineData("using var semaphore = new System.Threading.SemaphoreSlim(1);", "CW1001")]
    [InlineData("using var readerWriter = new System.Threading.ReaderWriterLockSlim();", "CW1001")]
    [InlineData("using var reset = new System.Threading.ManualResetEventSlim();", "CW1001")]
    [InlineData("using var mutex = new System.Threading.Mutex();", "CW1001")]
    [InlineData("using var semaphore = new System.Threading.Semaphore(1, 1);", "CW1001")]
    [InlineData("var barrier = new System.Threading.Barrier(1);", "CW1001")]
    [InlineData("var countdown = new System.Threading.CountdownEvent(1);", "CW1001")]
    public async Task ReportsExpectedDiagnostic(string statement, string expectedId)
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(Wrap(statement));

        Diagnostic single = Assert.Single(diagnostics);
        Assert.Equal(expectedId, single.Id);
    }

    [Theory]
    [InlineData("_ = System.DateTime.MinValue;")]
    [InlineData("_ = System.DateTime.Now.Year;")] // property access on the result is fine once flagged; ensure only one report
    [InlineData("_ = new System.DateTime(2024, 1, 1);")]
    [InlineData("_ = System.Guid.Empty;")]
    [InlineData("var r = new System.Random(1); _ = r.Next();")]
    [InlineData("_ = System.Threading.Thread.CurrentThread.Priority;")]
    public async Task DoesNotOverReportSafeUsage(string statement)
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(Wrap(statement));

        // At most a single controlled diagnostic (e.g. DateTime.Now / new Random(int)).
        Assert.True(diagnostics.Length <= 1);
    }

    [Theory]
    [InlineData("_ = new System.Threading.Tasks.TaskCompletionSource();")]
    [InlineData("_ = new System.Threading.Tasks.TaskCompletionSource<int>(\"state\");")]
    public async Task CompletionSourceConstructorsWithoutOptionsNeedNoInstrumentation(string statement) =>
        Assert.Empty(await AnalyzeAsync(Wrap(statement)));

    [Theory]
    [InlineData("_ = thread.Priority; thread.Priority = System.Threading.ThreadPriority.AboveNormal;")]
    [InlineData("_ = thread.ApartmentState; thread.ApartmentState = System.Threading.ApartmentState.STA;")]
    public async Task ThreadSettersRequireInstrumentationButGettersDoNot(string statements)
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(Wrap(
            "var thread = System.Threading.Thread.CurrentThread; " + statements));

        Diagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal("CW1001", diagnostic.Id);
    }

    [Theory]
    [InlineData("_ = System.Collections.Concurrent.Partitioner.Create(0, 10, 2);")]
    [InlineData("_ = System.Collections.Concurrent.Partitioner.Create(new[] { 1 }, true);")]
    [InlineData("_ = System.Collections.Concurrent.Partitioner.Create(new System.Collections.Generic.List<int>());")]
    public async Task PartitionerFactoriesWhichDoNotReadTheProcessorCountAreNotFlagged(string statement)
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(Wrap(statement));

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task GetElapsedTimeTwoArgOverloadIsNotFlaggedAsControlled()
    {
        // Only Stopwatch.GetElapsedTime(long) is redirected; the (long,long) overload is a documented hole.
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            Wrap("_ = System.Diagnostics.Stopwatch.GetElapsedTime(0L, 1L);"));

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task CoversEveryPublicStaticRandomNumberGeneratorApi()
    {
        const string source = """
            using System;
            using System.Security.Cryptography;

            class Probe
            {
                void M()
                {
                    Span<byte> bytes = stackalloc byte[8];
                    Span<char> chars = stackalloc char[8];
                    ReadOnlySpan<char> alphabet = "abcdef";
                    ReadOnlySpan<int> choices = [1, 2, 3];
                    Span<int> destination = stackalloc int[2];
                    using var a = RandomNumberGenerator.Create();
                    using var b = RandomNumberGenerator.Create("ignored");
                    RandomNumberGenerator.Fill(bytes);
                    _ = RandomNumberGenerator.GetBytes(8);
                    RandomNumberGenerator.GetHexString(chars, lowercase: false);
                    _ = RandomNumberGenerator.GetHexString(8, lowercase: false);
                    _ = RandomNumberGenerator.GetInt32(10);
                    _ = RandomNumberGenerator.GetInt32(1, 10);
                    RandomNumberGenerator.GetItems(choices, destination);
                    _ = RandomNumberGenerator.GetItems(choices, 2);
                    _ = RandomNumberGenerator.GetString(alphabet, 8);
                    RandomNumberGenerator.Shuffle(destination);
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(source);
        int publicStaticCount = typeof(System.Security.Cryptography.RandomNumberGenerator)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Length;

        Assert.Equal(publicStaticCount, diagnostics.Length);
        Assert.All(diagnostics, diagnostic => Assert.Equal("CW1001", diagnostic.Id));
    }

    [Fact]
    public async Task SeededRandomDiagnosticNamesTheSeededConstructor()
    {
        Diagnostic diagnostic = Assert.Single(await AnalyzeAsync(Wrap("_ = new System.Random(42);")));
        string message = diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture);

        Assert.Contains("new Random(int)", message);
        Assert.DoesNotContain("'new Random()'", message);
    }

    [Fact]
    public async Task ChannelMembersAreReportedUnlessTheReceiverProvablyRunsAnOverride()
    {
        const string source = """
            using System.Threading;
            using System.Threading.Channels;
            using System.Threading.Tasks;

            class Reader : ChannelReader<int>
            {
                public override bool TryRead(out int item) { item = 0; return false; }
                public override ValueTask<bool> WaitToReadAsync(CancellationToken token = default) => default;
                public override ValueTask<int> ReadAsync(CancellationToken token = default) => base.ReadAsync(token);
            }

            sealed class DerivedReader : Reader
            {
            }

            class Writer : ChannelWriter<int>
            {
                public override bool TryWrite(int item) => false;
                public override ValueTask<bool> WaitToWriteAsync(CancellationToken token = default) => default;
            }

            class Probe
            {
                void M(Reader reader, DerivedReader derived, Writer writer, ChannelReader<int> unknown)
                {
                    _ = reader.ReadAsync();
                    _ = derived.ReadAsync();
                    _ = reader.ReadAllAsync();
                    _ = writer.WriteAsync(1);
                    _ = unknown.ReadAsync();
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(source);

        // The base call, the inherited ReadAllAsync and WriteAsync, and a receiver whose type may inherit ReadAsync.
        Assert.Equal(
            ["base.ReadAsync(token)", "reader.ReadAllAsync()", "unknown.ReadAsync()", "writer.WriteAsync(1)"],
            diagnostics
                .Select(diagnostic => diagnostic.Location.SourceTree!.GetText().ToString(diagnostic.Location.SourceSpan))
                .Order(StringComparer.Ordinal));
        Assert.All(diagnostics, diagnostic => Assert.Equal("CW1001", diagnostic.Id));
    }

    [Fact]
    public void InventoryCoversEveryShippedTaskAndSynchronizationRuleMember()
    {
        BuiltInRuleFamily[] analyzerFamilies =
        [
            BuiltInRuleFamily.TaskCombinators,
            BuiltInRuleFamily.TaskSynchronization,
            BuiltInRuleFamily.TaskContinuations,
            BuiltInRuleFamily.TaskCompletionSource,
            BuiltInRuleFamily.ValueTaskConversions,
            BuiltInRuleFamily.TaskTime,
            BuiltInRuleFamily.TaskScheduling,
            BuiltInRuleFamily.TaskFactory,
            BuiltInRuleFamily.TaskFactoryContinuations,
            BuiltInRuleFamily.TaskFactoryFromAsync,
            BuiltInRuleFamily.Thread,
            BuiltInRuleFamily.ThreadPool,
            BuiltInRuleFamily.Timers,
            BuiltInRuleFamily.CancellationTimers,
            BuiltInRuleFamily.Parallel,
            BuiltInRuleFamily.Channels,
            BuiltInRuleFamily.Plinq,
            BuiltInRuleFamily.BlockingCollection,
            BuiltInRuleFamily.AsyncIO,
            BuiltInRuleFamily.BackgroundWork,
            BuiltInRuleFamily.Dataflow,
            BuiltInRuleFamily.WebSockets,
            BuiltInRuleFamily.HttpClient,
            BuiltInRuleFamily.Hosting,
            BuiltInRuleFamily.Monitor,
            BuiltInRuleFamily.Lock,
            BuiltInRuleFamily.Semaphore,
            BuiltInRuleFamily.ReaderWriterLockSlim,
            BuiltInRuleFamily.ManualResetEventSlim,
            BuiltInRuleFamily.Mutex,
            BuiltInRuleFamily.KernelSemaphore,
            BuiltInRuleFamily.SpinLock,
            BuiltInRuleFamily.ExecutionContext,
            BuiltInRuleFamily.SynchronizationContext,
            BuiltInRuleFamily.Barrier,
            BuiltInRuleFamily.CountdownEvent,
        ];

        foreach ((BuiltInRuleFamily family, Orleans.Testing.Simulation.Instrumentation.Rules.RewriteRule rule) in
            BuiltInRuleSets.ControlledTasksInventory.Where(entry => analyzerFamilies.Contains(entry.Family)))
        {
            string typeName = rule.Target.DeclaringTypeFullName.Replace('/', '+');
            if (rule.Target.MemberName is null)
            {
                Assert.True(
                    InstrumentedApiInventory.ContainsType(typeName),
                    $"Analyzer inventory does not cover type rule '{rule.Id}' ({typeName}).");
                continue;
            }

            string memberName = rule.Target.MemberName switch
            {
                string name when name.StartsWith("get_", StringComparison.Ordinal)
                    || name.StartsWith("set_", StringComparison.Ordinal) => name[4..],
                _ => rule.Target.MemberName,
            };
            Assert.True(
                InstrumentedApiInventory.Contains(typeName, memberName),
                $"Analyzer inventory does not cover rule '{rule.Id}' ({typeName}::{memberName}).");
        }
    }

    [Theory]
    [InlineData("var values = new System.Collections.Generic.Dictionary<int, int>(); _ = System.Linq.Enumerable.First(values);")]
    [InlineData("var values = new System.Collections.Generic.HashSet<int>(); _ = System.Linq.Enumerable.ElementAt(values, 0);")]
    [InlineData("var values = new System.Collections.Generic.Dictionary<int, int>(); _ = string.Join(\",\", values.Keys);")]
    public async Task ReportsActionableUnstableCollectionOrdering(string source)
    {
        Diagnostic diagnostic = Assert.Single(await AnalyzeAsync("class Probe { void M() { " + source + " } }"));

        Assert.Equal("CW1003", diagnostic.Id);
        Assert.Contains(
            "OrderBy",
            diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("var values = new System.Collections.Generic.Dictionary<int, int>(); values[1] = 2; _ = values[1];")]
    [InlineData("var values = new System.Collections.Generic.HashSet<int>(); values.Add(1); _ = values.Contains(1);")]
    [InlineData("var values = new System.Collections.Generic.Dictionary<int, int>(); _ = System.Linq.Enumerable.First(System.Linq.Enumerable.OrderBy(values, pair => pair.Key));")]
    [InlineData("var values = new System.Collections.Generic.List<int>(); values.Add(1); _ = values[0];")]
    [InlineData("var values = new System.Collections.Generic.HashSet<int>(); _ = System.Linq.Enumerable.SequenceEqual(values, values);")]
    public async Task DoesNotBlanketWarnForCollectionUsage(string source)
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync("class Probe { void M() { " + source + " } }");

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "CW1003");
    }

    private static string Wrap(string statement) =>
        "class Probe { void M() { " + statement + " } }";

    private static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string source)
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest));
        CSharpCompilation compilation = CSharpCompilation.Create(
            "AnalyzerProbe",
            [tree],
            ReferenceAssemblies,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        CompilationWithAnalyzers withAnalyzers = compilation.WithAnalyzers(
            [new NondeterministicApiAnalyzer()]);

        ImmutableArray<Diagnostic> analyzerDiagnostics = await withAnalyzers.GetAnalyzerDiagnosticsAsync();
        return [.. analyzerDiagnostics.Where(d => d.Id is "CW1001" or "CW1003")];
    }

    private static readonly MetadataReference[] ReferenceAssemblies = LoadReferences();

    private static MetadataReference[] LoadReferences()
    {
        string trusted = (string)(AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty);
        return trusted
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToArray();
    }
}
