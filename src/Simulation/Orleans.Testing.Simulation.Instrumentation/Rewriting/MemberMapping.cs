using Mono.Cecil;

namespace Orleans.Testing.Simulation.Instrumentation.Rewriting;

/// <summary>The outcome of projecting the type substitutions onto a method or field reference.</summary>
internal enum MemberMappingKind
{
    /// <summary>The reference mentions no substituted type and is left unchanged.</summary>
    Unaffected,

    /// <summary>The reference maps to <see cref="MemberMapping{TMember}.Reference"/>.</summary>
    Mapped,

    /// <summary>The reference mentions a substituted type, but no reference with the substituted signature binds.</summary>
    Unbindable,
}

/// <summary>
/// The result of <see cref="MemberSubstitutionMapper.MapMethod"/> or <see cref="MemberSubstitutionMapper.MapField"/>:
/// unaffected, mapped to a reference, or unbindable for a stated reason, which the caller reports as a rewrite error
/// instead of emitting a reference that throws <see cref="MissingMemberException"/> when the runtime binds it.
/// </summary>
/// <typeparam name="TMember">The kind of member reference.</typeparam>
internal readonly record struct MemberMapping<TMember>
    where TMember : MemberReference
{
    private MemberMapping(MemberMappingKind kind, TMember? reference, bool verifyAfterPass, string? reason)
    {
        Kind = kind;
        Reference = reference;
        VerifyAfterPass = verifyAfterPass;
        Reason = reason;
    }

    /// <summary>Gets the outcome.</summary>
    public MemberMappingKind Kind { get; }

    /// <summary>Gets the mapped reference when <see cref="Kind"/> is <see cref="MemberMappingKind.Mapped"/>.</summary>
    public TMember? Reference { get; }

    /// <summary>
    /// Gets a value indicating whether the mapped reference keeps a declaring type of the module being rewritten, so it
    /// binds only if that type is rewritten too, which is known once the pass has visited every type.
    /// </summary>
    public bool VerifyAfterPass { get; }

    /// <summary>Gets why no substituted reference binds when <see cref="Kind"/> is <see cref="MemberMappingKind.Unbindable"/>.</summary>
    public string? Reason { get; }

    /// <summary>Gets the unaffected outcome.</summary>
    public static MemberMapping<TMember> Unaffected => default;

    /// <summary>Creates a mapped outcome.</summary>
    public static MemberMapping<TMember> Mapped(TMember reference, bool verifyAfterPass) =>
        new(MemberMappingKind.Mapped, reference, verifyAfterPass, reason: null);

    /// <summary>Creates an unbindable outcome.</summary>
    public static MemberMapping<TMember> Unbindable(string reason) =>
        new(MemberMappingKind.Unbindable, reference: null, verifyAfterPass: false, reason);
}
