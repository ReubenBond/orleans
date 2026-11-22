namespace Orleans.Testing.Simulation.Instrumentation.Attributes;

/// <summary>
/// <para>
/// Legacy assembly-level marker read by <see cref="Rewriting.RewriteEngine"/> for compatibility
/// with previously rewritten assemblies. New rewrites use the framework's
/// <see cref="System.Reflection.AssemblyMetadataAttribute"/> so loading a rewritten assembly never
/// requires loading Clockwork's build-time instrumentation assembly. Its presence records the
/// engine version, the identity and version of the rule set
/// that was applied, a stable signature hash of that rule set (see
/// <see cref="Rules.RewriteRuleSet.ComputeSignature"/>), and the semantic rewrite-options fingerprint.
/// </para>
/// <para>
/// This marker is the basis of the engine's idempotence contract (idempotence requirement): running
/// the engine again over an already-rewritten assembly with the <em>same</em> rule-set signature and
/// options is a verified no-op, while a <em>different, incompatible</em> request fails clearly instead
/// of double-rewriting. The engine never inspects the CLR type at runtime - it reads the attribute's
/// stored arguments directly from assembly metadata via Mono.Cecil, so the attribute only needs to
/// exist as metadata, not to be loaded.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class ClockworkRewriteSignatureAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ClockworkRewriteSignatureAttribute"/> class.
    /// </summary>
    /// <param name="engineVersion">The <see cref="Rewriting.RewriteEngine"/> version that performed the rewrite.</param>
    /// <param name="ruleSetId">The stable identity of the applied <see cref="Rules.RewriteRuleSet"/>.</param>
    /// <param name="ruleSetVersion">The version of the applied rule set.</param>
    /// <param name="signature">A stable content hash of the applied rule set and engine version.</param>
    /// <param name="optionsFingerprint">A stable fingerprint of every rewrite option.</param>
    public ClockworkRewriteSignatureAttribute(
        string engineVersion,
        string ruleSetId,
        string ruleSetVersion,
        string signature,
        string optionsFingerprint)
    {
        EngineVersion = engineVersion;
        RuleSetId = ruleSetId;
        RuleSetVersion = ruleSetVersion;
        Signature = signature;
        OptionsFingerprint = optionsFingerprint;
    }

    /// <summary>Gets the engine version that performed the rewrite.</summary>
    public string EngineVersion { get; }

    /// <summary>Gets the identity of the applied rule set.</summary>
    public string RuleSetId { get; }

    /// <summary>Gets the version of the applied rule set.</summary>
    public string RuleSetVersion { get; }

    /// <summary>Gets the stable content hash of the applied rule set and engine version.</summary>
    public string Signature { get; }

    /// <summary>Gets the stable fingerprint of the rewrite options used to produce the assembly.</summary>
    public string OptionsFingerprint { get; }
}
