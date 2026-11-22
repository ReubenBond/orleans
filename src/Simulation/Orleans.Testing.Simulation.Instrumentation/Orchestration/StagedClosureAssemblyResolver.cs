using System.Runtime.InteropServices;
using Orleans.Testing.Simulation.Instrumentation.Closure;
using Orleans.Testing.Simulation.Instrumentation.Rewriting;
using Mono.Cecil;

namespace Orleans.Testing.Simulation.Instrumentation.Orchestration;

/// <summary>
/// Resolves the assemblies a staged closure references the way the runtime binds them when the closure runs from its
/// staging directory. A framework assembly is the runtime's own copy, which the host prefers to an app-local facade of
/// it; any other assembly is the one in the staging directory itself, falling back to a shared framework that provides
/// it. The staging directory's subdirectories are not searched: the runtime does not probe them, and they can hold
/// another target framework's build of the closure, whose framework facades would shadow the runtime's assemblies or
/// forward a type in a cycle. Every module the resolver reads, and every module read with
/// <see cref="CreateReaderParameters"/>, resolves types through one <see cref="ForwarderCycleGuard"/>.
/// </summary>
internal sealed class StagedClosureAssemblyResolver : IAssemblyResolver
{
    private static readonly Lazy<Dictionary<string, string>> SharedFrameworkAssemblies = new(FindSharedFrameworkAssemblies);
    private static readonly string[] AssemblyExtensions = [".dll", ".exe"];

    private readonly string _stagingDirectory;
    private readonly FrameworkAssemblyClassifier _frameworkClassifier;
    private readonly Dictionary<string, AssemblyDefinition?> _resolved = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Initializes a new instance of the <see cref="StagedClosureAssemblyResolver"/> class.</summary>
    /// <param name="stagingDirectory">The directory the closure is staged in.</param>
    /// <param name="frameworkClassifier">The classifier that kept framework assemblies out of the rewrite.</param>
    public StagedClosureAssemblyResolver(string stagingDirectory, FrameworkAssemblyClassifier frameworkClassifier)
    {
        _stagingDirectory = stagingDirectory;
        _frameworkClassifier = frameworkClassifier;
        Guard = new ForwarderCycleGuard(this);
    }

    /// <summary>Gets the metadata resolver of every module this resolver reads.</summary>
    public ForwarderCycleGuard Guard { get; }

    /// <summary>Creates the parameters with which to read a module that resolves through this resolver.</summary>
    /// <returns>The reader parameters.</returns>
    public ReaderParameters CreateReaderParameters() =>
        new() { AssemblyResolver = this, MetadataResolver = Guard, ReadSymbols = false, InMemory = true };

    /// <inheritdoc/>
    public AssemblyDefinition Resolve(AssemblyNameReference name) => Resolve(name, CreateReaderParameters());

    /// <inheritdoc/>
    /// <remarks>
    /// The caller's <paramref name="parameters"/> are not used: each assembly is read once, with the parameters every
    /// module of the closure shares, so that every resolution goes through <see cref="Guard"/>.
    /// </remarks>
    public AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!_resolved.TryGetValue(name.Name, out AssemblyDefinition? assembly))
        {
            assembly = Locate(name.Name) is string path
                ? AssemblyDefinition.ReadAssembly(path, CreateReaderParameters())
                : null;
            _resolved[name.Name] = assembly;
        }

        return assembly ?? throw new AssemblyResolutionException(name);
    }

    /// <summary>Returns the path the assembly with a simple name resolves to, or <see langword="null"/>.</summary>
    /// <param name="name">The simple assembly name.</param>
    /// <returns>The path, or <see langword="null"/> if the closure and the runtime do not provide it.</returns>
    public string? Locate(string name)
    {
        string? staged = Probe(name);
        string? shared = SharedFrameworkAssemblies.Value.GetValueOrDefault(name);
        return _frameworkClassifier.IsFrameworkAssembly(name) ? shared ?? staged : staged ?? shared;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (AssemblyDefinition? assembly in _resolved.Values)
        {
            assembly?.Dispose();
        }

        _resolved.Clear();
    }

    private string? Probe(string name) =>
        AssemblyExtensions
            .Select(extension => Path.Combine(_stagingDirectory, name + extension))
            .FirstOrDefault(File.Exists);

    // The runtime's trusted platform assemblies include the host application's own dependencies; only those of a shared
    // framework are ones the staged closure would bind to.
    private static Dictionary<string, string> FindSharedFrameworkAssemblies()
    {
        var assemblies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is not string trusted)
        {
            return assemblies;
        }

        // The runtime directory is <shared root>/<framework name>/<version>.
        string runtimeDirectory = Path.TrimEndingDirectorySeparator(RuntimeEnvironment.GetRuntimeDirectory());
        string? sharedRoot = Path.GetDirectoryName(Path.GetDirectoryName(runtimeDirectory));
        foreach (string path in trusted.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.IsNullOrEmpty(sharedRoot)
                || path.StartsWith(sharedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                assemblies.TryAdd(Path.GetFileNameWithoutExtension(path), path);
            }
        }

        return assemblies;
    }
}
