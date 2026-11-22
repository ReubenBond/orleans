using System.Collections.Immutable;
using Orleans.Testing.Simulation.Instrumentation.Diagnostics;
using Orleans.Testing.Simulation.Instrumentation.Manifest;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Orleans.Testing.Simulation.Instrumentation.Rewriting;

/// <summary>
/// Shared, mutable state threaded through every <see cref="RewritePass"/> for a single rewrite of a
/// single assembly. Passes reach the rule index and replacement resolver through it, and record
/// diagnostics, manifest transformations, and unresolved references into it. The engine reads the
/// accumulated results back out once all passes have run.
/// </summary>
internal sealed class RewriteSession
{
    private readonly List<RewriteDiagnostic> _diagnostics = [];
    private readonly List<ManifestTransformation> _transformations = [];
    private readonly SortedSet<string> _unresolvedReferences = new(StringComparer.Ordinal);

    public RewriteSession(
        ModuleDefinition targetModule,
        RewriteRuleMatcher matcher,
        ReplacementResolver resolver,
        IReadOnlySet<string> excludedTypeFullNames,
        IEnumerable<string> rewrittenAssemblyNames)
    {
        TargetModule = targetModule;
        Matcher = matcher;
        Resolver = resolver;
        ExcludedTypeFullNames = excludedTypeFullNames;
        RewrittenAssemblyNames = new HashSet<string>(rewrittenAssemblyNames, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Gets the module being rewritten.</summary>
    public ModuleDefinition TargetModule { get; }

    /// <summary>Gets the rule index for the active rule set.</summary>
    public RewriteRuleMatcher Matcher { get; }

    /// <summary>Gets the resolver that imports replacement members into <see cref="TargetModule"/>.</summary>
    public ReplacementResolver Resolver { get; }

    /// <summary>Gets the full names of the types excluded from rewriting, which no pass visits.</summary>
    public IReadOnlySet<string> ExcludedTypeFullNames { get; }

    /// <summary>
    /// Gets the simple names of the assemblies rewritten with the same rule set and options in the closure (see
    /// <see cref="RewriteOptions.RewrittenAssemblyNames"/>), compared as the runtime binds simple names.
    /// </summary>
    public IReadOnlySet<string> RewrittenAssemblyNames { get; }

    /// <summary>Gets a value indicating whether any error-severity diagnostic has been recorded.</summary>
    public bool HasErrors { get; private set; }

    /// <summary>Gets a value indicating whether <paramref name="type"/> is excluded from rewriting.</summary>
    public bool IsExcluded(TypeDefinition type) => ExcludedTypeFullNames.Contains(type.FullName);

    /// <summary>Records a diagnostic, tracking whether it is an error.</summary>
    public void AddDiagnostic(RewriteDiagnostic diagnostic)
    {
        _diagnostics.Add(diagnostic);
        if (diagnostic.IsError)
        {
            HasErrors = true;
        }
    }

    /// <summary>Records a manifest transformation entry for a site the engine acted on.</summary>
    public void AddTransformation(ManifestTransformation transformation) => _transformations.Add(transformation);

    /// <summary>Records an unresolved reference name for the manifest.</summary>
    public void AddUnresolvedReference(string reference) => _unresolvedReferences.Add(reference);

    /// <summary>Returns the recorded diagnostics in insertion order.</summary>
    public ImmutableArray<RewriteDiagnostic> GetDiagnostics() => [.. _diagnostics];

    /// <summary>Returns the recorded transformations in insertion order.</summary>
    public ImmutableArray<ManifestTransformation> GetTransformations() => [.. _transformations];

    /// <summary>Returns the recorded unresolved references in stable order.</summary>
    public ImmutableArray<string> GetUnresolvedReferences() => [.. _unresolvedReferences];

    /// <summary>
    /// Resolves the source file and line for an instruction from the method's debug information, if
    /// portable/embedded symbols are present. Returns <see langword="false"/> (with a <c>-1</c> line)
    /// when no sequence point covers the instruction.
    /// </summary>
    public static bool TryGetSequencePoint(MethodDefinition method, Instruction instruction, out string? file, out int line)
    {
        file = null;
        line = -1;

        if (method.DebugInformation is not { HasSequencePoints: true })
        {
            return false;
        }

        SequencePoint? point = method.DebugInformation.GetSequencePoint(instruction);

        // Fall back to the closest preceding sequence point so a rewritten call still maps to source.
        if (point is null)
        {
            foreach (Instruction current in method.Body.Instructions)
            {
                if (current.Offset > instruction.Offset)
                {
                    break;
                }

                SequencePoint? candidate = method.DebugInformation.GetSequencePoint(current);
                if (candidate is not null && !candidate.IsHidden)
                {
                    point = candidate;
                }
            }
        }

        if (point is null || point.IsHidden)
        {
            return false;
        }

        file = point.Document?.Url;
        line = point.StartLine;
        return true;
    }
}
