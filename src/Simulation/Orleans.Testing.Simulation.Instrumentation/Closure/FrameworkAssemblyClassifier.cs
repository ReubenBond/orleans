using System.Collections.Immutable;

namespace Orleans.Testing.Simulation.Instrumentation.Closure;

/// <summary>
/// Classifies an assembly (by its simple name) as a framework/reference assembly that must not be
/// rewritten. Framework assemblies are the runtime's own libraries (the BCL and shared framework);
/// rewriting them is out of scope for Clockwork's build integration and would change deterministic,
/// signed runtime components. The default set follows the conventional runtime prefixes; callers may
/// supply an additional set for platform- or product-specific components.
/// </summary>
public sealed class FrameworkAssemblyClassifier
{
    private static readonly ImmutableHashSet<string> DefaultExactNames = ImmutableHashSet.Create(
        StringComparer.OrdinalIgnoreCase,
        "mscorlib",
        "netstandard",
        "System",
        "WindowsBase",
        "PresentationCore",
        "PresentationFramework",
        "Microsoft.CSharp",
        "Microsoft.VisualBasic",
        "Microsoft.VisualBasic.Core");

    private static readonly ImmutableArray<string> DefaultPrefixes =
    [
        "System.",
        "Microsoft.NETCore.",
        "Microsoft.Win32.",
        "Microsoft.Extensions.DependencyModel",
    ];

    private readonly ImmutableHashSet<string> _exactNames;
    private readonly ImmutableArray<string> _prefixes;

    /// <summary>Initializes a new classifier using the default framework name set.</summary>
    public FrameworkAssemblyClassifier()
        : this(null)
    {
    }

    /// <summary>
    /// Initializes a new classifier, adding <paramref name="additionalExactNames"/> to the default
    /// set of framework assembly names.
    /// </summary>
    /// <param name="additionalExactNames">Extra simple assembly names to treat as framework, or <see langword="null"/>.</param>
    public FrameworkAssemblyClassifier(IEnumerable<string>? additionalExactNames)
    {
        _prefixes = DefaultPrefixes;
        _exactNames = additionalExactNames is null
            ? DefaultExactNames
            : DefaultExactNames.Union(additionalExactNames);
    }

    /// <summary>Gets the default classifier instance.</summary>
    public static FrameworkAssemblyClassifier Default { get; } = new();

    /// <summary>
    /// Returns <see langword="true"/> if an assembly with the given simple name is a framework or
    /// reference assembly that must not be rewritten.
    /// </summary>
    /// <param name="simpleName">The simple assembly name (no extension, no path).</param>
    /// <returns>Whether the assembly is a framework assembly.</returns>
    public bool IsFrameworkAssembly(string simpleName)
    {
        ArgumentException.ThrowIfNullOrEmpty(simpleName);
        if (_exactNames.Contains(simpleName))
        {
            return true;
        }

        foreach (string prefix in _prefixes)
        {
            if (simpleName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
