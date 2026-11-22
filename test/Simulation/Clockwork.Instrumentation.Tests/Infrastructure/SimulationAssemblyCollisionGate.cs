using System.IO.Compression;
using System.Text.Json;

namespace Orleans.Testing.Simulation.Instrumentation.Tests.Infrastructure;

/// <summary>
/// Rejects a boundary which contains both the legacy <c>Clockwork.dll</c> runtime and any assembly
/// from the final <c>Orleans.Testing.Simulation</c> family. Later package/import layers can reuse the
/// same gate for evaluated assets, resolved references, copied output, and runtime load inventories.
/// </summary>
internal static class SimulationAssemblyCollisionGate
{
    private const string LegacyAssemblyName = "Clockwork";
    private const string FinalAssemblyPrefix = "Orleans.Testing.Simulation";

    public static void AssertNoCollision(string boundary, IEnumerable<string> candidates)
    {
        string[] names = [.. candidates
            .Select(GetSimpleName)
            .Where(static name => name is not null)
            .Select(static name => name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)];
        bool hasLegacy = names.Contains(LegacyAssemblyName, StringComparer.OrdinalIgnoreCase);
        bool hasFinal = names.Any(IsFinalAssembly);
        if (hasLegacy && hasFinal)
        {
            throw new InvalidOperationException(
                $"{boundary} contains both legacy {LegacyAssemblyName}.dll and final simulation assemblies: "
                + string.Join(", ", names));
        }
    }

    public static void AssertNoCollisionInAssetsFile(string assetsFile)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(assetsFile));
        var assets = new List<string>();
        if (document.RootElement.TryGetProperty("targets", out JsonElement targets))
        {
            foreach (JsonProperty target in targets.EnumerateObject())
            {
                foreach (JsonProperty library in target.Value.EnumerateObject())
                {
                    AddAssetGroup(library.Value, "compile", assets);
                    AddAssetGroup(library.Value, "runtime", assets);
                    AddAssetGroup(library.Value, "runtimeTargets", assets);
                    AddAssetGroup(library.Value, "analyzers", assets);
                    AddAssetGroup(library.Value, "build", assets);
                    AddAssetGroup(library.Value, "buildMultiTargeting", assets);
                    AddAssetGroup(library.Value, "buildTransitive", assets);
                }
            }
        }

        AssertNoCollision($"evaluated assets in '{assetsFile}'", assets);
    }

    public static void AssertNoCollisionInPackage(string packagePath)
    {
        using ZipArchive package = ZipFile.OpenRead(packagePath);
        AssertNoCollision(
            $"package payload in '{packagePath}'",
            package.Entries.Select(static entry => entry.FullName));
    }

    public static void AssertNoCollisionInFile(string boundary, string path) =>
        AssertNoCollision(boundary, File.ReadAllLines(path));

    public static void AssertNoCollisionInDirectory(string boundary, string directory) =>
        AssertNoCollision(boundary, Directory.EnumerateFiles(directory, "*.dll", SearchOption.AllDirectories));

    private static void AddAssetGroup(JsonElement library, string propertyName, List<string> assets)
    {
        if (library.TryGetProperty(propertyName, out JsonElement group))
        {
            assets.AddRange(group.EnumerateObject().Select(static item => item.Name));
        }
    }

    private static string? GetSimpleName(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        string value = candidate.Trim();
        int comma = value.IndexOf(',');
        if (comma >= 0)
        {
            value = value[..comma];
        }

        value = value.Replace('\\', '/');
        string fileName = value[(value.LastIndexOf('/') + 1)..];
        return fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^4]
            : fileName;
    }

    private static bool IsFinalAssembly(string name) =>
        name.Equals(FinalAssemblyPrefix, StringComparison.OrdinalIgnoreCase)
        || name.StartsWith(FinalAssemblyPrefix + ".", StringComparison.OrdinalIgnoreCase);
}
