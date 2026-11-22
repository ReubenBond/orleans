using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Orleans.Durable.Storage.Architecture.Tests;

/// <summary>
/// Release builds compile <c>Debug.Assert</c> away, so only the Debug leg runs debug assertions: every component
/// (<see cref="Components"/>) whose code under src holds one is reached by a test project which sets <c>ClogDebugLeg</c>
/// (docs/spec/verification.md §12). A test project named for a component, as tests are organized by contract, reaches
/// that component and what it reaches; any other reaches every component of the projects it references.
/// </summary>
public sealed partial class DebugLegTests
{
    private static readonly StringComparer Paths = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    [Fact]
    public void EveryComponentWhoseCodeHoldsDebugAssertionsIsReachedByTheDebugLeg()
    {
        var root = Repository.FindRoot();
        var map = Components.Production;
        var leg = Directory
            .EnumerateFiles(Path.Combine(root, "tests"), "*.csproj", SearchOption.AllDirectories)
            .Where(static project => XDocument.Load(project).Descendants("ClogDebugLeg").Any(static element => element.Value.Trim() == "true"))
            .ToList();
        var asserting = Directory
            .EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(file => !IsBuildOutput(Path.GetRelativePath(Path.Combine(root, "src"), file)))
            .Where(static file => DebugAssertion().IsMatch(File.ReadAllText(file)))
            .Select(static file => Components.OfSource(file)?.Name ?? throw new InvalidOperationException($"{file} declares no type."))
            .ToHashSet(StringComparer.Ordinal);
        var reached = leg.SelectMany(project => ReachedComponents(map, project)).ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(leg);

        // The scan finds the assertions it must, so an empty result below is not vacuous.
        Assert.Contains("Orleans.Durable.Storage.Engine.Buffers", asserting);
        Assert.Empty(asserting.Where(component => !reached.Contains(component)).Order(StringComparer.Ordinal));
    }

    /// <summary>The components a Debug-leg test project reaches: its contract's, or else those of the projects it references.</summary>
    private static IEnumerable<string> ReachedComponents(ComponentMap map, string project)
    {
        var name = Path.GetFileNameWithoutExtension(project);
        if (name.EndsWith(".Tests", StringComparison.Ordinal) && map.ByName.ContainsKey(name[..^".Tests".Length]))
        {
            var tested = name[..^".Tests".Length];
            return map.Reachable(tested).Append(tested);
        }

        var assemblies = Reached([project]).Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.Ordinal);
        return map.ByName.Values.Where(component => assemblies.Contains(component.Assembly)).Select(static component => component.Name);
    }
    private static bool IsBuildOutput(string relativePath) =>
        relativePath.Split(Path.DirectorySeparatorChar)[0] is "bin" or "obj";

    /// <summary>
    /// Returns every project <paramref name="projects"/> reference, directly or transitively, with themselves.
    /// </summary>
    private static HashSet<string> Reached(IEnumerable<string> projects)
    {
        var reached = new HashSet<string>(Paths);
        var pending = new Stack<string>(projects.Select(Path.GetFullPath));
        while (pending.TryPop(out var project))
        {
            if (!reached.Add(project))
            {
                continue;
            }

            var directory = Path.GetDirectoryName(project)!;
            foreach (var include in XDocument.Load(project).Descendants("ProjectReference").Select(static element => element.Attribute("Include")?.Value))
            {
                // A reference through an MSBuild property or a wildcard names no single file this check can follow.
                if (include is null || include.Contains("$(", StringComparison.Ordinal) || include.Contains('*', StringComparison.Ordinal))
                {
                    continue;
                }

                var reference = Path.GetFullPath(Path.Combine(directory, include.Replace('\\', Path.DirectorySeparatorChar)));
                if (File.Exists(reference))
                {
                    pending.Push(reference);
                }
            }
        }

        return reached;
    }

    [GeneratedRegex(@"\bDebug\.(?:Assert|Fail)\s*\(", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex DebugAssertion();

}
