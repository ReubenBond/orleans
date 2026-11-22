using Mono.Cecil;

namespace Orleans.Testing.Simulation.Instrumentation.Tests.Infrastructure;

/// <summary>Writes assemblies that declare no types and forward one type to another assembly.</summary>
internal static class TypeForwarders
{
    /// <summary>Writes <paramref name="assemblyName"/>.dll forwarding <paramref name="type"/> to <paramref name="forwardedTo"/>.</summary>
    /// <param name="directory">The directory to write the assembly in.</param>
    /// <param name="assemblyName">The simple name of the assembly to write.</param>
    /// <param name="type">The namespace-qualified name of the forwarded type.</param>
    /// <param name="forwardedTo">The simple name of the assembly the type is forwarded to.</param>
    /// <returns>The path of the written assembly.</returns>
    public static string Write(string directory, string assemblyName, string type, string forwardedTo)
    {
        int separator = type.LastIndexOf('.');
        using AssemblyDefinition assembly = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition(assemblyName, new Version(0, 0, 0, 0)),
            assemblyName + ".dll",
            ModuleKind.Dll);
        var target = new AssemblyNameReference(forwardedTo, new Version(0, 0, 0, 0));
        assembly.MainModule.AssemblyReferences.Add(target);
        assembly.MainModule.ExportedTypes.Add(
            new ExportedType(type[..separator], type[(separator + 1)..], assembly.MainModule, target) { IsForwarder = true });

        string path = Path.Combine(directory, assemblyName + ".dll");
        assembly.Write(path);
        return path;
    }
}
