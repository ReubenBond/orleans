using Orleans.Testing.Simulation.Instrumentation.Diagnostics;
using Orleans.Testing.Simulation.Instrumentation.Inspection;
using Orleans.Testing.Simulation.Instrumentation.Tests.Infrastructure;
using Orleans.Testing.Simulation.Runtime.Shims;
using Orleans.Testing.Simulation.Tool;

namespace Orleans.Testing.Simulation.Instrumentation.Tests.Cli;

/// <summary>
/// Verifies the <c>orleans-simulation</c> CLI in-process by driving <see cref="Program.Run"/> with explicit
/// streams: the <c>inspect</c> command reports the true managed/strong-name/symbol/idempotence state,
/// the <c>instrument</c> command stages an instrumented closure and honors <c>--dry-run</c>, and every
/// failure class maps to its distinct exit code. Process-level packaging execution is covered
/// separately by the fixture and smoke tests.
/// </summary>
public sealed class CliCommandTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "cwr-cli-tests", Guid.NewGuid().ToString("n"));
    private readonly string _source;
    private readonly string _staging;

    public CliCommandTests()
    {
        _source = Path.Combine(_root, "source");
        _staging = Path.Combine(_root, "staging");
        Directory.CreateDirectory(_source);
    }

    [Fact]
    public void InspectReportsManagedIlOnlyUninstrumented()
    {
        string app = Compile("app", "namespace App { public static class A { public static int Go() => 1; } }");

        (ExitCode code, string output, _) = Invoke("inspect", app);

        Assert.Equal(ExitCode.Success, code);
        Assert.Contains("IL-only", output);
        Assert.Contains("instrumented: no", output);
    }

    [Fact]
    public void InspectJsonEmitsStructuredFacts()
    {
        string app = Compile("app", "namespace App { public static class A { public static int Go() => 1; } }");

        (ExitCode code, string output, _) = Invoke("inspect", app, "--json");

        Assert.Equal(ExitCode.Success, code);
        Assert.Contains("\"managed\": true", output);
        Assert.Contains("\"readyToRun\": false", output);
        Assert.Contains("\"instrumented\": false", output);
    }

    [Fact]
    public void InspectAcceptsDocumentedBuiltInOptions()
    {
        string app = Compile("app", "namespace App { public static class A { public static int Go() => 1; } }");

        (ExitCode code, string output, string errors) = Invoke(
            "inspect",
            app,
            "--builtin",
            "clockwork.bcl.deterministic",
            "--builtin-include",
            "Clock");

        Assert.Equal(ExitCode.Success, code);
        Assert.Contains("clockwork.bcl.deterministic", output);
        Assert.DoesNotContain("Unknown option", errors);
    }

    [Fact]
    public void HelpListsAcceptedInspectConfigurationOptions()
    {
        (ExitCode code, string output, _) = Invoke("--help");

        Assert.Equal(ExitCode.Success, code);
        Assert.Contains(
            "dotnet-orleans-simulation - deterministic Orleans simulation tool",
            output,
            StringComparison.Ordinal);
        foreach (string command in new[]
        {
            "instrument", "inspect", "record", "replay", "explore", "minimize", "trace show",
        })
        {
            Assert.Contains($"dotnet orleans-simulation {command}", output, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("dotnet clockwork", output, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet-clockwork", output, StringComparison.Ordinal);
        foreach (string option in new[]
        {
            "--config", "--rule-set", "--builtin", "--builtin-include", "--builtin-exclude",
            "--include", "--exclude", "--target-runtime", "--mode", "--json",
        })
        {
            Assert.Contains(option, output);
        }

        foreach (string removed in new[]
        {
            "--r2r", "--strong-name <", "--exclude-framework", "--instrument-dependencies",
        })
        {
            Assert.DoesNotContain(removed, output);
        }
    }

    [Fact]
    public void InstrumentAcceptsRaceExplorationMode()
    {
        BuildMinimalClosure();
        string ruleSet = WriteEmptyRuleSet();

        (ExitCode code, string output, string errors) = Invoke(
            "instrument", "--source", _source, "--output", _staging, "--rule-set", ruleSet,
            "--mode", "RaceExploration");

        Assert.Equal(ExitCode.Success, code);
        Assert.Contains("\"mode\": \"RaceExploration\"", File.ReadAllText(_staging + ".manifest.json"));
        Assert.Empty(errors);
    }

    [Fact]
    public void InstrumentStagesInstrumentedClosure()
    {
        BuildMinimalClosure();
        string ruleSet = WriteEmptyRuleSet();

        (ExitCode code, string output, string errors) = Invoke(
            "instrument", "--source", _source, "--output", _staging, "--rule-set", ruleSet);

        Assert.Equal(ExitCode.Success, code);
        Assert.True(File.Exists(Path.Combine(_staging, "app.dll")), errors);

        // The staged app carries the idempotence marker; the original source app does not.
        Assert.True(AssemblyInspector.Inspect(Path.Combine(_staging, "app.dll")).IsInstrumented);
        Assert.False(AssemblyInspector.Inspect(Path.Combine(_source, "app.dll")).IsInstrumented);
        Assert.Contains("success", output);
    }

    [Fact]
    public void InstrumentRejectsSourceManifestWithoutWritingOrReportingSuccess()
    {
        BuildMinimalClosure();
        string ruleSet = WriteEmptyRuleSet();
        string sourceAssembly = Path.Combine(_source, "app.dll");
        byte[] sourceContents = File.ReadAllBytes(sourceAssembly);

        (ExitCode code, string output, string errors) = Invoke(
            "instrument",
            "--source",
            _source,
            "--output",
            _staging,
            "--manifest",
            sourceAssembly,
            "--rule-set",
            ruleSet);

        Assert.Equal(ExitCode.ClosureError, code);
        Assert.Contains("ManifestPath", errors, StringComparison.Ordinal);
        Assert.Contains("SourceDirectory", errors, StringComparison.Ordinal);
        Assert.DoesNotContain("success", output, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(sourceContents, File.ReadAllBytes(sourceAssembly));
        Assert.False(Directory.Exists(_staging));
    }

    [Fact]
    public void InstrumentDryRunWritesNothingAndReportsPlan()
    {
        BuildMinimalClosure();
        string ruleSet = WriteEmptyRuleSet();

        (ExitCode code, string output, _) = Invoke(
            "instrument", "--source", _source, "--rule-set", ruleSet, "--dry-run");

        Assert.Equal(ExitCode.Success, code);
        Assert.False(Directory.Exists(_staging));
        Assert.Contains("app.dll", output);
        Assert.Contains("instrument", output);
    }

    [Fact]
    public void InstrumentDryRunUsesBuiltInRuleSetWhenSelected()
    {
        BuildMinimalClosure();

        (ExitCode code, string output, _) = Invoke(
            "instrument", "--source", _source, "--builtin", "clockwork.bcl.deterministic", "--dry-run");

        Assert.Equal(ExitCode.Success, code);
        Assert.Contains("clockwork.bcl.deterministic", output);
    }

    [Fact]
    public void InstrumentDryRunReportsTheTargetRuntimeAndItsSupport()
    {
        BuildMinimalClosure();

        (ExitCode derivedCode, string derived, _) = Invoke(
            "instrument", "--source", _source, "--builtin", "all", "--dry-run");
        (ExitCode otherCode, string other, _) = Invoke(
            "instrument", "--source", _source, "--builtin", "all", "--target-runtime", "12.0", "--dry-run");

        Assert.Equal(ExitCode.Success, derivedCode);
        Assert.Contains(
            $"Target runtime: {Environment.Version.Major}.{Environment.Version.Minor} (from 'app.runtimeconfig.json')",
            derived);
        Assert.Equal(ExitCode.InstrumentationError, otherCode);
        Assert.Contains("Target runtime: 12.0 (--target-runtime)", other);
        Assert.Single(other.Split('\n'), line => line.Contains(RewriteDiagnosticIds.RuntimeOutOfRange, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null, "12.0", "Target runtime: 12.0 (--target-runtime)")]
    [InlineData("12.0", null, "Target runtime: 12.0 ('targetRuntime' in configuration")]
    [InlineData("12.0", "12.0", "Target runtime: 12.0 ('targetRuntime' in configuration")]
    public void TargetRuntimeOptionCombinesWithTheConfigurationFile(string? file, string? option, string expected)
    {
        BuildMinimalClosure();
        string config = WriteConfiguration(file);
        List<string> args = ["instrument", "--source", _source, "--config", config, "--builtin", "all", "--dry-run"];
        if (option is not null)
        {
            args.AddRange(["--target-runtime", option]);
        }

        (ExitCode code, string output, string error) = Invoke([.. args]);

        Assert.True(code == ExitCode.InstrumentationError, error);
        Assert.Contains(expected, output);
    }

    [Fact]
    public void TargetRuntimeOptionThatConflictsWithTheConfigurationFileFails()
    {
        BuildMinimalClosure();
        string config = WriteConfiguration("11.0");

        (ExitCode code, _, string error) = Invoke(
            "instrument", "--source", _source, "--config", config, "--builtin", "all", "--target-runtime", "12.0", "--dry-run");

        Assert.Equal(ExitCode.ConfigurationError, code);
        Assert.Contains("--target-runtime names target runtime 12.0", error);
    }

    [Fact]
    public void TargetRuntimeMustBeAFeatureVersion()
    {
        BuildMinimalClosure();

        (ExitCode code, _, string error) = Invoke(
            "instrument", "--source", _source, "--builtin", "all", "--target-runtime", "11.0.2", "--dry-run");

        Assert.Equal(ExitCode.UsageError, code);
        Assert.Contains("not a runtime feature version", error);
    }

    [Fact]
    public void InstrumentDryRunBuiltInAllExpandsToEveryRuleSet()
    {
        BuildMinimalClosure();

        (ExitCode code, string output, _) = Invoke(
            "instrument", "--source", _source, "--builtin", "all", "--dry-run");

        Assert.Equal(ExitCode.Success, code);
        Assert.Contains("clockwork.bcl.deterministic", output);
    }

    [Fact]
    public void InstrumentBuiltInStagesSimulationOnlyExecutable()
    {
        BuildSimulationOnlyClosure();

        AppRunResult source = ProcessAppRunner.Run(Path.Combine(_source, "app.dll"));
        Assert.True(
            source.ExitCode == 0,
            $"Source process failed ({source.ExitCode}):\n{source.StandardOutput}\n{source.StandardError}");
        Assert.Contains("reached-end", source.Output);
        string sourceSideEffect = Path.Combine(_source, "side-effect.txt");
        Assert.True(File.Exists(sourceSideEffect));
        File.Delete(sourceSideEffect);

        (ExitCode code, _, string errors) = Invoke(
            "instrument",
            "--source",
            _source,
            "--output",
            _staging,
            "--builtin",
            "clockwork.bcl.deterministic");

        Assert.Equal(ExitCode.Success, code);
        AppRunResult staged = ProcessAppRunner.Run(Path.Combine(_staging, "app.dll"));
        Assert.NotEqual(0, staged.ExitCode);
        Assert.Empty(staged.Output);
        Assert.Contains(typeof(SimulationNotActiveException).FullName!, staged.StandardError, StringComparison.Ordinal);
        Assert.Contains(SimulationNotActiveException.DiagnosticMessage, staged.StandardError, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_staging, "side-effect.txt")), errors);
    }

    [Fact]
    public void InstrumentDryRunReportsAutomaticStrongNameStripping()
    {
        string keyPath = Path.Combine(_root, "test.snk");
        File.WriteAllBytes(keyPath, StrongNameKeys.CreatePrivateKeyBlob());
        FixtureCompiler.Compile(
            "app", "namespace App { public static class A { public static int Go() => 1; } }",
            _source, FixtureSymbols.PortableFile, optimize: false, strongNameKeyFile: keyPath);
        ProcessAppRunner.WriteRuntimeConfig(Path.Combine(_source, "app.dll"));
        string ruleSet = WriteEmptyRuleSet();

        (ExitCode code, string output, _) = Invoke(
            "instrument", "--source", _source, "--rule-set", ruleSet, "--dry-run");

        Assert.Equal(ExitCode.Success, code);
        Assert.Contains("strip strong-name identity", output);
    }

    [Fact]
    public void UnknownCommandIsUsageError()
    {
        (ExitCode code, _, string errors) = Invoke("frobnicate");

        Assert.Equal(ExitCode.UsageError, code);
        Assert.Contains("Unknown command", errors);
    }

    [Theory]
    [InlineData("rewrite")]
    [InlineData("run")]
    public void RemovedCommandAliasesAreUsageErrors(string command)
    {
        (ExitCode code, _, string errors) = Invoke(command);

        Assert.Equal(ExitCode.UsageError, code);
        Assert.Contains("Unknown command", errors);
    }

    [Theory]
    [InlineData("--key")]
    [InlineData("--rewrite-dependencies")]
    [InlineData("--r2r")]
    [InlineData("--strong-name")]
    [InlineData("--exclude-framework")]
    [InlineData("--instrument-dependencies")]
    public void RemovedInstrumentationOptionAliasesAreUsageErrors(string option)
    {
        BuildMinimalClosure();
        string ruleSet = WriteEmptyRuleSet();

        (ExitCode code, _, string errors) = Invoke(
            "instrument", "--source", _source, "--output", _staging, "--rule-set", ruleSet, option, "value");

        Assert.Equal(ExitCode.UsageError, code);
        Assert.Contains("Unknown option", errors);
    }

    [Fact]
    public void UnknownOptionIsUsageError()
    {
        BuildMinimalClosure();
        string ruleSet = WriteEmptyRuleSet();

        (ExitCode code, _, string errors) = Invoke(
            "instrument", "--source", _source, "--output", _staging, "--rule-set", ruleSet, "--bogus", "x");

        Assert.Equal(ExitCode.UsageError, code);
        Assert.Contains("Unknown option", errors);
    }

    [Fact]
    public void MissingConfigFileIsConfigurationError()
    {
        (ExitCode code, _, string errors) = Invoke(
            "instrument", "--source", _source, "--output", _staging,
            "--config", Path.Combine(_root, "does-not-exist.json"));

        Assert.Equal(ExitCode.ConfigurationError, code);
        Assert.Contains("not found", errors);
    }

    [Fact]
    public void MalformedRuleSetIsConfigurationError()
    {
        string ruleSet = Path.Combine(_root, "malformed-rules.json");
        File.WriteAllText(ruleSet, """{ "id": "clockwork.test", "version": "1.0" }""");

        (ExitCode code, string output, string errors) = Invoke(
            "instrument", "--source", _source, "--output", _staging, "--rule-set", ruleSet);

        Assert.Equal(ExitCode.ConfigurationError, code);
        Assert.Contains("'rules' array", errors);
        Assert.DoesNotContain("success", output, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(_staging));
    }

    [Fact]
    public void MissingSourceDirectoryIsClosureError()
    {
        string ruleSet = WriteEmptyRuleSet();

        (ExitCode code, _, string errors) = Invoke(
            "instrument", "--source", Path.Combine(_root, "nope"), "--output", _staging, "--rule-set", ruleSet);

        Assert.Equal(ExitCode.ClosureError, code);
        Assert.Contains("not found", errors);
    }

    [Fact]
    public void NoRuleSetIsConfigurationError()
    {
        BuildMinimalClosure();

        (ExitCode code, _, string errors) = Invoke("instrument", "--source", _source, "--output", _staging);

        Assert.Equal(ExitCode.ConfigurationError, code);
        Assert.Contains("rule-set", errors);
    }

    private static (ExitCode Code, string Output, string Errors) Invoke(params string[] args)
    {
        var output = new StringWriter();
        var errors = new StringWriter();
        ExitCode code = Program.Run(args, output, errors);
        return (code, output.ToString(), errors.ToString());
    }

    private string WriteConfiguration(string? targetRuntime)
    {
        string path = Path.Combine(_root, "orleans-simulation.config.json");
        File.WriteAllText(
            path,
            targetRuntime is null
                ? """{ "schemaVersion": 2 }"""
                : $$"""{ "schemaVersion": 2, "targetRuntime": "{{targetRuntime}}" }""");
        return path;
    }

    private void BuildMinimalClosure()
    {
        Compile("app", "namespace App { public static class A { public static int Go() => 1; } }");
        ProcessAppRunner.WriteRuntimeConfig(Path.Combine(_source, "app.dll"));
    }

    private void BuildSimulationOnlyClosure()
    {
        FixtureCompiler.Compile(
            "app",
            """
            using System;
            using System.IO;

            public static class Program
            {
                public static void Main()
                {
                    _ = DateTime.UtcNow;
                    File.WriteAllText("side-effect.txt", "unexpected");
                    Console.WriteLine("reached-end");
                }
            }
            """,
            _source,
            FixtureSymbols.PortableFile,
            optimize: false,
            outputKind: Microsoft.CodeAnalysis.OutputKind.ConsoleApplication);
        File.Copy(
            typeof(SimulationNotActiveException).Assembly.Location,
            Path.Combine(_source, "Orleans.Testing.Simulation.dll"),
            overwrite: true);
        ProcessAppRunner.WriteRuntimeConfig(Path.Combine(_source, "app.dll"));
    }

    private string Compile(string name, string source) =>
        FixtureCompiler.Compile(name, source, _source, FixtureSymbols.PortableFile, optimize: false);

    private string WriteEmptyRuleSet()
    {
        string path = Path.Combine(_root, "rules.json");
        File.WriteAllText(path, "{\"id\":\"clockwork.test\",\"version\":\"1.0\",\"rules\":[]}");
        return path;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
