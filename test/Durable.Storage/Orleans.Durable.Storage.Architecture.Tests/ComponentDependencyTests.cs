using System.Reflection;
using System.Runtime.CompilerServices;
using static Orleans.Durable.Storage.Architecture.Tests.Namespaces;

namespace Orleans.Durable.Storage.Architecture.Tests;

/// <summary>
/// Pins spec README §2.5's dependency rules over components (<see cref="Components"/>) rather than projects, so they keep
/// holding when several components share an assembly. Each rule reads every type's compiled references
/// (<see cref="MetadataReferences"/>): a type uses only its own component, the components its component reaches, the
/// platform's assemblies and the technologies of what its component reaches, and another component's non-public types and
/// members only under that component's grant; a technology package uses only layer contracts; and the table itself
/// has no cycle and no layer which reaches a composition. Each rule also runs over planted violations
/// (<c>ComponentFixtures</c>), whose components the fixture table declares, and must report exactly those.
/// </summary>
public sealed class ComponentDependencyTests
{
    private const string FixtureScope = "Orleans.Durable.Storage.Architecture.Tests.ComponentFixtures";

    private static readonly Lazy<ComponentChecks> Production = new(static () => new(
        Components.Production,
        [.. Repository.ProductionAssemblies().Select(static assembly => assembly.Path)]));

    private static readonly Lazy<ComponentChecks> Fixtures = new(static () => new(
        FixtureMap,
        [typeof(ComponentDependencyTests).Assembly.Location]));

    private static ComponentMap FixtureMap
    {
        get
        {
            const string Assembly = "Orleans.Durable.Storage.Architecture.Tests";
            return new(
                [FixtureScope],
                [
                    Fixture("Base", ComponentKind.Layer, [], isContract: true) with
                    {
                        Grants = new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            [FixtureScope + ".Composition"] = "a grant the composition uses",
                            [FixtureScope + ".Decisions"] = "a grant no type uses",
                        },
                    },
                    Fixture("Contracts", ComponentKind.Layer, ["Base"], isContract: true) with { Roots = [] },
                    Fixture("Shared", ComponentKind.Layer, ["Contracts", "Base"]),
                    Fixture("Upper", ComponentKind.Layer, ["Shared", "Contracts", "Base"]),
                    Fixture("Decisions", ComponentKind.Layer, ["Upper"]),
                    Fixture("Composition", ComponentKind.Composition, ["Upper", "Decisions"]) with { Technologies = ["Microsoft.CodeAnalysis"] },
                    Fixture("ProviderA", ComponentKind.Provider, ["Contracts", "Shared"]),
                    Fixture("ProviderB", ComponentKind.Provider, ["Contracts"]),
                    Fixture("Climber", ComponentKind.Layer, ["Composition"]),
                    Fixture("CycleA", ComponentKind.Layer, ["CycleB"]),
                    Fixture("CycleB", ComponentKind.Layer, ["CycleA"]),
                    Fixture("Misplaced", ComponentKind.Layer, []) with { Assembly = "Elsewhere" },
                    Fixture("Empty", ComponentKind.Layer, []),
                ],
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [FixtureScope + ".Shared.IFixtureContract"] = FixtureScope + ".Contracts",
                    [FixtureScope + ".Shared.Missing"] = FixtureScope + ".Contracts",
                });

            static Component Fixture(string name, ComponentKind kind, string[] dependencies, bool isContract = false) =>
                new(FixtureScope + "." + name, kind, [.. dependencies.Select(static dependency => FixtureScope + "." + dependency)])
                {
                    Assembly = Assembly,
                    IsContract = isContract,
                };
        }
    }

    /// <summary>
    /// A type resolves to the component whose root is the longest which contains its namespace, so a sub-namespace, such
    /// as the WAL's storage namespaces, belongs to its layer, and a listed type to the component which lists it.
    /// </summary>
    [Theory]
    [InlineData("Orleans.Durable.Storage.Engine.Wal.IoUring", "Orleans.Durable.Storage.Engine.Wal.IoUring.IoUringEngine", "Orleans.Durable.Storage.Engine.Wal")]
    [InlineData("Orleans.Durable.Storage.Engine.Wal.Portable", "Orleans.Durable.Storage.Engine.Wal.Portable.PortableWalStorage", "Orleans.Durable.Storage.Engine.Wal")]
    [InlineData("Orleans.Durable.Storage.Engine.Ledgers.Memory", "Orleans.Durable.Storage.Engine.Ledgers.Memory.MemoryLedgerReplica", "Orleans.Durable.Storage.Engine.Ledgers")]
    [InlineData("Orleans.Durable.Storage.Engine.Ledgers", "Orleans.Durable.Storage.Engine.Ledgers.NodeId", "Orleans.Durable.Storage.Engine.Ledgers.Abstractions")]
    [InlineData("Orleans.Durable.Storage.Engine.Ledgers", "Orleans.Durable.Storage.Engine.Ledgers.NodeId+Nested", "Orleans.Durable.Storage.Engine.Ledgers.Abstractions")]
    [InlineData("Orleans.Durable.Storage.RocksDb", "Orleans.Durable.Storage.RocksDb.RocksDbJournalIndex", "Orleans.Durable.Storage.RocksDb")]
    [InlineData("Orleans.Durable.Storage.Diagnostics", "Orleans.Durable.Storage.Diagnostics.FatalErrorHandler", "Orleans.Durable.Storage.Diagnostics")]
    [InlineData("Orleans.Durable.Storage.Engine", "Orleans.Durable.Storage.Engine.Backoff", "Orleans.Durable.Storage.Engine")]
    [InlineData("Orleans.Durable.Storage.Hosting", "Orleans.Durable.Storage.Hosting.Anything", "Orleans.Durable.Storage")]
    [InlineData("Orleans.Durable.Storage.Control", "Orleans.Durable.Storage.Control.NodeControlGrain", "Orleans.Durable.Storage")]
    [InlineData("Orleans.Durable.Storage.Engine.Cluster.Control", "Orleans.Durable.Storage.Engine.Cluster.Control.Unlisted", "Orleans.Durable.Storage.Engine.Cluster.Control")]
    [InlineData("OrleansCodeGen.Orleans.Durable.Storage.Control", "OrleansCodeGen.Orleans.Durable.Storage.Control.Codec_X", "Orleans.Durable.Storage")]
    public void ATypeResolvesToTheLongestRootOrItsListing(string typeNamespace, string fullName, string component) =>
        Assert.Equal(component, Components.Production.Of(typeNamespace, fullName)?.Name);

    [Fact]
    public void EachTypeBelongsToAComponentCompiledIntoItsAssembly() =>
        AssertNone(Production.Value.Membership());

    [Fact]
    public void TheMembershipCheckFindsEachPlantedViolation() =>
        Assert.Equal(
            [
                "Empty declares no type",
                "Misplaced.MisplacedType belongs to Misplaced, which Elsewhere compiles, but Orleans.Durable.Storage.Architecture.Tests declares it",
                "Orphans.Orphan belongs to no component",
                "Shared.Missing is listed for Contracts, but Orleans.Durable.Storage.Architecture.Tests does not declare it",
            ],
            Strip(Fixtures.Value.Membership()));

    /// <summary>"Dependencies point down only" (spec README §2.5): a type uses only the components its component reaches.</summary>
    [Fact]
    public void TypesUseOnlyTheComponentsTheirComponentReaches() =>
        AssertNone(Production.Value.Dependencies());

    [Fact]
    public void TheDependencyCheckFindsEachPlantedViolation() =>
        Assert.Equal(
            [
                "Base.UsesUpper -> Upper.UpperType (Base -> Upper)",
                "ProviderB.UsesProviderA -> ProviderA.UsesImplementation (ProviderB -> ProviderA)",
            ],
            Strip(Fixtures.Value.Dependencies()));

    /// <summary>
    /// "Technology packages ... depend only on layer contracts" (spec README §2.5): a provider's type uses no type of a
    /// component which is not a contract, so neither another provider nor a layer's implementation.
    /// </summary>
    [Fact]
    public void ProviderTypesUseOnlyLayerContracts() =>
        AssertNone(Production.Value.ProviderReferences());

    [Fact]
    public void TheProviderCheckFindsEachPlantedViolation() =>
        Assert.Equal(
            [
                "ProviderA.UsesImplementation -> Shared.SharedImplementation (ProviderA -> Shared)",
                "ProviderB.UsesProviderA -> ProviderA.UsesImplementation (ProviderB -> ProviderA)",
            ],
            Strip(Fixtures.Value.ProviderReferences()));

    /// <summary>
    /// A type uses only the platform's assemblies and the technologies of its component and of what it reaches: Orleans
    /// stays in the composition (build-packaging.md §2), so the controllers' decisions name no Orleans type, RocksDB in
    /// the state database, each cloud SDK in its technology package, and the shared base uses the platform alone.
    /// </summary>
    [Fact]
    public void TypesUseOnlyThePlatformAndTheTechnologiesTheirComponentReaches() =>
        AssertNone(Production.Value.TechnologyReferences());

    [Fact]
    public void TheTechnologyCheckFindsEachPlantedViolation() =>
        Assert.Equal(["Decisions.UsesRoslyn -> Microsoft.CodeAnalysis"], Strip(Fixtures.Value.TechnologyReferences()));

    /// <summary>
    /// A type uses another component's non-public type or member only under that component's grant, so <c>internal</c>
    /// keeps separating components which share an assembly. Every use resolves to its declaration, by signature, so an
    /// internal overload beside a public one is told apart; an unresolved use is reported.
    /// </summary>
    [Fact]
    public void TypesUseAnotherComponentsInternalsOnlyUnderItsGrant() =>
        AssertNone(Production.Value.InternalsViolations());

    [Fact]
    public void TheInternalsCheckFindsEachPlantedViolation() =>
        Assert.Equal(
            [
                "Upper.UpperDerived -> Base.FixtureBase.Hidden`0()Int32 (Upper -> Base)",
                "Upper.UsesInternals -> Base.FixtureInternal (Upper -> Base)",
                "Upper.UsesInternals -> Base.FixtureInternal.get_Value`0()Int32 (Upper -> Base)",
                "Upper.UsesInternals -> Base.FixtureOverloads.Secret:Object (Upper -> Base)",
                "Upper.UsesInternals -> Base.FixtureOverloads.Use`0(String)String (Upper -> Base)",
            ],
            Strip(Fixtures.Value.InternalsViolations()));

    /// <summary>Each grant is used, so the grants name exactly the components which reach another's internals.</summary>
    [Fact]
    public void EachGrantIsUsed() =>
        AssertNone(Production.Value.UnusedGrants());

    [Fact]
    public void TheGrantCheckFindsAnUnusedGrant() =>
        Assert.Equal(["Base grants Decisions"], Strip(Fixtures.Value.UnusedGrants()));

    /// <summary>
    /// A grant lies within one assembly, where <c>internal</c> no longer separates components, or goes to the composition
    /// through an <c>InternalsVisibleTo</c> of the owner's assembly; no technology package holds one, so each provider
    /// uses only the Engine's public contracts. Each <c>InternalsVisibleTo</c> between production assemblies goes to the
    /// composition and backs a grant.
    /// </summary>
    [Fact]
    public void EachGrantLiesWithinOneAssemblyOrGoesToTheComposition() =>
        AssertNone(ComponentChecks.GrantViolations(Components.Production, static assembly => Assembly.Load(assembly)
            .GetCustomAttributes<InternalsVisibleToAttribute>()
            .Select(static attribute => attribute.AssemblyName.Split(',')[0].Trim())));

    [Fact]
    public void TheGrantPlacementCheckFindsEachPlantedViolation()
    {
        var map = new ComponentMap(
            [FixtureScope],
            [
                new(FixtureScope + ".Base", ComponentKind.Layer, [])
                {
                    Assembly = "Engine",
                    Grants = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [FixtureScope + ".Inner"] = "a grant within the assembly",
                        [FixtureScope + ".Provider"] = "a provider's grant",
                        [FixtureScope + ".Composition"] = "a composition's grant, backed",
                        [FixtureScope + ".Unbacked"] = "a composition's grant, unbacked",
                    },
                },
                new(FixtureScope + ".Inner", ComponentKind.Layer, [FixtureScope + ".Base"]) { Assembly = "Engine" },
                new(FixtureScope + ".Provider", ComponentKind.Provider, [FixtureScope + ".Base"]) { Assembly = "Provider" },
                new(FixtureScope + ".Composition", ComponentKind.Composition, [FixtureScope + ".Base"]) { Assembly = "Composition" },
                new(FixtureScope + ".Unbacked", ComponentKind.Composition, [FixtureScope + ".Base"]) { Assembly = "Unbacked" },
                new(FixtureScope + ".Spare", ComponentKind.Composition, [FixtureScope + ".Base"]) { Assembly = "Spare" },
            ],
            new Dictionary<string, string>(StringComparer.Ordinal));
        var internalsVisibleTo = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["Engine"] = ["Composition", "Provider", "Spare", "Engine.Tests"],
        };

        Assert.Equal(
            [
                "Base grants Provider, which Provider compiles apart from Engine: only a composition holds a grant across assemblies",
                "Base grants Unbacked, but Engine has no InternalsVisibleTo Unbacked",
                "Engine has InternalsVisibleTo Provider, which compiles no composition",
                "Engine has InternalsVisibleTo Spare, which no grant needs",
            ],
            Strip(ComponentChecks.GrantViolations(map, assembly => internalsVisibleTo.GetValueOrDefault(assembly, []))));
    }
    /// <summary>
    /// The component graph has no cycle; no layer reaches a composition ("Compositions depend on layers; layers depend
    /// on no composition", spec README §2.5); and each technology package depends only on contracts.
    /// </summary>
    [Fact]
    public void TheComponentTableIsLayered() =>
        AssertNone(ComponentChecks.TableViolations(Components.Production));

    [Fact]
    public void TheTableCheckFindsEachPlantedViolation() =>
        Assert.Equal(
            [
                "Climber reaches the composition Composition",
                "CycleA reaches itself",
                "CycleB reaches itself",
                "ProviderA depends on Shared, which is not a contract",
            ],
            Strip(ComponentChecks.TableViolations(FixtureMap)));

    /// <summary>
    /// The shared base reaches no layer (spec README §2.2, §2.5), and the controllers' decisions
    /// reach no Orleans technology.
    /// </summary>
    [Fact]
    public void TheBasesReachNoLayerAndTheDecisionsNoOrleans()
    {
        var map = Components.Production;

        Assert.Empty(map.Reachable("Orleans.Durable.Storage.Diagnostics"));
        Assert.Empty(map.ByName["Orleans.Durable.Storage.Diagnostics"].Technologies);
        Assert.Equal(["Orleans.Durable.Storage.Diagnostics"], map.Reachable("Orleans.Durable.Storage.Engine"));
        Assert.False(map.Takes("Orleans.Durable.Storage.Engine.Cluster.Control", "Orleans.Core"));
        Assert.DoesNotContain("Orleans.Durable.Storage", map.Reachable("Orleans.Durable.Storage.Engine.Cluster.Control"));
    }

    /// <summary>
    /// The controllers' decisions, which the Engine compiles beside the layers they decide over, use no Orleans assembly:
    /// beyond the platform, only the fatal-error base's and their layers' technologies, so a simulation of them loads no
    /// Orleans assembly (orleans.md §2, verification.md §7.1).
    /// </summary>
    [Fact]
    public void TheDecisionsUseNoAssemblyBeyondTheirLayers() =>
        AssertNone(Production.Value.ExternalAssemblies("Orleans.Durable.Storage.Engine.Cluster.Control")
            .Except([Components.Diagnostics, "Polly.Core", "System.IO.Hashing"], StringComparer.Ordinal)
            .Select(static assembly => "Orleans.Durable.Storage.Engine.Cluster.Control -> " + assembly));

    [Fact]
    public void TheDecisionsCheckFindsThePlantedViolation() =>
        Assert.Equal(["Microsoft.CodeAnalysis"], Fixtures.Value.ExternalAssemblies(FixtureScope + ".Decisions"));
    /// <summary>Fails with every violation, one per line, which a collection assertion would truncate.</summary>
    private static void AssertNone(IEnumerable<string> violations)
    {
        var listed = violations.Order(StringComparer.Ordinal).ToArray();
        if (listed.Length > 0)
        {
            Assert.Fail(string.Join(Environment.NewLine, listed));
        }
    }

    private static string[] Strip(IEnumerable<string> violations) =>
        [.. violations.Select(static violation => violation.Replace(FixtureScope + ".", string.Empty, StringComparison.Ordinal)).Order(StringComparer.Ordinal)];
}

/// <summary>The component rules over a table and the assemblies which compile its components.</summary>
internal sealed class ComponentChecks
{
    private static readonly string PlatformDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location)!;

    private readonly ComponentMap _map;
    private readonly IReadOnlyList<(string Assembly, ScannedType Type, Component? Component)> _types;
    private readonly Dictionary<string, DeclaredType> _declaredTypes = [with(StringComparer.Ordinal)];
    private readonly Dictionary<MemberUse, bool> _declaredMembers = [];

    public ComponentChecks(ComponentMap map, IReadOnlyList<string> assemblyPaths)
    {
        _map = map;
        _types =
        [
            .. assemblyPaths.SelectMany(path => MetadataReferences.Scan(path, map.IsInScope)
                .Select(type => (Path.GetFileNameWithoutExtension(path), type, map.Of(type.Namespace, type.FullName)))),
        ];
        foreach (var path in assemblyPaths)
        {
            var access = MetadataReferences.Accessibility(path);
            foreach (var (name, type) in access.Types.Where(entry => IsInScope(entry.Key)))
            {
                _declaredTypes.Add(name, type);
            }

            foreach (var (member, isVisible) in access.Members.Where(entry => IsInScope(entry.Key.DeclaringType)))
            {
                _declaredMembers.Add(member, isVisible);
            }
        }
    }

    /// <summary>Types outside every component or in another assembly than their component's, stale listings, and components without types.</summary>
    public IEnumerable<string> Membership()
    {
        var declared = _types.Where(static entry => !entry.Type.FullName.Contains('+', StringComparison.Ordinal)).ToArray();
        var misplaced = declared.Select(static entry => entry.Component is null
            ? $"{entry.Type.FullName} belongs to no component"
            : entry.Component.Assembly != entry.Assembly
                ? $"{entry.Type.FullName} belongs to {entry.Component.Name}, which {entry.Component.Assembly} compiles, but {entry.Assembly} declares it"
                : null);
        var stale = _map.Members
            .Where(member => !declared.Any(entry => entry.Type.FullName == member.Key && entry.Assembly == _map.ByName[member.Value].Assembly))
            .Select(member => $"{member.Key} is listed for {member.Value}, but {_map.ByName[member.Value].Assembly} does not declare it");
        var empty = _map.ByName.Keys
            .Where(name => !declared.Any(entry => entry.Component?.Name == name))
            .Select(static name => $"{name} declares no type");
        return misplaced.OfType<string>().Concat(stale).Concat(empty).Distinct(StringComparer.Ordinal);
    }

    /// <summary>Each use of a type of another component which the user's component does not reach, or of a type of no component.</summary>
    public IEnumerable<string> Dependencies() =>
        ForeignReferences().Select(reference => reference.Target is null
                ? $"{reference.User} -> {reference.Type} (no component)"
                : !_map.Reachable(reference.Source.Name).Contains(reference.Target.Name)
                    ? $"{reference.User} -> {reference.Type} ({reference.Source.Name} -> {reference.Target.Name})"
                    : null)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal);

    /// <summary>Each use by a provider's type of a type of another component which is not a contract.</summary>
    public IEnumerable<string> ProviderReferences() =>
        ForeignReferences()
            .Where(static reference => reference.Source.Kind == ComponentKind.Provider && reference.Target is { IsContract: false })
            .Select(static reference => $"{reference.User} -> {reference.Type} ({reference.Source.Name} -> {reference.Target!.Name})")
            .Distinct(StringComparer.Ordinal);

    /// <summary>Each external assembly a type uses which is neither the platform's nor a technology its component reaches.</summary>
    public IEnumerable<string> TechnologyReferences()
    {
        var componentAssemblies = _map.ByName.Values.Select(static component => component.Assembly).ToHashSet(StringComparer.Ordinal);
        return _types
            .Where(static entry => entry.Component is not null)
            .SelectMany(entry => entry.Type.AssemblyTypes
                .Select(static reference => reference.Assembly)
                .Where(assembly => !componentAssemblies.Contains(assembly)
                    && !File.Exists(Path.Combine(PlatformDirectory, assembly + ".dll"))
                    && !_map.Takes(entry.Component!.Name, assembly))
                .Select(assembly => $"{Outermost(entry.Type.FullName)} -> {assembly}"))
            .Distinct(StringComparer.Ordinal);
    }

    /// <summary>The assemblies other than the platform's and its own which a component's types use.</summary>
    public IEnumerable<string> ExternalAssemblies(string component) =>
        _types
            .Where(entry => entry.Component?.Name == component)
            .SelectMany(entry => entry.Type.AssemblyTypes
                .Select(static reference => reference.Assembly)
                .Where(assembly => assembly != entry.Assembly && !File.Exists(Path.Combine(PlatformDirectory, assembly + ".dll"))))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);
    /// <summary>Each use of another component's non-public type or member without its grant, and each use which resolves to no declaration.</summary>
    public IEnumerable<string> InternalsViolations() =>
        InternalUses()
            .Where(use => use.Target is null || !use.Target.Grants.ContainsKey(use.Source.Name))
            .Select(static use => use.Target is null
                ? $"{use.User} -> {use.Used} is declared by no scanned assembly"
                : $"{use.User} -> {use.Used} ({use.Source.Name} -> {use.Target.Name})")
            .Distinct(StringComparer.Ordinal);

    /// <summary>Each grant which no use of a non-public type or member needs.</summary>
    public IEnumerable<string> UnusedGrants()
    {
        var used = InternalUses()
            .Where(static use => use.Target is not null)
            .Select(static use => (Owner: use.Target!.Name, User: use.Source.Name))
            .ToHashSet();
        return _map.ByName.Values
            .SelectMany(owner => owner.Grants.Keys
                .Where(user => !used.Contains((owner.Name, user)))
                .Select(user => $"{owner.Name} grants {user}"));
    }

    /// <summary>
    /// Each grant across assemblies which is not a composition's, or which no <c>InternalsVisibleTo</c> backs, and each
    /// <c>InternalsVisibleTo</c> between the table's assemblies which goes to no composition or backs no grant.
    /// </summary>
    public static IEnumerable<string> GrantViolations(ComponentMap map, Func<string, IEnumerable<string>> internalsVisibleTo)
    {
        var assemblies = map.ByName.Values.Select(static component => component.Assembly).ToHashSet(StringComparer.Ordinal);
        var granted = new HashSet<(string Owner, string User)>();
        foreach (var owner in map.ByName.Values.OrderBy(static component => component.Name, StringComparer.Ordinal))
        {
            foreach (var user in owner.Grants.Keys.Order(StringComparer.Ordinal).Select(name => map.ByName[name]).Where(user => user.Assembly != owner.Assembly))
            {
                if (user.Kind != ComponentKind.Composition)
                {
                    yield return $"{owner.Name} grants {user.Name}, which {user.Assembly} compiles apart from {owner.Assembly}: only a composition holds a grant across assemblies";
                }
                else if (!internalsVisibleTo(owner.Assembly).Contains(user.Assembly, StringComparer.Ordinal))
                {
                    yield return $"{owner.Name} grants {user.Name}, but {owner.Assembly} has no InternalsVisibleTo {user.Assembly}";
                }

                granted.Add((owner.Assembly, user.Assembly));
            }
        }

        foreach (var owner in assemblies.Order(StringComparer.Ordinal))
        {
            foreach (var user in internalsVisibleTo(owner).Where(user => user != owner && assemblies.Contains(user)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                if (!map.ByName.Values.Any(component => component.Assembly == user && component.Kind == ComponentKind.Composition))
                {
                    yield return $"{owner} has InternalsVisibleTo {user}, which compiles no composition";
                }
                else if (!granted.Contains((owner, user)))
                {
                    yield return $"{owner} has InternalsVisibleTo {user}, which no grant needs";
                }
            }
        }
    }
    /// <summary>The table's cycles, its layers which reach a composition, and its providers' dependencies which are not contracts.</summary>
    public static IEnumerable<string> TableViolations(ComponentMap map)
    {
        foreach (var component in map.ByName.Values.OrderBy(static component => component.Name, StringComparer.Ordinal))
        {
            var reachable = map.Reachable(component.Name);
            if (reachable.Contains(component.Name))
            {
                yield return $"{component.Name} reaches itself";
            }

            if (component.Kind != ComponentKind.Composition)
            {
                foreach (var composition in reachable.Where(name => map.ByName[name].Kind == ComponentKind.Composition).Order(StringComparer.Ordinal))
                {
                    yield return $"{component.Name} reaches the composition {composition}";
                }
            }

            if (component.Kind == ComponentKind.Provider)
            {
                foreach (var dependency in component.Dependencies.Where(name => !map.ByName[name].IsContract))
                {
                    yield return $"{component.Name} depends on {dependency}, which is not a contract";
                }
            }
        }
    }

    private static string Outermost(string fullName) => fullName.Split('+')[0];

    private static string NamespaceOf(string fullName)
    {
        var outermost = Outermost(fullName);
        var lastDot = outermost.LastIndexOf('.');
        return lastDot < 0 ? string.Empty : outermost[..lastDot];
    }

    private bool IsInScope(string fullName) => _map.IsInScope(NamespaceOf(fullName));

    private Component? ComponentOf(string fullName) => _map.Of(NamespaceOf(fullName), fullName);

    /// <summary>Each in-scope type a component's type references outside its own component, with the referenced type's component, if any.</summary>
    private IEnumerable<(string User, string Type, Component Source, Component? Target)> ForeignReferences() =>
        _types
            .Where(static entry => entry.Component is not null)
            .SelectMany(entry => entry.Type.Types
                .Where(reference => _map.IsInScope(reference.Namespace))
                .Select(reference => (User: Outermost(entry.Type.FullName), Type: reference.FullName, Source: entry.Component!, Target: _map.Of(reference.Namespace, reference.FullName)))
                .Where(static reference => reference.Target != reference.Source));

    /// <summary>
    /// Each use by a component's type of another component's non-public type or member, or of a type or member no scanned
    /// assembly declares, with the owner, when one is known.
    /// </summary>
    private IEnumerable<(string User, string Used, Component Source, Component? Target)> InternalUses()
    {
        foreach (var (_, type, source) in _types)
        {
            if (source is null)
            {
                continue;
            }

            var user = Outermost(type.FullName);
            foreach (var reference in type.Types.Where(reference => _map.IsInScope(reference.Namespace)).Select(static reference => reference.FullName).Distinct(StringComparer.Ordinal))
            {
                var target = ComponentOf(reference);
                if (target != source && !(_declaredTypes.TryGetValue(reference, out var declared) && declared.IsVisible))
                {
                    yield return (user, reference, source, _declaredTypes.ContainsKey(reference) ? target : null);
                }
            }

            foreach (var member in type.MemberUses.Where(member => IsInScope(member.DeclaringType)))
            {
                var target = ComponentOf(member.DeclaringType);
                if (target != source && IsVisible(member) is not true)
                {
                    yield return (user, member.ToString(), source, IsVisible(member) is null ? null : target);
                }
            }
        }
    }

    /// <summary>
    /// Whether a member's declaration is visible outside its assembly: the member's own, or an inherited one through the
    /// base types; true for a member inherited from a type outside the scope, which no component declares; null when
    /// no scanned assembly declares it.
    /// </summary>
    private bool? IsVisible(MemberUse member)
    {
        for (var declaring = member.DeclaringType; ;)
        {
            if (!IsInScope(declaring))
            {
                return true;
            }

            if (_declaredMembers.TryGetValue(member with { DeclaringType = declaring }, out var isVisible))
            {
                return isVisible;
            }

            if (!_declaredTypes.TryGetValue(declaring, out var type) || type.BaseType is null)
            {
                return null;
            }

            declaring = type.BaseType;
        }
    }
}
