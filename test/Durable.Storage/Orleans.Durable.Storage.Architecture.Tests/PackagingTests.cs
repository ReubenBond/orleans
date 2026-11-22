using System.Text.Json;
using System.Xml.Linq;

namespace Orleans.Durable.Storage.Architecture.Tests;

/// <summary>
/// Pins build-packaging.md §2 and §6: every <c>src</c> project builds with the trim, AOT, and single-file analyzers and
/// takes its package ID in the Microsoft.Orleans.* family, and every Orleans package is of the one pinned build.
/// </summary>
public sealed class PackagingTests
{
    [Fact]
    public void NoProductionProjectOptsOutOfAotAnalysis()
    {
        var root = Repository.FindRoot();

        foreach (var projectFile in EnumerateProductionProjects(root))
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
        var root = Repository.FindRoot();
        var directoryProperties = XDocument.Load(Path.Combine(root, "Directory.Build.props"));

        var defaulted = directoryProperties
            .Descendants("IsAotCompatible")
            .Any(static element =>
                string.Equals(element.Value, "true", StringComparison.OrdinalIgnoreCase) &&
                element.Attribute("Condition") is not null);

        Assert.True(defaulted, "Directory.Build.props must default IsAotCompatible for src projects.");
    }

    /// <summary>
    /// The Orleans pin is one build (build-packaging.md §2): every Orleans package a lock file holds is at
    /// <c>OrleansVersion</c>, or, alpha-tagged, at <c>$(OrleansVersion).alpha.1</c>, and each restored package's nuspec
    /// records <c>OrleansCommit</c>.
    /// </summary>
    [Fact]
    public void EveryOrleansPackageIsOfThePinnedBuild()
    {
        var root = Repository.FindRoot();
        var properties = XDocument.Load(Path.Combine(root, "Directory.Packages.props"));
        var version = properties.Descendants("OrleansVersion").Single().Value;
        var commit = properties.Descendants("OrleansCommit").Single().Value;
        var locked = ((string[])["src", "tests", "fuzz", "benchmarks", "apphost", "samples"])
            .Select(directory => Path.Combine(root, directory))
            .Where(Directory.Exists)
            .SelectMany(static directory => Directory.EnumerateFiles(directory, "packages.lock.json", SearchOption.AllDirectories))
            .SelectMany(LockedOrleansPackages)
            .Distinct()
            .ToArray();

        Assert.NotEmpty(locked);
        Assert.All(locked, package => Assert.Contains(package.Version, (string[])[version, version + ".alpha.1"]));
        var folders = PackageFolders(Path.Combine(root, "artifacts", "obj", Components.Composition, "project.assets.json"));
        Assert.All(locked, package => Assert.Equal(commit, RepositoryCommit(folders, package)));
    }

    /// <summary>
    /// Each src project's package ID is its assembly's name in the Microsoft.Orleans.* family (build-packaging.md §6),
    /// as its restore records it.
    /// </summary>
    [Fact]
    public void EachProductionPackageIdIsItsAssemblyNameInTheMicrosoftOrleansFamily()
    {
        var root = Repository.FindRoot();
        var projects = EnumerateProductionProjects(root).Select(static path => Path.GetFileNameWithoutExtension(path)).ToArray();

        Assert.NotEmpty(projects);
        Assert.All(projects, project =>
        {
            Assert.StartsWith("Orleans.", project, StringComparison.Ordinal);
            var assets = Path.Combine(root, "artifacts", "obj", project, "project.assets.json");
            Assert.True(File.Exists(assets), $"{assets} does not exist: restore the solution first.");
            using var document = JsonDocument.Parse(File.ReadAllText(assets));
            Assert.Equal("Microsoft." + project, document.RootElement.GetProperty("project").GetProperty("restore").GetProperty("projectName").GetString());
        });
    }
    private static IEnumerable<(string Id, string Version)> LockedOrleansPackages(string lockFile)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(lockFile));
        return
        [
            .. document.RootElement.GetProperty("dependencies").EnumerateObject()
                .SelectMany(static framework => framework.Value.EnumerateObject())
                .Where(static package => !LayeringTests.IsProject(package.Value))
                // The families which share Orleans' prefix, Fast CASPaxos and the simulation framework, build from source.
                .Where(static package => LayeringTests.IsOrleans(package.Name, "Microsoft.Orleans."))
                .Select(static package => (package.Name, package.Value.GetProperty("resolved").GetString()!)),
        ];
    }

    /// <summary>Returns the package folders the composition's restore used, in the order NuGet probes them.</summary>
    private static string[] PackageFolders(string assetsFile)
    {
        Assert.True(File.Exists(assetsFile), $"{assetsFile} does not exist: restore the solution first.");
        using var document = JsonDocument.Parse(File.ReadAllText(assetsFile));
        return [.. document.RootElement.GetProperty("packageFolders").EnumerateObject().Select(static folder => folder.Name)];
    }

    /// <summary>Returns the commit the restored package's nuspec records as its repository's.</summary>
    private static string? RepositoryCommit(string[] folders, (string Id, string Version) package)
    {
        var id = package.Id.ToLowerInvariant();
        var nuspec = folders
            .Select(folder => Path.Combine(folder, id, package.Version.ToLowerInvariant(), id + ".nuspec"))
            .FirstOrDefault(File.Exists);
        Assert.True(nuspec is not null, $"{package.Id} {package.Version} is not restored.");
        return XDocument.Load(nuspec).Descendants().SingleOrDefault(static element => element.Name.LocalName == "repository")?.Attribute("commit")?.Value;
    }

    private static IEnumerable<string> EnumerateProductionProjects(string root) =>
        Directory
            .EnumerateFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories)
            .OrderBy(static path => path, StringComparer.Ordinal);
}
