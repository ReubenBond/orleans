using Mono.Cecil;

namespace Orleans.Testing.Simulation.Instrumentation.Rewriting;

/// <summary>
/// A metadata resolver that stops at a cycle of type forwarders. Cecil follows an exported type to the assembly it
/// forwards to, so assemblies that forward a type back to each other - as facades built for different target frameworks
/// can - would be followed until the stack overflows, which terminates the process. A resolution that re-enters itself
/// throws <see cref="AssemblyResolutionException"/> instead, whose inner <see cref="TypeLoadException"/> describes the
/// cycle, and records the cycle in <see cref="Cycles"/>. Every module that may take part in a resolution must use the
/// same guard: the module being read, and every module its assembly resolver reads.
/// </summary>
/// <param name="assemblyResolver">The assembly resolver whose modules use this guard.</param>
internal sealed class ForwarderCycleGuard(IAssemblyResolver assemblyResolver) : MetadataResolver(assemblyResolver)
{
    private readonly List<(string Scope, string Type)> _resolving = [];
    private readonly List<string> _cycles = [];

    /// <summary>Gets a description of each forwarder cycle found, once each, in the order found.</summary>
    public IReadOnlyList<string> Cycles => _cycles;

    /// <inheritdoc/>
    public override TypeDefinition Resolve(TypeReference type)
    {
        TypeReference element = type.GetElementType();
        (string Scope, string Type) key = (element.Scope?.Name ?? string.Empty, element.FullName);
        int start = _resolving.IndexOf(key);
        if (start >= 0)
        {
            IEnumerable<string> chain = _resolving
                .Skip(start)
                .Where(entry => entry.Type == key.Type)
                .Select(entry => entry.Scope)
                .Append(key.Scope);
            string cycle = $"The type forwarders for '{key.Type}' form a cycle: {string.Join(" -> ", chain)}";
            if (!_cycles.Contains(cycle))
            {
                _cycles.Add(cycle);
            }

            throw new AssemblyResolutionException(
                element.Scope as AssemblyNameReference ?? new AssemblyNameReference(key.Scope, version: null),
                new TypeLoadException(cycle));
        }

        _resolving.Add(key);
        try
        {
            return base.Resolve(type);
        }
        finally
        {
            _resolving.RemoveAt(_resolving.Count - 1);
        }
    }
}

/// <summary>
/// A <see cref="DefaultAssemblyResolver"/> whose assemblies resolve types through one <see cref="ForwarderCycleGuard"/>,
/// so a cycle of type forwarders among them fails a resolution instead of overflowing the stack. The module the resolver
/// is created for must be read with <see cref="Guard"/> as its metadata resolver too.
/// </summary>
internal sealed class GuardedAssemblyResolver : DefaultAssemblyResolver
{
    /// <summary>Initializes a new instance of the <see cref="GuardedAssemblyResolver"/> class.</summary>
    public GuardedAssemblyResolver() => Guard = new ForwarderCycleGuard(this);

    /// <summary>Gets the metadata resolver of every module this resolver reads.</summary>
    public ForwarderCycleGuard Guard { get; }

    /// <inheritdoc/>
    public override AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        parameters.MetadataResolver ??= Guard;
        return base.Resolve(name, parameters);
    }
}
