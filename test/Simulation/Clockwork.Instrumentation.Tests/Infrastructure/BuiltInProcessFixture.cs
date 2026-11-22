using Orleans.Testing.Simulation.Instrumentation.Configuration;
using Orleans.Testing.Simulation.Instrumentation.Orchestration;
using Orleans.Testing.Simulation.Instrumentation.Rules;
using Orleans.Testing.Simulation.Instrumentation.Rules.BuiltIn;
using Orleans.Testing.Simulation.Runtime.Tasks;
using Microsoft.CodeAnalysis;

namespace Orleans.Testing.Simulation.Instrumentation.Tests.Infrastructure;

/// <summary>
/// Builds a real executable plus a third-party dependency which uses only ordinary BCL APIs, rewrites
/// both with Clockwork's built-in rules, and runs the source/staged closures in independent processes.
/// </summary>
internal sealed class BuiltInProcessFixture : IDisposable
{
    private const string AppAssemblyName = "Fx.BuiltInApp";
    private const string DependencyAssemblyName = "ThirdParty.Sync";

    private const string DependencySource = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;

        namespace ThirdParty.Sync
        {
            public static class Probe
            {
                public static string Run()
                {
                    string monitor;
                    try { _ = Monitor.LockContentionCount; monitor = "real"; }
                    catch (Exception ex) when (ex.GetType().Name == "SimulationApiException") { monitor = "rejected"; }

                    var dedicated = new Lock();
                    lock (dedicated) { }

                    string semaphore;
                    using (var slim = new SemaphoreSlim(1, 1))
                    {
                        slim.Wait();
                        slim.Release();
                        // AvailableWaitHandle is controlled: a permit is available
                        // (count == 1) so the bridged handle is signalled in both source and staged runs.
                        WaitHandle h = slim.AvailableWaitHandle;
                        semaphore = h.WaitOne(0) ? "signaled" : "unsignaled";
                    }

                    Func<Task>[] delays =
                    [
                        () => Task.Delay(0),
                        () => Task.Delay(TimeSpan.Zero),
                        () => Task.Delay(0, CancellationToken.None),
                        () => Task.Delay(TimeSpan.Zero, CancellationToken.None),
                        () => Task.Delay(TimeSpan.Zero, TimeProvider.System),
                        () => Task.Delay(TimeSpan.Zero, TimeProvider.System, CancellationToken.None),
                    ];
                    int completedDelays = 0;
                    foreach (Func<Task> delay in delays)
                    {
                        if (delay().IsCompletedSuccessfully) { completedDelays++; }
                    }

                    using var timer = new Timer(_ => { });
                    return $"monitor={monitor};lock={dedicated.GetType().FullName};semaphore={semaphore};delays={completedDelays};timer={timer.GetType().FullName}";
                }
            }
        }
        """;

    private const string AppSource = """
        using System;
        using Orleans.Testing.Simulation;
        using ThirdParty.Sync;

        public static class Program
        {
            public static int Main()
            {
                var simulation = new SimulationCluster(seed: 1);
                var node = simulation.AddNode("node");
                string output = "";
                node.Context.SchedulerLane.EnqueueAfter(() => output = Probe.Run(), TimeSpan.Zero);
                simulation.RunUntilIdle(System.Threading.CancellationToken.None);
                Console.WriteLine(output);
                return 0;
            }
        }
        """;

    /// <summary>
    /// An app shaped like an instrumented test project: the entry assembly stays unrewritten, and the rewritten code
    /// under test runs only behind a delegate inside the simulation, so it loads after the simulation starts. Each
    /// attempt posts that delegate and then <c>Send</c>s from outside the simulation, which pumps the simulation until
    /// its own callback runs. It simulates twice and reports each attempt.
    /// </summary>
    public const string DelegatedAppSource = """
        using System;
        using System.Threading;
        using Orleans.Testing.Simulation;
        using Orleans.Testing.Simulation.Runtime;
        using ThirdParty.Sync;

        public static class Program
        {
            public static int Main()
            {
                for (int attempt = 1; attempt <= 2; attempt++)
                {
                    try
                    {
                        Simulate();
                        Console.WriteLine($"attempt {attempt}: ran");
                    }
                    catch (UnsupportedRuntimeException exception)
                    {
                        Console.WriteLine($"attempt {attempt}: rejected {exception.AssemblyName}");
                    }
                }

                return 0;
            }

            private static void Simulate()
            {
                var simulation = new SimulationCluster(seed: 1);
                SynchronizationContext context = simulation.AddNode("node").Context.SynchronizationContext;
                context.Post(_ => _ = Probe.Run(), null);
                context.Send(_ => { }, null);
            }
        }
        """;

    private BuiltInProcessFixture(string root, string sourceDirectory, string stagingDirectory)
    {
        Root = root;
        SourceDirectory = sourceDirectory;
        StagingDirectory = stagingDirectory;
    }

    public string Root { get; }

    public string SourceDirectory { get; }

    public string StagingDirectory { get; }

    public string SourceAppPath => Path.Combine(SourceDirectory, AppAssemblyName + ".dll");

    public string StagedAppPath => Path.Combine(StagingDirectory, AppAssemblyName + ".dll");

    public static BuiltInProcessFixture Create(bool optimize, string appSource = AppSource)
    {
        string root = TestArtifacts.CreateUnique("cwr-builtins-process");
        string source = Path.Combine(root, "source");
        string staging = Path.Combine(root, "staged");
        Directory.CreateDirectory(source);

        var fixture = new BuiltInProcessFixture(root, source, staging);
        string clockworkBuild = ClockworkAssemblyPath();
        string clockwork = CopyAssembly(clockworkBuild, source);
        string runtime = CopyAssembly(typeof(ControlledTask).Assembly.Location, source);

        string dependency = FixtureCompiler.Compile(
            DependencyAssemblyName,
            DependencySource,
            source,
            FixtureSymbols.PortableFile,
            optimize);
        FixtureCompiler.Compile(
            AppAssemblyName,
            appSource,
            source,
            FixtureSymbols.PortableFile,
            optimize,
            [dependency, clockwork, runtime],
            outputKind: OutputKind.ConsoleApplication);
        ProcessAppRunner.WriteRuntimeConfig(fixture.SourceAppPath);
        return fixture;
    }

    /// <summary>Instruments the app with the built-in rules for the runtime its runtimeconfig.json names.</summary>
    public InstrumentationResult Instrument() =>
        Instrument(
            new InstrumentationConfiguration { IncludePatterns = [AppAssemblyName + ".dll", DependencyAssemblyName + ".dll"] },
            BuiltInRuleSets.BuildControlledTasks(BuiltInRuleSets.AllFamilies));

    /// <summary>Instruments the app with the built-in rules classified for <paramref name="runtime"/> instead.</summary>
    /// <param name="runtime">The runtime the rules are classified for and the target runtime.</param>
    /// <param name="rewriteApp">Whether the entry assembly is rewritten along with its dependency.</param>
    public InstrumentationResult Instrument(Version runtime, bool rewriteApp = true)
    {
        RewriteRuleSet builtIn = BuiltInRuleSets.BuildControlledTasks(BuiltInRuleSets.AllFamilies);
        return Instrument(
            new InstrumentationConfiguration
            {
                TargetRuntime = runtime,
                IncludePatterns = rewriteApp
                    ? [AppAssemblyName + ".dll", DependencyAssemblyName + ".dll"]
                    : [DependencyAssemblyName + ".dll"],
            },
            new RewriteRuleSet(
                builtIn.Id,
                builtIn.Version,
                builtIn.Rules.Select(rule => rule with { SupportedRuntimes = new RuntimeVersionRange(runtime, runtime) })));
    }

    private InstrumentationResult Instrument(InstrumentationConfiguration configuration, RewriteRuleSet ruleSet) =>
        InstrumentationRunner.Run(new InstrumentationRequest
        {
            SourceDirectory = SourceDirectory,
            StagingDirectory = StagingDirectory,
            EntryAssemblyName = AppAssemblyName,
            Configuration = configuration,
            RuleSet = ruleSet,
        });

    public AppRunResult RunSource() => ProcessAppRunner.Run(SourceAppPath);

    public AppRunResult RunStaged() => ProcessAppRunner.Run(StagedAppPath);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string CopyAssembly(string path, string destinationDirectory)
    {
        string destination = Path.Combine(destinationDirectory, Path.GetFileName(path));
        File.Copy(path, destination, overwrite: true);
        return destination;
    }

    private static string ClockworkAssemblyPath()
    {
        var baseDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        string configuration = baseDirectory.Parent?.Name ?? "Release";
        DirectoryInfo? current = baseDirectory;
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "Orleans.slnx")))
        {
            current = current.Parent;
        }

        if (current is null)
        {
            throw new InvalidOperationException("Could not locate Orleans.slnx.");
        }

        return Path.Combine(
            current.FullName,
            "src",
            "Simulation",
            "Orleans.Testing.Simulation",
            "bin",
            configuration,
            "net11.0",
            "Orleans.Testing.Simulation.dll");
    }
}
