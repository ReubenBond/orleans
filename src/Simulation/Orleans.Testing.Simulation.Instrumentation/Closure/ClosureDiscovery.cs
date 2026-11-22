using System.Collections.Immutable;
using System.IO.Enumeration;
using Orleans.Testing.Simulation.Instrumentation.Configuration;
using Orleans.Testing.Simulation.Instrumentation.Imaging;

namespace Orleans.Testing.Simulation.Instrumentation.Closure;

/// <summary>
/// Discovers the deterministic set of assets in an application output or publish directory and
/// classifies each as a managed-IL rewrite candidate or a verbatim copy. Discovery is bounded to the
/// closure root (it never follows paths outside it), respects include/exclude patterns and the
/// mandatory framework-assembly boundary, treats satellite resource assemblies and native libraries as
/// copy-only, and detects the entry assembly from a <c>*.runtimeconfig.json</c> when present. The
/// result keeps a closure runnable: everything that is not rewritten is copied unchanged.
/// </summary>
public static class ClosureDiscovery
{
    /// <summary>
    /// Discovers and classifies the application closure rooted at <paramref name="rootDirectory"/>.
    /// </summary>
    /// <param name="rootDirectory">The directory to scan (an app output or publish directory).</param>
    /// <param name="configuration">The instrumentation configuration governing selection.</param>
    /// <param name="entryAssemblyName">
    /// The entry assembly's simple name or file name, or <see langword="null"/> to auto-detect it
    /// from a <c>*.runtimeconfig.json</c> in the root.
    /// </param>
    /// <param name="frameworkClassifier">The framework-assembly classifier, or <see langword="null"/> for the default.</param>
    /// <returns>The deterministic closure plan.</returns>
    /// <exception cref="ClosureException">The root is missing, or the entry cannot be determined when required.</exception>
    public static ClosurePlan Discover(
        string rootDirectory,
        InstrumentationConfiguration configuration,
        string? entryAssemblyName = null,
        FrameworkAssemblyClassifier? frameworkClassifier = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(rootDirectory);
        ArgumentNullException.ThrowIfNull(configuration);
        if (!Directory.Exists(rootDirectory))
        {
            throw new ClosureException($"Closure root directory was not found: '{rootDirectory}'.");
        }

        string root = Path.GetFullPath(rootDirectory);
        FrameworkAssemblyClassifier classifier = frameworkClassifier ?? FrameworkAssemblyClassifier.Default;

        string? entryRelative = ResolveEntryRelativePath(root, entryAssemblyName);

        var assets = new List<ClosureAsset>();
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            string relative = NormalizeRelative(Path.GetRelativePath(root, file));
            bool isEntry = entryRelative is not null
                && string.Equals(relative, entryRelative, StringComparison.OrdinalIgnoreCase);
            assets.Add(Classify(file, relative, configuration, classifier, isEntry));
        }

        ImmutableArray<ClosureAsset> ordered =
            [.. assets.OrderBy(a => a.RelativePath, StringComparer.Ordinal)];
        return new ClosurePlan(root, entryRelative, ordered);
    }

    private static ClosureAsset Classify(
        string file,
        string relative,
        InstrumentationConfiguration configuration,
        FrameworkAssemblyClassifier classifier,
        bool isEntry)
    {
        string fileName = Path.GetFileName(file);
        string extension = Path.GetExtension(fileName).ToLowerInvariant();

        if (extension == ".pdb")
        {
            return Copy(file, relative, AssetKind.DebugSymbols, null);
        }

        if (fileName.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase))
        {
            return Copy(file, relative, AssetKind.DepsJson, null);
        }

        if (fileName.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase))
        {
            return Copy(file, relative, AssetKind.RuntimeConfig, null);
        }

        if (extension is ".so" or ".dylib")
        {
            return Copy(file, relative, AssetKind.NativeLibrary, null);
        }

        if (extension != ".dll")
        {
            return Copy(file, relative, AssetKind.Other, null);
        }

        return ClassifyManagedCandidate(file, relative, fileName, configuration, classifier, isEntry);
    }

    private static ClosureAsset ClassifyManagedCandidate(
        string file,
        string relative,
        string fileName,
        InstrumentationConfiguration configuration,
        FrameworkAssemblyClassifier classifier,
        bool isEntry)
    {
        AssemblyImageInfo image;
        try
        {
            image = AssemblyImageInfo.Inspect(file);
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException)
        {
            if (isEntry)
            {
                throw new ClosureException(
                    $"Entry assembly '{relative}' is not a valid managed PE image with usable IL.");
            }

            // Not a readable PE: treat as an opaque native/content asset and copy it unchanged.
            return Copy(file, relative, AssetKind.NativeLibrary, null);
        }

        if (!image.IsManagedAssembly)
        {
            if (isEntry)
            {
                throw new ClosureException(
                    $"Entry assembly '{relative}' is native-only and does not contain usable managed IL.");
            }

            return Copy(file, relative, AssetKind.NativeLibrary, null);
        }

        string simpleName = Path.GetFileNameWithoutExtension(fileName);
        if (simpleName.EndsWith(".resources", StringComparison.OrdinalIgnoreCase))
        {
            return Copy(file, relative, AssetKind.SatelliteAssembly, "satellite resource assembly");
        }

        if (classifier.IsFrameworkAssembly(simpleName))
        {
            return Copy(file, relative, AssetKind.ManagedAssembly, "framework assembly");
        }

        if (Matches(configuration.ExcludePatterns, relative, fileName))
        {
            return Copy(file, relative, AssetKind.ManagedAssembly, "excluded by pattern");
        }

        if (!configuration.IncludePatterns.IsDefaultOrEmpty &&
            configuration.IncludePatterns.Length > 0 &&
            !Matches(configuration.IncludePatterns, relative, fileName))
        {
            return Copy(file, relative, AssetKind.ManagedAssembly, "not matched by include pattern");
        }

        return new ClosureAsset(file, relative, AssetKind.ManagedAssembly, Rewrite: true, SkipReason: null);
    }

    private static ClosureAsset Copy(string file, string relative, AssetKind kind, string? reason) =>
        new(file, relative, kind, Rewrite: false, SkipReason: reason);

    private static bool Matches(ImmutableArray<string> patterns, string relative, string fileName)
    {
        if (patterns.IsDefaultOrEmpty)
        {
            return false;
        }

        foreach (string pattern in patterns)
        {
            if (FileSystemName.MatchesSimpleExpression(pattern, relative, ignoreCase: true) ||
                FileSystemName.MatchesSimpleExpression(pattern, fileName, ignoreCase: true))
            {
                return true;
            }
        }

        return false;
    }

    private static string? ResolveEntryRelativePath(string root, string? entryAssemblyName)
    {
        if (!string.IsNullOrEmpty(entryAssemblyName))
        {
            string fileName = entryAssemblyName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                ? entryAssemblyName
                : entryAssemblyName + ".dll";
            string candidate = Path.Combine(root, fileName);
            return File.Exists(candidate) ? NormalizeRelative(fileName) : null;
        }

        // Auto-detect from a single top-level *.runtimeconfig.json (its base name is the app assembly).
        string[] runtimeConfigs = Directory.GetFiles(root, "*.runtimeconfig.json", SearchOption.TopDirectoryOnly);
        if (runtimeConfigs.Length != 1)
        {
            return null;
        }

        string baseName = Path.GetFileName(runtimeConfigs[0]);
        baseName = baseName[..^".runtimeconfig.json".Length];
        string entryFile = baseName + ".dll";
        return File.Exists(Path.Combine(root, entryFile)) ? NormalizeRelative(entryFile) : null;
    }

    private static string NormalizeRelative(string relative) =>
        relative.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
}
