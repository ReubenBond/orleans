using System.Text.RegularExpressions;

namespace Orleans.Durable.Storage.Architecture.Tests;

/// <summary>
/// Pins the conditional seams (docs/spec/build-packaging.md §1, verification.md §7.2): code which only the simulation
/// configurations compile, under <c>ORLEANS_DURABLE_STORAGE_SIMULATION</c>. No Debug or Release build of a src project carries the
/// simulation configurations' sentinel, the simulation builds of src do, and each <c>#if ORLEANS_DURABLE_STORAGE_SIMULATION</c> block in src
/// names a seam which verification §7.2 lists, as each listed seam is in src.
/// </summary>
public sealed partial class ConditionalSeamTests
{
    private const string Sentinel = "OrleansDurableStorageSimulation";

    [Fact]
    public void NoDebugOrReleaseBuildOfSrcCarriesTheSimulationSentinel()
    {
        var assemblies = Repository.ProductionAssemblies();
        Assert.NotEmpty(assemblies);
        Assert.All(assemblies, static assembly => Assert.False(
            Repository.AssemblyMetadata(assembly.Path).ContainsKey(Sentinel),
            $"{assembly.Path}, a {Repository.OutputConfiguration} build, carries {Sentinel}: it compiled ORLEANS_DURABLE_STORAGE_SIMULATION."));
    }

    /// <summary>
    /// The src projects a simulation project's closure rewrites build in its simulation configuration with the sentinel,
    /// so the check above is not vacuous.
    /// </summary>
    [Fact]
    public void SimulationBuildsOfSrcCarryTheSimulationSentinel()
    {
        var built = Repository.ProductionProjectFiles()
            .Select(Repository.SimulationOutputAssembly)
            .Where(File.Exists)
            .ToList();

        Assert.Contains(built, static path => Path.GetFileName(path) == Components.Engine + ".dll");
        Assert.All(built, static path => Assert.True(
            Repository.AssemblyMetadata(path).TryGetValue(Sentinel, out var value) && value == "true",
            $"{path}, a {Repository.SimulationConfiguration} build, does not carry {Sentinel}."));
    }

    [Fact]
    public void EveryConditionalSeamInSrcIsListedAndEveryListedSeamIsInSrc()
    {
        var root = Repository.FindRoot();
        var sources = Repository.ProductionSourceFiles("*.cs")
            .Select(file => (Path.GetRelativePath(root, file), File.ReadAllText(file)));
        var listed = ListedSeams(File.ReadAllText(Path.Combine(Repository.StorageRoot, "docs", "spec", "verification.md")));

        var (seams, violations) = Scan(sources, listed);

        Assert.Empty(violations);
        Assert.Contains("memory-replica-yields", seams);
    }

    /// <summary>
    /// The scan reports exactly its planted violations: a block naming no seam, one naming an unlisted seam, a negated
    /// condition, the symbol outside an <c>#if</c>, and a listed seam no block names.
    /// </summary>
    [Fact]
    public void TheScanReportsEachPlantedViolation()
    {
        (string, string)[] sources =
        [
            ("src/A.cs", "#if ORLEANS_DURABLE_STORAGE_SIMULATION // seam: listed\n#endif\n#if ORLEANS_DURABLE_STORAGE_SIMULATION\n#endif\n"),
            ("src/B.cs", "#if ORLEANS_DURABLE_STORAGE_SIMULATION // seam: unlisted\n#endif\n#if !ORLEANS_DURABLE_STORAGE_SIMULATION // seam: listed\n#endif\n"),
            ("src/C.cs", "var name = \"ORLEANS_DURABLE_STORAGE_SIMULATION\";\n"),
        ];

        var (seams, violations) = Scan(sources, ["listed", "absent"]);

        Assert.Equal(["listed", "unlisted"], seams.Order(StringComparer.Ordinal));
        Assert.Equal(
            [
                "src/A.cs:3 names no seam: '#if ORLEANS_DURABLE_STORAGE_SIMULATION'",
                "src/B.cs:1 names the seam 'unlisted', which verification §7.2 does not list",
                "src/B.cs:3 names no seam: '#if !ORLEANS_DURABLE_STORAGE_SIMULATION // seam: listed'",
                "src/C.cs:1 names no seam: 'var name = \"ORLEANS_DURABLE_STORAGE_SIMULATION\";'",
                "verification §7.2 lists the seam 'absent', which no block in src names",
            ],
            violations);
    }

    /// <summary>
    /// Returns the seams the blocks of <paramref name="sources"/> name, and each violation: a line which mentions the
    /// symbol other than as <c>#if ORLEANS_DURABLE_STORAGE_SIMULATION // seam: name</c>, a seam <paramref name="listed"/> does not hold, and a
    /// listed seam no block names.
    /// </summary>
    private static (HashSet<string> Seams, List<string> Violations) Scan(IEnumerable<(string Path, string Text)> sources, IReadOnlyCollection<string> listed)
    {
        var seams = new HashSet<string>(StringComparer.Ordinal);
        var violations = new List<string>();
        foreach (var (path, text) in sources)
        {
            var lines = text.ReplaceLineEndings("\n").Split('\n');
            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index];
                if (!line.Contains("ORLEANS_DURABLE_STORAGE_SIMULATION", StringComparison.Ordinal))
                {
                    continue;
                }

                var location = $"{path.Replace('\\', '/')}:{index + 1}";
                if (SeamDirective().Match(line) is not { Success: true } match)
                {
                    violations.Add($"{location} names no seam: '{line.Trim()}'");
                    continue;
                }

                var seam = match.Groups["seam"].Value;
                _ = seams.Add(seam);
                if (!listed.Contains(seam))
                {
                    violations.Add($"{location} names the seam '{seam}', which verification §7.2 does not list");
                }
            }
        }

        violations.AddRange(listed.Where(seam => !seams.Contains(seam)).Select(static seam => $"verification §7.2 lists the seam '{seam}', which no block in src names"));
        return (seams, violations);
    }

    /// <summary>
    /// Returns the seams the table under verification §7.2's <c>Conditional seams</c> heading lists, by the name in each
    /// row's first cell.
    /// </summary>
    private static List<string> ListedSeams(string verification)
    {
        var lines = verification.ReplaceLineEndings("\n").Split('\n');
        var start = Array.FindIndex(lines, static line => line.StartsWith("#### Conditional seams", StringComparison.Ordinal));
        Assert.True(start >= 0, "verification.md has no '#### Conditional seams' section.");
        var seams = lines.Skip(start + 1)
            .TakeWhile(static line => !line.StartsWith('#'))
            .Select(static line => ListedSeam().Match(line))
            .Where(static match => match.Success)
            .Select(static match => match.Groups["seam"].Value)
            .ToList();
        Assert.NotEmpty(seams);
        return seams;
    }

    [GeneratedRegex(@"^\s*#if ORLEANS_DURABLE_STORAGE_SIMULATION // seam: (?<seam>[a-z0-9]+(?:-[a-z0-9]+)*)\s*$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SeamDirective();

    [GeneratedRegex(@"^\| `(?<seam>[a-z0-9]+(?:-[a-z0-9]+)*)` \|", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ListedSeam();
}
