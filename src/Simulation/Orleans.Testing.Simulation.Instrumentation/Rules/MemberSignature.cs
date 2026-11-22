using System.Collections.Immutable;

namespace Orleans.Testing.Simulation.Instrumentation.Rules;

/// <summary>
/// A stable, tooling-authored identifier for a member (or a whole type) targeted by a
/// <see cref="RewriteRule"/>. Matching is performed against Mono.Cecil references at rewrite time
/// (see the engine's internal matcher); this type is pure data so rule sets can be authored and
/// hashed without a dependency on Mono.Cecil.
/// </summary>
/// <remarks>
/// <para>
/// <paramref name="DeclaringTypeFullName"/> uses Cecil's full-name convention: namespace-qualified,
/// nested types joined with <c>/</c>, and open generic types carrying their arity backtick (e.g.
/// <c>System.Collections.Generic.List`1</c>). For a generic-instance declaring type, matching
/// normalizes to the open element type's full name.
/// </para>
/// <para>
/// A <see langword="null"/> <paramref name="MemberName"/> denotes that the signature targets the
/// <em>type itself</em> (used by <see cref="RewriteOperationKind.SubstituteType"/>). A
/// <see langword="null"/> <paramref name="ParameterTypeFullNames"/> means "match any overload with
/// this member name"; a non-null list matches the parameter count and each parameter's Cecil full
/// name exactly (including <c>&amp;</c> for by-ref, <c>[]</c> for arrays, and <c>*</c> for pointers),
/// enabling precise overload disambiguation.
/// </para>
/// </remarks>
/// <param name="DeclaringTypeFullName">The Cecil full name of the declaring type.</param>
/// <param name="MemberName">The member name (<c>.ctor</c> for constructors), or <see langword="null"/> to target the type itself.</param>
/// <param name="ParameterTypeFullNames">The parameter type full names for overload disambiguation, or <see langword="null"/> to match any overload.</param>
public readonly record struct MemberSignature(
    string DeclaringTypeFullName,
    string? MemberName = null,
    ImmutableArray<string> ParameterTypeFullNames = default)
{
    /// <summary>Gets a value indicating whether this signature targets a type rather than a member.</summary>
    public bool IsTypeOnly => MemberName is null;

    /// <summary>Gets a value indicating whether parameter types are specified for overload matching.</summary>
    public bool HasParameterConstraint => !ParameterTypeFullNames.IsDefault;

    /// <summary>Creates a signature for a method with the given parameter type full names.</summary>
    public static MemberSignature Method(string declaringTypeFullName, string memberName, params string[] parameterTypeFullNames) =>
        new(declaringTypeFullName, memberName, [.. parameterTypeFullNames]);

    /// <summary>Creates a signature for a constructor with the given parameter type full names.</summary>
    public static MemberSignature Constructor(string declaringTypeFullName, params string[] parameterTypeFullNames) =>
        new(declaringTypeFullName, ".ctor", [.. parameterTypeFullNames]);

    /// <summary>Creates a signature that targets a type itself (for type substitution).</summary>
    public static MemberSignature Type(string declaringTypeFullName) => new(declaringTypeFullName);

    /// <summary>Returns a stable canonical string for signature hashing and diagnostics.</summary>
    public string ToCanonicalString()
    {
        var canonical = new CanonicalEncoding(nameof(MemberSignature));
        canonical.AddString(nameof(DeclaringTypeFullName), DeclaringTypeFullName);
        canonical.AddString(nameof(MemberName), MemberName);
        canonical.AddStringArray(nameof(ParameterTypeFullNames), ParameterTypeFullNames);
        return canonical.ToString();
    }

    /// <inheritdoc/>
    public override string ToString() => ToCanonicalString();
}
