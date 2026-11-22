using System.Collections.Frozen;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Xml.Linq;
using static Orleans.Durable.Storage.Architecture.Tests.Namespaces;

namespace Orleans.Durable.Storage.Architecture.Tests;

/// <summary>A reference which would start work outside a simulation's scheduler: the referring type and the type it names.</summary>
internal readonly record struct SchedulerEscape(string Type, string Reference);

/// <summary>
/// Pins verification.md §7.1: every assembly a Clockwork simulation project rewrites, as its Clockwork manifest lists
/// them, Clog's, the kit's, and packages' alike, references from the Microsoft.Extensions abstractions only what runs
/// under the simulation's scheduler. The .NET 11 shared framework supplies those abstractions, so a simulation runs
/// them unrewritten. The check fails closed: a reference into any Microsoft.Extensions assembly outside the reviewed
/// allow-list is an escape, and so is a type of an allowed assembly whose callbacks are signalled from outside the
/// scheduler. A layer's <c>.Hosting</c> namespace, in the layer's own assembly (spec README §2.1), may reference the
/// hosting abstractions; the simulation kit references no <c>.Hosting</c> type.
/// </summary>
public sealed class SimulationClosureTests
{
    private const string Extensions = "Microsoft.Extensions.";

    private const string HostingAbstractions = "Microsoft.Extensions.Hosting.Abstractions";

    private const string FixtureLayer = "Orleans.Durable.Storage.Architecture.Tests.SimulationFixtures";

    /// <summary>The simulation kernel's project, the one which supplies every simulation of a build.</summary>
    private const string SimulationKernel = "src/Simulation/Orleans.Testing.Simulation/Orleans.Testing.Simulation.csproj";

    private const string SimulationKernelAssembly = "Orleans.Testing.Simulation.dll";

    /// <summary>The kernel's assembly before Clockwork took the Microsoft.Orleans.Testing.Simulation names.</summary>
    private const string LegacyKernelAssembly = "Clockwork.dll";

    /// <summary>The Microsoft.Extensions assemblies the rewritten closure may reference, each with why its work stays under the scheduler.</summary>
    private static readonly FrozenDictionary<string, string> AllowedAssemblies = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Microsoft.Extensions.Logging.Abstractions"] = "a log call runs on its caller's thread, as [LoggerMessage] generates it",
        ["Microsoft.Extensions.Options"] = "options are read once, at construction; the monitors are denied",
        ["Microsoft.Extensions.Primitives"] = "values such as StringValues; the change tokens are denied",
        ["Microsoft.Extensions.DependencyInjection.Abstractions"] = "constructor injection, which starts nothing",
        ["Microsoft.Extensions.Configuration.Abstractions"] = "configuration is read once, at construction, as options are; the reload tokens are denied",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    /// The escapes of package types reviewed as never running under a simulation, by assembly, referring type, and the
    /// type it names, each with why. Azure.Core binds clients from configuration: a simulation constructs the Azure SDK's
    /// clients directly, so it calls neither the configuration section whose reload token never fires nor the host
    /// builder's registration.
    /// </summary>
    private static readonly FrozenDictionary<(string Assembly, string Type, string Reference), string> ReviewedEscapes =
        new Dictionary<(string, string, string), string>
        {
            [("Azure.Core", "Azure.Identity.ChainedChildSection", "Microsoft.Extensions.Primitives.IChangeToken")] = "a configuration section's reload token, which never fires",
            [("Azure.Core", "Azure.Identity.ConfigurationExtensions", "Microsoft.Extensions.Hosting.IHostApplicationBuilder")] = "client registration at a host's startup",
        }.ToFrozenDictionary();

    /// <summary>The types of allowed assemblies which reload or notify on a thread outside the scheduler.</summary>
    private static readonly FrozenSet<string> DeniedTypes = new[]
    {
        "Microsoft.Extensions.Options.IOptionsMonitor`1",
        "Microsoft.Extensions.Options.IOptionsSnapshot`1",
        "Microsoft.Extensions.Options.OptionsMonitor`1",
        "Microsoft.Extensions.Primitives.IChangeToken",
        "Microsoft.Extensions.Primitives.ChangeToken",
        "Microsoft.Extensions.Primitives.CancellationChangeToken",
        "Microsoft.Extensions.Primitives.CompositeChangeToken",
    }.ToFrozenSet(StringComparer.Ordinal);

    [Fact]
    public void RewrittenAssembliesReferenceOnlyAbstractionsWhichRunUnderTheScheduler()
    {
        var references = ProjectReferences(Path.Combine(Repository.StorageRoot, "Orleans.Durable.Storage.Architecture.Tests", "Orleans.Durable.Storage.Architecture.Tests.csproj"));
        var rewritten = new SortedSet<string>(StringComparer.Ordinal);
        var escapes = new List<string>();
        foreach (var project in SimulationProjects())
        {
            // The reference orders the simulation project's build, and so its rewrite, before this test's.
            Assert.True(references.Contains(Path.GetFullPath(project)), $"Orleans.Durable.Storage.Architecture.Tests does not reference {project}.");
            var (manifest, directory) = ClockworkOutput(project);
            foreach (var assembly in RewrittenAssemblies(manifest))
            {
                var name = Path.GetFileNameWithoutExtension(assembly);
                rewritten.Add(name);
                foreach (var escape in Escapes(Path.Combine(directory, assembly), Allowed(name), static _ => true))
                {
                    // A reviewed type's nested types, its closures and state machines among them, share its review.
                    if (ReviewedEscapes.ContainsKey((name, escape.Type.Split('+')[0], escape.Reference)))
                    {
                        continue;
                    }

                    escapes.Add($"{Path.GetFileNameWithoutExtension(project)}: {name}: {escape.Type} -> {escape.Reference}");
                }
            }
        }

        // The closure holds the storage's layers, the kit, and a package, so an empty result below is not vacuous.
        Assert.Contains("Orleans.Durable.Storage.Engine", rewritten);
        Assert.Contains("Orleans.Durable.Storage.Simulation", rewritten);
        Assert.Contains(rewritten, static name => !name.StartsWith("Orleans.Durable.Storage.", StringComparison.Ordinal));
        Assert.Empty(escapes);
    }

    [Fact]
    public void TheSimulationKitReferencesNoHostingType()
    {
        var kit = Repository.SimulationOutputAssembly(Path.Combine(Repository.StorageRoot, "Orleans.Durable.Storage.Simulation", "Orleans.Durable.Storage.Simulation.csproj"));
        var violations = new List<string>();
        foreach (var type in MetadataReferences.Scan(kit, static _ => true))
        {
            foreach (var reference in type.Types)
            {
                if (reference.Namespace.Split('.').Contains("Hosting", StringComparer.Ordinal))
                {
                    violations.Add($"{type.FullName} -> {reference.FullName}");
                }
            }
        }

        Assert.Empty(violations);
    }

    /// <summary>
    /// A simulation stores what it writes in its simulated file system, so the simulation kit and every simulation project
    /// name no entry point which reaches the real disk (verification.md §7.1): the WAL's operating-system file system,
    /// its portable and io_uring storages, the LedgerStore's open and the node-root claim which default to that file
    /// system, the file-system providers' constructors which do, and the RocksDB storage. The fail-stop answers of a file
    /// system's contention (<c>FileSystemAnswers</c>) run only behind those entry points, so a simulation never meets them.
    /// </summary>
    [Fact]
    public void SimulationsNameNoRealDiskEntryPoint()
    {
        var assemblies = SimulationProjects()
            .Append(Path.Combine(Repository.StorageRoot, "Orleans.Durable.Storage.Simulation", "Orleans.Durable.Storage.Simulation.csproj"))
            .Select(Repository.SimulationOutputAssembly)
            .ToArray();
        Assert.All(assemblies, static assembly => Assert.True(File.Exists(assembly), $"{assembly} does not exist."));
        var uses = assemblies.SelectMany(assembly => RealDiskUses(MetadataReferences.Scan(assembly, static _ => true), RealDiskEntryPoints)
            .Select(use => $"{Path.GetFileNameWithoutExtension(assembly)}: {use}"))
            .ToArray();

        Assert.True(uses.Length == 0, "Simulations name real-disk entry points:" + Environment.NewLine + string.Join(Environment.NewLine, uses));
    }

    /// <summary>Each real-disk entry point is a namespace, type, or member which production declares, so a rename cannot retire it.</summary>
    [Fact]
    public void EachRealDiskEntryPointIsDeclared()
    {
        var declared = Repository.ProductionAssemblies().Select(static assembly => MetadataReferences.Accessibility(assembly.Path)).ToArray();
        var types = declared.SelectMany(static access => access.Types.Keys).ToHashSet(StringComparer.Ordinal);
        var members = declared.SelectMany(static access => access.Members.Keys).ToArray();

        Assert.All(RealDiskEntryPoints.Namespaces, ns => Assert.Contains(types, type => type.Contains('.', StringComparison.Ordinal) && IsWithin(type[..type.LastIndexOf('.')], ns)));
        Assert.All(RealDiskEntryPoints.Types, type => Assert.Contains(type, types));
        Assert.All(RealDiskEntryPoints.Members, entry => Assert.Contains(members, entry.Matches));
    }

    [Fact]
    public void TheRealDiskCheckFindsEachPlantedUse()
    {
        const string Fixtures = FixtureLayer + ".RealDisk";
        var entryPoints = new RealDiskEntryPointTable(
            [Fixtures + ".Storage"],
            [Fixtures + ".FixtureNativeStorage", Fixtures + ".Portable.FixturePortableStorage"],
            [
                new(Fixtures + ".FixtureFileSystem", "get_Os", null),
                new(Fixtures + ".FixtureStore", "OpenAsync", "FixtureFileSystem"),
                new(Fixtures + ".FixtureStore", ".ctor", "FixtureFileSystem"),
            ]);
        var users = MetadataReferences.Scan(typeof(SimulationClosureTests).Assembly.Location, static ns => ns == Fixtures)
            .Where(static type => type.FullName.StartsWith(Fixtures + ".FixtureRealDiskUser", StringComparison.Ordinal));

        Assert.Equal(
            [
                "FixtureRealDiskUser -> FixtureFileSystem.get_Os`0()FixtureFileSystem",
                "FixtureRealDiskUser -> FixtureNativeStorage",
                "FixtureRealDiskUser -> FixtureStore..ctor`0(String)Void",
                "FixtureRealDiskUser -> FixtureStore.OpenAsync`0(String)FixtureStore",
                "FixtureRealDiskUser -> Portable.FixturePortableStorage",
                "FixtureRealDiskUser -> Storage.FixtureDiskStorage",
            ],
            RealDiskUses(users, entryPoints).Select(use => use.Replace(Fixtures + ".", string.Empty, StringComparison.Ordinal)));
    }
    /// <summary>
    /// The crash-state enumeration's recording file system opens the portable storage's files over itself, as a
    /// simulation over it would: the check over the production entry points passes its compiled form, in the WAL's tests,
    /// which references the portable files and not their storage.
    /// </summary>
    [Fact]
    public void ARecordingFileSystemOpensThePortableFilesWithoutReachingTheRealDisk()
    {
        var walTests = Repository.OutputAssembly(Path.Combine(Repository.StorageRoot, "Orleans.Durable.Storage.Engine.Wal.Tests", "Orleans.Durable.Storage.Engine.Wal.Tests.csproj"));
        Assert.True(File.Exists(walTests), $"{walTests} has not been built.");
        var recording = MetadataReferences.Scan(walTests, static ns => ns == "Orleans.Durable.Storage.Testing")
            .Where(static type => type.FullName.Split('+')[0] == "Orleans.Durable.Storage.Testing.RecordingFileSystem")
            .ToArray();

        Assert.Contains(recording, static type => type.Types.Any(static reference => reference.FullName == "Orleans.Durable.Storage.Engine.Wal.Portable.PortableWalFile"));
        Assert.Empty(RealDiskUses(recording, RealDiskEntryPoints));
    }

    /// <summary>
    /// The check reports each denied type and each reference into a denied assembly the fixtures make, and neither the
    /// allowed references nor the fixture layer's <c>.Hosting</c> namespace.
    /// </summary>
    [Fact]
    public void TheCheckReportsEachEscapeAndNothingElse()
    {
        SchedulerEscape[] expected =
        [
            new(FixtureLayer + ".FixtureCacheReader", "Microsoft.Extensions.Caching.Distributed.IDistributedCache"),
            new(FixtureLayer + ".FixtureChangeTokens", "Microsoft.Extensions.Primitives.CancellationChangeToken"),
            new(FixtureLayer + ".FixtureChangeTokens", "Microsoft.Extensions.Primitives.ChangeToken"),
            new(FixtureLayer + ".FixtureChangeTokens", "Microsoft.Extensions.Primitives.CompositeChangeToken"),
            new(FixtureLayer + ".FixtureChangeTokens", "Microsoft.Extensions.Primitives.IChangeToken"),
            new(FixtureLayer + ".FixtureHostedService", "Microsoft.Extensions.Hosting.IHostedService"),
            new(FixtureLayer + ".FixtureOptionsReader", "Microsoft.Extensions.Options.IOptionsMonitor`1"),
            new(FixtureLayer + ".FixtureOptionsReader", "Microsoft.Extensions.Options.IOptionsSnapshot`1"),
            new(FixtureLayer + ".FixtureOptionsReader", "Microsoft.Extensions.Options.OptionsMonitor`1"),
        ];

        Assert.Equal(
            expected,
            Escapes(typeof(SimulationClosureTests).Assembly.Location, [FixtureLayer], static ns => IsWithin(ns, FixtureLayer))
                .Distinct()
                .OrderBy(static escape => escape.Type, StringComparer.Ordinal)
                .ThenBy(static escape => escape.Reference, StringComparer.Ordinal));
    }

    /// <summary>The real-disk entry points <see cref="SimulationsNameNoRealDiskEntryPoint"/> forbids.</summary>
    /// <remarks>
    /// The portable storage's files (<c>PortableWalFile</c>) issue their I/O through the file system they are given, so
    /// a simulation may open them over a simulated or recording file system; its storage, which gives them the
    /// operating system's, may not.
    /// </remarks>
    private static readonly RealDiskEntryPointTable RealDiskEntryPoints = new(
        ["Orleans.Durable.Storage.Engine.Wal.IoUring"],
        ["Orleans.Durable.Storage.RocksDb.RocksDbJournalIndex", "Orleans.Durable.Storage.Engine.Wal.Portable.PortableWalStorage"],
        [
            new("Orleans.Durable.Storage.Engine.Wal.FileSystem.WalFileSystem", "get_Os", null),
            new("Orleans.Durable.Storage.Engine.Wal.FileSystem.WalFileSystem", "CreateOs", null),
            new("Orleans.Durable.Storage.Engine.Ledgers.Store.LedgerStore", "OpenAsync", "Orleans.Durable.Storage.Engine.Ledgers.Store.INodeRootClaim"),
            new("Orleans.Durable.Storage.Engine.Ledgers.Store.FileNodeRootClaim", "Claim", "Orleans.Durable.Storage.Engine.Wal.FileSystem.WalFileSystem"),
            new("Orleans.Durable.Storage.FileSystem.Archive.FileSystemObjectStore", ".ctor", "Orleans.Durable.Storage.Engine.Wal.FileSystem.WalFileSystem"),
            new("Orleans.Durable.Storage.FileSystem.MetaStore.FileSystemMetaStore", ".ctor", "Orleans.Durable.Storage.Engine.Wal.FileSystem.WalFileSystem"),
            new("Orleans.Durable.Storage.Engine.Archive.Lsm.BlockCache", "OpenAsync", "Orleans.Durable.Storage.Engine.Wal.FileSystem.WalFileSystem"),
        ]);

    /// <summary>Each use by <paramref name="types"/> of an entry point of <paramref name="entryPoints"/>, by the outermost using type.</summary>
    private static IEnumerable<string> RealDiskUses(IEnumerable<ScannedType> types, RealDiskEntryPointTable entryPoints) =>
        types
            .SelectMany(type => type.Types
                .Where(reference => entryPoints.Namespaces.Any(ns => IsWithin(reference.Namespace, ns)) || entryPoints.Types.Contains(reference.FullName.Split('+')[0], StringComparer.Ordinal))
                .Select(static reference => reference.FullName.Split('+')[0])
                .Concat(type.MemberUses.Where(use => entryPoints.Members.Any(entry => entry.Matches(use))).Select(static use => use.ToString()))
                .Select(used => $"{type.FullName.Split('+')[0]} -> {used}"))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);
    /// <summary>
    /// The references of the types of the assembly at <paramref name="assemblyPath"/> which <paramref name="isScanned"/>
    /// selects that would start work outside the scheduler. <paramref name="layers"/> are the namespaces whose
    /// <c>.Hosting</c> namespaces may reference the hosting abstractions: the roots of the assembly's components.
    /// </summary>
    private static List<SchedulerEscape> Escapes(string assemblyPath, string[] layers, Func<string, bool> isScanned)
    {
        var escapes = new List<SchedulerEscape>();
        foreach (var type in MetadataReferences.Scan(assemblyPath, isScanned))
        {
            var isHosting = layers.Any(layer => IsWithin(type.Namespace, layer + ".Hosting"));
            foreach (var reference in type.AssemblyTypes)
            {
                if (IsEscape(reference, isHosting))
                {
                    escapes.Add(new(type.FullName, reference.FullName));
                }
            }
        }

        return escapes;
    }

    private static bool IsEscape(AssemblyType reference, bool fromHosting)
    {
        if (!reference.Assembly.StartsWith(Extensions, StringComparison.Ordinal))
        {
            return false;
        }

        if (AllowedAssemblies.ContainsKey(reference.Assembly))
        {
            return DeniedTypes.Contains(reference.FullName.Split('+')[0]);
        }

        return !(fromHosting && reference.Assembly == HostingAbstractions);
    }

    /// <summary>
    /// One simulation kernel per build: every Storage project which reaches the Microsoft.Orleans.Testing.Simulation
    /// kernel reaches the repository's (src/Simulation), the CASPaxos adapter's tests through the consensus family's
    /// simulation and testing projects among them, and none reaches another kernel's project, such as an imported
    /// Clockwork checkout's or the legacy kernel's. Each project's restore records every project it reaches, so the check
    /// reads the assets of every Storage project which has them, and requires those of the adapter's tests and the
    /// simulation tests, which a restore of Orleans.slnx writes.
    /// </summary>
    [Fact]
    public void EveryProjectReachesOnlyOneSimulationKernel()
    {
        var root = Repository.FindRoot();
        var reached = Repository.StorageProjectFiles()
            .Select(file => (Project: Path.GetFileNameWithoutExtension(file), Reached: ReachedProjects(root, file)))
            .Where(static project => project.Reached is not null)
            .ToDictionary(static project => project.Project, static project => project.Reached!, StringComparer.Ordinal);

        Assert.SkipUnless(
            reached.ContainsKey("Orleans.Durable.Storage.CasPaxos.Tests") && reached.ContainsKey("Orleans.Durable.Storage.Simulation.Tests"),
            "Restore Orleans.slnx so every project's assets exist.");
        Assert.Contains(SimulationKernel, reached["Orleans.Durable.Storage.CasPaxos.Tests"]);
        Assert.Contains(SimulationKernel, reached["Orleans.Durable.Storage.Simulation.Tests"]);
        Assert.Empty(OtherKernels(reached));
    }

    [Fact]
    public void TheKernelCheckFindsAProjectWhichReachesAnotherKernel() =>
        Assert.Equal(
            ["Embedded: vendor/Clockwork/src/Orleans.Testing.Simulation/Orleans.Testing.Simulation.csproj", "Legacy: vendor/Clockwork/src/Clockwork/Clockwork.csproj"],
            OtherKernels(new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["Storage"] = [SimulationKernel, "src/Orleans.Durable.Storage.Engine/Orleans.Durable.Storage.Engine.csproj"],
                ["Embedded"] = [SimulationKernel, "vendor/Clockwork/src/Orleans.Testing.Simulation/Orleans.Testing.Simulation.csproj"],
                ["Legacy"] = ["vendor/Clockwork/src/Clockwork/Clockwork.csproj"],
            }));

    /// <summary>
    /// No build output holds the legacy kernel, <c>Clockwork.dll</c>, beside the simulation kernel,
    /// <c>Orleans.Testing.Simulation.dll</c>: each assembly of a simulation's closure, rewritten or not, comes from one
    /// kernel. The check reads the output directory of every Storage project in this configuration and in its simulation
    /// configuration.
    /// </summary>
    [Fact]
    public void NoOutputHoldsTheLegacyKernelBesideTheSimulationKernel()
    {
        var outputs = Repository.StorageProjectFiles()
            .SelectMany(static project => new[] { Repository.OutputAssembly(project), Repository.SimulationOutputAssembly(project) })
            .Select(static assembly => Path.GetDirectoryName(assembly)!)
            .Where(Directory.Exists)
            .ToArray();

        Assert.Contains(outputs, static output => File.Exists(Path.Combine(output, SimulationKernelAssembly)));
        Assert.Empty(LegacyKernelOutputs(outputs));
    }

    [Fact]
    public void TheOutputCheckFindsTheLegacyKernelBesideTheSimulationKernel()
    {
        var root = Directory.CreateTempSubdirectory("clog-kernel-check-");
        try
        {
            string[] outputs = [Directory.CreateDirectory(Path.Combine(root.FullName, "Both")).FullName, Directory.CreateDirectory(Path.Combine(root.FullName, "One")).FullName];
            File.WriteAllBytes(Path.Combine(outputs[0], SimulationKernelAssembly), []);
            File.WriteAllBytes(Path.Combine(outputs[0], LegacyKernelAssembly), []);
            File.WriteAllBytes(Path.Combine(outputs[1], SimulationKernelAssembly), []);

            Assert.Equal([outputs[0]], LegacyKernelOutputs(outputs));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    /// <summary>Each project, with each project it reaches, by path from the root, which is a kernel other than the repository's.</summary>
    private static IEnumerable<string> OtherKernels(IReadOnlyDictionary<string, IReadOnlyList<string>> reached) =>
        reached
            .SelectMany(static project => project.Value
                .Where(static path => path != SimulationKernel
                    && (path.EndsWith("/Orleans.Testing.Simulation.csproj", StringComparison.Ordinal) || path.EndsWith("/Clockwork.csproj", StringComparison.Ordinal)))
                .Select(path => $"{project.Key}: {path}"))
            .Order(StringComparer.Ordinal);

    /// <summary>The directories among <paramref name="outputs"/> which hold both the simulation kernel and the legacy one.</summary>
    private static IEnumerable<string> LegacyKernelOutputs(IEnumerable<string> outputs) =>
        outputs.Where(static output => File.Exists(Path.Combine(output, SimulationKernelAssembly)) && File.Exists(Path.Combine(output, LegacyKernelAssembly)));

    /// <summary>
    /// The projects <paramref name="projectFile"/> reaches, by path from <paramref name="root"/> with forward slashes, as
    /// its restore's assets list them; null when it has not been restored.
    /// </summary>
    private static IReadOnlyList<string>? ReachedProjects(string root, string projectFile)
    {
        var assets = Repository.AssetsFile(projectFile);
        if (!File.Exists(assets))
        {
            return null;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(assets));
        var directory = Path.GetDirectoryName(projectFile)!;
        return
        [
            .. document.RootElement.GetProperty("libraries").EnumerateObject()
                .Where(static library => library.Value.GetProperty("type").GetString() == "project")
                .Select(library => Path.GetRelativePath(root, Path.GetFullPath(Path.Combine(directory, library.Value.GetProperty("path").GetString()!)))
                    .Replace(Path.DirectorySeparatorChar, '/')),
        ];
    }

    /// <summary>
    /// The Storage test projects whose builds the simulation instrumentation rewrites. A props or targets file which
    /// turned rewriting on would make every project which imports it a simulation project, so the property is set in
    /// project files alone.
    /// </summary>
    internal static List<string> SimulationProjects()
    {
        var root = Repository.FindRoot();
        var projects = new List<string>();
        var storageBuildFiles = Repository.TrackedFiles()
            .Where(static path => Path.GetExtension(path).ToUpperInvariant() is ".CSPROJ" or ".PROPS" or ".TARGETS")
            .Select(path => Path.GetFullPath(Path.Combine(root, path)));
        foreach (var file in storageBuildFiles)
        {
            var setsIt = XDocument.Load(file)
                .Descendants()
                .Any(static element => string.Equals(element.Name.LocalName, "OrleansSimulationInstrumentedTestProject", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(element.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase));
            if (setsIt)
            {
                Assert.True(file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase), $"{file} turns on Clockwork's rewrite for every project which imports it.");
                projects.Add(file);
            }
        }

        Assert.NotEmpty(projects);
        return projects;
    }

    /// <summary>The repository's project, props, and targets files, every one a Storage project could import.</summary>
    internal static IEnumerable<string> BuildFiles(string root) =>
        Repository.TrackedBuildFiles().Select(path => Path.GetFullPath(Path.Combine(root, path)));

    /// <summary>
    /// The full paths of the projects <paramref name="projectFile"/> references by name, or by the repository's
    /// <c>$(SourceRoot)</c>. A wildcard include, as the one which names every project under src, names no simulation
    /// project; an include through another property fails the check, which cannot resolve it.
    /// </summary>
    internal static HashSet<string> ProjectReferences(string projectFile)
    {
        var references = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var include in XDocument.Load(projectFile).Descendants("ProjectReference").Select(static element => element.Attribute("Include")?.Value))
        {
            Assert.False(include is null || include.Replace("$(SourceRoot)", string.Empty, StringComparison.Ordinal).Contains("$(", StringComparison.Ordinal), $"{projectFile} has a project reference this check cannot resolve: {include}");
            if (!include.Contains('*', StringComparison.Ordinal))
            {
                references.Add(Repository.ResolveInclude(projectFile, include));
            }
        }

        return references;
    }

    /// <summary>
    /// The Clockwork manifest of the simulation project <paramref name="projectFile"/>'s assembly, and the directory of
    /// its rewritten assemblies, from the paths, relative to the project's directory, which the simulation build stamps
    /// on it as assembly metadata.
    /// </summary>
    internal static (string Manifest, string Directory) ClockworkOutput(string projectFile)
    {
        var assemblyPath = Repository.SimulationOutputAssembly(projectFile);
        Assert.True(File.Exists(assemblyPath), $"{assemblyPath} has not been built.");
        var metadata = Repository.AssemblyMetadata(assemblyPath);
        Assert.True(metadata.TryGetValue("OrleansSimulationManifestPath", out var manifest), $"{assemblyPath} carries no OrleansSimulationManifestPath.");
        Assert.True(metadata.TryGetValue("OrleansSimulationInstrumentationOutputPath", out var directory), $"{assemblyPath} carries no OrleansSimulationInstrumentationOutputPath.");
        var projectDirectory = Path.GetDirectoryName(projectFile)!;
        manifest = Path.GetFullPath(manifest, projectDirectory);
        directory = Path.GetFullPath(directory, projectDirectory);
        Assert.True(File.Exists(manifest), $"The Clockwork manifest {manifest} does not exist.");
        return (manifest, directory);
    }

    /// <summary>The assemblies a Clockwork manifest records as rewritten, by their paths relative to its output.</summary>
    internal static List<string> RewrittenAssemblies(string manifest)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(manifest));
        var assemblies = new List<string>();
        foreach (var assembly in document.RootElement.GetProperty("assemblies").EnumerateArray())
        {
            if (assembly.GetProperty("wasRewritten").GetBoolean())
            {
                assemblies.Add(assembly.GetProperty("relativePath").GetString()!);
            }
        }

        Assert.NotEmpty(assemblies);
        return assemblies;
    }
}

/// <summary>
/// The entry points which reach the real disk: every type of a namespace, a type, and members, each by its declaring
/// type and name, apart from the overloads whose signature names <see cref="RealDiskMember.Unless"/>, the parameter
/// which takes a simulated file system or claim.
/// </summary>
internal sealed record RealDiskEntryPointTable(string[] Namespaces, string[] Types, RealDiskMember[] Members);

/// <summary>A member which reaches the real disk unless its signature names <paramref name="Unless"/>.</summary>
internal sealed record RealDiskMember(string DeclaringType, string Name, string? Unless)
{
    public bool Matches(MemberUse use) =>
        use.DeclaringType == DeclaringType
        && use.Name == Name
        && (Unless is null || !use.Signature.Contains(Unless, StringComparison.Ordinal));
}
