using System.Collections.Immutable;

namespace Orleans.Testing.Simulation.Instrumentation.Rules;

/// <summary>
/// Identifies the replacement member (or type) a <see cref="RewriteRule"/> redirects a matched site
/// to. Like <see cref="MemberSignature"/>, this is pure data: the engine resolves it against a
/// configured set of replacement ("shim") assemblies at rewrite time.
/// </summary>
/// <remarks>
/// For <see cref="RewriteOperationKind.RedirectCall"/>, <see cref="RewriteOperationKind.WrapAfterCall"/>,
/// and <see cref="RewriteOperationKind.InjectRejection"/>, the replacement names a <em>static</em>
/// method. For <see cref="RewriteOperationKind.RedirectNewObj"/> it names a static factory method.
/// For <see cref="RewriteOperationKind.SubstituteType"/>, <paramref name="MemberName"/> is
/// <see langword="null"/> and <paramref name="DeclaringTypeFullName"/> names the substitute type.
/// </remarks>
/// <param name="AssemblyName">The simple name of the assembly that declares the replacement.</param>
/// <param name="DeclaringTypeFullName">The Cecil full name of the type declaring the replacement (or the substitute type itself).</param>
/// <param name="MemberName">The replacement method name, or <see langword="null"/> for a type substitution.</param>
/// <param name="ParameterTypeFullNames">The replacement method's parameter full names for overload disambiguation, or <see langword="null"/> to match by name.</param>
public readonly record struct RewriteReplacement(
    string AssemblyName,
    string DeclaringTypeFullName,
    string? MemberName = null,
    ImmutableArray<string> ParameterTypeFullNames = default)
{
    /// <summary>Gets a value indicating whether this replacement is a type substitution.</summary>
    public bool IsTypeOnly => MemberName is null;

    /// <summary>Gets a value indicating whether parameter types are specified for overload matching.</summary>
    public bool HasParameterConstraint => !ParameterTypeFullNames.IsDefault;

    /// <summary>Creates a replacement pointing at a static method.</summary>
    public static RewriteReplacement Method(string assemblyName, string declaringTypeFullName, string memberName, params string[] parameterTypeFullNames) =>
        new(assemblyName, declaringTypeFullName, memberName, parameterTypeFullNames.Length == 0 ? default : [.. parameterTypeFullNames]);

    /// <summary>Creates a replacement pointing at a substitute type.</summary>
    public static RewriteReplacement Type(string assemblyName, string declaringTypeFullName) =>
        new(assemblyName, declaringTypeFullName);

    /// <summary>Returns a stable canonical string for signature hashing and diagnostics.</summary>
    public string ToCanonicalString()
    {
        var canonical = new CanonicalEncoding(nameof(RewriteReplacement));
        canonical.AddString(nameof(AssemblyName), AssemblyName);
        canonical.AddString(nameof(DeclaringTypeFullName), DeclaringTypeFullName);
        canonical.AddString(nameof(MemberName), MemberName);
        canonical.AddStringArray(nameof(ParameterTypeFullNames), ParameterTypeFullNames);
        return canonical.ToString();
    }

    /// <inheritdoc/>
    public override string ToString() => ToCanonicalString();
}
