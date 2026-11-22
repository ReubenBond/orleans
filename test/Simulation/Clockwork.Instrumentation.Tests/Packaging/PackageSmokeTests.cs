using System.Runtime.InteropServices;
using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using Orleans.Testing.Simulation.Instrumentation.Configuration;
using Orleans.Testing.Simulation.Instrumentation.Diagnostics;
using Orleans.Testing.Simulation.Instrumentation.Orchestration;
using Orleans.Testing.Simulation.Instrumentation.Rules;
using Orleans.Testing.Simulation.Instrumentation.Rules.BuiltIn;
using Orleans.Testing.Simulation.Instrumentation.Tests.Infrastructure;
using Orleans.Testing.Simulation.Runtime.Shims;

namespace Orleans.Testing.Simulation.Instrumentation.Tests.Packaging;

/// <summary>
/// End-to-end package smoke tests. These pack the seven production NuGet packages into an isolated
/// local feed, inspect their package boundaries, then consume them exactly as an end user would -
/// the build package via a <c>PackageReference</c> that runs the real MSBuild targets during
/// <c>dotnet build</c>, the kernel and testing packages from ordinary package-only projects, and
/// the tool via <c>dotnet tool install</c> followed by invoking <c>dotnet-orleans-simulation</c>. Project
/// references are deliberately avoided so the published package topology is the system under test.
/// </summary>
/// <remarks>
/// They are gated behind the <c>ORLEANS_SIMULATION_SMOKE_TESTS=1</c> environment variable because each test
/// runs several full SDK operations (pack, restore, build, tool install) and is far slower than a
/// unit test. CI enables them in a dedicated step; a normal local test run skips them.
/// </remarks>
public sealed class PackageSmokeTests
{
    private const string SmokeEnvironmentVariable = "ORLEANS_SIMULATION_SMOKE_TESTS";
    private const string SimulationPackageId = "Microsoft.Orleans.Testing.Simulation";
    private const string InstrumentationPackageId = "Microsoft.Orleans.Testing.Simulation.Instrumentation";
    private const string BuildPackageId = "Microsoft.Orleans.Testing.Simulation.Instrumentation.Build";
    private const string AnalyzersPackageId = "Microsoft.Orleans.Testing.Simulation.Analyzers";
    private const string TestingPackageId = "Microsoft.Orleans.Testing.Simulation.Testing";
    private const string TestingMtpPackageId = "Microsoft.Orleans.Testing.Simulation.Testing.MTP";
    private const string TestingMtpBuilderHookId = "2E7C3A6B-6F8D-4F4B-9D52-1A0D7C8E5B93";
    private const string TestingMtpBuilderHookType =
        "Orleans.Testing.Simulation.Testing.TestingPlatformBuilderHook";
    private const string ToolPackageId = "Microsoft.Orleans.Testing.Simulation.Tool";
    private const string ProductionFixturePackageId = "Clockwork.Fixtures.Production";

    private static readonly bool SmokeEnabled =
        string.Equals(Environment.GetEnvironmentVariable(SmokeEnvironmentVariable), "1", StringComparison.Ordinal);

    private static readonly string[] TestingSupportPackagePayload =
    [
        "LICENSE",
        "README.md",
        "THIRD-PARTY-NOTICES.md",
        "lib/net11.0/Orleans.Testing.Simulation.Testing.dll",
        "lib/net11.0/Orleans.Testing.Simulation.Testing.xml",
        "logo_128.png",
    ];

    private static readonly string[] TestingMtpPackagePayload =
    [
        "LICENSE",
        "README.md",
        "THIRD-PARTY-NOTICES.md",
        "buildMultiTargeting/Microsoft.Orleans.Testing.Simulation.Testing.MTP.props",
        "buildTransitive/Microsoft.Orleans.Testing.Simulation.Testing.MTP.props",
        "lib/net11.0/Orleans.Testing.Simulation.Testing.MTP.dll",
        "lib/net11.0/Orleans.Testing.Simulation.Testing.MTP.xml",
        "logo_128.png",
    ];

    private static readonly Lazy<PackagedArtifacts> Artifacts = new(PackagedArtifacts.Build);

    [Fact]
    public void PackedPackagesContainExpectedAssemblyAssetsAndTargetFrameworks()
    {
        Assert.SkipUnless(SmokeEnabled, $"Set {SmokeEnvironmentVariable}=1 to run package smoke tests.");
        PackagedArtifacts artifacts = Artifacts.Value;

        AssertAssemblyAsset(
            artifacts.PackagePath(SimulationPackageId),
            "lib/net11.0/Orleans.Testing.Simulation.dll",
            "Orleans.Testing.Simulation",
            ".NETCoreApp,Version=v11.0");
        AssertAssemblyAsset(
            artifacts.PackagePath(InstrumentationPackageId),
            "lib/net11.0/Orleans.Testing.Simulation.Instrumentation.dll",
            "Orleans.Testing.Simulation.Instrumentation",
            ".NETCoreApp,Version=v11.0");
        AssertAssemblyAsset(
            artifacts.PackagePath(BuildPackageId),
            "tasks/net11.0/Orleans.Testing.Simulation.Instrumentation.Build.dll",
            "Orleans.Testing.Simulation.Instrumentation.Build",
            ".NETCoreApp,Version=v11.0");
        AssertAssemblyAsset(
            artifacts.PackagePath(AnalyzersPackageId),
            "analyzers/dotnet/cs/Orleans.Testing.Simulation.Analyzers.dll",
            "Orleans.Testing.Simulation.Analyzers",
            ".NETStandard,Version=v2.0");
        AssertAssemblyAsset(
            artifacts.PackagePath(TestingPackageId),
            "lib/net11.0/Orleans.Testing.Simulation.Testing.dll",
            "Orleans.Testing.Simulation.Testing",
            ".NETCoreApp,Version=v11.0");
        AssertAssemblyAsset(
            artifacts.PackagePath(TestingMtpPackageId),
            "lib/net11.0/Orleans.Testing.Simulation.Testing.MTP.dll",
            "Orleans.Testing.Simulation.Testing.MTP",
            ".NETCoreApp,Version=v11.0");
        AssertAssemblyAsset(
            artifacts.PackagePath(ToolPackageId),
            "tools/net11.0/any/Orleans.Testing.Simulation.Tool.dll",
            "Orleans.Testing.Simulation.Tool",
            ".NETCoreApp,Version=v11.0");
    }

    [Fact]
    public void PackedPackagesHaveExpectedDependencyGroups()
    {
        Assert.SkipUnless(SmokeEnabled, $"Set {SmokeEnvironmentVariable}=1 to run package smoke tests.");
        PackagedArtifacts artifacts = Artifacts.Value;

        AssertDependencyGroup(artifacts.PackagePath(SimulationPackageId), "net11.0");
        AssertDependencyGroup(
            artifacts.PackagePath(InstrumentationPackageId),
            "net11.0",
            SimulationPackageId,
            "Mono.Cecil");
        AssertNoDependencyGroups(artifacts.PackagePath(BuildPackageId));
        AssertNoDependencyGroups(artifacts.PackagePath(AnalyzersPackageId));
        AssertDependencyGroup(
            artifacts.PackagePath(TestingPackageId),
            "net11.0",
            SimulationPackageId);
        AssertDependencyGroup(
            artifacts.PackagePath(TestingMtpPackageId),
            "net11.0",
            "Microsoft.Testing.Platform",
            TestingPackageId);
        AssertNoDependencyGroups(artifacts.PackagePath(ToolPackageId));
    }

    [Fact]
    public void AnalyzerPackageContainsOnlyCompilerAnalyzerAsset()
    {
        Assert.SkipUnless(SmokeEnabled, $"Set {SmokeEnvironmentVariable}=1 to run package smoke tests.");
        string packagePath = Artifacts.Value.PackagePath(AnalyzersPackageId);
        string[] entries = PackageEntries(packagePath);

        Assert.Contains("analyzers/dotnet/cs/Orleans.Testing.Simulation.Analyzers.dll", entries);
        Assert.DoesNotContain(entries, static entry => entry.StartsWith("lib/", StringComparison.Ordinal));
        Assert.DoesNotContain(entries, static entry => entry.StartsWith("runtimes/", StringComparison.Ordinal));
        AssertNoDependencyGroups(packagePath);
    }

    [Fact]
    public void BuildPackageContainsCompleteNet11TaskClosureWithoutNuGetDependencies()
    {
        Assert.SkipUnless(SmokeEnabled, $"Set {SmokeEnvironmentVariable}=1 to run package smoke tests.");
        string packagePath = Artifacts.Value.PackagePath(BuildPackageId);
        string[] entries = PackageEntries(packagePath);

        string propsPath = Assert.Single(
            entries,
            static entry =>
                entry.StartsWith("build/", StringComparison.Ordinal)
                && entry.EndsWith(".props", StringComparison.Ordinal));
        string targetsPath = Assert.Single(
            entries,
            static entry =>
                entry.StartsWith("build/", StringComparison.Ordinal)
                && entry.EndsWith(".targets", StringComparison.Ordinal));
        Assert.Contains(
            "<OrleansSimulationInstrumentationEnabled",
            ReadPackageEntry(packagePath, propsPath),
            StringComparison.Ordinal);
        Assert.Contains(
            "TaskName=\"Orleans.Testing.Simulation.Instrumentation.Build.OrleansSimulationInstrumentTask\"",
            ReadPackageEntry(packagePath, targetsPath),
            StringComparison.Ordinal);
        Assert.Contains(
            "TaskName=\"Orleans.Testing.Simulation.Instrumentation.Build.OrleansSimulationValidateInstrumentedTestTask\"",
            ReadPackageEntry(packagePath, targetsPath),
            StringComparison.Ordinal);
        Assert.DoesNotContain("ClockworkInstrumentationEnabled", ReadPackageEntry(packagePath, propsPath), StringComparison.Ordinal);
        Assert.DoesNotContain("ClockworkInstrumentTask", ReadPackageEntry(packagePath, targetsPath), StringComparison.Ordinal);
        Assert.Equal(
            [
                "tasks/net11.0/Mono.Cecil.Mdb.dll",
                "tasks/net11.0/Mono.Cecil.Pdb.dll",
                "tasks/net11.0/Mono.Cecil.Rocks.dll",
                "tasks/net11.0/Mono.Cecil.dll",
                "tasks/net11.0/Orleans.Testing.Simulation.Instrumentation.Build.dll",
                "tasks/net11.0/Orleans.Testing.Simulation.Instrumentation.dll",
                "tasks/net11.0/Orleans.Testing.Simulation.dll",
            ],
            entries
                .Where(static entry =>
                    entry.StartsWith("tasks/net11.0/", StringComparison.Ordinal)
                    && entry.EndsWith(".dll", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal));
        Assert.DoesNotContain(entries, static entry => entry.StartsWith("lib/", StringComparison.Ordinal));
        AssertNoDependencyGroups(packagePath);
    }

    [Fact]
    public void SimulationPackageSupportsOrdinaryDotNetConsumptionWithoutForbiddenRuntimeDependencies()
    {
        Assert.SkipUnless(SmokeEnabled, $"Set {SmokeEnvironmentVariable}=1 to run package smoke tests.");
        ConsumerProject consumer = Artifacts.Value.ScaffoldOrdinarySimulationConsumer("OrdinarySimulationConsumer");

        AppRunResult build = consumer.Build();
        Assert.True(build.ExitCode == 0, $"Build failed:\n{build.StandardOutput}\n{build.StandardError}");

        AppRunResult run = consumer.Run();
        Assert.Equal(0, run.ExitCode);
        Assert.Contains("Orleans.Testing.Simulation", run.Output, StringComparison.Ordinal);

        AssertAssetsExcludePackages(
            consumer.AssetsPath,
            "Orleans.Runtime",
            "Orleans.Durable.Storage",
            InstrumentationPackageId,
            "Microsoft.Testing.Platform",
            ToolPackageId);
        Assert.DoesNotContain(
            Directory.EnumerateFiles(consumer.OutputDirectory, "*.dll", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName),
            static fileName =>
                fileName is not null
                && (fileName.StartsWith("Orleans.Runtime", StringComparison.OrdinalIgnoreCase)
                    || fileName.StartsWith("Orleans.Durable.Storage", StringComparison.OrdinalIgnoreCase)
                    || fileName.StartsWith(
                        "Orleans.Testing.Simulation.Instrumentation",
                        StringComparison.OrdinalIgnoreCase)
                    || fileName.StartsWith("Microsoft.Testing.Platform", StringComparison.OrdinalIgnoreCase)
                    || fileName.StartsWith("Orleans.Testing.Simulation.Tool", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void TestingSupportPackageKeepsOrdinaryLoggerConsumerFrameworkNeutral()
    {
        Assert.SkipUnless(SmokeEnabled, $"Set {SmokeEnvironmentVariable}=1 to run package smoke tests.");
        ConsumerProject consumer = Artifacts.Value.ScaffoldOrdinaryTestingSupportConsumer(
            "OrdinaryTestingSupportConsumer");

        XDocument project = XDocument.Load(consumer.ProjectPath);
        Assert.Empty(project.Descendants("OutputType"));
        Assert.Equal(
            [TestingPackageId],
            project.Descendants("PackageReference")
                .Select(static element => (string?)element.Attribute("Include"))
                .Where(static value => value is not null)
                .Select(static value => value!)
                .ToArray());

        AppRunResult build = consumer.Build();
        Assert.True(build.ExitCode == 0, $"Build failed:\n{build.StandardOutput}\n{build.StandardError}");

        AppRunResult evaluation = consumer.EvaluateProject(
            "-getProperty:OutputType,IsTestingPlatformApplication",
            "-getItem:TestingPlatformBuilderHook");
        Assert.True(
            evaluation.ExitCode == 0,
            $"Project evaluation failed:\n{evaluation.StandardOutput}\n{evaluation.StandardError}");
        using JsonDocument evaluated = JsonDocument.Parse(evaluation.Output);
        JsonElement properties = evaluated.RootElement.GetProperty("Properties");
        Assert.Equal("Library", properties.GetProperty("OutputType").GetString());
        Assert.False(
            string.Equals(
                "true",
                properties.GetProperty("IsTestingPlatformApplication").GetString(),
                StringComparison.OrdinalIgnoreCase));
        Assert.Empty(
            evaluated.RootElement.GetProperty("Items").GetProperty("TestingPlatformBuilderHook").EnumerateArray());

        string[] packageIds = PackageBoundaryInspector.ReadPackageIds(consumer.AssetsPath);
        Assert.Contains(SimulationPackageId, packageIds);
        Assert.Contains(TestingPackageId, packageIds);
        Assert.DoesNotContain(TestingMtpPackageId, packageIds);
        Assert.DoesNotContain("Microsoft.Testing.Platform", packageIds);
        Assert.DoesNotContain(
            Directory.EnumerateFiles(consumer.OutputDirectory, "*.dll", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName),
            static fileName =>
                fileName is not null
                && (fileName.StartsWith("Microsoft.Testing.Platform", StringComparison.OrdinalIgnoreCase)
                    || fileName.StartsWith(
                        "Orleans.Testing.Simulation.Testing.MTP",
                        StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void ProductionAppAndPackageRemainSimulationFreeUnderDeploymentAnalyzers()
    {
        Assert.SkipUnless(SmokeEnabled, $"Set {SmokeEnvironmentVariable}=1 to run package smoke tests.");
        PackagedArtifacts artifacts = Artifacts.Value;
        ProductionBoundaryFixture fixture = artifacts.ScaffoldProductionBoundaryFixture("ProductionBoundary");

        AppRunResult build = fixture.Build();
        Assert.True(build.ExitCode == 0, $"Production build failed:\n{build.StandardOutput}\n{build.StandardError}");

        string[] evaluatedBoundary = fixture.EvaluateDependencyBoundary();
        Assert.Contains(
            evaluatedBoundary,
            line =>
                line.StartsWith("project|", StringComparison.Ordinal)
                && line.EndsWith("Clockwork.Fixtures.Production.csproj", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            evaluatedBoundary,
            line => line.StartsWith("package|", StringComparison.Ordinal)
                && line.Contains("Orleans.Testing.Simulation.PackageApi", StringComparison.Ordinal));
        Assert.Contains(
            evaluatedBoundary,
            line => line.StartsWith("package|", StringComparison.Ordinal)
                && line.Contains("Orleans.Durable.Storage.Diagnostics", StringComparison.Ordinal));
        string[] evaluatedPackageIds =
        [
            .. evaluatedBoundary
                .Where(static line => line.StartsWith("package|", StringComparison.Ordinal))
                .Select(static line => line.Split('|')[2]),
        ];
        Assert.Empty(PackageBoundaryInspector.FindForbiddenPackages(evaluatedPackageIds));

        string[] qualificationLines =
        [
            .. evaluatedBoundary.Where(static line => line.StartsWith("qualification|", StringComparison.Ordinal)),
        ];
        Assert.Equal(2, qualificationLines.Length);
        Assert.All(
            qualificationLines,
            static line => Assert.EndsWith("|true|true|true|true", line, StringComparison.OrdinalIgnoreCase));

        foreach (string assetsPath in new[] { fixture.AppAssetsPath, fixture.LibraryAssetsPath })
        {
            string[] packageIds = PackageBoundaryInspector.ReadPackageIds(assetsPath);
            Assert.Contains("Orleans.Testing.Simulation.PackageApi", packageIds);
            Assert.Contains("Orleans.Durable.Storage.Diagnostics", packageIds);
            Assert.Empty(
                PackageBoundaryInspector.FindForbiddenPackages(
                    packageIds));
            string[] resolvedAssets = PackageBoundaryInspector.ReadResolvedAssets(
                assetsPath,
                "compile",
                "runtime",
                "runtimeTargets");
            Assert.Contains(
                resolvedAssets,
                static asset => asset.EndsWith("PackageApi.dll", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(
                resolvedAssets,
                static asset => asset.EndsWith(
                    "Orleans.Durable.Storage.Diagnostics.dll",
                    StringComparison.OrdinalIgnoreCase));
            Assert.Empty(
                PackageBoundaryInspector.FindForbiddenRuntimeAssets(
                    resolvedAssets));
            SimulationAssemblyCollisionGate.AssertNoCollisionInAssetsFile(assetsPath);
        }

        string[] outputAssemblies =
        [
            .. Directory.EnumerateFiles(fixture.OutputDirectory, "*.dll", SearchOption.TopDirectoryOnly),
        ];
        Assert.Contains(outputAssemblies, path => Path.GetFileName(path) == "Clockwork.Fixtures.Production.dll");
        Assert.Contains(outputAssemblies, path => Path.GetFileName(path) == "Orleans.Durable.Storage.Diagnostics.dll");
        Assert.Empty(PackageBoundaryInspector.FindForbiddenAssemblyFiles(outputAssemblies));
        SimulationAssemblyCollisionGate.AssertNoCollisionInDirectory(
            "production application output",
            fixture.OutputDirectory);

        PackageArchiveInspection package = PackageBoundaryInspector.InspectPackage(
            artifacts.PackagePath(ProductionFixturePackageId));
        Assert.Equal(ProductionFixturePackageId, package.PackageId);
        Assert.Contains("lib/net11.0/Clockwork.Fixtures.Production.dll", package.Entries);
        PackageDependencyGroup dependencyGroup = Assert.Single(package.DependencyGroups);
        Assert.Equal("net11.0", dependencyGroup.TargetFramework);
        Assert.Equal(
            ["Orleans.Durable.Storage.Diagnostics", "Orleans.Testing.Simulation.PackageApi"],
            dependencyGroup.PackageIds);
        Assert.Empty(PackageBoundaryInspector.FindForbiddenPackages(dependencyGroup.PackageIds));
        Assert.Empty(PackageBoundaryInspector.FindForbiddenRuntimeAssets(package.Entries));
        SimulationAssemblyCollisionGate.AssertNoCollisionInPackage(
            artifacts.PackagePath(ProductionFixturePackageId));
    }

    [Fact]
    public void TestingSupportPackageIsFrameworkNeutralAndContainsNoBuildHooks()
    {
        Assert.SkipUnless(SmokeEnabled, $"Set {SmokeEnvironmentVariable}=1 to run package smoke tests.");
        string packagePath = Artifacts.Value.PackagePath(TestingPackageId);
        string[] entries = PackageEntries(packagePath);

        AssertDependencyGroup(
            packagePath,
            "net11.0",
            SimulationPackageId);
        Assert.Equal(TestingSupportPackagePayload, PackagePayloadEntries(entries));
    }

    [Fact]
    public void TestingMtpPackageOwnsMicrosoftTestingPlatformDependencyAndBuilderHook()
    {
        Assert.SkipUnless(SmokeEnabled, $"Set {SmokeEnvironmentVariable}=1 to run package smoke tests.");
        string packagePath = Artifacts.Value.PackagePath(TestingMtpPackageId);
        string[] entries = PackageEntries(packagePath);

        AssertDependencyGroup(
            packagePath,
            "net11.0",
            "Microsoft.Testing.Platform",
            TestingPackageId);
        Assert.Equal(TestingMtpPackagePayload, PackagePayloadEntries(entries));
        string propsPath = Assert.Single(
            entries,
            static entry =>
                entry.StartsWith("buildTransitive/", StringComparison.Ordinal)
                && entry.EndsWith(".props", StringComparison.Ordinal));
        string multiTargetingPropsPath = Assert.Single(
            entries,
            static entry =>
                entry.StartsWith("buildMultiTargeting/", StringComparison.Ordinal)
                && entry.EndsWith(".props", StringComparison.Ordinal));
        string props = ReadPackageEntry(packagePath, propsPath);
        XElement hook = Assert.Single(XDocument.Parse(props).Descendants("TestingPlatformBuilderHook"));
        Assert.Equal(TestingMtpBuilderHookId, hook.Attribute("Include")?.Value);
        Assert.Equal(TestingMtpBuilderHookType, hook.Element("TypeFullName")?.Value);
        Assert.Equal(TestingMtpPackageId, hook.Element("DisplayName")?.Value);
        Assert.Equal(
            props,
            ReadPackageEntry(packagePath, multiTargetingPropsPath));
    }

    [Fact]
    public void BuildPackageInstrumentsAppWhenEnabled()
    {
        Assert.SkipUnless(SmokeEnabled, $"Set {SmokeEnvironmentVariable}=1 to run package smoke tests.");
        PackagedArtifacts artifacts = Artifacts.Value;

        ConsumerProject consumer = artifacts.ScaffoldConsumer("EnabledApp", instrumentationEnabled: true);
        AppRunResult build = consumer.Build();
        Assert.True(build.ExitCode == 0, $"Build failed:\n{build.StandardOutput}\n{build.StandardError}");

        // The opt-in build emits the manifest to the predictable intermediate path.
        Assert.True(File.Exists(consumer.ManifestPath), $"Manifest not found at {consumer.ManifestPath}");

        // The uninstrumented output behaves normally; the staged, instrumented output dispatches to
        // the shim - proving the packaged targets rewrote the real build output out-of-place.
        AppRunResult normal = ProcessAppRunner.Run(consumer.OutputAppPath);
        Assert.Equal(0, normal.ExitCode);
        Assert.Contains("ticks=100", normal.Output);

        AppRunResult staged = ProcessAppRunner.Run(consumer.StagedAppPath);
        Assert.Equal(0, staged.ExitCode);
        Assert.Contains("ticks=999", staged.Output);
    }

    [Fact]
    public void BuildPackageBuiltInExecutableRequiresActiveSimulation()
    {
        Assert.SkipUnless(SmokeEnabled, $"Set {SmokeEnvironmentVariable}=1 to run package smoke tests.");
        ConsumerProject consumer = Artifacts.Value.ScaffoldConsumer(
            "BuiltInApp",
            instrumentationEnabled: true,
            useBuiltInRules: true);
        AppRunResult build = consumer.Build();
        Assert.True(build.ExitCode == 0, $"Build failed:\n{build.StandardOutput}\n{build.StandardError}");

        AppRunResult normal = ProcessAppRunner.Run(consumer.OutputAppPath);
        Assert.Equal(0, normal.ExitCode);
        Assert.Contains("reached-end", normal.Output);
        Assert.True(File.Exists(ConsumerProject.SideEffectPath(consumer.OutputAppPath)));
        File.Delete(ConsumerProject.SideEffectPath(consumer.OutputAppPath));

        AppRunResult staged = ProcessAppRunner.Run(consumer.StagedAppPath);
        Assert.NotEqual(0, staged.ExitCode);
        Assert.Empty(staged.Output);
        Assert.Contains(typeof(SimulationNotActiveException).FullName!, staged.StandardError, StringComparison.Ordinal);
        Assert.Contains(SimulationNotActiveException.DiagnosticMessage, staged.StandardError, StringComparison.Ordinal);
        Assert.False(File.Exists(ConsumerProject.SideEffectPath(consumer.StagedAppPath)));
    }

    [Fact]
    public void BuildPackageFailsBuiltInRulesForAnotherTargetRuntime()
    {
        Assert.SkipUnless(SmokeEnabled, $"Set {SmokeEnvironmentVariable}=1 to run package smoke tests.");
        ConsumerProject consumer = Artifacts.Value.ScaffoldConsumer(
            "BuiltInOtherRuntimeApp",
            instrumentationEnabled: true,
            useBuiltInRules: true,
            useConfigurationFile: true);

        // The configuration file names no targetRuntime, so OrleansSimulationTargetRuntime sets the target.
        AppRunResult build = consumer.Build("-p:OrleansSimulationTargetRuntime=12.0");

        Assert.NotEqual(0, build.ExitCode);
        Assert.Contains(RewriteDiagnosticIds.RuntimeOutOfRange, build.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPackageDoesNothingWhenDisabled()
    {
        Assert.SkipUnless(SmokeEnabled, $"Set {SmokeEnvironmentVariable}=1 to run package smoke tests.");
        PackagedArtifacts artifacts = Artifacts.Value;

        ConsumerProject consumer = artifacts.ScaffoldConsumer("DisabledApp", instrumentationEnabled: false);
        AppRunResult build = consumer.Build();
        Assert.True(build.ExitCode == 0, $"Build failed:\n{build.StandardOutput}\n{build.StandardError}");

        // An ordinary build must not instrument: no manifest, no staged closure.
        Assert.False(File.Exists(consumer.ManifestPath), "Manifest was emitted on a non-opted-in build.");
        Assert.False(Directory.Exists(consumer.StagingDirectory), "Staging directory was created on a non-opted-in build.");
        Assert.False(File.Exists(consumer.SuccessPath), "Success marker was emitted on a non-opted-in build.");
        Assert.False(File.Exists(consumer.ModeStampPath), "Mode stamp was emitted on a non-opted-in build.");

        AppRunResult normal = ProcessAppRunner.Run(consumer.OutputAppPath);
        Assert.Equal(0, normal.ExitCode);
        Assert.Contains("ticks=100", normal.Output);
    }

    [Fact]
    public void BuildPackageRunsInstrumentedTestProjectsFromStagedClosure()
    {
        Assert.SkipUnless(SmokeEnabled, $"Set {SmokeEnvironmentVariable}=1 to run package smoke tests.");
        ConsumerProject consumer = Artifacts.Value.ScaffoldConsumer(
            "InstrumentedTestApp",
            instrumentationEnabled: false,
            instrumentedTestProject: true);

        AppRunResult build = consumer.Build();
        Assert.True(build.ExitCode == 0, $"Build failed:\n{build.StandardOutput}\n{build.StandardError}");

        AppRunResult pristine = ProcessAppRunner.Run(consumer.UninstrumentedAppPath);
        Assert.Equal(0, pristine.ExitCode);
        Assert.Contains("instrumented=False", pristine.Output);

        AppRunResult deployed = ProcessAppRunner.Run(consumer.OutputAppPath);
        Assert.Equal(0, deployed.ExitCode);
        Assert.Contains("instrumented=True", deployed.Output);

        AppRunResult testRun = consumer.Run("forwarded");
        Assert.True(
            testRun.ExitCode == 0,
            $"Instrumented test run failed ({testRun.ExitCode}):\n{testRun.StandardOutput}\n{testRun.StandardError}");
        Assert.Contains("instrumented=True", testRun.Output);
        Assert.Contains("argument=forwarded", testRun.Output);
    }

    [Fact]
    public void MtpStagedClosureKeepsHostKernelAndExcludedDiagnosticsOrdinaryWhileRewritingSubject()
    {
        Assert.SkipUnless(SmokeEnabled, $"Set {SmokeEnvironmentVariable}=1 to run package smoke tests.");
        ConsumerProject consumer = Artifacts.Value.ScaffoldConsumer(
            "InstrumentedMtpTestApp",
            instrumentationEnabled: false,
            instrumentedTestProject: true,
            testingPlatformProject: true);

        AppRunResult build = consumer.Build();
        Assert.True(build.ExitCode == 0, $"Build failed:\n{build.StandardOutput}\n{build.StandardError}");

        using (Mono.Cecil.ModuleDefinition ordinary = Mono.Cecil.ModuleDefinition.ReadModule(consumer.UninstrumentedAppPath))
        {
            Assert.False(CecilInspect.HasRewriteSignature(ordinary));
        }
        using (Mono.Cecil.ModuleDefinition deployed = Mono.Cecil.ModuleDefinition.ReadModule(consumer.OutputAppPath))
        {
            Assert.False(CecilInspect.HasRewriteSignature(deployed));
        }
        using (Mono.Cecil.ModuleDefinition host = Mono.Cecil.ModuleDefinition.ReadModule(consumer.StagedHostPath))
        {
            Assert.False(CecilInspect.HasRewriteSignature(host));
        }
        using (Mono.Cecil.ModuleDefinition kernel = Mono.Cecil.ModuleDefinition.ReadModule(consumer.StagedKernelPath))
        {
            Assert.False(CecilInspect.HasRewriteSignature(kernel));
        }
        using (Mono.Cecil.ModuleDefinition diagnostics = Mono.Cecil.ModuleDefinition.ReadModule(consumer.StagedDiagnosticsPath))
        {
            Assert.False(CecilInspect.HasRewriteSignature(diagnostics));
        }
        using (Mono.Cecil.ModuleDefinition subject = Mono.Cecil.ModuleDefinition.ReadModule(consumer.StagedSubjectPath))
        {
            Assert.True(CecilInspect.HasRewriteSignature(subject));
        }

        AppRunResult testRun = consumer.TestNoBuild();
        Assert.True(
            testRun.ExitCode == 0,
            $"Instrumented dotnet test --no-build failed ({testRun.ExitCode}):\n{testRun.StandardOutput}\n{testRun.StandardError}");
        Assert.Contains("succeeded: 1", testRun.StandardOutput, StringComparison.OrdinalIgnoreCase);

        consumer.AddPassingTest();
        AppRunResult incrementalBuild = consumer.Build();
        Assert.True(
            incrementalBuild.ExitCode == 0,
            $"Incremental build failed:\n{incrementalBuild.StandardOutput}\n{incrementalBuild.StandardError}");
        AppRunResult incrementalTestRun = consumer.TestNoBuild();
        Assert.True(
            incrementalTestRun.ExitCode == 0,
            $"Instrumented test after incremental build failed ({incrementalTestRun.ExitCode}):\n{incrementalTestRun.StandardOutput}\n{incrementalTestRun.StandardError}");
        Assert.Contains("succeeded: 2", incrementalTestRun.StandardOutput, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PackageOnlyMtpConsumerRunsInstrumentedDotnetTestNoBuildWithOrdinaryDiagnosticsAndNoCollisions()
    {
        Assert.SkipUnless(SmokeEnabled, $"Set {SmokeEnvironmentVariable}=1 to run package smoke tests.");
        ConsumerProject consumer = Artifacts.Value.ScaffoldPackageOnlyMtpConsumer("PackageOnlyMtpConsumer");

        XDocument project = XDocument.Load(consumer.ProjectPath);
        string projectXml = File.ReadAllText(consumer.ProjectPath);
        Assert.Empty(project.Descendants("ProjectReference"));
        Assert.Contains(
            "<OrleansSimulationInstrumentedTestProject>true</OrleansSimulationInstrumentedTestProject>",
            projectXml,
            StringComparison.Ordinal);
        Assert.Contains("<OrleansSimulationRuleSet ", projectXml, StringComparison.Ordinal);
        Assert.Contains("<OrleansSimulationBuiltInRuleSet ", projectXml, StringComparison.Ordinal);
        Assert.Contains("<OrleansSimulationExclude ", projectXml, StringComparison.Ordinal);
        Assert.DoesNotContain("<Clockwork", projectXml, StringComparison.Ordinal);
        string[] packageReferences =
        [
            .. project.Descendants("PackageReference")
                .Select(static element => (string?)element.Attribute("Include"))
                .Where(static value => value is not null)
                .Select(static value => value!),
        ];
        Assert.Contains(SimulationPackageId, packageReferences);
        Assert.Contains(TestingMtpPackageId, packageReferences);
        Assert.DoesNotContain(TestingPackageId, packageReferences);
        Assert.Contains(BuildPackageId, packageReferences);
        Assert.Contains(AnalyzersPackageId, packageReferences);
        Assert.Contains(ProductionFixturePackageId, packageReferences);
        Assert.Contains("Orleans.Durable.Storage.Diagnostics", packageReferences);

        AppRunResult build = consumer.Build();
        Assert.True(build.ExitCode == 0, $"Build failed:\n{build.StandardOutput}\n{build.StandardError}");

        string[] restoredPackageIds = PackageBoundaryInspector.ReadPackageIds(consumer.AssetsPath);
        Assert.Contains(SimulationPackageId, restoredPackageIds);
        Assert.Contains(TestingPackageId, restoredPackageIds);
        Assert.Contains(TestingMtpPackageId, restoredPackageIds);
        Assert.Contains(BuildPackageId, restoredPackageIds);
        Assert.Contains(AnalyzersPackageId, restoredPackageIds);
        string[] restoredBuildPackageFiles =
            PackageBoundaryInspector.ReadPackageFiles(consumer.AssetsPath, BuildPackageId);
        Assert.Contains(
            "build/Microsoft.Orleans.Testing.Simulation.Instrumentation.Build.props",
            restoredBuildPackageFiles);
        Assert.Contains(
            "build/Microsoft.Orleans.Testing.Simulation.Instrumentation.Build.targets",
            restoredBuildPackageFiles);
        Assert.Contains(
            "tasks/net11.0/Orleans.Testing.Simulation.Instrumentation.Build.dll",
            restoredBuildPackageFiles);
        string[] restoredAnalyzerPackageFiles =
            PackageBoundaryInspector.ReadPackageFiles(consumer.AssetsPath, AnalyzersPackageId);
        Assert.Contains(
            "analyzers/dotnet/cs/Orleans.Testing.Simulation.Analyzers.dll",
            restoredAnalyzerPackageFiles);
        Assert.DoesNotContain(
            restoredAnalyzerPackageFiles,
            static path => path.StartsWith("lib/", StringComparison.OrdinalIgnoreCase));

        string[] evaluatedImports = File.ReadAllLines(consumer.EvaluatedImportsPath);
        string buildTaskImport = Assert.Single(
            evaluatedImports,
            static line => line.StartsWith("buildTask|", StringComparison.Ordinal));
        string normalizedBuildTaskImport = buildTaskImport.Replace('\\', '/');
        Assert.Contains(BuildPackageId, normalizedBuildTaskImport, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(
            "tasks/net11.0/Orleans.Testing.Simulation.Instrumentation.Build.dll",
            normalizedBuildTaskImport,
            StringComparison.OrdinalIgnoreCase);
        string analyzerImport = Assert.Single(
            evaluatedImports,
            static line =>
                line.StartsWith("analyzer|", StringComparison.Ordinal)
                && line.EndsWith(
                    "Orleans.Testing.Simulation.Analyzers.dll",
                    StringComparison.OrdinalIgnoreCase));
        string normalizedAnalyzerImport = analyzerImport.Replace('\\', '/');
        Assert.Contains(AnalyzersPackageId, normalizedAnalyzerImport, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(
            "analyzers/dotnet/cs/Orleans.Testing.Simulation.Analyzers.dll",
            normalizedAnalyzerImport,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            evaluatedImports,
            static line =>
                line == $"mtpHook|{TestingMtpBuilderHookId}|{TestingMtpBuilderHookType}|{TestingMtpPackageId}");
        string[] evaluatedPackageIds =
        [
            .. evaluatedImports
                .Where(static line => line.StartsWith("package|", StringComparison.Ordinal))
                .Select(static line => line.Split('|')[1]),
        ];
        Assert.Equal(
            new[]
            {
                AnalyzersPackageId,
                BuildPackageId,
                "Orleans.Durable.Storage.Diagnostics",
                ProductionFixturePackageId,
                SimulationPackageId,
                TestingMtpPackageId,
                "xunit.v3.mtp-v2",
            }.Order(StringComparer.OrdinalIgnoreCase),
            evaluatedPackageIds.Order(StringComparer.OrdinalIgnoreCase));

        using (Mono.Cecil.ModuleDefinition ordinary =
               Mono.Cecil.ModuleDefinition.ReadModule(consumer.UninstrumentedProductionLibraryPath))
        {
            Assert.False(CecilInspect.HasRewriteSignature(ordinary));
        }
        using (Mono.Cecil.ModuleDefinition rewritten =
               Mono.Cecil.ModuleDefinition.ReadModule(consumer.OutputProductionLibraryPath))
        {
            Assert.True(CecilInspect.HasRewriteSignature(rewritten));
        }
        using (Mono.Cecil.ModuleDefinition driver = Mono.Cecil.ModuleDefinition.ReadModule(consumer.OutputAppPath))
        {
            Assert.False(CecilInspect.HasRewriteSignature(driver));
        }
        using (Mono.Cecil.ModuleDefinition diagnostics =
               Mono.Cecil.ModuleDefinition.ReadModule(consumer.OutputDiagnosticsPath))
        {
            Assert.False(CecilInspect.HasRewriteSignature(diagnostics));
        }

        ClosureManifest manifest = ClosureManifestJson.Read(consumer.ManifestPath, out _);
        Assert.Contains(
            manifest.CopiedAssets,
            static asset => asset.RelativePath == "Orleans.Durable.Storage.Diagnostics.dll");
        Assert.Contains(
            manifest.CopiedAssets,
            static asset => asset.RelativePath == "SmokeApp.dll");
        Assert.DoesNotContain(
            manifest.Assemblies,
            static assembly => assembly.RelativePath == "Orleans.Durable.Storage.Diagnostics.dll");
        Assert.DoesNotContain(
            manifest.Assemblies,
            static assembly => assembly.RelativePath == "SmokeApp.dll");
        Assert.Contains(
            manifest.Assemblies,
            static assembly => assembly.RelativePath == "Clockwork.Fixtures.Production.dll");

        AppRunResult testRun = consumer.TestNoBuild();
        Assert.True(
            testRun.ExitCode == 0,
            $"Package-only instrumented dotnet test --no-build failed ({testRun.ExitCode}):\n"
            + $"{testRun.StandardOutput}\n{testRun.StandardError}");
        Assert.Contains("succeeded: 2", testRun.StandardOutput, StringComparison.OrdinalIgnoreCase);

        SimulationAssemblyCollisionGate.AssertNoCollisionInAssetsFile(consumer.AssetsPath);
        string[] compileReferences = File.ReadAllLines(consumer.EvaluatedReferencesPath);
        Assert.Contains(
            compileReferences,
            static path => path.EndsWith("Clockwork.Fixtures.Production.dll", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            compileReferences,
            static path => path.EndsWith("Orleans.Testing.Simulation.dll", StringComparison.OrdinalIgnoreCase));
        SimulationAssemblyCollisionGate.AssertNoCollisionInFile(
            "evaluated compile references",
            consumer.EvaluatedReferencesPath);
        string[] runtimeReferences = File.ReadAllLines(consumer.CopyLocalReferencesPath);
        Assert.Contains(
            runtimeReferences,
            static path => path.EndsWith("Clockwork.Fixtures.Production.dll", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            runtimeReferences,
            static path => path.EndsWith(
                "Orleans.Durable.Storage.Diagnostics.dll",
                StringComparison.OrdinalIgnoreCase));
        SimulationAssemblyCollisionGate.AssertNoCollisionInFile(
            "evaluated runtime references",
            consumer.CopyLocalReferencesPath);
        SimulationAssemblyCollisionGate.AssertNoCollisionInDirectory(
            "copied test output",
            consumer.OutputDirectory);
        string[] runtimeLoads = File.ReadAllLines(consumer.RuntimeLoadsPath);
        Assert.Contains("SmokeApp", runtimeLoads);
        Assert.Contains("Clockwork.Fixtures.Production", runtimeLoads);
        Assert.Contains("Orleans.Durable.Storage.Diagnostics", runtimeLoads);
        Assert.Contains("Orleans.Testing.Simulation", runtimeLoads);
        SimulationAssemblyCollisionGate.AssertNoCollisionInFile(
            "runtime loads",
            consumer.RuntimeLoadsPath);
    }

    [Fact]
    public void BuildPackageRejectsMalformedConfigurationWithoutInstrumentationOutputs()
    {
        Assert.SkipUnless(SmokeEnabled, $"Set {SmokeEnvironmentVariable}=1 to run package smoke tests.");
        ConsumerProject consumer = Artifacts.Value.ScaffoldConsumer(
            "MalformedConfigurationApp",
            instrumentationEnabled: true,
            useConfigurationFile: true);
        consumer.WriteConfiguration("""{ "schemaVersion": 2, "ruleSets": [ }""");

        AppRunResult build = consumer.Build();

        Assert.NotEqual(0, build.ExitCode);
        Assert.Contains("JSON", build.StandardOutput + build.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(consumer.ManifestPath));
        Assert.False(Directory.Exists(consumer.StagingDirectory));
        Assert.False(File.Exists(consumer.SuccessPath));
    }

    [Fact]
    public void BuildPackageBuiltInRulesKeepTestingPlatformRunnable()
    {
        Assert.SkipUnless(SmokeEnabled, $"Set {SmokeEnvironmentVariable}=1 to run package smoke tests.");
        ConsumerProject consumer = Artifacts.Value.ScaffoldConsumer(
            "InstrumentedBuiltInMtpTestApp",
            instrumentationEnabled: false,
            useBuiltInRules: true,
            instrumentedTestProject: true,
            testingPlatformProject: true,
            useConfigurationFile: true);

        AppRunResult build = consumer.Build();
        Assert.True(build.ExitCode == 0, $"Build failed:\n{build.StandardOutput}\n{build.StandardError}");

        AppRunResult testRun = consumer.TestNoBuild();
        Assert.True(
            testRun.ExitCode == 0,
            $"Built-in instrumented test failed ({testRun.ExitCode}):\n{testRun.StandardOutput}\n{testRun.StandardError}");
    }

    [Fact]
    public void BuildPackageSupportsExplicitRaceExplorationMode()
    {
        Assert.SkipUnless(SmokeEnabled, $"Set {SmokeEnvironmentVariable}=1 to run package smoke tests.");
        ConsumerProject consumer = Artifacts.Value.ScaffoldConsumer(
            "RaceExplorationApp",
            instrumentationEnabled: true,
            mode: InstrumentationMode.RaceExploration);

        AppRunResult build = consumer.Build();

        Assert.True(build.ExitCode == 0, $"Build failed:\n{build.StandardOutput}\n{build.StandardError}");
        Assert.Contains("\"mode\": \"RaceExploration\"", File.ReadAllText(consumer.ManifestPath));
        Assert.True(File.Exists(Path.Combine(consumer.StagingDirectory, "Orleans.Testing.Simulation.dll")));
        AppRunResult staged = ProcessAppRunner.Run(consumer.StagedAppPath);
        Assert.Equal(0, staged.ExitCode);
        Assert.Contains("ticks=999", staged.Output);
    }

    [Fact]
    public void BuildPackageReinstrumentsWhenModeTogglesBackAndForth()
    {
        Assert.SkipUnless(SmokeEnabled, $"Set {SmokeEnvironmentVariable}=1 to run package smoke tests.");
        ConsumerProject consumer = Artifacts.Value.ScaffoldConsumer(
            "ModeToggleApp",
            instrumentationEnabled: true);
        Assert.Equal(0, consumer.Build().ExitCode);
        Assert.Contains("\"mode\": \"Controlled\"", File.ReadAllText(consumer.ManifestPath));

        consumer.SetInstrumentationMode(InstrumentationMode.RaceExploration);
        Assert.Equal(0, consumer.Build().ExitCode);
        Assert.Contains("\"mode\": \"RaceExploration\"", File.ReadAllText(consumer.ManifestPath));

        consumer.SetInstrumentationMode(InstrumentationMode.Controlled);
        Assert.Equal(0, consumer.Build().ExitCode);
        Assert.Contains("\"mode\": \"Controlled\"", File.ReadAllText(consumer.ManifestPath));
        using Mono.Cecil.ModuleDefinition module = Mono.Cecil.ModuleDefinition.ReadModule(consumer.StagedAppPath);
        Assert.False(CecilInspect.AnyMethodCallsContaining(module, "RaceInstrumentation"));
    }

    [Fact]
    public void BuildPackageStripsStrongNameWithoutMutatingProductionOutput()
    {
        Assert.SkipUnless(SmokeEnabled, $"Set {SmokeEnvironmentVariable}=1 to run package smoke tests.");
        ConsumerProject consumer = Artifacts.Value.ScaffoldConsumer(
            "SignedApp", instrumentationEnabled: true, signEntryAssembly: true);

        AppRunResult build = consumer.Build();

        Assert.True(build.ExitCode == 0, $"Build failed:\n{build.StandardOutput}\n{build.StandardError}");
        Assert.Equal(
            Orleans.Testing.Simulation.Instrumentation.Signing.StrongNameStatus.StrongNameSigned,
            Orleans.Testing.Simulation.Instrumentation.Signing.StrongNameInspector.Inspect(consumer.OutputAppPath).Status);
        Assert.Equal(
            Orleans.Testing.Simulation.Instrumentation.Signing.StrongNameStatus.None,
            Orleans.Testing.Simulation.Instrumentation.Signing.StrongNameInspector.Inspect(consumer.StagedAppPath).Status);
        Assert.Contains("ticks=999", ProcessAppRunner.Run(consumer.StagedAppPath).Output);
        Assert.True(File.Exists(consumer.SuccessPath));
    }

    [Fact]
    public void BuildPackageReinstrumentsWhenOnlyDependencyChanges()
    {
        Assert.SkipUnless(SmokeEnabled, $"Set {SmokeEnvironmentVariable}=1 to run package smoke tests.");
        ConsumerProject consumer = Artifacts.Value.ScaffoldConsumer("DependencyChangeApp", instrumentationEnabled: true);

        AppRunResult first = consumer.Build();
        Assert.Equal(0, first.ExitCode);
        Assert.Contains("ticks=999", ProcessAppRunner.Run(consumer.StagedAppPath).Output);

        consumer.ReplaceShimTicks(777);
        AppRunResult second = consumer.Build();

        Assert.Equal(0, second.ExitCode);
        Assert.Contains("Orleans simulation: instrumenting", second.StandardOutput);
        Assert.Contains("ticks=777", ProcessAppRunner.Run(consumer.StagedAppPath).Output);
    }

    [Fact]
    public void BuildPackageRegeneratesDeletedManifest()
    {
        Assert.SkipUnless(SmokeEnabled, $"Set {SmokeEnvironmentVariable}=1 to run package smoke tests.");
        ConsumerProject consumer = Artifacts.Value.ScaffoldConsumer("DeletedManifestApp", instrumentationEnabled: true);
        Assert.Equal(0, consumer.Build().ExitCode);
        File.Delete(consumer.ManifestPath);

        AppRunResult second = consumer.Build();

        Assert.Equal(0, second.ExitCode);
        Assert.Contains("Orleans simulation: instrumenting", second.StandardOutput);
        Assert.True(File.Exists(consumer.ManifestPath));
    }

    [Fact]
    public void InstalledToolReportsVersion()
    {
        Assert.SkipUnless(SmokeEnabled, $"Set {SmokeEnvironmentVariable}=1 to run package smoke tests.");
        PackagedArtifacts artifacts = Artifacts.Value;

        AppRunResult version = artifacts.RunTool(["--version"]);
        Assert.Equal(0, version.ExitCode);
        // The tool reports the engine assembly version, which the smoke pack pinned to 9.9.9.
        Assert.StartsWith("9.9.9", version.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void InstalledToolListsReplayCommands()
    {
        Assert.SkipUnless(SmokeEnabled, $"Set {SmokeEnvironmentVariable}=1 to run package smoke tests.");
        AppRunResult help = Artifacts.Value.RunTool(["--help"]);

        Assert.Equal(0, help.ExitCode);
        Assert.Contains("dotnet-orleans-simulation - deterministic Orleans simulation tool", help.Output, StringComparison.Ordinal);
        Assert.Contains("dotnet orleans-simulation instrument", help.Output, StringComparison.Ordinal);
        Assert.Contains("dotnet orleans-simulation record", help.Output, StringComparison.Ordinal);
        Assert.Contains("dotnet orleans-simulation replay", help.Output, StringComparison.Ordinal);
        Assert.Contains("dotnet orleans-simulation explore", help.Output, StringComparison.Ordinal);
        Assert.Contains("dotnet orleans-simulation minimize", help.Output, StringComparison.Ordinal);
        Assert.Contains("dotnet orleans-simulation trace show", help.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet clockwork", help.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet-clockwork", help.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void InstalledToolInspectsAssemblyAsJson()
    {
        Assert.SkipUnless(SmokeEnabled, $"Set {SmokeEnvironmentVariable}=1 to run package smoke tests.");
        PackagedArtifacts artifacts = Artifacts.Value;

        string probeDir = Path.Combine(artifacts.Root, "probe");
        Directory.CreateDirectory(probeDir);
        string assembly = FixtureCompiler.Compile(
            "SmokeProbe",
            "namespace SmokeProbe { public static class P { public static int V() => 1; } }",
            probeDir,
            FixtureSymbols.PortableFile,
            optimize: false);

        AppRunResult inspect = artifacts.RunTool(["inspect", assembly, "--json"]);
        Assert.Equal(0, inspect.ExitCode);
        Assert.Contains("\"managed\": true", inspect.Output);
        Assert.Contains("SmokeProbe.dll", inspect.Output);
    }

    [Fact]
    public void InstalledToolBuiltInInstrumentationRequiresActiveSimulation()
    {
        Assert.SkipUnless(SmokeEnabled, $"Set {SmokeEnvironmentVariable}=1 to run package smoke tests.");
        PackagedArtifacts artifacts = Artifacts.Value;
        ConsumerProject consumer = artifacts.ScaffoldConsumer(
            "ToolBuiltInApp",
            instrumentationEnabled: false,
            useBuiltInRules: true);
        AppRunResult build = consumer.Build();
        Assert.True(build.ExitCode == 0, $"Build failed:\n{build.StandardOutput}\n{build.StandardError}");

        AppRunResult normal = ProcessAppRunner.Run(consumer.OutputAppPath);
        Assert.Equal(0, normal.ExitCode);
        Assert.Contains("reached-end", normal.Output);
        File.Delete(ConsumerProject.SideEffectPath(consumer.OutputAppPath));

        string source = Path.GetDirectoryName(consumer.OutputAppPath)!;
        string staging = Path.Combine(consumer.ProjectDirectory, "tool-staged");
        AppRunResult instrument = artifacts.RunTool(
            ["instrument", "--source", source, "--output", staging, "--builtin", BuiltInRuleSets.DeterministicBclId]);
        Assert.True(instrument.ExitCode == 0, $"Instrumentation failed:\n{instrument.StandardOutput}\n{instrument.StandardError}");

        string stagedApp = Path.Combine(staging, "SmokeApp.dll");
        AppRunResult staged = ProcessAppRunner.Run(stagedApp);
        Assert.NotEqual(0, staged.ExitCode);
        Assert.Empty(staged.Output);
        Assert.Contains(SimulationNotActiveException.DiagnosticMessage, staged.StandardError, StringComparison.Ordinal);
        Assert.False(File.Exists(ConsumerProject.SideEffectPath(stagedApp)));
    }

    [Fact]
    public void PackedPackagesCarryLicenseMetadataAndRedistributionNotices()
    {
        Assert.SkipUnless(SmokeEnabled, $"Set {SmokeEnvironmentVariable}=1 to run package smoke tests.");
        PackagedArtifacts artifacts = Artifacts.Value;

        AssertPackageMetadata(artifacts.PackagePath(SimulationPackageId), SimulationPackageId);
        AssertPackageMetadata(artifacts.PackagePath(InstrumentationPackageId), InstrumentationPackageId);
        AssertPackageMetadata(artifacts.PackagePath(BuildPackageId), BuildPackageId);
        AssertPackageMetadata(artifacts.PackagePath(AnalyzersPackageId), AnalyzersPackageId);
        AssertPackageMetadata(artifacts.PackagePath(TestingPackageId), TestingPackageId);
        AssertPackageMetadata(artifacts.PackagePath(TestingMtpPackageId), TestingMtpPackageId);
        AssertPackageMetadata(artifacts.PackagePath(ToolPackageId), ToolPackageId);
    }

    [Fact]
    public void PackedTestingMtpPackageUsesCurrentPackageIdForBuildProps()
    {
        Assert.SkipUnless(SmokeEnabled, $"Set {SmokeEnvironmentVariable}=1 to run package smoke tests.");
        using ZipArchive package = ZipFile.OpenRead(Artifacts.Value.PackagePath(TestingMtpPackageId));
        string[] entries = package.Entries.Select(static entry => entry.FullName).ToArray();

        Assert.Contains("buildTransitive/Microsoft.Orleans.Testing.Simulation.Testing.MTP.props", entries);
        Assert.Contains("buildMultiTargeting/Microsoft.Orleans.Testing.Simulation.Testing.MTP.props", entries);
        Assert.DoesNotContain(
            entries,
            static entry =>
                entry.StartsWith("buildTransitive/", StringComparison.Ordinal)
                && entry.EndsWith(".props", StringComparison.Ordinal)
                && !string.Equals(
                    entry,
                    "buildTransitive/Microsoft.Orleans.Testing.Simulation.Testing.MTP.props",
                    StringComparison.Ordinal));
        Assert.DoesNotContain(
            entries,
            static entry =>
                entry.StartsWith("buildMultiTargeting/", StringComparison.Ordinal)
                && entry.EndsWith(".props", StringComparison.Ordinal)
                && !string.Equals(
                    entry,
                    "buildMultiTargeting/Microsoft.Orleans.Testing.Simulation.Testing.MTP.props",
                    StringComparison.Ordinal));
    }

    [Fact]
    public void PackedBuildTaskAndToolPayloadsHaveOneSimulationAssemblyAndNoNuGetDependenciesOrCollisions()
    {
        Assert.SkipUnless(SmokeEnabled, $"Set {SmokeEnvironmentVariable}=1 to run package smoke tests.");
        PackagedArtifacts artifacts = Artifacts.Value;

        AssertPackageTopology(
            artifacts.PackagePath(BuildPackageId),
            [
                "build/Microsoft.Orleans.Testing.Simulation.Instrumentation.Build.props",
                "build/Microsoft.Orleans.Testing.Simulation.Instrumentation.Build.targets",
                "tasks/net11.0/Mono.Cecil.dll",
                "tasks/net11.0/Mono.Cecil.Mdb.dll",
                "tasks/net11.0/Mono.Cecil.Pdb.dll",
                "tasks/net11.0/Mono.Cecil.Rocks.dll",
                "tasks/net11.0/Orleans.Testing.Simulation.dll",
                "tasks/net11.0/Orleans.Testing.Simulation.Instrumentation.Build.dll",
                "tasks/net11.0/Orleans.Testing.Simulation.Instrumentation.dll",
            ]);
        AssertPackageTopology(
            artifacts.PackagePath(ToolPackageId),
            [
                "tools/net11.0/any/DotnetToolSettings.xml",
                "tools/net11.0/any/Mono.Cecil.dll",
                "tools/net11.0/any/Mono.Cecil.Mdb.dll",
                "tools/net11.0/any/Mono.Cecil.Pdb.dll",
                "tools/net11.0/any/Mono.Cecil.Rocks.dll",
                "tools/net11.0/any/Orleans.Testing.Simulation.dll",
                "tools/net11.0/any/Orleans.Testing.Simulation.Instrumentation.dll",
                "tools/net11.0/any/Orleans.Testing.Simulation.Instrumentation.xml",
                "tools/net11.0/any/Orleans.Testing.Simulation.Tool.deps.json",
                "tools/net11.0/any/Orleans.Testing.Simulation.Tool.dll",
                "tools/net11.0/any/Orleans.Testing.Simulation.Tool.runtimeconfig.json",
                "tools/net11.0/any/Orleans.Testing.Simulation.Tool.xml",
                "tools/net11.0/any/Orleans.Testing.Simulation.xml",
            ]);
    }

    private static void AssertPackageTopology(string packagePath, string[] expectedPayload)
    {
        using ZipArchive package = ZipFile.OpenRead(packagePath);
        string[] payload =
        [
            .. package.Entries
                .Select(static entry => entry.FullName)
                .Where(static path =>
                    path.StartsWith("build/", StringComparison.Ordinal) ||
                    path.StartsWith("tasks/", StringComparison.Ordinal) ||
                    path.StartsWith("tools/", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal),
        ];

        Assert.Equal(expectedPayload.Order(StringComparer.Ordinal), payload);
        Assert.Single(
            package.Entries,
            static entry => string.Equals(
                Path.GetFileName(entry.FullName),
                "Orleans.Testing.Simulation.dll",
                StringComparison.Ordinal));
        SimulationAssemblyCollisionGate.AssertNoCollision(packagePath, payload);

        ZipArchiveEntry nuspecEntry = Assert.Single(
            package.Entries,
            static entry => entry.FullName.EndsWith(".nuspec", StringComparison.Ordinal));
        using Stream nuspecStream = nuspecEntry.Open();
        XDocument nuspec = XDocument.Load(nuspecStream);
        XNamespace ns = nuspec.Root!.Name.Namespace;
        Assert.Empty(nuspec.Descendants(ns + "group"));
        Assert.Empty(nuspec.Descendants(ns + "dependency"));
    }

    private static void AssertAssemblyAsset(
        string packagePath,
        string entryPath,
        string expectedAssemblyName,
        string expectedTargetFramework)
    {
        using ZipArchive package = ZipFile.OpenRead(packagePath);
        ZipArchiveEntry entry = Assert.Single(package.Entries, entry => entry.FullName == entryPath);
        using var buffer = new MemoryStream();
        using (Stream stream = entry.Open())
        {
            stream.CopyTo(buffer);
        }

        buffer.Position = 0;
        using Mono.Cecil.AssemblyDefinition assembly = Mono.Cecil.AssemblyDefinition.ReadAssembly(buffer);
        Assert.Equal(expectedAssemblyName, assembly.Name.Name);
        Mono.Cecil.CustomAttribute targetFramework = Assert.Single(
            assembly.CustomAttributes,
            static attribute =>
                attribute.AttributeType.FullName == "System.Runtime.Versioning.TargetFrameworkAttribute");
        Assert.Equal(expectedTargetFramework, targetFramework.ConstructorArguments[0].Value);
    }

    private static void AssertDependencyGroup(
        string packagePath,
        string expectedTargetFramework,
        params string[] expectedPackageIds)
    {
        XElement metadata = ReadNuspecMetadata(packagePath, out XNamespace ns);
        XElement dependencies = Assert.IsType<XElement>(metadata.Element(ns + "dependencies"));
        Assert.Empty(dependencies.Elements(ns + "dependency"));
        XElement group = Assert.Single(dependencies.Elements(ns + "group"));
        Assert.Equal(expectedTargetFramework, group.Attribute("targetFramework")?.Value);
        Assert.Equal(
            expectedPackageIds.Order(StringComparer.OrdinalIgnoreCase),
            group.Elements(ns + "dependency")
                .Select(dependency => (string?)dependency.Attribute("id"))
                .Where(static id => id is not null)
                .Select(static id => id!)
                .Order(StringComparer.OrdinalIgnoreCase));
    }

    private static void AssertNoDependencyGroups(string packagePath)
    {
        XElement metadata = ReadNuspecMetadata(packagePath, out XNamespace ns);
        XElement? dependencies = metadata.Element(ns + "dependencies");
        if (dependencies is null)
        {
            return;
        }

        Assert.Empty(dependencies.Elements());
    }

    private static XElement ReadNuspecMetadata(string packagePath, out XNamespace ns)
    {
        using ZipArchive package = ZipFile.OpenRead(packagePath);
        ZipArchiveEntry nuspecEntry = Assert.Single(package.Entries, static entry =>
            entry.FullName.EndsWith(".nuspec", StringComparison.Ordinal));
        using Stream stream = nuspecEntry.Open();
        XDocument nuspec = XDocument.Load(stream);
        ns = nuspec.Root!.Name.Namespace;
        return new XElement(nuspec.Root.Element(ns + "metadata")!);
    }

    private static string[] PackageEntries(string packagePath)
    {
        using ZipArchive package = ZipFile.OpenRead(packagePath);
        return [.. package.Entries.Select(static entry => entry.FullName)];
    }

    private static string[] PackagePayloadEntries(IEnumerable<string> entries) =>
    [
        .. entries
            .Where(static entry =>
                entry != "[Content_Types].xml"
                && !entry.StartsWith("_rels/", StringComparison.Ordinal)
                && !entry.StartsWith("package/services/metadata/core-properties/", StringComparison.Ordinal)
                && !entry.EndsWith(".nuspec", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal),
    ];

    private static string ReadPackageEntry(string packagePath, string entryPath)
    {
        using ZipArchive package = ZipFile.OpenRead(packagePath);
        ZipArchiveEntry entry = Assert.Single(package.Entries, entry => entry.FullName == entryPath);
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    private static void AssertAssetsExcludePackages(string assetsPath, params string[] forbiddenPackageIds)
    {
        string[] packageIds = PackageBoundaryInspector.ReadPackageIds(assetsPath);

        foreach (string forbiddenPackageId in forbiddenPackageIds)
        {
            Assert.DoesNotContain(
                packageIds,
                packageId => packageId.StartsWith(forbiddenPackageId, StringComparison.OrdinalIgnoreCase));
        }
    }

    private static void AssertPackageMetadata(string packagePath, string expectedPackageId)
    {
        using ZipArchive package = ZipFile.OpenRead(packagePath);
        string[] entries = package.Entries.Select(entry => entry.FullName).ToArray();
        Assert.Contains("LICENSE", entries);
        Assert.Contains("README.md", entries);
        Assert.Contains("THIRD-PARTY-NOTICES.md", entries);

        ZipArchiveEntry nuspecEntry = Assert.Single(package.Entries, entry =>
            entry.FullName.EndsWith(".nuspec", StringComparison.Ordinal));
        using Stream nuspecStream = nuspecEntry.Open();
        XDocument nuspec = XDocument.Load(nuspecStream);
        XNamespace ns = nuspec.Root!.Name.Namespace;
        XElement metadata = nuspec.Root.Element(ns + "metadata")!;
        Assert.Equal(expectedPackageId, metadata.Element(ns + "id")?.Value);
        XElement license = metadata.Element(ns + "license")!;
        Assert.Equal("expression", license.Attribute("type")?.Value);
        Assert.Equal("MIT", license.Value);
        Assert.Equal(
            "https://github.com/dotnet/orleans",
            metadata.Element(ns + "projectUrl")?.Value);

        ZipArchiveEntry noticesEntry = package.GetEntry("THIRD-PARTY-NOTICES.md")!;
        using var reader = new StreamReader(noticesEntry.Open());
        string notices = reader.ReadToEnd();
        Assert.Contains("Copyright (c) 2008 - 2015 Jb Evain", notices, StringComparison.Ordinal);
        Assert.Contains("Copyright (c) 2008 - 2011 Novell, Inc.", notices, StringComparison.Ordinal);
        Assert.Contains("THE SOFTWARE IS PROVIDED \"AS IS\"", notices, StringComparison.Ordinal);
    }

    /// <summary>
    /// The packed local feed plus the installed tool, built once and shared across the smoke tests.
    /// </summary>
    private sealed class PackagedArtifacts
    {
        private PackagedArtifacts(string root, string feed, string packagesDirectory, string version, string toolPath)
        {
            Root = root;
            _feed = feed;
            _packagesDirectory = packagesDirectory;
            _version = version;
            _toolPath = toolPath;
        }

        public string Root { get; }

        private readonly string _feed;
        private readonly string _packagesDirectory;
        private readonly string _version;
        private readonly string _toolPath;

        public static PackagedArtifacts Build()
        {
            string repoRoot = FindRepositoryRoot();
            string root = TestArtifacts.CreateUnique("cwr-smoke");
            string feed = Path.Combine(root, "feed");
            string packages = Path.Combine(root, "packages");
            string toolPath = Path.Combine(root, "tool");
            Directory.CreateDirectory(feed);
            Directory.CreateDirectory(packages);

            // Every consumer and the tool install sit under this root, so they find the repository's global.json by
            // walking up: they build with the SDK band Orleans simulation builds with, and dotnet test selects the same runner.
            File.Copy(Path.Combine(repoRoot, "global.json"), Path.Combine(root, "global.json"));
            // A unique prerelease version per run guarantees the consumer resolves these freshly
            // packed artifacts rather than any identically named package in a shared cache.
            string version = "9.9.9-smoke." + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture);

            SeedPackageClosureFromCurrentTestHost(repoRoot, feed);
            Pack(
                repoRoot,
                "src/Simulation/Orleans.Testing.Simulation/Orleans.Testing.Simulation.csproj",
                version,
                feed,
                packages);
            Pack(
                repoRoot,
                "src/Simulation/Orleans.Testing.Simulation.Instrumentation/Orleans.Testing.Simulation.Instrumentation.csproj",
                version,
                feed,
                packages);
            Pack(
                repoRoot,
                "src/Simulation/Orleans.Testing.Simulation.Instrumentation.Build/Orleans.Testing.Simulation.Instrumentation.Build.csproj",
                version,
                feed,
                packages);
            Pack(
                repoRoot,
                "src/Simulation/Orleans.Testing.Simulation.Analyzers/Orleans.Testing.Simulation.Analyzers.csproj",
                version,
                feed,
                packages);
            Pack(
                repoRoot,
                "src/Simulation/Orleans.Testing.Simulation.Testing/Orleans.Testing.Simulation.Testing.csproj",
                version,
                feed,
                packages);
            Pack(
                repoRoot,
                "src/Simulation/Orleans.Testing.Simulation.Testing.MTP/Orleans.Testing.Simulation.Testing.MTP.csproj",
                version,
                feed,
                packages);
            Pack(
                repoRoot,
                "src/Simulation/Orleans.Testing.Simulation.Tool/Orleans.Testing.Simulation.Tool.csproj",
                version,
                feed,
                packages);
            PackFixturePackages(root, version, feed, packages);

            InstallTool(feed, packages, toolPath, version);

            return new PackagedArtifacts(root, feed, packages, version, toolPath);
        }

        public AppRunResult RunTool(IReadOnlyList<string> arguments)
        {
            string executable = Path.Combine(
                _toolPath,
                RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                    ? "dotnet-orleans-simulation.exe"
                    : "dotnet-orleans-simulation");
            return ProcessAppRunner.Execute(executable, arguments, _toolPath, timeout: TimeSpan.FromSeconds(120));
        }

        public string PackagePath(string packageId) =>
            Directory.GetFiles(_feed, "*.nupkg")
                .Single(path => Path.GetFileName(path).StartsWith(
                    packageId + "." + _version,
                    StringComparison.OrdinalIgnoreCase));

        public ConsumerProject ScaffoldConsumer(
            string name,
            bool instrumentationEnabled,
            bool signEntryAssembly = false,
            bool useBuiltInRules = false,
            bool instrumentedTestProject = false,
            bool testingPlatformProject = false,
            bool useConfigurationFile = false,
            InstrumentationMode mode = InstrumentationMode.Controlled)
        {
            string rootDir = Path.Combine(Root, name);
            string appDir = Path.Combine(rootDir, "app");
            string libDir = Path.Combine(rootDir, "lib");
            string subjectDir = Path.Combine(rootDir, "subject");
            string hostDir = Path.Combine(rootDir, "host");
            string kernelDir = Path.Combine(rootDir, "kernel");
            string diagnosticsDir = Path.Combine(rootDir, "diagnostics");
            Directory.CreateDirectory(appDir);
            Directory.CreateDirectory(libDir);
            Directory.CreateDirectory(subjectDir);
            if (testingPlatformProject)
            {
                CreateOrdinaryAssemblyProject(hostDir, "SmokeHost");
                CreateOrdinaryAssemblyProject(kernelDir, "SmokeKernel");
                CreateOrdinaryAssemblyProject(diagnosticsDir, "SmokeDiagnostics");
            }

            // A single nuget.config at the solution root is discovered by both projects via the
            // standard walk-up, wiring in the freshly packed local feed.
            File.WriteAllText(Path.Combine(rootDir, "nuget.config"), $"""
                <?xml version="1.0" encoding="utf-8"?>
                <configuration>
                  <packageSources>
                    <clear />
                    <add key="orleans-simulation-local" value="{_feed}" />
                  </packageSources>
                </configuration>
                """);

            // The controlled API and the shim live in one dependency assembly, in its own directory
            // so the executable project's default source glob does not also compile it. The redirect
            // rule points at the shim, so the runner copies this assembly verbatim while rewriting
            // the app.
            File.WriteAllText(Path.Combine(libDir, "SmokeApi.cs"), """
                namespace SmokeApi
                {
                    public static class RealClock
                    {
                        public static long UtcNowTicks() => 100L;
                    }

                    public static class Shim
                    {
                        public static long UtcNowTicks() => 999L;
                    }
                }
                """);
            File.WriteAllText(Path.Combine(libDir, "SmokeApi.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net11.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <AssemblyName>SmokeApi</AssemblyName>
                  </PropertyGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(subjectDir, "SmokeSubject.cs"), """
                using SmokeApi;

                namespace SmokeSubject;

                public static class Subject
                {
                    public static long Capture() => RealClock.UtcNowTicks();
                }
                """);
            File.WriteAllText(Path.Combine(subjectDir, "SmokeSubject.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net11.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <AssemblyName>SmokeSubject</AssemblyName>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="../lib/SmokeApi.csproj" />
                  </ItemGroup>
                </Project>
                """);

            string programSource = testingPlatformProject && useBuiltInRules
                ? """
                    using Xunit;

                    public sealed class InstrumentedTests
                    {
                        [Fact]
                        public void TestingPlatformStartsOutsideSimulation() => Assert.True(true);
                    }
                    """
                : testingPlatformProject
                ? """
                    using SmokeSubject;
                    using System.Reflection;
                    using Xunit;

                    public sealed class InstrumentedTests
                    {
                        [Fact]
                        public void UsesRewrittenDependency() =>
                            Assert.True(
                                typeof(Subject).Assembly
                                    .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>()
                                    .Any(attribute => attribute.Key == "Clockwork.RewriteSignature"));
                    }
                    """
                : useBuiltInRules
                ? """
                    _ = System.DateTime.UtcNow;
                    System.IO.File.WriteAllText("side-effect.txt", "unexpected");
                    System.Console.WriteLine("reached-end");
                    """
                : instrumentedTestProject
                    ? """
                        using SmokeSubject;
                        using System.Reflection;

                        bool instrumented = typeof(Subject).Assembly
                            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>()
                            .Any(attribute => attribute.Key == "Clockwork.RewriteSignature");
                        System.Console.WriteLine("instrumented=" + instrumented);
                        System.Console.WriteLine("argument=" + args.SingleOrDefault());
                        """
                : """
                    using SmokeApi;

                    System.Console.WriteLine("ticks=" + RealClock.UtcNowTicks());
                    """;
            File.WriteAllText(Path.Combine(appDir, "Program.cs"), programSource);

            string enabled = instrumentationEnabled ? "true" : "false";
            string builtIn = useBuiltInRules && !useConfigurationFile ? "true" : "false";
            string testProject = instrumentedTestProject ? "true" : "false";
            string testingPackage = testingPlatformProject
                ? """
                      <PackageReference Include="xunit.v3.mtp-v2" Version="3.2.1" />
                    """
                : string.Empty;
            string ordinaryIlProjectReferences = testingPlatformProject
                ? """
                      <ProjectReference Include="../host/SmokeHost.csproj" />
                      <ProjectReference Include="../kernel/SmokeKernel.csproj" />
                      <ProjectReference Include="../diagnostics/SmokeDiagnostics.csproj" />
                    """
                : string.Empty;
            string ordinaryIlExclusions = testingPlatformProject
                ? """
                      <OrleansSimulationExclude Include="SmokeHost.dll" />
                      <OrleansSimulationExclude Include="SmokeKernel.dll" />
                      <OrleansSimulationExclude Include="SmokeDiagnostics.dll" />
                    """
                : string.Empty;
            string runtimeReference = useBuiltInRules
                ? $"""
                      <Reference Include="Orleans.Testing.Simulation">
                        <HintPath>{typeof(SimulationNotActiveException).Assembly.Location}</HintPath>
                        <Private>true</Private>
                      </Reference>
                    """
                : string.Empty;
            string ruleSetItem = useBuiltInRules
                ? testingPlatformProject && !useConfigurationFile
                    ? """
                          <OrleansSimulationBuiltInRuleSet Include="clockwork.tasks.controlled" />
                        """
                    : string.Empty
                : useConfigurationFile
                    ? string.Empty
                    : """
                      <OrleansSimulationRuleSet Include="$(MSBuildProjectDirectory)/orleans-simulation.rules.json" />
                    """;
            string signingProperties = string.Empty;
            if (signEntryAssembly)
            {
                File.WriteAllBytes(Path.Combine(appDir, "test.snk"), StrongNameKeys.CreatePrivateKeyBlob());
                signingProperties = """
                    <SignAssembly>true</SignAssembly>
                    <AssemblyOriginatorKeyFile>test.snk</AssemblyOriginatorKeyFile>
                """;
            }

            File.WriteAllText(Path.Combine(appDir, "SmokeApp.csproj"), $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net11.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <AssemblyName>SmokeApp</AssemblyName>
                    <OrleansSimulationInstrumentationEnabled>{enabled}</OrleansSimulationInstrumentationEnabled>
                    <OrleansSimulationInstrumentedTestProject>{testProject}</OrleansSimulationInstrumentedTestProject>
                    <OrleansSimulationInstrumentationMode>{mode}</OrleansSimulationInstrumentationMode>
                    <OrleansSimulationUseBuiltInRules>{builtIn}</OrleansSimulationUseBuiltInRules>
                {signingProperties}
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="{BuildPackageId}" Version="{_version}" />
                {testingPackage}
                {runtimeReference}
                  </ItemGroup>
                  <ItemGroup>
                    <ProjectReference Include="../subject/SmokeSubject.csproj" />
                {ordinaryIlProjectReferences}
                  </ItemGroup>
                  <ItemGroup>
                {ruleSetItem}
                {ordinaryIlExclusions}
                  </ItemGroup>
                </Project>
                """);

            var ruleSet = new RewriteRuleSet(
                "smoke.rules",
                "1.0",
                [
                    RewriteRule.RedirectCall(
                        "redirect-utcnowticks",
                        MemberSignature.Method("SmokeApi.RealClock", "UtcNowTicks"),
                        RewriteReplacement.Method("SmokeApi", "SmokeApi.Shim", "UtcNowTicks")),
                ]);
            File.WriteAllText(Path.Combine(appDir, "orleans-simulation.rules.json"), RuleSetJson.Write(ruleSet));
            if (useConfigurationFile)
            {
                string configuration = useBuiltInRules
                    ? """
                        {
                          "schemaVersion": 2,
                          "builtInRuleSets": [
                            "clockwork.bcl.deterministic",
                            "clockwork.tasks.controlled"
                          ]
                        }
                        """
                    : """
                        {
                          "schemaVersion": 2,
                          "ruleSets": ["orleans-simulation.rules.json"]
                        }
                        """;
                File.WriteAllText(Path.Combine(appDir, "orleans-simulation.config.json"), configuration);
            }

            return new ConsumerProject(appDir, _packagesDirectory, mode);
        }

        private static void CreateOrdinaryAssemblyProject(string directory, string assemblyName)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(
                Path.Combine(directory, assemblyName + ".cs"),
                $$"""
                namespace {{assemblyName}};

                public static class Marker
                {
                    public static string Identity => "{{assemblyName}}";
                }
                """);
            File.WriteAllText(
                Path.Combine(directory, assemblyName + ".csproj"),
                $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net11.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <AssemblyName>{{assemblyName}}</AssemblyName>
                  </PropertyGroup>
                </Project>
                """);
        }

        public ConsumerProject ScaffoldPackageOnlyMtpConsumer(string name)
        {
            string rootDir = Path.Combine(Root, name);
            string appDir = Path.Combine(rootDir, "app");
            Directory.CreateDirectory(appDir);
            WriteNuGetConfig(rootDir);

            File.WriteAllText(Path.Combine(appDir, "Program.cs"), """
                using Orleans.Testing.Simulation;
                using Orleans.Testing.Simulation.Testing;
                using Orleans.Durable.Storage.Diagnostics;
                using Clockwork.Fixtures.Production;
                using Xunit;

                public sealed class PackagedSimulationTests
                {
                    [Fact]
                    public void PackageDependencyIsInstrumented()
                    {
                        Assert.NotEmpty(typeof(SimulationCluster).Assembly.Location);
                        Assert.NotEmpty(typeof(ReplayTestFixture).Assembly.Location);
                        Assert.Equal(999L, ProductionSubject.Capture());
                    }

                    [Fact]
                    public void DurableStorageDiagnosticsRemainsOrdinaryIl()
                    {
                        Assert.True(OrdinaryProbe.UtcNowTicks() > 0);
                        _ = typeof(PackagedSimulationTests).Assembly;
                        _ = typeof(ProductionSubject).Assembly;
                        _ = typeof(SimulationCluster).Assembly;
                        _ = typeof(ReplayTestFixture).Assembly;
                        string[] loaded = AppDomain.CurrentDomain.GetAssemblies()
                            .Select(static assembly => assembly.GetName().Name)
                            .Where(static name => name is not null)
                            .Select(static name => name!)
                            .Order(StringComparer.Ordinal)
                            .ToArray();
                        File.WriteAllLines(
                            Path.Combine(AppContext.BaseDirectory, "orleans-simulation-runtime-loads.txt"),
                            loaded);
                    }
                }
                """);

            File.WriteAllText(Path.Combine(appDir, "SmokeApp.csproj"), $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net11.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <AssemblyName>SmokeApp</AssemblyName>
                    <OrleansSimulationInstrumentedTestProject>true</OrleansSimulationInstrumentedTestProject>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="xunit.v3.mtp-v2" Version="3.2.1" />
                    <PackageReference Include="{SimulationPackageId}" Version="{_version}" />
                    <PackageReference Include="{TestingMtpPackageId}" Version="{_version}" />
                    <PackageReference Include="{BuildPackageId}" Version="{_version}" />
                    <PackageReference Include="{AnalyzersPackageId}" Version="{_version}" PrivateAssets="all" />
                    <PackageReference Include="{ProductionFixturePackageId}" Version="{_version}" />
                    <PackageReference Include="Orleans.Durable.Storage.Diagnostics" Version="{_version}" />
                  </ItemGroup>
                  <ItemGroup>
                    <OrleansSimulationRuleSet Include="$(MSBuildProjectDirectory)/orleans-simulation.rules.json" />
                    <OrleansSimulationBuiltInRuleSet Include="clockwork.bcl.deterministic" />
                    <OrleansSimulationExclude Include="Orleans.Durable.Storage.Diagnostics.dll" />
                  </ItemGroup>
                  <Target Name="CapturePackageBoundaryReferences" AfterTargets="ResolveReferences">
                    <WriteLinesToFile
                        File="$(IntermediateOutputPath)orleans-simulation-evaluated-imports.txt"
                        Lines="@(PackageReference->'package|%(Identity)')"
                        Overwrite="true" />
                    <WriteLinesToFile
                        File="$(IntermediateOutputPath)orleans-simulation-evaluated-imports.txt"
                        Lines="@(Analyzer->'analyzer|%(FullPath)')"
                        Overwrite="false" />
                    <WriteLinesToFile
                        File="$(IntermediateOutputPath)orleans-simulation-evaluated-imports.txt"
                        Lines="buildTask|$(OrleansSimulationInstrumentationTaskAssembly)"
                        Overwrite="false" />
                    <WriteLinesToFile
                        File="$(IntermediateOutputPath)orleans-simulation-evaluated-imports.txt"
                        Lines="@(TestingPlatformBuilderHook->'mtpHook|%(Identity)|%(TypeFullName)|%(DisplayName)')"
                        Overwrite="false" />
                    <WriteLinesToFile
                        File="$(IntermediateOutputPath)orleans-simulation-evaluated-references.txt"
                        Lines="@(ReferencePath->'%(FullPath)')"
                        Overwrite="true" />
                    <WriteLinesToFile
                        File="$(IntermediateOutputPath)orleans-simulation-copy-local-references.txt"
                        Lines="@(ReferenceCopyLocalPaths->'%(FullPath)')"
                        Overwrite="true" />
                  </Target>
                </Project>
                """);

            var ruleSet = new RewriteRuleSet(
                "package-smoke.rules",
                "1.0",
                [
                    RewriteRule.RedirectCall(
                        "redirect-package-probe",
                        MemberSignature.Method("Orleans.Testing.Simulation.PackageApi.RealClock", "UtcNowTicks"),
                        RewriteReplacement.Method(
                            "PackageApi",
                            "Orleans.Testing.Simulation.PackageApi.Shim",
                            "UtcNowTicks")),
                ]);
            File.WriteAllText(Path.Combine(appDir, "orleans-simulation.rules.json"), RuleSetJson.Write(ruleSet));
            return new ConsumerProject(appDir, _packagesDirectory, InstrumentationMode.Controlled);
        }

        public ConsumerProject ScaffoldOrdinarySimulationConsumer(string name)
        {
            string rootDir = Path.Combine(Root, name);
            string appDir = Path.Combine(rootDir, "app");
            Directory.CreateDirectory(appDir);
            WriteNuGetConfig(rootDir);

            File.WriteAllText(Path.Combine(appDir, "Program.cs"), """
                using Orleans.Testing.Simulation;

                Console.WriteLine(typeof(SimulationCluster).Assembly.GetName().Name);
                """);
            File.WriteAllText(Path.Combine(appDir, "SmokeApp.csproj"), $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net11.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <AssemblyName>SmokeApp</AssemblyName>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="{SimulationPackageId}" Version="{_version}" />
                  </ItemGroup>
                </Project>
                """);

            return new ConsumerProject(appDir, _packagesDirectory, InstrumentationMode.Controlled);
        }

        public ConsumerProject ScaffoldOrdinaryTestingSupportConsumer(string name)
        {
            string rootDir = Path.Combine(Root, name);
            string appDir = Path.Combine(rootDir, "app");
            Directory.CreateDirectory(appDir);
            WriteNuGetConfig(rootDir);

            File.WriteAllText(Path.Combine(appDir, "SupportConsumer.cs"), """
                using Microsoft.Extensions.Logging;
                using Orleans.Testing.Simulation.Testing;

                namespace OrdinaryTestingSupportConsumer;

                public static class SupportConsumer
                {
                    public static LogEntry Capture()
                    {
                        using var provider = new InMemoryLoggerProvider();
                        ILogger logger = provider.CreateLogger("ordinary-library");
                        logger.LogInformation("captured");
                        return provider.Buffer.AllEntries.Single();
                    }
                }
                """);
            File.WriteAllText(Path.Combine(appDir, "SmokeApp.csproj"), $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net11.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <AssemblyName>SmokeApp</AssemblyName>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="{TestingPackageId}" Version="{_version}" />
                  </ItemGroup>
                </Project>
                """);

            return new ConsumerProject(appDir, _packagesDirectory, InstrumentationMode.Controlled);
        }

        public ProductionBoundaryFixture ScaffoldProductionBoundaryFixture(string name)
        {
            string rootDir = Path.Combine(Root, name);
            string appDir = Path.Combine(rootDir, "app");
            Directory.CreateDirectory(appDir);
            WriteNuGetConfig(rootDir);

            string productionProject = Path.Combine(
                Root,
                "fixture-packages",
                "production",
                "Clockwork.Fixtures.Production.csproj");
            string relativeProductionProject = Path.GetRelativePath(appDir, productionProject).Replace('\\', '/');
            File.WriteAllText(Path.Combine(appDir, "Program.cs"), """
                using Clockwork.Fixtures.Production;

                Console.WriteLine(ProductionSubject.Capture());
                """);
            File.WriteAllText(Path.Combine(appDir, "Production.App.csproj"), $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net11.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <AssemblyName>Production.App</AssemblyName>
                    <IsAotCompatible>true</IsAotCompatible>
                    <EnableTrimAnalyzer>true</EnableTrimAnalyzer>
                    <EnableAotAnalyzer>true</EnableAotAnalyzer>
                    <EnableSingleFileAnalyzer>true</EnableSingleFileAnalyzer>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="{relativeProductionProject}" />
                  </ItemGroup>
                </Project>
                """);

            string boundaryTargetsPath = Path.Combine(appDir, "DependencyBoundary.targets");
            File.WriteAllText(boundaryTargetsPath, """
                <Project>
                  <Target Name="ReportSimulationDependencyBoundary"
                          Returns="@(_SimulationDependencyBoundary)">
                    <MSBuild Projects="@(ProjectReference)"
                             Targets="ReportSimulationDependencyBoundary"
                             BuildInParallel="false"
                             Condition="'@(ProjectReference)' != ''">
                      <Output TaskParameter="TargetOutputs"
                              ItemName="_ReferencedSimulationDependencyBoundary" />
                    </MSBuild>
                    <ItemGroup>
                      <_SimulationDependencyBoundary Include="@(_ReferencedSimulationDependencyBoundary)" />
                      <_SimulationDependencyBoundary Include="project|$(MSBuildProjectFullPath)|%(ProjectReference.FullPath)" />
                      <_SimulationDependencyBoundary Include="package|$(MSBuildProjectFullPath)|%(PackageReference.Identity)" />
                      <_SimulationDependencyBoundary Include="qualification|$(MSBuildProjectFullPath)|$(IsAotCompatible)|$(EnableTrimAnalyzer)|$(EnableAotAnalyzer)|$(EnableSingleFileAnalyzer)" />
                    </ItemGroup>
                    <Message Importance="high"
                             Text="SIMULATION_DEPENDENCY_BOUNDARY|%(_SimulationDependencyBoundary.Identity)" />
                  </Target>
                </Project>
                """);

            return new ProductionBoundaryFixture(
                appDir,
                productionProject,
                boundaryTargetsPath,
                _packagesDirectory);
        }

        private static void Pack(string repoRoot, string relativeProject, string version, string feed, string packages)
            => PackProject(Path.Combine(repoRoot, relativeProject), repoRoot, version, feed, packages);

        private static void PackProject(
            string projectPath,
            string workingDirectory,
            string version,
            string feed,
            string packages)
        {
            AppRunResult result = ProcessAppRunner.Execute(
                "dotnet",
                ["pack", projectPath, "-c", "Release", $"-p:Version={version}", "-o", feed, "--nologo", "--disable-build-servers"],
                workingDirectory,
                NuGetEnvironment(packages),
                TimeSpan.FromSeconds(300));

            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Packing '{projectPath}' failed:\n{result.StandardOutput}\n{result.StandardError}");
            }
        }

        private static void PackFixturePackages(string root, string version, string feed, string packages)
        {
            string fixtures = Path.Combine(root, "fixture-packages");
            string api = Path.Combine(fixtures, "api");
            string probe = Path.Combine(fixtures, "probe");
            string diagnostics = Path.Combine(fixtures, "diagnostics");
            string production = Path.Combine(fixtures, "production");
            Directory.CreateDirectory(api);
            Directory.CreateDirectory(probe);
            Directory.CreateDirectory(diagnostics);
            Directory.CreateDirectory(production);

            File.WriteAllText(Path.Combine(fixtures, "nuget.config"), $"""
                <?xml version="1.0" encoding="utf-8"?>
                <configuration>
                  <packageSources>
                    <clear />
                    <add key="orleans-simulation-local" value="{feed}" />
                  </packageSources>
                </configuration>
                """);

            File.WriteAllText(Path.Combine(api, "Orleans.Testing.Simulation.PackageApi.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net11.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <AssemblyName>PackageApi</AssemblyName>
                    <PackageId>Orleans.Testing.Simulation.PackageApi</PackageId>
                  </PropertyGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(api, "Api.cs"), """
                namespace Orleans.Testing.Simulation.PackageApi;

                public static class RealClock
                {
                    public static long UtcNowTicks() => 100L;
                }

                public static class Shim
                {
                    public static long UtcNowTicks() => 999L;
                }
                """);

            File.WriteAllText(Path.Combine(probe, "Orleans.Testing.Simulation.PackageProbe.csproj"), $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net11.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <AssemblyName>PackageProbe</AssemblyName>
                    <PackageId>Orleans.Testing.Simulation.PackageProbe</PackageId>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="Orleans.Testing.Simulation.PackageApi" Version="{version}" />
                  </ItemGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(probe, "Probe.cs"), """
                using Orleans.Testing.Simulation.PackageApi;

                namespace Orleans.Testing.Simulation.PackageProbe;

                public static class Subject
                {
                    public static long Capture() => RealClock.UtcNowTicks();
                }
                """);

            File.WriteAllText(Path.Combine(diagnostics, "Orleans.Durable.Storage.Diagnostics.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net11.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <AssemblyName>Orleans.Durable.Storage.Diagnostics</AssemblyName>
                    <PackageId>Orleans.Durable.Storage.Diagnostics</PackageId>
                  </PropertyGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(diagnostics, "OrdinaryProbe.cs"), """
                namespace Orleans.Durable.Storage.Diagnostics;

                public static class OrdinaryProbe
                {
                    public static long UtcNowTicks() => DateTime.UtcNow.Ticks;
                }
                """);

            File.WriteAllText(Path.Combine(production, "Clockwork.Fixtures.Production.csproj"), $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net11.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <AssemblyName>Clockwork.Fixtures.Production</AssemblyName>
                    <PackageId>{ProductionFixturePackageId}</PackageId>
                    <IsAotCompatible>true</IsAotCompatible>
                    <EnableTrimAnalyzer>true</EnableTrimAnalyzer>
                    <EnableAotAnalyzer>true</EnableAotAnalyzer>
                    <EnableSingleFileAnalyzer>true</EnableSingleFileAnalyzer>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="Orleans.Testing.Simulation.PackageApi" Version="{version}" />
                    <PackageReference Include="Orleans.Durable.Storage.Diagnostics" Version="{version}" />
                  </ItemGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(production, "ProductionSubject.cs"), """
                using Orleans.Durable.Storage.Diagnostics;
                using Orleans.Testing.Simulation.PackageApi;

                namespace Clockwork.Fixtures.Production;

                public static class ProductionSubject
                {
                    public static long Capture() => RealClock.UtcNowTicks();

                    public static long CaptureDiagnosticTimestamp() => OrdinaryProbe.UtcNowTicks();
                }
                """);

            PackProject(
                Path.Combine(api, "Orleans.Testing.Simulation.PackageApi.csproj"),
                api,
                version,
                feed,
                packages);
            PackProject(
                Path.Combine(probe, "Orleans.Testing.Simulation.PackageProbe.csproj"),
                probe,
                version,
                feed,
                packages);
            PackProject(
                Path.Combine(diagnostics, "Orleans.Durable.Storage.Diagnostics.csproj"),
                diagnostics,
                version,
                feed,
                packages);
            PackProject(
                Path.Combine(production, "Clockwork.Fixtures.Production.csproj"),
                production,
                version,
                feed,
                packages);
        }

        private static void SeedPackageClosureFromCurrentTestHost(string repoRoot, string feed)
        {
            string assetsPath = Path.Combine(
                repoRoot,
                "test",
                "Simulation",
                "Clockwork.Instrumentation.Tests",
                "obj",
                "project.assets.json");
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(assetsPath));
            string[] packageFolders =
            [
                .. document.RootElement.GetProperty("packageFolders")
                .EnumerateObject()
                .Select(static property => property.Name),
            ];

            foreach (JsonProperty library in document.RootElement.GetProperty("libraries").EnumerateObject())
            {
                if (!string.Equals(
                    library.Value.GetProperty("type").GetString(),
                    "package",
                    StringComparison.Ordinal))
                {
                    continue;
                }

                int separator = library.Name.LastIndexOf('/');
                string packageId = library.Name[..separator];
                string version = library.Name[(separator + 1)..];
                string packagePath = packageFolders
                    .Select(folder => Path.Combine(folder, packageId.ToLowerInvariant(), version))
                    .Where(Directory.Exists)
                    .SelectMany(static directory =>
                        Directory.GetFiles(directory, "*.nupkg", SearchOption.TopDirectoryOnly))
                    .First();
                File.Copy(packagePath, Path.Combine(feed, Path.GetFileName(packagePath)), overwrite: false);
            }
        }

        private void WriteNuGetConfig(string rootDir) =>
            File.WriteAllText(Path.Combine(rootDir, "nuget.config"), $"""
                <?xml version="1.0" encoding="utf-8"?>
                <configuration>
                  <packageSources>
                    <clear />
                    <add key="orleans-simulation-local" value="{_feed}" />
                  </packageSources>
                </configuration>
                """);

        private static void InstallTool(string feed, string packages, string toolPath, string version)
        {
            string configPath = Path.Combine(feed, "nuget.config");
            File.WriteAllText(configPath, $"""
                <?xml version="1.0" encoding="utf-8"?>
                <configuration>
                  <packageSources>
                    <clear />
                    <add key="orleans-simulation-local" value="{feed}" />
                  </packageSources>
                </configuration>
                """);
            AppRunResult result = ProcessAppRunner.Execute(
                "dotnet",
                [
                    "tool",
                    "install",
                    ToolPackageId,
                    "--version",
                    version,
                    "--tool-path",
                    toolPath,
                    "--configfile",
                    configPath,
                ],
                feed,
                NuGetEnvironment(packages),
                TimeSpan.FromSeconds(300));

            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Installing the Orleans simulation tool failed:\n{result.StandardOutput}\n{result.StandardError}");
            }
        }

        private static Dictionary<string, string> NuGetEnvironment(string packagesDirectory) => new()
        {
            ["NUGET_PACKAGES"] = packagesDirectory,
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
            ["DOTNET_NOLOGO"] = "1",
        };

        private static string FindRepositoryRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Orleans.slnx")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            throw new InvalidOperationException("Could not locate the repository root (Orleans.slnx).");
        }
    }

    private sealed class ProductionBoundaryFixture
    {
        private readonly string _productionProjectPath;
        private readonly string _boundaryTargetsPath;
        private readonly string _packagesDirectory;

        public ProductionBoundaryFixture(
            string appDirectory,
            string productionProjectPath,
            string boundaryTargetsPath,
            string packagesDirectory)
        {
            AppDirectory = appDirectory;
            _productionProjectPath = productionProjectPath;
            _boundaryTargetsPath = boundaryTargetsPath;
            _packagesDirectory = packagesDirectory;
        }

        public string AppDirectory { get; }

        public string OutputDirectory => Path.Combine(AppDirectory, "bin", "Release", "net11.0");

        public string AppAssetsPath => Path.Combine(AppDirectory, "obj", "project.assets.json");

        public string LibraryAssetsPath =>
            Path.Combine(Path.GetDirectoryName(_productionProjectPath)!, "obj", "project.assets.json");

        public AppRunResult Build() => ProcessAppRunner.Execute(
            "dotnet",
            ["build", "Production.App.csproj", "-c", "Release", "--nologo", "--disable-build-servers"],
            AppDirectory,
            Environment(),
            TimeSpan.FromSeconds(300));

        public string[] EvaluateDependencyBoundary()
        {
            AppRunResult result = ProcessAppRunner.Execute(
                "dotnet",
                [
                    "msbuild",
                    "Production.App.csproj",
                    "-t:ReportSimulationDependencyBoundary",
                    $"-p:CustomAfterMicrosoftCommonTargets={_boundaryTargetsPath}",
                    "-p:Configuration=Release",
                    "--nologo",
                    "--disable-build-servers",
                ],
                AppDirectory,
                Environment(),
                TimeSpan.FromSeconds(120));
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Evaluating the production dependency boundary failed:\n"
                    + $"{result.StandardOutput}\n{result.StandardError}");
            }

            const string prefix = "SIMULATION_DEPENDENCY_BOUNDARY|";
            return
            [
                .. result.StandardOutput
                    .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.IndexOf(prefix, StringComparison.Ordinal) is int index && index >= 0
                        ? line[(index + prefix.Length)..]
                        : null)
                    .Where(static line => line is not null)
                    .Select(static line => line!)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal),
            ];
        }

        private Dictionary<string, string> Environment() => new()
        {
            ["NUGET_PACKAGES"] = _packagesDirectory,
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
            ["DOTNET_NOLOGO"] = "1",
        };
    }

    /// <summary>A scaffolded consumer project that references the packed build package.</summary>
    private sealed class ConsumerProject
    {
        private const string OutputRelative = "bin/Release/net11.0";
        private const string StagingRelative = "obj/Release/net11.0/orleans-simulation/instrumented";
        private const string ManifestRelative =
            "obj/Release/net11.0/orleans-simulation/orleans-simulation.manifest.json";
        private readonly string _packagesDirectory;
        private readonly InstrumentationMode _mode;

        public ConsumerProject(
            string projectDirectory,
            string packagesDirectory,
            InstrumentationMode mode)
        {
            ProjectDirectory = projectDirectory;
            _packagesDirectory = packagesDirectory;
            _mode = mode;
        }

        public string ProjectDirectory { get; }

        public string ProjectPath => Path.Combine(ProjectDirectory, "SmokeApp.csproj");

        public string OutputDirectory => Path.Combine(ProjectDirectory, OutputRelative);

        public string OutputAppPath => Path.Combine(ProjectDirectory, OutputRelative, "SmokeApp.dll");

        public string UninstrumentedAppPath =>
            Path.Combine(ProjectDirectory, "obj/Release/net11.0/orleans-simulation/source", "SmokeApp.dll");

        public string StagingDirectory => Path.Combine(ProjectDirectory, StagingRelative);

        public string StagedAppPath => Path.Combine(StagingDirectory, "SmokeApp.dll");

        public string StagedSubjectPath => Path.Combine(StagingDirectory, "SmokeSubject.dll");

        public string StagedHostPath => Path.Combine(StagingDirectory, "SmokeHost.dll");

        public string StagedKernelPath => Path.Combine(StagingDirectory, "SmokeKernel.dll");

        public string StagedDiagnosticsPath => Path.Combine(StagingDirectory, "SmokeDiagnostics.dll");

        public string UninstrumentedProductionLibraryPath =>
            Path.Combine(
                ProjectDirectory,
                "obj/Release/net11.0/orleans-simulation/source",
                "Clockwork.Fixtures.Production.dll");

        public string OutputProductionLibraryPath =>
            Path.Combine(OutputDirectory, "Clockwork.Fixtures.Production.dll");

        public string OutputDiagnosticsPath =>
            Path.Combine(OutputDirectory, "Orleans.Durable.Storage.Diagnostics.dll");

        public string AssetsPath => Path.Combine(ProjectDirectory, "obj", "project.assets.json");

        public string EvaluatedReferencesPath =>
            Path.Combine(ProjectDirectory, "obj/Release/net11.0/orleans-simulation-evaluated-references.txt");

        public string CopyLocalReferencesPath =>
            Path.Combine(ProjectDirectory, "obj/Release/net11.0/orleans-simulation-copy-local-references.txt");

        public string EvaluatedImportsPath =>
            Path.Combine(ProjectDirectory, "obj/Release/net11.0/orleans-simulation-evaluated-imports.txt");

        public string RuntimeLoadsPath => Path.Combine(OutputDirectory, "orleans-simulation-runtime-loads.txt");

        public string ManifestPath => Path.Combine(ProjectDirectory, ManifestRelative);

        public string SuccessPath => Path.Combine(
            ProjectDirectory,
            $"obj/Release/net11.0/orleans-simulation/orleans-simulation.{_mode}.success");

        public string ModeStampPath => Path.Combine(
            ProjectDirectory,
            "obj/Release/net11.0/orleans-simulation/orleans-simulation.mode");

        public static string SideEffectPath(string appPath) =>
            Path.Combine(Path.GetDirectoryName(appPath)!, "side-effect.txt");

        public void ReplaceShimTicks(long ticks)
        {
            string sourcePath = Path.Combine(ProjectDirectory, "..", "lib", "SmokeApi.cs");
            string source = File.ReadAllText(sourcePath);
            File.WriteAllText(sourcePath, source.Replace("999L", ticks + "L", StringComparison.Ordinal));
        }

        public void WriteConfiguration(string contents) =>
            File.WriteAllText(
                Path.Combine(ProjectDirectory, "orleans-simulation.config.json"),
                contents);

        public void AddPassingTest()
        {
            string sourcePath = Path.Combine(ProjectDirectory, "Program.cs");
            string source = File.ReadAllText(sourcePath);
            const string existing =
                "public void UsesRewrittenDependency() =>";
            int methodStart = source.IndexOf(existing, StringComparison.Ordinal);
            int classEnd = source.LastIndexOf('}');
            if (methodStart < 0 || classEnd < 0)
            {
                throw new InvalidOperationException("Could not locate generated test source.");
            }

            File.WriteAllText(
                sourcePath,
                source.Insert(
                    classEnd,
                    Environment.NewLine +
                    "    [Fact]" + Environment.NewLine +
                    "    public void RebuiltSourceIsUsed() => Assert.True(true);" + Environment.NewLine));
        }

        public void SetInstrumentationMode(InstrumentationMode mode)
        {
            string projectPath = Path.Combine(ProjectDirectory, "SmokeApp.csproj");
            string project = File.ReadAllText(projectPath);
            foreach (InstrumentationMode existing in Enum.GetValues<InstrumentationMode>())
            {
                project = project.Replace(
                    $"<OrleansSimulationInstrumentationMode>{existing}</OrleansSimulationInstrumentationMode>",
                    $"<OrleansSimulationInstrumentationMode>{mode}</OrleansSimulationInstrumentationMode>",
                    StringComparison.Ordinal);
            }

            File.WriteAllText(projectPath, project);
        }

        public AppRunResult Build(params string[] arguments) => ProcessAppRunner.Execute(
            "dotnet",
            ["build", "SmokeApp.csproj", "-c", "Release", "--nologo", .. arguments],
            ProjectDirectory,
            new Dictionary<string, string>
            {
                ["NUGET_PACKAGES"] = _packagesDirectory,
                ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
                ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
                ["DOTNET_NOLOGO"] = "1",
            },
            TimeSpan.FromSeconds(300));

        public AppRunResult EvaluateProject(params string[] arguments) => ProcessAppRunner.Execute(
            "dotnet",
            ["msbuild", "SmokeApp.csproj", "--nologo", .. arguments],
            ProjectDirectory,
            new Dictionary<string, string>
            {
                ["NUGET_PACKAGES"] = _packagesDirectory,
                ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
                ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
                ["DOTNET_NOLOGO"] = "1",
            },
            TimeSpan.FromSeconds(120));

        public AppRunResult Run(params string[] arguments)
        {
            var command = new List<string>
            {
                "run",
                "--project",
                "SmokeApp.csproj",
                "--configuration",
                "Release",
                "--no-build",
                "--",
            };
            command.AddRange(arguments);
            return ProcessAppRunner.Execute(
                "dotnet",
                command,
                ProjectDirectory,
                new Dictionary<string, string>
                {
                    ["NUGET_PACKAGES"] = _packagesDirectory,
                    ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
                    ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
                    ["DOTNET_NOLOGO"] = "1",
                },
                TimeSpan.FromSeconds(300));
        }

        public AppRunResult TestNoBuild() => ProcessAppRunner.Execute(
            "dotnet",
            [
                "test",
                "--project",
                "SmokeApp.csproj",
                "--configuration",
                "Release",
                "--no-build",
                "--no-ansi",
                "--no-progress",
            ],
            ProjectDirectory,
            new Dictionary<string, string>
            {
                ["NUGET_PACKAGES"] = _packagesDirectory,
                ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
                ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
                ["DOTNET_NOLOGO"] = "1",
            },
            TimeSpan.FromSeconds(300));
    }
}
