using Orleans.Testing.Simulation.Instrumentation.Configuration;
using Orleans.Testing.Simulation.Instrumentation.Orchestration;
using Orleans.Testing.Simulation.Instrumentation.Rules;
using Microsoft.CodeAnalysis;

namespace Orleans.Testing.Simulation.Instrumentation.Tests.Infrastructure;

/// <summary>
/// Builds a complete, runnable application closure on disk - a console <em>application</em> plus a
/// third-party dependency, the controlled-API assembly, and the shim assembly - then stages an
/// instrumented copy with the real <see cref="InstrumentationRunner"/> so both the original and the
/// instrumented executables can be launched as separate processes. This is what lets build/tool integration
/// prove honestly that an <em>enabled staged executable dispatches to the test shim</em> while a
/// <em>normal executable does not</em>: only an independent host resolving the staged closure from
/// disk can demonstrate that end to end.
/// </summary>
internal sealed class ExecutionClosureFixture : IDisposable
{
    /// <summary>The simple name of the console application fixture.</summary>
    public const string AppAssemblyName = "Fx.App";

    /// <summary>The simple name of the third-party dependency the application calls into.</summary>
    public const string ThirdPartyAssemblyName = "ThirdParty.Library";

    /// <summary>
    /// The default application: it prints the results of a directly controlled call, a controlled
    /// call made through the third-party dependency, and an instance call. Every printed value
    /// changes when the closure is instrumented, so stdout alone proves shim dispatch.
    /// </summary>
    public const string DefaultAppSource = """
        using System;
        using ClockworkFixtures.Api;
        using ThirdParty.Library;

        public static class Program
        {
            public static int Main()
            {
                Console.WriteLine("app.ticks=" + RealClock.UtcNowTicks());
                Console.WriteLine("dep.ticks=" + Calculator.Ticks());
                var service = new Service(3);
                Console.WriteLine("app.value=" + service.GetValue());
                return 0;
            }
        }
        """;

    /// <summary>
    /// An application that invokes a rejected API. A normal build runs it (the real method is a
    /// no-op); an instrumented build injects a deterministic throw before the call, so the staged
    /// process fails with a non-zero exit code and a diagnostic on stderr.
    /// </summary>
    public const string RejectionAppSource = """
        using System;
        using ClockworkFixtures.Api;

        public static class Program
        {
            public static int Main()
            {
                Forbidden.DangerousWrite("boom");
                Console.WriteLine("reached-end");
                return 0;
            }
        }
        """;

    private const string ThirdPartySource = """
        using ClockworkFixtures.Api;

        namespace ThirdParty.Library
        {
            public static class Calculator
            {
                public static long Ticks() => RealClock.UtcNowTicks();
            }
        }
        """;

    private ExecutionClosureFixture(string root, string sourceDirectory, string stagingDirectory)
    {
        Root = root;
        SourceDirectory = sourceDirectory;
        StagingDirectory = stagingDirectory;
    }

    /// <summary>Gets the root sandbox directory (deleted on dispose).</summary>
    public string Root { get; }

    /// <summary>Gets the source application directory (an app output directory; never modified).</summary>
    public string SourceDirectory { get; }

    /// <summary>Gets the staging directory the instrumented closure is written to.</summary>
    public string StagingDirectory { get; }

    /// <summary>Gets the path of the original (uninstrumented) application assembly.</summary>
    public string SourceAppPath => Path.Combine(SourceDirectory, AppAssemblyName + ".dll");

    /// <summary>Gets the path of the staged (instrumented) application assembly.</summary>
    public string StagedAppPath => Path.Combine(StagingDirectory, AppAssemblyName + ".dll");

    /// <summary>Builds the source closure with the requested symbol form, optimization, and signing.</summary>
    public static ExecutionClosureFixture Create(
        FixtureSymbols symbols = FixtureSymbols.PortableFile,
        bool optimize = false,
        bool strongName = false,
        string appSource = DefaultAppSource)
    {
        string root = TestArtifacts.CreateUnique("cwr-exec-tests");
        string source = Path.Combine(root, "app");
        string staging = Path.Combine(root, "staged");
        Directory.CreateDirectory(source);

        var fixture = new ExecutionClosureFixture(root, source, staging);

        string? keyPath = null;
        if (strongName)
        {
            keyPath = Path.Combine(root, "closure.snk");
            File.WriteAllBytes(keyPath, StrongNameKeys.CreatePrivateKeyBlob());
        }

        string api = FixtureCompiler.Compile(
            FixtureSources.ApiAssemblyName, FixtureSources.Api, source, symbols, optimize, strongNameKeyFile: keyPath);
        FixtureCompiler.Compile(
            FixtureSources.ShimAssemblyName, FixtureSources.Shims, source, symbols, optimize,
            additionalReferencePaths: [api], strongNameKeyFile: keyPath);
        string third = FixtureCompiler.Compile(
            ThirdPartyAssemblyName, ThirdPartySource, source, symbols, optimize,
            additionalReferencePaths: [api], strongNameKeyFile: keyPath);
        FixtureCompiler.Compile(
            AppAssemblyName, appSource, source, symbols, optimize,
            additionalReferencePaths: [api, third], strongNameKeyFile: keyPath,
            outputKind: OutputKind.ConsoleApplication);

        ProcessAppRunner.WriteRuntimeConfig(fixture.SourceAppPath);
        return fixture;
    }

    /// <summary>Gets the standard golden rule set (redirects, wrap, and a rejection).</summary>
    public static RewriteRuleSet StandardRuleSet() => RewriteTestContext.StandardRuleSet();

    /// <summary>Runs the instrumentation orchestrator over the source closure into the staging directory.</summary>
    public InstrumentationResult Instrument(
        InstrumentationConfiguration? configuration = null,
        RewriteRuleSet? ruleSet = null)
        => InstrumentationRunner.Run(new InstrumentationRequest
        {
            SourceDirectory = SourceDirectory,
            StagingDirectory = StagingDirectory,
            Configuration = configuration ?? new InstrumentationConfiguration(),
            RuleSet = ruleSet ?? StandardRuleSet(),
            EntryAssemblyName = AppAssemblyName,
        });

    /// <summary>Runs the original, uninstrumented application as a separate process.</summary>
    public AppRunResult RunSource() => ProcessAppRunner.Run(SourceAppPath);

    /// <summary>Runs the staged, instrumented application as a separate process.</summary>
    public AppRunResult RunStaged() => ProcessAppRunner.Run(StagedAppPath);

    /// <inheritdoc/>
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
            // Best-effort cleanup; a locked file must not fail the test.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup.
        }
    }
}
