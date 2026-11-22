using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Xml.Linq;

namespace Orleans.Durable.Storage.Architecture.Tests;

/// <summary>A Storage project under src, and the path in the test output of its assembly.</summary>
internal sealed record ProductionAssembly(string Project, string Path);

/// <summary>Locates the repository, the Durable Storage tree, and its production assemblies from the test output.</summary>
internal static class Repository
{
    private static readonly Lazy<string[]> ProductionProjects = new(static () =>
    [
        .. Directory
            .EnumerateFiles(Path.Combine(FindRoot(), "src"), "Orleans.Durable.Storage*.csproj", SearchOption.AllDirectories)
            .Where(static projectFile => !IsBuildOutput(projectFile))
            .Order(StringComparer.Ordinal),
    ]);

    private static readonly Lazy<ProductionAssembly[]> Production = new(static () =>
    [
        .. ProductionProjects.Value
            .Select(static projectFile =>
            {
                var project = Path.GetFileNameWithoutExtension(projectFile);
                return new ProductionAssembly(project, Path.Combine(AppContext.BaseDirectory, AssemblyName(projectFile) + ".dll"));
            })
            .OrderBy(static assembly => assembly.Project, StringComparer.Ordinal),
    ]);

    /// <summary>Finds the repository root: the nearest directory above the test output which holds <c>Orleans.slnx</c>.</summary>
    public static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
            directory is not null;
            directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Orleans.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not find the Orleans repository root.");
    }

    /// <summary>
    /// The Durable Storage tree, <c>test/Durable.Storage</c>: the family's tests, simulation kit, fuzz harness, benchmarks,
    /// emulator host, scripts, specification, and models.
    /// </summary>
    public static string StorageRoot => Path.Combine(FindRoot(), "test", "Durable.Storage");

    /// <summary>The pathspecs of the Durable Storage family's directories, from the repository's root.</summary>
    private static readonly string[] StoragePathspecs =
    [
        "src/Orleans.Durable.Storage*", "src/*/Orleans.Durable.Storage.*", "test/Durable.Storage", "playground/DurableStorage",
        "eng/durable-storage",
    ];

    /// <summary>The sample's directory, <c>playground/DurableStorage</c>.</summary>
    public static string SamplesRoot => Path.Combine(FindRoot(), "playground", "DurableStorage");

    /// <summary>The project files of the Storage production assemblies: the <c>Orleans.Durable.Storage</c> projects under src.</summary>
    public static IReadOnlyList<string> ProductionProjectFiles() => ProductionProjects.Value;

    /// <summary>The files a pattern selects in the Storage production projects' directories, outside their build output.</summary>
    public static IEnumerable<string> ProductionSourceFiles(string searchPattern) =>
        ProductionProjects.Value
            .SelectMany(projectFile => Directory.EnumerateFiles(Path.GetDirectoryName(projectFile)!, searchPattern, SearchOption.AllDirectories))
            .Where(static file => !IsBuildOutput(file));

    /// <summary>
    /// The project files of every Storage project, production, test, tool, and sample: those under src which
    /// <see cref="ProductionProjectFiles"/> lists, and every project under <see cref="StorageRoot"/> and <see cref="SamplesRoot"/>.
    /// </summary>
    public static IEnumerable<string> StorageProjectFiles() =>
        ProductionProjects.Value
            .Concat(Directory.EnumerateFiles(StorageRoot, "*.csproj", SearchOption.AllDirectories))
            .Concat(Directory.EnumerateFiles(SamplesRoot, "*.csproj", SearchOption.AllDirectories))
            .Where(static file => !IsBuildOutput(file));

    /// <summary>
    /// The path a project item's include names, relative to <paramref name="projectFile"/>, with the repository's
    /// <c>$(SourceRoot)</c>, by which Orleans' projects name their references, expanded.
    /// </summary>
    public static string ResolveInclude(string projectFile, string include)
    {
        var expanded = include
            .Replace("$(SourceRoot)", FindRoot() + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            .Replace('\\', Path.DirectorySeparatorChar);
        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(projectFile)!, expanded));
    }

    /// <summary>The restore graph NuGet recorded for <paramref name="projectFile"/>.</summary>
    public static string AssetsFile(string projectFile) => Path.Combine(Path.GetDirectoryName(projectFile)!, "obj", "project.assets.json");

    /// <summary>Whether a path lies in a project's build output, under a <c>bin</c> or <c>obj</c> directory.</summary>
    public static bool IsBuildOutput(string path) =>
        path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(static part => part is "bin" or "obj");

    /// <summary>The name of the assembly <paramref name="projectFile"/> builds: its <c>AssemblyName</c>, or the project's name.</summary>
    public static string AssemblyName(string projectFile) =>
        XDocument.Load(projectFile).Descendants("AssemblyName").SingleOrDefault()?.Value ?? Path.GetFileNameWithoutExtension(projectFile);

    /// <summary>
    /// The path of the assembly which <paramref name="projectFile"/> builds in this configuration: each project's output
    /// lies under its own <c>bin/&lt;configuration&gt;/net11.0</c>.
    /// </summary>
    public static string OutputAssembly(string projectFile) => OutputAssembly(projectFile, OutputConfiguration);

    /// <summary>
    /// The path of the assembly which <paramref name="projectFile"/> builds in this configuration's simulation
    /// configuration, <c>SimulationRelease</c> or <c>SimulationDebug</c> (docs/spec/build-packaging.md §1): a simulation
    /// project's, and those of the src projects its closure rewrites.
    /// </summary>
    public static string SimulationOutputAssembly(string projectFile) => OutputAssembly(projectFile, SimulationConfiguration);

    /// <summary>This test's configuration, <c>Release</c> or <c>Debug</c>: the directory above its output's framework directory.</summary>
    public static string OutputConfiguration =>
        new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Parent!.Name;

    /// <summary>This configuration's simulation configuration, <c>SimulationRelease</c> or <c>SimulationDebug</c>.</summary>
    public static string SimulationConfiguration => "Simulation" + OutputConfiguration;

    /// <summary>
    /// Reads the <see cref="System.Reflection.AssemblyMetadataAttribute"/> pairs of the assembly at
    /// <paramref name="assemblyPath"/>, without loading it.
    /// </summary>
    public static Dictionary<string, string> AssemblyMetadata(string assemblyPath)
    {
        using var image = new PEReader(File.OpenRead(assemblyPath));
        var reader = image.GetMetadataReader();
        var pairs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var handle in reader.GetAssemblyDefinition().GetCustomAttributes())
        {
            var attribute = reader.GetCustomAttribute(handle);
            if (attribute.Constructor.Kind == HandleKind.MemberReference
                && reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent is { Kind: HandleKind.TypeReference } parent
                && reader.StringComparer.Equals(reader.GetTypeReference((TypeReferenceHandle)parent).Name, "AssemblyMetadataAttribute"))
            {
                var value = reader.GetBlobReader(attribute.Value);
                _ = value.ReadUInt16();
                var key = value.ReadSerializedString();
                var text = value.ReadSerializedString();
                if (key is not null && text is not null)
                {
                    pairs[key] = text;
                }
            }
        }

        return pairs;
    }

    private static string OutputAssembly(string projectFile, string configuration) =>
        Path.Combine(Path.GetDirectoryName(projectFile)!, "bin", configuration, "net11.0", AssemblyName(projectFile) + ".dll");

    /// <summary>
    /// Every Storage project under src, with the path in the test output of its assembly. This project references each of
    /// them, and NamespaceTests checks that each path exists.
    /// </summary>
    public static IReadOnlyList<ProductionAssembly> ProductionAssemblies() => Production.Value;

    /// <summary>
    /// The production assemblies which may declare the types of <paramref name="typeNamespace"/> or of a namespace within
    /// it: those which compile a component (<see cref="Components"/>) whose root contains the namespace or lies within it,
    /// or which lists a type there. None while no component claims such a type.
    /// </summary>
    public static IReadOnlyList<ProductionAssembly> AssembliesDeclaring(string typeNamespace)
    {
        var map = Components.Production;
        var assemblies = map.ByName.Values
            .Where(component => component.Roots.Any(root => Namespaces.IsWithin(typeNamespace, root) || Namespaces.IsWithin(root, typeNamespace)))
            .Concat(map.Members.Where(member => Namespaces.IsWithin(member.Key[..member.Key.LastIndexOf('.')], typeNamespace)).Select(member => map.ByName[member.Value]))
            .Select(static component => component.Assembly)
            .ToHashSet(StringComparer.Ordinal);
        return [.. Production.Value.Where(assembly => assemblies.Contains(assembly.Project))];
    }
    /// <summary>
    /// The Durable Storage files of the repository's working tree which git tracks or would track, by their path from the
    /// root with forward slashes, in order: what <c>git ls-files --cached --others --exclude-standard</c> lists under the
    /// family's directories, so no ignored path, such as the scripts' artifacts, contributes one. A tracked file deleted
    /// from the working tree contributes none.
    /// </summary>
    public static IReadOnlyList<string> TrackedFiles() => GitFiles(StoragePathspecs);

    /// <summary>
    /// The project, props, and targets files of the whole repository which git tracks or would track, by their path from
    /// the root with forward slashes, in order: every build file a Storage project could import.
    /// </summary>
    public static IReadOnlyList<string> TrackedBuildFiles() => GitFiles(":(glob)**/*.csproj", ":(glob)**/*.props", ":(glob)**/*.targets");

    private static string[] GitFiles(params string[] pathspecs)
    {
        var root = FindRoot();
        var start = new ProcessStartInfo("git") { WorkingDirectory = root, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var argument in (string[])["ls-files", "--cached", "--others", "--exclude-standard", "-z", "--", .. pathspecs])
        {
            start.ArgumentList.Add(argument);
        }

        using var git = Process.Start(start) ?? throw new InvalidOperationException("git did not start.");
        var listing = git.StandardOutput.ReadToEnd();
        git.WaitForExit();
        if (git.ExitCode != 0)
        {
            throw new InvalidOperationException($"git ls-files exited with code {git.ExitCode}.");
        }

        return
        [
            .. listing.Split('\0', StringSplitOptions.RemoveEmptyEntries)
                .Distinct(StringComparer.Ordinal)
                .Where(path => File.Exists(Path.Combine(root, path)))
                .Order(StringComparer.Ordinal),
        ];
    }
}
