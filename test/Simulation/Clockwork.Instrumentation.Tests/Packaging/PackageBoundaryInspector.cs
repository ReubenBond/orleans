using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;

namespace Orleans.Testing.Simulation.Instrumentation.Tests.Packaging;

internal sealed record PackageDependencyGroup(string TargetFramework, string[] PackageIds);

internal sealed record PackageArchiveInspection(
    string PackageId,
    string[] Entries,
    PackageDependencyGroup[] DependencyGroups);

/// <summary>
/// Reads the evaluated NuGet artifacts used by dependency-boundary tests. This deliberately
/// inspects restored assets and packed archives instead of inferring package behavior from
/// <c>PrivateAssets</c> or project-file text.
/// </summary>
internal static class PackageBoundaryInspector
{
    public static readonly string[] ForbiddenProductionPackageIds =
    [
        "Microsoft.Orleans.Testing.Simulation",
        "Microsoft.Orleans.Testing.Simulation.Instrumentation",
        "Microsoft.Orleans.Testing.Simulation.Instrumentation.Build",
        "Microsoft.Orleans.Testing.Simulation.Analyzers",
        "Microsoft.Orleans.Testing.Simulation.Testing",
        "Microsoft.Orleans.Testing.Simulation.Testing.MTP",
        "Microsoft.Orleans.Testing.Simulation.Tool",
    ];

    public static string[] ReadPackageIds(string assetsPath)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(assetsPath));
        return
        [
            .. document.RootElement.GetProperty("libraries")
                .EnumerateObject()
                .Where(static library =>
                    string.Equals(
                        library.Value.GetProperty("type").GetString(),
                        "package",
                        StringComparison.Ordinal))
                .Select(static library => PackageIdFromLibraryName(library.Name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase),
        ];
    }

    public static string[] ReadPackageFiles(string assetsPath, string packageId)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(assetsPath));
        JsonProperty library = document.RootElement.GetProperty("libraries")
            .EnumerateObject()
            .Single(candidate =>
                string.Equals(
                    PackageIdFromLibraryName(candidate.Name),
                    packageId,
                    StringComparison.OrdinalIgnoreCase));
        if (!library.Value.TryGetProperty("files", out JsonElement files))
        {
            return [];
        }

        return [.. files.EnumerateArray().Select(static file => file.GetString()!).Order(StringComparer.Ordinal)];
    }

    public static string[] ReadResolvedAssets(string assetsPath, params string[] assetGroups)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(assetsPath));
        var assets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty target in document.RootElement.GetProperty("targets").EnumerateObject())
        {
            foreach (JsonProperty library in target.Value.EnumerateObject())
            {
                foreach (string assetGroup in assetGroups)
                {
                    if (library.Value.TryGetProperty(assetGroup, out JsonElement group))
                    {
                        foreach (JsonProperty asset in group.EnumerateObject())
                        {
                            assets.Add(asset.Name);
                        }
                    }
                }
            }
        }

        return [.. assets.Order(StringComparer.OrdinalIgnoreCase)];
    }

    public static string[] FindForbiddenPackages(IEnumerable<string> packageIds) =>
    [
        .. packageIds
            .Where(IsForbiddenProductionPackage)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase),
    ];

    public static string[] FindForbiddenRuntimeAssets(IEnumerable<string> assetPaths) =>
    [
        .. assetPaths
            .Where(static path =>
            {
                string normalized = path.Replace('\\', '/');
                if (!normalized.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                bool runtimeAsset =
                    normalized.StartsWith("lib/", StringComparison.OrdinalIgnoreCase)
                    || normalized.StartsWith("runtimes/", StringComparison.OrdinalIgnoreCase);
                return runtimeAsset
                    && Path.GetFileName(normalized)
                        .StartsWith("Orleans.Testing.Simulation", StringComparison.OrdinalIgnoreCase);
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase),
    ];

    public static string[] FindForbiddenAssemblyFiles(IEnumerable<string> paths) =>
    [
        .. paths
            .Where(static path =>
                Path.GetFileName(path)
                    .StartsWith("Orleans.Testing.Simulation", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase),
    ];

    public static PackageArchiveInspection InspectPackage(string packagePath)
    {
        using ZipArchive package = ZipFile.OpenRead(packagePath);
        string[] entries = [.. package.Entries.Select(static entry => entry.FullName).Order(StringComparer.Ordinal)];
        ZipArchiveEntry nuspecEntry = package.Entries.Single(
            static entry => entry.FullName.EndsWith(".nuspec", StringComparison.Ordinal));
        using Stream stream = nuspecEntry.Open();
        XDocument nuspec = XDocument.Load(stream);
        XNamespace ns = nuspec.Root!.Name.Namespace;
        XElement metadata = nuspec.Root.Element(ns + "metadata")!;
        string packageId = metadata.Element(ns + "id")!.Value;
        PackageDependencyGroup[] groups =
        [
            .. metadata
                .Element(ns + "dependencies")?
                .Elements(ns + "group")
                .Select(group => new PackageDependencyGroup(
                    (string?)group.Attribute("targetFramework") ?? string.Empty,
                    [
                        .. group.Elements(ns + "dependency")
                            .Select(static dependency => (string?)dependency.Attribute("id"))
                            .Where(static id => id is not null)
                            .Select(static id => id!)
                            .Order(StringComparer.OrdinalIgnoreCase),
                    ]))
                ?? [],
        ];

        return new PackageArchiveInspection(packageId, entries, groups);
    }

    private static bool IsForbiddenProductionPackage(string packageId) =>
        ForbiddenProductionPackageIds.Contains(packageId, StringComparer.OrdinalIgnoreCase);

    private static string PackageIdFromLibraryName(string libraryName)
    {
        int separator = libraryName.LastIndexOf('/');
        return separator < 0 ? libraryName : libraryName[..separator];
    }
}
