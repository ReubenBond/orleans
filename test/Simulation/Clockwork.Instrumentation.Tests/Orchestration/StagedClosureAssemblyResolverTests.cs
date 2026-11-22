using System.Runtime.InteropServices;
using Orleans.Testing.Simulation.Instrumentation.Closure;
using Orleans.Testing.Simulation.Instrumentation.Orchestration;
using Orleans.Testing.Simulation.Instrumentation.Tests.Infrastructure;
using Mono.Cecil;

namespace Orleans.Testing.Simulation.Instrumentation.Tests.Orchestration;

/// <summary>
/// The staged closure is checked against the assemblies the runtime binds when it runs from its staging directory: the
/// runtime's own framework assemblies, and the closure's assemblies in the staging directory itself. Another target
/// framework's build in a subdirectory, and its framework facades, are not candidates.
/// </summary>
public sealed class StagedClosureAssemblyResolverTests : IDisposable
{
    private readonly string _staging = TestArtifacts.CreateUnique("cwr-staged-resolver");

    [Fact]
    public void FrameworkAssemblyResolvesToTheRuntimesCopyRatherThanAStagedFacade()
    {
        string nested = Directory.CreateDirectory(Path.Combine(_staging, "net472")).FullName;
        TypeForwarders.Write(_staging, "System.Runtime", "System.Object", "netstandard");
        TypeForwarders.Write(nested, "System.Runtime", "System.Object", "netstandard");
        using var resolver = new StagedClosureAssemblyResolver(_staging, FrameworkAssemblyClassifier.Default);

        string runtimeCopy = Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "System.Runtime.dll");
        Assert.Equal(Path.GetFullPath(runtimeCopy), Path.GetFullPath(resolver.Locate("System.Runtime")!), ignoreCase: true);

        // The staged facades would forward System.Object to netstandard; the runtime's forwards it to its core library.
        TypeDefinition? resolved = ResolveFromSystemRuntime(resolver, "System.Object");
        Assert.Equal(typeof(object).Assembly.GetName().Name, resolved?.Module.Assembly.Name.Name);
    }

    [Fact]
    public void OtherAssemblyResolvesFromTheStagingDirectoryOnly()
    {
        string nested = Directory.CreateDirectory(Path.Combine(_staging, "net472")).FullName;
        string staged = TypeForwarders.Write(_staging, "Fx.Staged", "Fx.Marker", "Fx.Elsewhere");
        TypeForwarders.Write(nested, "Fx.Nested", "Fx.Marker", "Fx.Elsewhere");
        using var resolver = new StagedClosureAssemblyResolver(_staging, FrameworkAssemblyClassifier.Default);

        Assert.Equal(staged, resolver.Locate("Fx.Staged"));
        Assert.Null(resolver.Locate("Fx.Nested"));
        Assert.Throws<AssemblyResolutionException>(() => resolver.Resolve(new AssemblyNameReference("Fx.Nested", new Version(0, 0, 0, 0))));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_staging, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }

    private static TypeDefinition? ResolveFromSystemRuntime(StagedClosureAssemblyResolver resolver, string fullName)
    {
        int separator = fullName.LastIndexOf('.');
        using ModuleDefinition user = ModuleDefinition.CreateModule(
            "Fx.User",
            new ModuleParameters { Kind = ModuleKind.Dll, AssemblyResolver = resolver, MetadataResolver = resolver.Guard });
        var reference = new TypeReference(
            fullName[..separator], fullName[(separator + 1)..], user, new AssemblyNameReference("System.Runtime", new Version(10, 0, 0, 0)));
        return reference.Resolve();
    }
}
