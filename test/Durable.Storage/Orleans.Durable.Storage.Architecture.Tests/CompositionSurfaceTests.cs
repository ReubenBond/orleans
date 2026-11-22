using System.Reflection;
using Orleans.Durable.Storage.Engine.Cluster.Control;

namespace Orleans.Durable.Storage.Architecture.Tests;

/// <summary>
/// The Orleans composition's public surface is the smallest which lets an app configure a silo (orleans.md §2, §6): the
/// registration, the options root and its sections, the placement strategy an app's grain names, and the exception an app
/// grain's journal write may meet. Its public types, with every type their public properties reach, which an app's
/// configuration names by their members, and their public and protected members are the snapshot beside this file, so a
/// change to the surface, a lower layer's options among it, is an explicit spec change; everything else stays internal.
/// </summary>
public sealed class CompositionSurfaceTests
{
    private const string SnapshotName = nameof(CompositionSurfaceTests) + ".PublicMembers";

    /// <summary>The namespace of the codecs Orleans' code generator emits, which are Orleans' own and follow its version.</summary>
    private const string GeneratedNamespace = "OrleansCodeGen";

    /// <summary>
    /// The public types of the composition and of the controllers' decisions, which the Engine compiles, apart from
    /// Orleans' generated codecs, and their members, one per line in ordinal
    /// order, are the checked-in snapshot. On a difference the test writes the rendered surface beside the snapshot as a
    /// <c>.received.txt</c> file, which git ignores.
    /// </summary>
    [Fact]
    public void TheCompositionsPublicMembersAreItsSnapshot()
    {
        var directory = Path.Combine(Repository.StorageRoot, "Orleans.Durable.Storage.Architecture.Tests");
        var snapshot = Path.Combine(directory, SnapshotName + ".txt");
        var received = Path.Combine(directory, SnapshotName + ".received.txt");
        var rendered = LedgerReplicaContractTests.Render(Reached(
            new[] { typeof(DurableStorageOptions).Assembly, typeof(ControlOptions).Assembly }
                .SelectMany(static assembly => assembly.GetExportedTypes())
                .Where(static type => type.Namespace?.StartsWith(GeneratedNamespace, StringComparison.Ordinal) != true
                    && Components.Production.Of(type.Namespace ?? string.Empty, type.FullName!)?.Name is "Orleans.Durable.Storage" or "Orleans.Durable.Storage.Engine.Cluster.Control")));
        var expected = File.Exists(snapshot) ? File.ReadAllLines(snapshot) : [];
        if (rendered.SequenceEqual(expected, StringComparer.Ordinal))
        {
            File.Delete(received);
            return;
        }

        File.WriteAllText(received, string.Join('\n', rendered) + "\n");
        var removed = expected.Except(rendered, StringComparer.Ordinal).Select(static line => "- " + line);
        var added = rendered.Except(expected, StringComparer.Ordinal).Select(static line => "+ " + line);
        Assert.Fail(
            $"The Orleans composition's public surface differs from {snapshot}. If the change is intended, specify it in " +
            $"orleans.md §2 and §6, then replace the snapshot with {received}.\n" +
            string.Join('\n', removed.Concat(added)));
    }

    /// <summary>
    /// orleans.md §6 documents the configuration an app binds: the keys its table names are exactly the keys the silo's
    /// options bind, every public settable option of the root and its sections, a function excepted, which is set in code.
    /// </summary>
    [Fact]
    public void TheConfigurationTableNamesEveryBindableKey()
    {
        var spec = File.ReadAllText(Path.Combine(Repository.StorageRoot, "docs", "spec", "compositions", "orleans.md"));
        var start = spec.IndexOf("\n## 6. Configuration", StringComparison.Ordinal);
        var end = spec.IndexOf("\n## 7. ", start, StringComparison.Ordinal);
        var documented = spec[start..end]
            .Split('\n')
            .Where(static line => line.StartsWith('|'))
            .Select(static line => line.TrimEnd().TrimEnd('|').Split('|')[^1])
            .SelectMany(static cell => System.Text.RegularExpressions.Regex.Matches(cell, "`((?:Node|Control):[A-Za-z:]+)`").Select(static match => match.Groups[1].Value))
            .ToHashSet(StringComparer.Ordinal);
        var bindable = Keys(typeof(Orleans.Durable.Storage.Hosting.DurableStorageSiloOptions), prefix: null).ToHashSet(StringComparer.Ordinal);

        Assert.Empty(bindable.Except(documented).Select(static key => "undocumented: " + key).Concat(documented.Except(bindable).Select(static key => "binds nothing: " + key)));

        static IEnumerable<string> Keys(Type type, string? prefix) =>
            type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(static property => property.SetMethod is { IsPublic: true } && !typeof(Delegate).IsAssignableFrom(property.PropertyType))
                .SelectMany(property =>
                {
                    var key = prefix is null ? property.Name : $"{prefix}:{property.Name}";
                    var value = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
                    return value.IsClass && value != typeof(string) && value != typeof(Uri) ? Keys(value, key) : [key];
                });
    }

    /// <summary>
    /// The samples see the composition as an app does: no Storage <c>src</c> project grants them its internals, so their smoke test
    /// runs on the public surface alone.
    /// </summary>
    [Fact]
    public void NoProductionProjectGrantsTheSamplesItsInternals()
    {
        var src = Path.Combine(Repository.FindRoot(), "src");
        var grants = Repository.ProductionSourceFiles("*.cs")
            .Concat(Repository.ProductionProjectFiles())
            .Where(static file => File.ReadAllText(file).Contains("\"Orleans.Durable.Storage.Samples", StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(src, file));

        Assert.Empty(grants);
    }

    /// <summary>
    /// Returns <paramref name="roots"/> and every public type of the storage which their public properties reach, through nullable, array,
    /// and generic arguments, transitively.
    /// </summary>
    private static IEnumerable<Type> Reached(IEnumerable<Type> roots)
    {
        var reached = new HashSet<Type>(roots);
        var pending = new Queue<Type>(reached);
        while (pending.TryDequeue(out var type))
        {
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                foreach (var named in Named(property.PropertyType))
                {
                    if (named.IsPublic && named.Namespace?.StartsWith("Orleans.Durable.Storage", StringComparison.Ordinal) == true && reached.Add(named))
                    {
                        pending.Enqueue(named);
                    }
                }
            }
        }

        return reached;

        static IEnumerable<Type> Named(Type type) =>
            type.HasElementType ? Named(type.GetElementType()!)
            : type.IsGenericType ? type.GetGenericArguments().SelectMany(Named).Prepend(type.GetGenericTypeDefinition())
            : [type];
    }
}
