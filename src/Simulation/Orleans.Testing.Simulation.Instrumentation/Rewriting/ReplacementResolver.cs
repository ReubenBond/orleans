using Orleans.Testing.Simulation.Instrumentation.Rules;
using Mono.Cecil;

namespace Orleans.Testing.Simulation.Instrumentation.Rewriting;

/// <summary>
/// Loads the replacement ("shim") assemblies referenced by a rule set and resolves a
/// <see cref="RewriteReplacement"/> to a Mono.Cecil reference imported into the assembly being
/// rewritten. Owns the loaded shim modules, which must stay alive until after the rewritten output
/// has been written, so this type is disposable.
/// </summary>
internal sealed class ReplacementResolver : IDisposable
{
    private readonly Dictionary<string, ModuleDefinition> _modulesByAssemblyName = new(StringComparer.Ordinal);
    private readonly List<AssemblyDefinition> _owned = [];
    private bool _disposed;

    public ReplacementResolver(IEnumerable<string> shimAssemblyPaths, IEnumerable<string> searchDirectories)
    {
        var resolver = new GuardedAssemblyResolver();
        foreach (string dir in searchDirectories)
        {
            if (!string.IsNullOrEmpty(dir))
            {
                resolver.AddSearchDirectory(dir);
            }
        }

        foreach (string path in shimAssemblyPaths)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                continue;
            }

            string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir))
            {
                resolver.AddSearchDirectory(dir);
            }

            AssemblyDefinition definition = AssemblyDefinition.ReadAssembly(path, new ReaderParameters
            {
                AssemblyResolver = resolver,
                MetadataResolver = resolver.Guard,
                ReadSymbols = false,
                InMemory = true,
            });

            _owned.Add(definition);
            _modulesByAssemblyName[definition.Name.Name] = definition.MainModule;
        }
    }

    /// <summary>
    /// Resolves a type-substitution replacement to a type reference imported into
    /// <paramref name="targetModule"/>.
    /// </summary>
    public bool TryResolveType(ModuleDefinition targetModule, RewriteReplacement replacement, out TypeReference imported, out string? error)
    {
        imported = null!;
        if (!TryFindType(replacement, out TypeDefinition? type, out error))
        {
            return false;
        }

        imported = targetModule.ImportReference(type);
        return true;
    }

    /// <summary>
    /// Resolves a method replacement to its (open) definition and a reference imported into
    /// <paramref name="targetModule"/>. The caller applies any generic instantiation.
    /// </summary>
    public bool TryResolveMethod(
        ModuleDefinition targetModule,
        RewriteReplacement replacement,
        out MethodReference importedOpen,
        out MethodDefinition definition,
        out string? error)
    {
        importedOpen = null!;
        definition = null!;

        if (!TryFindType(replacement, out TypeDefinition? type, out error))
        {
            return false;
        }

        var matches = new List<MethodDefinition>();
        foreach (MethodDefinition candidate in type.Methods)
        {
            if (candidate.Name != replacement.MemberName)
            {
                continue;
            }

            if (replacement.HasParameterConstraint)
            {
                if (candidate.Parameters.Count != replacement.ParameterTypeFullNames.Length)
                {
                    continue;
                }

                bool parametersMatch = true;
                for (int i = 0; i < candidate.Parameters.Count; i++)
                {
                    if (CecilNames.ParameterFullName(candidate.Parameters[i].ParameterType) != replacement.ParameterTypeFullNames[i])
                    {
                        parametersMatch = false;
                        break;
                    }
                }

                if (!parametersMatch)
                {
                    continue;
                }
            }

            matches.Add(candidate);
        }

        if (matches.Count == 0)
        {
            error = $"Replacement method '{replacement.ToCanonicalString()}' was not found.";
            return false;
        }

        if (matches.Count != 1)
        {
            error =
                $"Replacement method '{replacement.ToCanonicalString()}' is ambiguous: {matches.Count} same-name methods match. " +
                "Specify the full replacement parameter list.";
            return false;
        }

        MethodDefinition match = matches[0];
        definition = match;
        importedOpen = targetModule.ImportReference(match);
        return true;
    }

    private bool TryFindType(RewriteReplacement replacement, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out TypeDefinition? type, out string? error)
    {
        type = null;
        if (!_modulesByAssemblyName.TryGetValue(replacement.AssemblyName, out ModuleDefinition? module))
        {
            error = $"Replacement assembly '{replacement.AssemblyName}' was not provided to the engine.";
            return false;
        }

        type = module.GetType(replacement.DeclaringTypeFullName);
        if (type is null)
        {
            error = $"Replacement type '{replacement.DeclaringTypeFullName}' was not found in assembly '{replacement.AssemblyName}'.";
            return false;
        }

        error = null;
        return true;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        foreach (AssemblyDefinition definition in _owned)
        {
            definition.Dispose();
        }

        _disposed = true;
    }
}
