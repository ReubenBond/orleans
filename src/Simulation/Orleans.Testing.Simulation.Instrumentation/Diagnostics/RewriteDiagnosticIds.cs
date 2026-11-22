namespace Orleans.Testing.Simulation.Instrumentation.Diagnostics;

/// <summary>
/// Stable, machine-readable identifiers for the diagnostics the rewrite engine can emit. Kept as
/// constants (rather than an enum) so they serialize as stable strings in the manifest and so new
/// ids can be added without renumbering. The <c>CWR</c> prefix denotes "Clockwork rewrite".
/// </summary>
public static class RewriteDiagnosticIds
{
    /// <summary>A targeted member matched by a rule could not be resolved to a replacement.</summary>
    public const string UnresolvedReplacement = "CWR0001";

    /// <summary>An assembly reference could not be resolved.</summary>
    public const string UnresolvedReference = "CWR0003";

    /// <summary>Debug symbols were requested but are absent for the input assembly.</summary>
    public const string SymbolsAbsent = "CWR0004";

    /// <summary>The input assembly uses a symbol form the engine cannot preserve (e.g. native/Windows PDB).</summary>
    public const string UnsupportedSymbolForm = "CWR0005";

    /// <summary>A type was excluded from rewriting (via <see cref="Attributes.DoNotRewriteAttribute"/> or options).</summary>
    public const string TypeExcluded = "CWR0006";

    /// <summary>The assembly was already rewritten with a matching signature; rewriting was a verified no-op.</summary>
    public const string AlreadyRewritten = "CWR0007";

    /// <summary>The assembly was rewritten with an incompatible engine or rule-set version.</summary>
    public const string IncompatibleRewriteVersion = "CWR0008";

    /// <summary>Post-rewrite validation (read-back / integrity check) failed.</summary>
    public const string ValidationFailed = "CWR0009";

    /// <summary>
    /// The selected rules do not support the target runtime: a rule neither supports it nor has alternatives that do.
    /// Reported once, before anything is rewritten.
    /// </summary>
    public const string RuntimeOutOfRange = "CWR0010";

    /// <summary>A mixed-mode assembly cannot be rewritten by Mono.Cecil.</summary>
    public const string MixedModeAssembly = "CWR0011";

    /// <summary>A resolved replacement method is incompatible with the target invocation's IL stack contract.</summary>
    public const string ReplacementContractMismatch = "CWR0012";

    /// <summary>
    /// A method compiled with runtime async (<c>MethodImplOptions.Async</c>) suspends and resumes through the runtime's
    /// own continuation machinery, which a rule set that controls the async state machine cannot control.
    /// </summary>
    public const string RuntimeAsyncMethod = "CWR0013";

    /// <summary>A ReadyToRun input's native image was stripped, producing IL-only staged output.</summary>
    public const string ReadyToRunStripped = "CWR0101";

    /// <summary>A strong-named input requires re-signing but no usable identity-preserving key is available.</summary>
    public const string StrongNameReSignRequired = "CWR0102";

    /// <summary>A rewritten assembly was re-signed with the supplied strong-name key.</summary>
    public const string StrongNameReSigned = "CWR0103";

    /// <summary>An Authenticode-signed input's signature cannot be preserved across a rewrite and is dropped.</summary>
    public const string AuthenticodeDropped = "CWR0104";

    /// <summary>A rewritten assembly's strong-name identity was stripped and closure references were retargeted.</summary>
    public const string StrongNameStripped = "CWR0105";

    /// <summary>
    /// A rewritten call into an uncontrolled (non-rewritten, non-BCL, non-shim) assembly returns a
    /// <see cref="System.Threading.Tasks.Task"/>/<see cref="System.Threading.Tasks.ValueTask"/> or other
    /// awaitable whose continuation could escape the deterministic scheduler. The escape is surfaced rather
    /// than silently accepted (cross-assembly controlled-task enforcement).
    /// </summary>
    public const string UncontrolledTaskReturn = "CWR0200";

    /// <summary>A custom-awaitable return type could not be resolved, so task-like status could not be determined.</summary>
    public const string AwaitableResolutionFailed = "CWR0201";
}
