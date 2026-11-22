using System.Text.Json;
using System.Xml.Linq;

namespace Orleans.Durable.Storage.Architecture.Tests;

/// <summary>
/// Pins build-packaging.md §2 and §6: every Storage <c>src</c> project builds with the trim, AOT, and single-file analyzers
/// and takes its package ID in the Microsoft.Orleans.* family, and Orleans reaches Storage only as this repository's
/// projects.
/// </summary>
public sealed class PackagingTests
{
    [Fact]
    public void NoProductionProjectOptsOutOfAotAnalysis()
    {
        foreach (var projectFile in Repository.ProductionProjectFiles())
        {
            var declared = XDocument
                .Load(projectFile)
                .Descendants("IsAotCompatible")
                .Select(static element => element.Value)
                .ToArray();

            Assert.All(
                declared,
                value => Assert.Equal("true", value, StringComparer.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void ProductionProjectsInheritAotAnalysisByDefault()
    {
        var buildProperties = XDocument.Load(Path.Combine(Repository.FindRoot(), "eng", "durable-storage", "Build.props"));

        var defaulted = buildProperties
            .Descendants("IsAotCompatible")
            .Any(static element =>
                string.Equals(element.Value, "true", StringComparison.OrdinalIgnoreCase) &&
                element.Parent?.Attribute("Condition") is not null);

        Assert.True(defaulted, "eng/durable-storage/Build.props must default IsAotCompatible for Storage src projects.");
    }

    /// <summary>
    /// Orleans is this repository's source (build-packaging.md §2): no Storage project's restore graph holds an Orleans
    /// package, so every Orleans assembly a Storage project loads is built from the same commit as Storage.
    /// </summary>
    [Fact]
    public void NoStorageProjectRestoresAnOrleansPackage()
    {
        var restored = Repository.StorageProjectFiles()
            .Where(static projectFile => File.Exists(Repository.AssetsFile(projectFile)))
            .SelectMany(static projectFile => OrleansPackages(Repository.AssetsFile(projectFile))
                .Select(package => $"{Path.GetFileNameWithoutExtension(projectFile)} -> {package}"))
            .ToArray();
        var composition = Repository.ProductionProjectFiles().Single(static projectFile => Path.GetFileNameWithoutExtension(projectFile) == Components.Composition);

        Assert.True(File.Exists(Repository.AssetsFile(composition)), $"{Repository.AssetsFile(composition)} does not exist: restore the Storage projects first.");
        // The restore records a referenced project by its package ID; the composition reaches Orleans.Durable as a project.
        Assert.Contains("Microsoft.Orleans.Durable", LayeringTests.RestoredLibraries(Repository.AssetsFile(composition)));
        Assert.Empty(restored);
    }

    /// <summary>
    /// Each Storage src project's package ID is its assembly's name in the Microsoft.Orleans.* family (build-packaging.md
    /// §6), as its restore records it.
    /// </summary>
    [Fact]
    public void EachProductionPackageIdIsItsAssemblyNameInTheMicrosoftOrleansFamily()
    {
        var projects = Repository.ProductionProjectFiles();

        Assert.NotEmpty(projects);
        Assert.All(projects, projectFile =>
        {
            var project = Path.GetFileNameWithoutExtension(projectFile);
            Assert.StartsWith("Orleans.", project, StringComparison.Ordinal);
            var assets = Repository.AssetsFile(projectFile);
            Assert.True(File.Exists(assets), $"{assets} does not exist: restore the Storage projects first.");
            using var document = JsonDocument.Parse(File.ReadAllText(assets));
            Assert.Equal("Microsoft." + project, document.RootElement.GetProperty("project").GetProperty("restore").GetProperty("projectName").GetString());
        });
    }

    /// <summary>The Orleans runtime packages the restore graph <paramref name="assetsFile"/> records.</summary>
    private static IEnumerable<string> OrleansPackages(string assetsFile)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(assetsFile));
        return
        [
            .. document.RootElement.GetProperty("libraries").EnumerateObject()
                .Where(static library => library.Value.GetProperty("type").ValueEquals("package"))
                .Select(static library => library.Name)
                // The families which share Orleans' prefix, Fast CASPaxos and the simulation framework, build from source.
                .Where(static library => LayeringTests.IsOrleans(library, "Microsoft.Orleans.")),
        ];
    }
}
