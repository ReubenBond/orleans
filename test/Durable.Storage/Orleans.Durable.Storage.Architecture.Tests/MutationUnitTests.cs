using System.Text.Json;
using System.Text.RegularExpressions;

namespace Orleans.Durable.Storage.Architecture.Tests;

/// <summary>
/// Pins the mutation units of verification.md §9: each is a component (<see cref="Components"/>), so a unit's score
/// measures one component's tests however many components share its assembly. Beside each src project which builds
/// nothing under vendor lies one Stryker configuration per component the project compiles,
/// <c>stryker-config.&lt;Component&gt;.json</c>, whose mutate patterns select exactly that component's source files; and
/// the baseline (eng/mutation-baseline.json) names units, and files which exist.
/// </summary>
public sealed partial class MutationUnitTests
{
    [Fact]
    public void EachProjectHoldsAConfigurationPerComponent()
    {
        var expected = Components.Production.ByName.Values
            .Where(static component => component.ExternalProjects.Length == 0)
            .Select(static component => $"{component.Assembly}/stryker-config.{component.Name}.json");

        Assert.Equal(expected.Order(StringComparer.Ordinal), Configurations().Select(static configuration => configuration.Name).Order(StringComparer.Ordinal));
        Assert.All(Configurations(), static configuration => Assert.Equal(Path.GetFileName(configuration.Project) + ".csproj", configuration.Settings.GetProperty("project").GetString()));
    }

    [Fact]
    public void EachUnitMutatesExactlyItsComponentsSources()
    {
        var violations = new List<string>();
        foreach (var group in Configurations().GroupBy(static configuration => configuration.Project))
        {
            foreach (var file in Directory.EnumerateFiles(group.Key, "*.cs", SearchOption.AllDirectories).Where(file => !IsBuildOutput(group.Key, file)))
            {
                var relative = Path.GetRelativePath(group.Key, file).Replace('\\', '/');
                var units = group.Where(configuration => IsMutated(relative, configuration.Patterns)).Select(static configuration => configuration.Unit).ToArray();

                // A file which declares no type, such as an assembly's attributes, holds no mutant.
                if (Components.OfSource(file)?.Name is { } component && (units is not [var unit] || unit != component))
                {
                    violations.Add($"{Path.GetFileName(group.Key)}/{relative} ({component}) is mutated by [{string.Join(", ", units)}]");
                }
            }
        }

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Theory]
    [InlineData("Ledgers/Ledger.cs", true)]
    [InlineData("Ledgers/Store/LedgerStore.cs", false)]
    [InlineData("Ledgers/NodeId.cs", false)]
    [InlineData("Wal/Ledger.cs", false)]
    public void ThePatternsSelectAsStrykerDoes(string path, bool isMutated) =>
        Assert.Equal(isMutated, IsMutated(path, ["Ledgers/**/*.cs", "!Ledgers/Store/**/*.cs", "!Ledgers/NodeId.cs"]));

    [Fact]
    public void TheBaselineNamesUnitsAndFilesWhichExist()
    {
        var root = Repository.FindRoot();
        using var baseline = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "eng", "mutation-baseline.json")));
        var units = Configurations().Select(static configuration => configuration.Unit).ToHashSet(StringComparer.Ordinal);
        var projects = baseline.RootElement.GetProperty("projects").EnumerateObject().ToArray();
        var files = projects
            .SelectMany(static project => project.Value.TryGetProperty("equivalent", out var equivalent) ? equivalent.EnumerateArray() : [])
            .Concat(baseline.RootElement.TryGetProperty("equivalent", out var shared) ? shared.EnumerateArray() : [])
            .Select(static entry => entry.TryGetProperty("file", out var file) ? file.GetString() : null)
            .OfType<string>();

        Assert.NotEmpty(projects);
        Assert.All(projects, project => Assert.Contains(project.Name, units));
        Assert.All(files, file => Assert.True(File.Exists(Path.Combine(root, file)), $"{file} does not exist."));
    }

    /// <summary>Whether Stryker's mutate patterns select a path relative to the project: an include matches, and no <c>!</c> exclusion does.</summary>
    private static bool IsMutated(string path, string[] patterns) =>
        patterns.Where(static pattern => !pattern.StartsWith('!')).Any(pattern => Glob(pattern).IsMatch(path))
        && !patterns.Where(static pattern => pattern.StartsWith('!')).Any(pattern => Glob(pattern[1..]).IsMatch(path));

    /// <summary>A pattern as Stryker's globs read it: <c>**/</c> spans directories, and <c>*</c> stays within one name.</summary>
    private static Regex Glob(string pattern) =>
        new("^" + Regex.Escape(pattern).Replace(@"\*\*/", "(?:.*/)?", StringComparison.Ordinal).Replace(@"\*", "[^/]*", StringComparison.Ordinal) + "$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static bool IsBuildOutput(string project, string file) =>
        Path.GetRelativePath(project, file).Split(Path.DirectorySeparatorChar)[0] is "bin" or "obj";

    private static IEnumerable<UnitConfiguration> Configurations() =>
        Directory.EnumerateFiles(Path.Combine(Repository.FindRoot(), "src"), "stryker-config*.json", SearchOption.AllDirectories)
            .Select(static path =>
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                var settings = document.RootElement.GetProperty("stryker-config").Clone();
                var name = Path.GetFileName(path);
                return new UnitConfiguration(
                    Path.GetDirectoryName(path)!,
                    $"{Path.GetFileName(Path.GetDirectoryName(path))}/{name}",
                    UnitName().Match(name).Groups["unit"].Value,
                    settings,
                    [.. settings.GetProperty("mutate").EnumerateArray().Select(static pattern => pattern.GetString()!)]);
            });

    [GeneratedRegex(@"^stryker-config\.(?<unit>.+)\.json$", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex UnitName();

    private sealed record UnitConfiguration(string Project, string Name, string Unit, JsonElement Settings, string[] Patterns);
}
