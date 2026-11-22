using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Xml.Linq;

namespace Orleans.Durable.Storage.Architecture.Tests;

/// <summary>A project under src, and the path in the test output of its assembly.</summary>
internal sealed record ProductionAssembly(string Project, string Path);

/// <summary>Locates the repository and its production assemblies from the test output.</summary>
internal static class Repository
{
    private static readonly Lazy<ProductionAssembly[]> Production = new(static () =>
    [
        .. Directory
            .EnumerateFiles(Path.Combine(FindRoot(), "src"), "*.csproj", SearchOption.AllDirectories)
            .Select(static projectFile =>
            {
                var project = Path.GetFileNameWithoutExtension(projectFile);
                return new ProductionAssembly(project, Path.Combine(AppContext.BaseDirectory, AssemblyName(projectFile) + ".dll"));
            })
            .OrderBy(static assembly => assembly.Project, StringComparer.Ordinal),
    ]);

    /// <summary>Finds the repository root: the nearest directory above the test output which holds <c>Clog.slnx</c>.</summary>
    public static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
            directory is not null;
            directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Clog.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not find the Clog repository root.");
    }

    /// <summary>The name of the assembly <paramref name="projectFile"/> builds: its <c>AssemblyName</c>, or the project's name.</summary>
    public static string AssemblyName(string projectFile) =>
        XDocument.Load(projectFile).Descendants("AssemblyName").SingleOrDefault()?.Value ?? Path.GetFileNameWithoutExtension(projectFile);

    /// <summary>
    /// The path of the assembly which <paramref name="projectFile"/> builds in this configuration: the artifacts layout
    /// (<c>UseArtifactsOutput</c>) puts each project's output beside this project's, under the same configuration.
    /// </summary>
    public static string OutputAssembly(string projectFile) => OutputAssembly(projectFile, OutputConfiguration);

    /// <summary>
    /// The path of the assembly which <paramref name="projectFile"/> builds in this configuration's simulation
    /// configuration, <c>SimulationRelease</c> or <c>SimulationDebug</c> (docs/spec/build-packaging.md §1): a simulation
    /// project's, and those of the src projects its closure rewrites.
    /// </summary>
    public static string SimulationOutputAssembly(string projectFile) => OutputAssembly(projectFile, SimulationConfiguration);

    /// <summary>The artifacts layout's directory name for this test's configuration: <c>release</c> or <c>debug</c>.</summary>
    public static string OutputConfiguration =>
        Path.GetFileName(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    /// <summary>The artifacts layout's directory name for this configuration's simulation configuration.</summary>
    public static string SimulationConfiguration => "simulation" + OutputConfiguration;

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

    private static string OutputAssembly(string projectFile, string configuration)
    {
        var output = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return Path.Combine(output.Parent!.Parent!.FullName, Path.GetFileNameWithoutExtension(projectFile), configuration, AssemblyName(projectFile) + ".dll");
    }

    /// <summary>
    /// Every project under src, with the path in the test output of its assembly. This project references each of
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
    /// The files of the repository's working tree which git tracks or would track, by their path from the root with
    /// forward slashes, in order: what <c>git ls-files --cached --others --exclude-standard</c> lists, so no ignored
    /// path, such as the repository-local NuGet cache, contributes one. A submodule and a tracked file deleted from the
    /// working tree contribute none.
    /// </summary>
    public static IReadOnlyList<string> TrackedFiles()
    {
        var root = FindRoot();
        var start = new ProcessStartInfo("git") { WorkingDirectory = root, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var argument in (string[])["ls-files", "--cached", "--others", "--exclude-standard", "-z"])
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
