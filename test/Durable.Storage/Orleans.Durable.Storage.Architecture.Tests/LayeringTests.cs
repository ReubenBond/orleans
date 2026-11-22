using System.Text.Json;
using System.Xml.Linq;
using static Orleans.Durable.Storage.Architecture.Tests.Namespaces;

namespace Orleans.Durable.Storage.Architecture.Tests;

public sealed class LayeringTests
{
    /// <summary>The production projects, each with the components it compiles (<see cref="Components"/>).</summary>
    private static readonly ILookup<string, Component> ProjectComponents =
        Components.Production.ByName.Values.ToLookup(static component => component.Assembly, StringComparer.Ordinal);

    /// <summary>
    /// The fuzz harness of verification.md §6.1: each layer's targets reference the project which compiles that layer
    /// and the shared oracle framework, so the layer's tests replay them without reaching a higher layer (spec README
    /// §2.5), and the campaign executable gathers every layer's targets.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string[]> FuzzReferences =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["Orleans.Durable.Storage.Fuzz"] = ["Orleans.Durable.Storage.Fuzz.Archive", "Orleans.Durable.Storage.Fuzz.Archive.Logs", "Orleans.Durable.Storage.Fuzz.Journals", "Orleans.Durable.Storage.Fuzz.Ledgers", "Orleans.Durable.Storage.Fuzz.MetaStore", "Orleans.Durable.Storage.Fuzz.Orleans", "Orleans.Durable.Storage.Fuzz.Wal"],
            ["Orleans.Durable.Storage.Fuzz.Abstractions"] = [],
            ["Orleans.Durable.Storage.Fuzz.Archive"] = ["Orleans.Durable.Storage.Fuzz.Abstractions", Components.Engine],

            // Archive logs' token codec and Azure's metadata reader (archive-logs.md §10).
            ["Orleans.Durable.Storage.Fuzz.Archive.Logs"] = ["Orleans.Durable.Storage.Fuzz.Abstractions", Components.Engine, Components.AzureStorage],
            ["Orleans.Durable.Storage.Fuzz.Journals"] = ["Orleans.Durable.Storage.Fuzz.Abstractions", Components.Engine],
            ["Orleans.Durable.Storage.Fuzz.Ledgers"] = ["Orleans.Durable.Storage.Fuzz.Abstractions", Components.Engine],
            ["Orleans.Durable.Storage.Fuzz.MetaStore"] = ["Orleans.Durable.Storage.Fuzz.Abstractions", Components.FileSystem],
            ["Orleans.Durable.Storage.Fuzz.Orleans"] = ["Orleans.Durable.Storage.Fuzz.Abstractions", Components.Composition],
            ["Orleans.Durable.Storage.Fuzz.Wal"] = ["Orleans.Durable.Storage.Fuzz.Abstractions", Components.Engine],
        };

    /// <summary>
    /// The projects which compile the compositions of spec README §2.3 and §2.6, to which the composition rules below
    /// apply.
    /// </summary>
    internal static readonly HashSet<string> Compositions =
        [.. Components.Production.ByName.Values.Where(static component => component.Kind == ComponentKind.Composition).Select(static component => component.Assembly)];

    /// <summary>
    /// The projects which take Orleans' runtime: the compositions', and those of the technology packages Orleans hosts
    /// (<see cref="Component.HostsOnOrleans"/>).
    /// </summary>
    internal static readonly HashSet<string> OrleansHosts =
        [.. Components.Production.ByName.Values.Where(static component => component.Kind == ComponentKind.Composition || component.HostsOnOrleans).Select(static component => component.Assembly)];

    /// <summary>
    /// The projects which compile the Contracts column of spec README §2.6 for every layer in code.
    /// </summary>
    private static readonly HashSet<string> LayerContracts =
        [.. Components.Production.ByName.Values.Where(static component => component.IsContract).Select(static component => component.Assembly)];

    /// <summary>
    /// The packages and projects whose code generators write serializers: Orleans' for grain messages, as packages or as
    /// this repository's code generator project, and Protocol Buffers and gRPC.
    /// </summary>
    private static readonly string[] SerializationPackagePrefixes = ["Orleans.CodeGenerator", "Google.Protobuf", "Grpc."];

    private static readonly string[] ImplicitBuildFiles = ["Directory.Build.props", "Directory.Build.targets"];

    /// <summary>
    /// The families which share Orleans' prefix without being Orleans' runtime: Clog's own assemblies, Fast CASPaxos,
    /// which the MetaStore's provider takes, and the simulation framework, which the simulation tests and Fast
    /// CASPaxos' testing take.
    /// </summary>
    private static readonly string[] OrleansSiblingFamilies = ["Durable.Storage", "Consensus.CasPaxos", "Testing.Simulation"];

    /// <summary>
    /// Each project under src compiles the components the table assigns it, references only the projects which compile
    /// its components' dependencies and the projects outside src it builds from, and reaches, through those, every
    /// project which compiles a component its components reach (spec README §2.5).
    /// </summary>
    [Fact]
    public void ProductionProjectReferencesFollowLayerAllowList()
    {
        var map = Components.Production;
        var projectFiles = ProductionProjectFiles();

        Assert.Equal(
            ProjectComponents.Select(static group => group.Key).Order(StringComparer.Ordinal),
            projectFiles.Keys.Order(StringComparer.Ordinal));

        var violations = new List<string>();
        foreach (var (project, projectFile) in projectFiles)
        {
            var components = ProjectComponents[project].ToArray();
            var allowed = components
                .SelectMany(static component => component.Dependencies)
                .Select(dependency => map.ByName[dependency].Assembly)
                .Concat(components.SelectMany(static component => component.ExternalProjects))
                .Where(name => name != project)
                .ToHashSet(StringComparer.Ordinal);
            var required = components
                .SelectMany(component => map.Reachable(component.Name))
                .Select(dependency => map.ByName[dependency].Assembly)
                .Where(name => name != project)
                .ToHashSet(StringComparer.Ordinal);
            var reached = Reachable(project);

            // The compositions and the technology packages Orleans hosts take Orleans' runtime, which other tests confine to
            // them, by project reference.
            violations.AddRange(ProjectReferences(projectFile)
                .Where(reference => !allowed.Contains(reference) && !(OrleansHosts.Contains(project) && IsOrleans(reference, "Orleans.")))
                .Select(reference => $"{project} -> {reference} (not a dependency)"));
            violations.AddRange(required.Where(assembly => !reached.Contains(assembly)).Select(assembly => $"{project} does not reach {assembly}"));
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void FuzzProjectReferencesFollowTheirAllowList()
    {
        var projectFiles = FuzzProjectFiles();

        Assert.Equal(
            FuzzReferences.Keys.OrderBy(static name => name, StringComparer.Ordinal),
            projectFiles.Keys.Order(StringComparer.Ordinal));
        foreach (var (project, projectFile) in projectFiles)
        {
            Assert.Equal(
                FuzzReferences[project].OrderBy(static name => name, StringComparer.Ordinal),
                ProjectReferences(projectFile).OrderBy(static name => name, StringComparer.Ordinal));
        }
    }

    /// <summary>
    /// Each layer's fuzz targets use the types of the components whose formats they decode (the layer's components, or
    /// for the MetaStore's register file the file-system technology package) and of what those components reach, and
    /// the shared oracle framework only (verification.md §6.1). The check reads each target assembly's compiled
    /// references, since several layers share the Engine's assembly.
    /// </summary>
    [Theory]
    [InlineData("Orleans.Durable.Storage.Fuzz.Archive", "Orleans.Durable.Storage.Engine.Archive.Lsm")]
    [InlineData("Orleans.Durable.Storage.Fuzz.Archive.Logs", "Orleans.Durable.Storage.Engine.Archive.Logs", "Orleans.Durable.Storage.AzureStorage")]
    [InlineData("Orleans.Durable.Storage.Fuzz.Journals", "Orleans.Durable.Storage.Engine.Journals")]
    [InlineData("Orleans.Durable.Storage.Fuzz.Wal", "Orleans.Durable.Storage.Engine.Wal")]
    [InlineData("Orleans.Durable.Storage.Fuzz.Ledgers", "Orleans.Durable.Storage.Engine.Ledgers", "Orleans.Durable.Storage.Engine.Ledgers.Store")]
    [InlineData("Orleans.Durable.Storage.Fuzz.MetaStore", "Orleans.Durable.Storage.FileSystem")]
    [InlineData("Orleans.Durable.Storage.Fuzz.Orleans", "Orleans.Durable.Storage")]
    public void FuzzTargetsReachOnlyTheComponentsTheyDecode(string targets, params string[] decoded)
    {
        var map = Components.Production;
        var allowed = decoded.SelectMany(component => map.Reachable(component).Append(component)).ToHashSet(StringComparer.Ordinal);
        var assembly = Repository.OutputAssembly(FuzzProjectFiles()[targets]);
        Assert.True(File.Exists(assembly), $"{assembly} does not exist.");

        Assert.Empty(MetadataReferences.Scan(assembly, static typeNamespace => IsWithin(typeNamespace, "Orleans.Durable.Storage.Fuzz"))
            .SelectMany(type => type.Types
                .Select(reference => (Type: type.FullName, Reference: reference.FullName, Component: map.Of(reference.Namespace, reference.FullName)))
                .Where(reference => reference.Component is not null && !allowed.Contains(reference.Component.Name) && !IsWithin(reference.Reference, "Orleans.Durable.Storage.Fuzz"))
                .Select(static reference => $"{reference.Type} -> {reference.Reference} ({reference.Component!.Name})"))
            .Distinct(StringComparer.Ordinal));
        Assert.Equal(
            decoded.SelectMany(component => map.Reachable(component).Append(component)).Select(component => map.ByName[component].Assembly).Append("Orleans.Durable.Storage.Fuzz.Abstractions").Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal),
            Reachable(targets).Where(static project => project.StartsWith("Orleans.Durable.Storage", StringComparison.Ordinal)).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// The controllers' decisions name no Orleans type, so deterministic simulation runs them outside the grains which
    /// host them (orleans.md §2, verification §7): the Engine, which compiles them, takes no Orleans package, directly,
    /// through a project, or through its whole restore graph, and the decisions reach no composition.
    /// </summary>
    [Fact]
    public void TheEngineWhichCompilesTheDecisionsTakesNoOrleans()
    {
        var map = Components.Production;
        var projectFiles = ProductionProjectFiles();
        var engine = map.ByName["Orleans.Durable.Storage.Engine.Cluster.Control"].Assembly;

        Assert.Equal(Components.Engine, engine);
        foreach (var project in Reachable(engine).Append(engine).Where(projectFiles.ContainsKey))
        {
            var file = projectFiles[project];
            Assert.DoesNotContain(PackageReferences(file), static package => package.Contains("Orleans", StringComparison.Ordinal));
            Assert.DoesNotContain(RestoredLibraries(Repository.AssetsFile(file)), static library => IsOrleans(library, "Microsoft.Orleans.") || IsOrleans(library, "Orleans."));
        }

        Assert.DoesNotContain("Orleans.Durable.Storage", map.Reachable("Orleans.Durable.Storage.Engine.Cluster.Control"));
    }

    /// <summary>
    /// The shared base lies beneath every layer: its project, which compiles the fatal-error types alone, references no
    /// project and no package (spec README §2.5).
    /// </summary>
    [Fact]
    public void TheSharedBaseReferencesNoProjectOrPackage()
    {
        var project = ProductionProjectFiles()[Components.Production.ByName["Orleans.Durable.Storage.Diagnostics"].Assembly];

        Assert.Equal(["Orleans.Durable.Storage.Diagnostics"], ProjectComponents[Components.Diagnostics].Select(static component => component.Name));
        Assert.Empty(ProjectReferences(project));
        Assert.Empty(PackageReferences(project));
    }
    /// <summary>
    /// The MetaStore is its own building block beneath Ledgers (spec README §2.2, metastore.md §1): its contract
    /// reaches only the buffers beneath every layer, in which an entry owns its value (conventions.md §1), and no
    /// MetaStore component reaches a layer which uses it.
    /// </summary>
    [Theory]
    [InlineData("Orleans.Durable.Storage.Engine.MetaStore")]
    [InlineData("Orleans.Durable.Storage.Engine.MetaStore.Abstractions")]
    public void MetaStoreReachesNoOtherLayer(string project) =>
        Assert.All(Components.Production.Reachable(project), static reference => Assert.Contains(reference, (string[])["Orleans.Durable.Storage.Engine.Buffers", "Orleans.Durable.Storage.Engine.MetaStore.Abstractions"]));

    /// <summary>
    /// The group committer lies beneath the layers which bind it, reaching the buffers and, of the threading project,
    /// only the admission gate beneath every layer (spec README §2.5, conventions.md §6). The check reads the compiled
    /// metadata, so a new use of another threading type fails it.
    /// </summary>
    [Fact]
    public void TheGroupCommitterUsesOnlyTheAdmissionGateOfThreading()
    {
        var map = Components.Production;
        var groupCommit = map.ByName["Orleans.Durable.Storage.Engine.GroupCommit"];

        Assert.Equal(
            ["Orleans.Durable.Storage.Engine.Threading.AdmissionGate"],
            Repository.ProductionAssemblies()
                .SelectMany(assembly => MetadataReferences.Scan(assembly.Path, map.IsInScope))
                .Where(type => map.Of(type.Namespace, type.FullName) == groupCommit)
                .SelectMany(static type => type.Types)
                .Where(reference => map.Of(reference.Namespace, reference.FullName)?.Name == "Orleans.Durable.Storage.Engine.Threading")
                .Select(static reference => reference.FullName.Split('+')[0])
                .Distinct(StringComparer.Ordinal));
    }

    /// <summary>
    /// "no type in a <c>Orleans.Durable.Storage.Engine.Ledgers</c> namespace outside <c>Orleans.Durable.Storage.Engine.Ledgers.Memory</c> references a type in
    /// <c>Orleans.Durable.Storage.Engine.Ledgers.Memory</c>, no type in a <c>Orleans.Durable.Storage.Engine.ObjectStore</c> namespace outside <c>Orleans.Durable.Storage.Engine.ObjectStore.Memory</c>
    /// references a type in <c>Orleans.Durable.Storage.Engine.ObjectStore.Memory</c>, and no type in a <c>Orleans.Durable.Storage.Engine.Archive</c> namespace outside
    /// <c>Orleans.Durable.Storage.Engine.Archive.Memory</c> references a type in <c>Orleans.Durable.Storage.Engine.Archive.Memory</c>" (spec README §2.5): each memory provider shares its project with
    /// its layer, which reaches it only through the layer's contract. The check reads the compiled metadata of every
    /// production assembly, as the plane checks do (<see cref="MetadataReferences"/>), and fails if the provider's
    /// namespace declares no type, so a rename cannot retire it.
    /// </summary>
    [Theory]
    [InlineData("Orleans.Durable.Storage.Engine.Ledgers", "Orleans.Durable.Storage.Engine.Ledgers.Memory")]
    [InlineData("Orleans.Durable.Storage.Engine.ObjectStore", "Orleans.Durable.Storage.Engine.ObjectStore.Memory")]
    [InlineData("Orleans.Durable.Storage.Engine.Archive", "Orleans.Durable.Storage.Engine.Archive.Memory")]
    [InlineData("Orleans.Durable.Storage.Engine.Journals", "Orleans.Durable.Storage.Engine.Journals.Memory")]
    public void NoLayerTypeOutsideItsMemoryProviderReferencesIt(string layer, string memoryProvider)
    {
        var layerTypes = Repository.ProductionAssemblies()
            .SelectMany(assembly => MetadataReferences.Scan(assembly.Path, typeNamespace => IsWithin(typeNamespace, layer)))
            .ToArray();

        Assert.Contains(layerTypes, type => IsWithin(type.Namespace, memoryProvider));
        Assert.Empty(layerTypes
            .Where(type => !IsWithin(type.Namespace, memoryProvider))
            .SelectMany(type => type.Types
                .Where(reference => IsWithin(reference.Namespace, memoryProvider))
                .Select(reference => $"{type.FullName} -> {reference.FullName} ({reference.Site})")));
    }

    /// <summary>
    /// The WAL's core uses its storage contracts, and each concrete storage lives in its own namespace (wal.md §9).
    /// File-system helpers are shared by the real providers and the file-system components above the WAL.
    /// </summary>
    [Theory]
    [InlineData("Orleans.Durable.Storage.Engine.Wal.FileSystem")]
    [InlineData("Orleans.Durable.Storage.Engine.Wal.Portable")]
    [InlineData("Orleans.Durable.Storage.Engine.Wal.IoUring")]
    [InlineData("Orleans.Durable.Storage.Engine.Wal.Simulation")]
    public void TheWalCoreReachesStorageOnlyThroughItsContracts(string storageNamespace)
    {
        var map = Components.Production;
        var wal = map.ByName["Orleans.Durable.Storage.Engine.Wal"];
        var types = Repository.ProductionAssemblies()
            .SelectMany(assembly => MetadataReferences.Scan(assembly.Path, map.IsInScope))
            .Where(type => map.Of(type.Namespace, type.FullName) == wal)
            .ToArray();

        Assert.Contains(types, type => IsWithin(type.Namespace, storageNamespace));
        Assert.Empty(types
            .Where(static type => type.Namespace == "Orleans.Durable.Storage.Engine.Wal")
            .SelectMany(type => type.Types
                .Where(reference => IsWithin(reference.Namespace, storageNamespace))
                .Select(reference => $"{type.FullName} -> {reference.FullName} ({reference.Site})")));
    }

    /// <summary>
    /// "A lower layer's contract, implementation, tests, and formats name nothing from a higher layer" (spec README
    /// §2.5), so the tests of a component which is not a composition reach no composition's project either.
    /// </summary>
    [Fact]
    public void LayerTestsReachNoComposition()
    {
        var map = Components.Production;
        var violations = Directory
            .EnumerateFiles(Repository.StorageRoot, "*.Tests.csproj", SearchOption.AllDirectories)
            .Where(path => map.ByName.TryGetValue(Path.GetFileNameWithoutExtension(path)[..^".Tests".Length], out var tested) && tested.Kind != ComponentKind.Composition)
            .SelectMany(static path => ProjectClosure(path)
                .Where(Compositions.Contains)
                .Select(composition => $"{Path.GetFileNameWithoutExtension(path)} -> {composition}"));

        Assert.Empty(violations);
    }

    /// <summary>
    /// "Generated serializers stay in the composition which owns their protocol and out of domain abstractions" (spec
    /// README §2.5), so no project which compiles a contract of spec README §2.6, nor a build file it imports
    /// implicitly, references a serialization generator's package, and none reaches one transitively: its restore graph
    /// holds none.
    /// </summary>
    [Fact]
    public void LayerContractsReferenceNoSerializationGenerator()
    {
        var root = Repository.FindRoot();
        var projectFiles = ProductionProjectFiles();
        var violations = LayerContracts
            .SelectMany(project =>
            {
                var projectFile = projectFiles[project];
                return BuildFiles(root, projectFile)
                    .SelectMany(PackageReferences)
                    .Concat(RestoredLibraries(Repository.AssetsFile(projectFile)))
                    .Where(IsSerializationPackage)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(package => $"{project} -> {package}");
            });

        Assert.Empty(violations);
    }

    /// <summary>
    /// Orleans stays in the composition and the technology packages it hosts (build-packaging.md §2): a Storage project's
    /// restore graph holds an Orleans package or project only if the project is one of theirs or references one, directly
    /// or through other projects, and no other production assembly references an Orleans assembly.
    /// </summary>
    [Fact]
    public void OnlyTheCompositionAndTheProjectsWhichReferenceItTakeOrleans()
    {
        var restoredOutside = Repository.StorageProjectFiles()
            .Where(static projectFile => File.Exists(Repository.AssetsFile(projectFile))
                && !OrleansHosts.Contains(Path.GetFileNameWithoutExtension(projectFile))
                && !ProjectClosure(projectFile).Any(OrleansHosts.Contains))
            .SelectMany(static projectFile => RestoredLibraries(Repository.AssetsFile(projectFile))
                .Where(static library => IsOrleans(library, "Microsoft.Orleans.") || IsOrleans(library, "Orleans."))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(package => $"{Path.GetFileNameWithoutExtension(projectFile)} -> {package}"));
        var referencedOutside = Repository.ProductionAssemblies()
            .Where(static assembly => !OrleansHosts.Contains(assembly.Project))
            .SelectMany(static assembly => MetadataReferences.Scan(assembly.Path, static _ => true)
                .SelectMany(static type => type.AssemblyTypes)
                .Where(static reference => IsOrleans(reference.Assembly, "Orleans."))
                .Select(reference => $"{assembly.Project} -> {reference.Assembly}"))
            .Distinct(StringComparer.Ordinal);

        Assert.Empty(restoredOutside);
        Assert.Empty(referencedOutside);
    }
    /// <summary>
    /// Orleans' runtime is the composition's alone; the families which share its prefix, Clog's own assemblies, Fast CASPaxos,
    /// and the simulation framework, are not Orleans' runtime.
    /// </summary>
    [Theory]
    [InlineData("Orleans.Core", "Orleans.", true)]
    [InlineData("Orleans.Durable.Journaling", "Orleans.", true)]
    [InlineData("Microsoft.Orleans.Runtime", "Microsoft.Orleans.", true)]
    [InlineData("Orleans.Consensus.CasPaxos.Server", "Orleans.", false)]
    [InlineData("Orleans.Testing.Simulation", "Orleans.", false)]
    [InlineData("Microsoft.Orleans.Consensus.CasPaxos", "Microsoft.Orleans.", false)]
    [InlineData("Microsoft.Orleans.Testing.Simulation.Testing.MTP", "Microsoft.Orleans.", false)]
    [InlineData("Microsoft.Orleans.TestingHost", "Microsoft.Orleans.", true)]
    [InlineData("Orleans.Durable", "Orleans.", true)]
    [InlineData("Orleans.Durable.Storage.Engine", "Orleans.", false)]
    public void OnlyOrleansRuntimeCountsAsOrleans(string name, string prefix, bool isOrleans) =>
        Assert.Equal(isOrleans, IsOrleans(name, prefix));

    /// <summary>The Storage projects under src, by name, each with its project file.</summary>
    private static Dictionary<string, string> ProductionProjectFiles() =>
        Repository.ProductionProjectFiles().ToDictionary(static path => Path.GetFileNameWithoutExtension(path), StringComparer.Ordinal);

    /// <summary>The fuzz harness's projects, by name, each with its project file.</summary>
    private static Dictionary<string, string> FuzzProjectFiles() =>
        Directory
            .EnumerateFiles(Path.Combine(Repository.StorageRoot, "fuzz"), "*.csproj", SearchOption.AllDirectories)
            .ToDictionary(static path => Path.GetFileNameWithoutExtension(path), StringComparer.Ordinal);

    /// <summary>The projects which a project under src or fuzz references, directly or through other projects.</summary>
    private static HashSet<string> Reachable(string project) =>
        ProjectClosure(ProductionProjectFiles().GetValueOrDefault(project) ?? FuzzProjectFiles()[project]);

    /// <summary>
    /// The names of the projects <paramref name="projectFile"/> references, directly or through other projects, Orleans'
    /// and its sibling families' among them. A reference whose file does not exist ends the walk with its name.
    /// </summary>
    private static HashSet<string> ProjectClosure(string projectFile)
    {
        var reached = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>([projectFile]);
        while (pending.TryPop(out var next))
        {
            foreach (var referenced in ReferencedProjects(next))
            {
                if (reached.Add(Path.GetFileNameWithoutExtension(referenced)) && File.Exists(referenced))
                {
                    pending.Push(referenced);
                }
            }
        }

        return reached;
    }

    // A wildcard include, such as the architecture tests' production projects, names every project it matches, and an
    // include may list several paths.
    private static IEnumerable<string> ReferencedProjects(string projectFile) => XDocument
        .Load(projectFile)
        .Descendants("ProjectReference")
        .Select(static element => element.Attribute("Include")?.Value)
        .OfType<string>()
        .SelectMany(static include => include.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        .SelectMany(include =>
        {
            var path = Repository.ResolveInclude(projectFile, include);
            var wildcard = path.IndexOf('*', StringComparison.Ordinal);
            return wildcard < 0
                ? [path]
                : Directory.EnumerateFiles(Path.GetDirectoryName(path[..wildcard])!, Path.GetFileName(path), SearchOption.AllDirectories);
        });
    private static string[] ProjectReferences(string projectFile) =>
        [
            .. XDocument
                .Load(projectFile)
                .Descendants("ProjectReference")
                .Select(static element => element.Attribute("Include")?.Value)
                .Where(static include => include is not null)
                .Select(static include => Path.GetFileNameWithoutExtension(
                    include!.Replace('\\', Path.DirectorySeparatorChar))),
        ];

    private static string[] PackageReferences(string projectFile) =>
        [
            .. XDocument
                .Load(projectFile)
                .Descendants("PackageReference")
                .Select(static element => element.Attribute("Include")?.Value)
                .OfType<string>(),
        ];

    /// <summary>
    /// Returns the name of every package and project in the restore graph <paramref name="assetsFile"/> records, which
    /// holds the project's whole dependency closure.
    /// </summary>
    internal static string[] RestoredLibraries(string assetsFile)
    {
        Assert.True(File.Exists(assetsFile), $"{assetsFile} does not exist: restore the Storage projects first.");
        using var document = JsonDocument.Parse(File.ReadAllText(assetsFile));
        return
        [
            .. document.RootElement
                .GetProperty("libraries")
                .EnumerateObject()
                .Select(static library => library.Name[..library.Name.IndexOf('/', StringComparison.Ordinal)])
                .Distinct(StringComparer.OrdinalIgnoreCase),
        ];
    }

    /// <summary>
    /// Whether an assembly or package, named under <paramref name="prefix"/> (<c>Orleans.</c> or <c>Microsoft.Orleans.</c>),
    /// is Orleans' runtime rather than one of its sibling families (<see cref="OrleansSiblingFamilies"/>).
    /// </summary>
    internal static bool IsOrleans(string name, string prefix) =>
        name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
        && !OrleansSiblingFamilies.Any(family => name.StartsWith(prefix + family, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether a package, or a project by its package ID, generates or carries serializers: Orleans' runtime packages,
    /// which bring its code generator, and the other generators' packages.
    /// </summary>
    private static bool IsSerializationPackage(string package) =>
        IsOrleans(package, "Microsoft.Orleans.")
        || SerializationPackagePrefixes.Any(prefix => package.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Returns <paramref name="projectFile"/> and every <c>Directory.Build.props</c> and <c>Directory.Build.targets</c>
    /// from its directory up to <paramref name="root"/>, which include those MSBuild imports into it implicitly.
    /// </summary>
    private static IEnumerable<string> BuildFiles(string root, string projectFile)
    {
        yield return projectFile;
        for (var directory = Path.GetDirectoryName(projectFile);
            directory is not null && directory.StartsWith(root, StringComparison.OrdinalIgnoreCase);
            directory = Path.GetDirectoryName(directory))
        {
            foreach (var name in ImplicitBuildFiles)
            {
                var file = Path.Combine(directory, name);
                if (File.Exists(file))
                {
                    yield return file;
                }
            }
        }
    }
}
