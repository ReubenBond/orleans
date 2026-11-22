using System.Text.Json;
using System.Xml.Linq;
using Orleans.Testing.Simulation.Instrumentation.Tests.Infrastructure;

namespace Orleans.Testing.Simulation.Instrumentation.Tests.Packaging;

public sealed class SourceBuildPackageMapTests
{
    private static readonly SourceBuildPackage[] ExpectedPackages =
    [
        new(
            "Microsoft.Orleans.Testing.Simulation",
            "src/Simulation/Orleans.Testing.Simulation/Orleans.Testing.Simulation.csproj",
            "Orleans.Testing.Simulation",
            "net11.0",
            "SimulationKernel"),
        new(
            "Microsoft.Orleans.Testing.Simulation.Instrumentation",
            "src/Simulation/Orleans.Testing.Simulation.Instrumentation/Orleans.Testing.Simulation.Instrumentation.csproj",
            "Orleans.Testing.Simulation.Instrumentation",
            "net11.0",
            "InstrumentationRuntime"),
        new(
            "Microsoft.Orleans.Testing.Simulation.Instrumentation.Build",
            "src/Simulation/Orleans.Testing.Simulation.Instrumentation.Build/Orleans.Testing.Simulation.Instrumentation.Build.csproj",
            "Orleans.Testing.Simulation.Instrumentation.Build",
            "net11.0",
            "InstrumentationBuild"),
        new(
            "Microsoft.Orleans.Testing.Simulation.Analyzers",
            "src/Simulation/Orleans.Testing.Simulation.Analyzers/Orleans.Testing.Simulation.Analyzers.csproj",
            "Orleans.Testing.Simulation.Analyzers",
            "netstandard2.0",
            "Analyzer"),
        new(
            "Microsoft.Orleans.Testing.Simulation.Testing",
            "src/Simulation/Orleans.Testing.Simulation.Testing/Orleans.Testing.Simulation.Testing.csproj",
            "Orleans.Testing.Simulation.Testing",
            "net11.0",
            "TestingSupport"),
        new(
            "Microsoft.Orleans.Testing.Simulation.Testing.MTP",
            "src/Simulation/Orleans.Testing.Simulation.Testing.MTP/Orleans.Testing.Simulation.Testing.MTP.csproj",
            "Orleans.Testing.Simulation.Testing.MTP",
            "net11.0",
            "MicrosoftTestingPlatformIntegration"),
        new(
            "Microsoft.Orleans.Testing.Simulation.Tool",
            "src/Simulation/Orleans.Testing.Simulation.Tool/Orleans.Testing.Simulation.Tool.csproj",
            "Orleans.Testing.Simulation.Tool",
            "net11.0",
            "Tool"),
    ];

    [Fact]
    public void SourceBuildMapHasExactStablePackageMetadata()
    {
        SourceBuildPackage[] actual = ReadEvaluatedMap();
        XDocument sourceMap = XDocument.Load(
            Repository.PathOf("eng", "Microsoft.Orleans.Testing.Simulation.SourceBuild.props"));

        Assert.Equal(ExpectedPackages, actual);
        Assert.Equal(actual.Length, actual.Select(static package => package.PackageId).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(actual.Length, actual.Select(static package => package.SourceProject).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(actual.Length, actual.Select(static package => package.AssemblyName).Distinct(StringComparer.Ordinal).Count());
        Assert.All(actual, package => Assert.True(File.Exists(Repository.PathOf(package.SourceProject.Split('/')))));
        Assert.Empty(sourceMap.Descendants("Import"));
        Assert.Empty(sourceMap.Descendants("PropertyGroup"));
        Assert.Empty(sourceMap.Descendants("Target"));
        Assert.Empty(sourceMap.Descendants("ProjectReference"));
        Assert.Empty(sourceMap.Descendants("PackageReference"));
    }

    [Fact]
    public void SourceBuildMapMatchesEvaluatedShippingProjects()
    {
        foreach (SourceBuildPackage package in ReadEvaluatedMap())
        {
            AppRunResult result = ProcessAppRunner.Execute(
                "dotnet",
                [
                    "msbuild",
                    Repository.PathOf(package.SourceProject.Split('/')),
                    "-getProperty:PackageId,AssemblyName,TargetFramework",
                    "--nologo",
                ],
                Repository.PathOf(),
                timeout: TimeSpan.FromSeconds(60));
            Assert.True(
                result.ExitCode == 0,
                $"Project evaluation failed for {package.SourceProject}:{Environment.NewLine}"
                + $"{result.StandardOutput}{Environment.NewLine}{result.StandardError}");

            using JsonDocument document = JsonDocument.Parse(result.Output);
            JsonElement properties = document.RootElement.GetProperty("Properties");
            Assert.Equal(package.PackageId, properties.GetProperty("PackageId").GetString());
            Assert.Equal(package.AssemblyName, properties.GetProperty("AssemblyName").GetString());
            Assert.Equal(package.TargetFramework, properties.GetProperty("TargetFramework").GetString());
        }
    }

    private static SourceBuildPackage[] ReadEvaluatedMap()
    {
        string directory = TestArtifacts.CreateUnique("orleans-source-build-map");
        string projectPath = Path.Combine(directory, "SourceBuildMap.proj");
        File.WriteAllText(
            projectPath,
            $"""
            <Project>
              <Import Project="{Repository.PathOf("eng", "Microsoft.Orleans.Testing.Simulation.SourceBuild.props")}" />
            </Project>
            """);

        AppRunResult result = ProcessAppRunner.Execute(
            "dotnet",
            ["msbuild", projectPath, "-getItem:OrleansSimulationSourceBuildPackage", "--nologo"],
            directory,
            timeout: TimeSpan.FromSeconds(60));
        Assert.True(
            result.ExitCode == 0,
            $"Source-build map evaluation failed:{Environment.NewLine}"
            + $"{result.StandardOutput}{Environment.NewLine}{result.StandardError}");

        using JsonDocument document = JsonDocument.Parse(result.Output);
        return
        [
            .. document.RootElement
                .GetProperty("Items")
                .GetProperty("OrleansSimulationSourceBuildPackage")
                .EnumerateArray()
                .Select(static item => new SourceBuildPackage(
                    item.GetProperty("PackageId").GetString()!,
                    item.GetProperty("SourceProject").GetString()!.Replace('\\', '/'),
                    item.GetProperty("AssemblyName").GetString()!,
                    item.GetProperty("TargetFramework").GetString()!,
                    item.GetProperty("Role").GetString()!)),
        ];
    }

    private sealed record SourceBuildPackage(
        string PackageId,
        string SourceProject,
        string AssemblyName,
        string TargetFramework,
        string Role);
}
