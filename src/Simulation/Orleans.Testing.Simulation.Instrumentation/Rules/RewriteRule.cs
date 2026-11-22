using Orleans.Testing.Simulation.Runtime.Policy;

namespace Orleans.Testing.Simulation.Instrumentation.Rules;

/// <summary>
/// A single versioned instruction to the rewrite engine: at every site matching <see cref="Target"/>,
/// perform <see cref="Operation"/> using <see cref="Replacement"/>. A rule also carries its simulation
/// policy classification (<see cref="Policy"/>) and the range of target runtimes it supports
/// (<see cref="SupportedRuntimes"/>).
/// </summary>
/// <remarks>
/// Rules are pure data and Mono.Cecil-free, so a rule set can be authored, versioned, and hashed
/// independently of the engine. The <see cref="Policy"/> integrates the simulation API-policy model:
/// a <see cref="SimulationApiPolicy.Controlled"/> target is redirected/wrapped, a
/// <see cref="SimulationApiPolicy.Rejected"/> target is rejected. APIs without rules are left
/// unchanged, keeping the engine decoupled from concrete BCL shims.
/// </remarks>
public sealed record RewriteRule
{
    /// <summary>Gets the stable, unique-within-a-rule-set identity of this rule.</summary>
    public required string Id { get; init; }

    /// <summary>Gets the transformation this rule performs.</summary>
    public required RewriteOperationKind Operation { get; init; }

    /// <summary>Gets the member or type this rule matches.</summary>
    public required MemberSignature Target { get; init; }

    /// <summary>Gets the replacement member or type this rule redirects to.</summary>
    public required RewriteReplacement Replacement { get; init; }

    /// <summary>
    /// Gets the replacement for a non-virtual <c>call</c> of the target: the base call with which an override invokes
    /// the implementation it overrides. A <see cref="Replacement"/> which dispatches to an override as a
    /// <c>callvirt</c> does would re-enter that override, so a rule over a virtual member whose replacement dispatches
    /// gives its base calls a replacement which runs the base implementation. <see langword="null"/> redirects every
    /// call site to <see cref="Replacement"/>.
    /// </summary>
    public RewriteReplacement? BaseCallReplacement { get; init; }

    /// <summary>Gets the simulation API-policy classification of the target. Defaults to <see cref="SimulationApiPolicy.Controlled"/>.</summary>
    public SimulationApiPolicy Policy { get; init; } = SimulationApiPolicy.Controlled;

    /// <summary>Gets the range of target runtimes this rule supports. Defaults to <see cref="RuntimeVersionRange.All"/>.</summary>
    public RuntimeVersionRange SupportedRuntimes { get; init; } = RuntimeVersionRange.All;

    /// <summary>Gets an optional human-readable description of the rule's intent.</summary>
    public string? Description { get; init; }

    /// <summary>Gets the kinds of site the rule rewrites, which the matcher and runtime-support checks share.</summary>
    internal RewriteSites Sites => Operation switch
    {
        RewriteOperationKind.RedirectCall or RewriteOperationKind.WrapAfterCall => RewriteSites.Call,
        RewriteOperationKind.RedirectNewObj => RewriteSites.NewObj,
        RewriteOperationKind.InjectRejection => RewriteSites.Call | RewriteSites.NewObj,
        RewriteOperationKind.SubstituteType => RewriteSites.Type,
        _ => RewriteSites.None,
    };

    /// <summary>Creates a rule that redirects a <c>call</c>/<c>callvirt</c> to a static replacement method.</summary>
    public static RewriteRule RedirectCall(string id, MemberSignature target, RewriteReplacement replacement,
        SimulationApiPolicy policy = SimulationApiPolicy.Controlled) =>
        new() { Id = id, Operation = RewriteOperationKind.RedirectCall, Target = target, Replacement = replacement, Policy = policy };

    /// <summary>Creates a rule that redirects a <c>newobj</c> to a static factory method.</summary>
    public static RewriteRule RedirectNewObj(string id, MemberSignature target, RewriteReplacement replacement,
        SimulationApiPolicy policy = SimulationApiPolicy.Controlled) =>
        new() { Id = id, Operation = RewriteOperationKind.RedirectNewObj, Target = target, Replacement = replacement, Policy = policy };

    /// <summary>Creates a rule that substitutes references to a type with another type.</summary>
    public static RewriteRule SubstituteType(string id, string targetTypeFullName, RewriteReplacement replacementType,
        SimulationApiPolicy policy = SimulationApiPolicy.Controlled) =>
        new()
        {
            Id = id,
            Operation = RewriteOperationKind.SubstituteType,
            Target = MemberSignature.Type(targetTypeFullName),
            Replacement = replacementType,
            Policy = policy,
        };

    /// <summary>Creates a rule that inserts a post-call wrapper after a matched call.</summary>
    public static RewriteRule WrapAfterCall(string id, MemberSignature target, RewriteReplacement replacement,
        SimulationApiPolicy policy = SimulationApiPolicy.Controlled) =>
        new() { Id = id, Operation = RewriteOperationKind.WrapAfterCall, Target = target, Replacement = replacement, Policy = policy };

    /// <summary>Creates a rule that injects a deterministic rejection before a matched invocation.</summary>
    public static RewriteRule InjectRejection(string id, MemberSignature target, RewriteReplacement rejectionMethod) =>
        new()
        {
            Id = id,
            Operation = RewriteOperationKind.InjectRejection,
            Target = target,
            Replacement = rejectionMethod,
            Policy = SimulationApiPolicy.Rejected,
        };

    /// <summary>Returns a stable canonical string for signature hashing and diagnostics.</summary>
    public string ToCanonicalString()
    {
        var canonical = new CanonicalEncoding(nameof(RewriteRule));
        canonical.AddString(nameof(Id), Id);
        canonical.AddString(nameof(Operation), Operation.ToString());
        canonical.AddString(nameof(Target), Target.ToCanonicalString());
        canonical.AddString(nameof(Replacement), Replacement.ToCanonicalString());
        if (BaseCallReplacement is { } baseCallReplacement)
        {
            canonical.AddString(nameof(BaseCallReplacement), baseCallReplacement.ToCanonicalString());
        }
        canonical.AddString(nameof(Policy), Policy.ToString());
        canonical.AddString(nameof(SupportedRuntimes), SupportedRuntimes.ToCanonicalString());
        canonical.AddString(nameof(Description), Description);
        return canonical.ToString();
    }
}
