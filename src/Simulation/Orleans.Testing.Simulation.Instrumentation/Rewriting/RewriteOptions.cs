using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace Orleans.Testing.Simulation.Instrumentation.Rewriting;

/// <summary>
/// Configures a single rewrite: where to resolve replacement ("shim") assemblies and additional
/// references, the target runtime the rules are evaluated against, type-name exclusions, and symbol
/// handling. Options are immutable; use the <c>with</c> expression to derive variants.
/// </summary>
public sealed record RewriteOptions
{
    /// <summary>
    /// Gets the paths of the replacement assemblies that declare the members rules redirect to. The
    /// engine loads these to import replacement references into the rewritten assembly.
    /// </summary>
    public ImmutableArray<string> ReplacementAssemblyPaths { get; init; } = [];

    /// <summary>Gets additional directories searched when resolving assembly references.</summary>
    public ImmutableArray<string> ReferenceSearchDirectories { get; init; } = [];

    /// <summary>
    /// Gets the feature version (major.minor) of the target runtime the rules are evaluated against. The rule set
    /// must support it (see <see cref="Rules.RuleSetRuntimeSupport"/>), and each rewritten assembly records the
    /// runtimes the rules support, which a simulation checks when it starts.
    /// </summary>
    public required Version TargetRuntime { get; init; }

    /// <summary>
    /// Gets the Cecil full names of types excluded from rewriting (in addition to types marked with
    /// <see cref="Attributes.DoNotRewriteAttribute"/>). As with the attribute, the compiler-generated types nested
    /// in an excluded type are excluded with it, transitively.
    /// </summary>
    public ImmutableArray<string> ExcludedTypeFullNames { get; init; } = [];

    /// <summary>
    /// Gets a value indicating whether an unresolved <em>optional</em> assembly reference (one not
    /// targeted by any rule) is reported as a warning. Unresolved <em>targeted</em> members always
    /// fail regardless of this setting. Defaults to <see langword="true"/>.
    /// </summary>
    public bool WarnOnUnresolvedReferences { get; init; } = true;

    /// <summary>
    /// Gets a value indicating whether to compute the SHA-256 of the written output for the manifest.
    /// Defaults to <see langword="true"/>.
    /// </summary>
    public bool ComputeOutputHash { get; init; } = true;

    /// <summary>
    /// Gets a value indicating whether to run the exception-hardening pass, which injects a guard at the
    /// start of every broad <c>catch (Exception)</c> / <c>catch</c> block and every exception <c>filter</c>
    /// so a rewritten assembly's user handlers cannot swallow the scheduler's internal control-flow signal.
    /// When enabled, the shim assembly declaring
    /// <c>Orleans.Testing.Simulation.Runtime.SimulationExceptionGuard.ThrowIfControlSignal</c> must be supplied in
    /// <see cref="ReplacementAssemblyPaths"/>. Defaults to <see langword="false"/> (the built-in
    /// controlled-task activation turns it on); narrow typed catches, finally blocks, rethrow-only
    /// handlers, and compiler-generated async-state-machine handlers are never instrumented, so normal
    /// application exception handling is unchanged. The closure runner enables this automatically
    /// when the effective rule set contains built-in controlled-task rules.
    /// </summary>
    public bool HardenExceptionHandlers { get; init; }

    /// <summary>
    /// Gets a value indicating whether to run the cross-assembly task-detection pass, which emits a
    /// <see cref="Diagnostics.RewriteDiagnosticIds.UncontrolledTaskReturn"/> warning at every call site that
    /// invokes a method in an uncontrolled assembly (one that is neither being rewritten, nor part of the
    /// BCL, nor the Clockwork runtime shim) and returns a <see cref="System.Threading.Tasks.Task"/>,
    /// <see cref="System.Threading.Tasks.ValueTask"/>, or other awaitable whose continuation could escape the
    /// deterministic scheduler. The escape is surfaced with a precise source/IL call-site diagnostic rather
    /// than being silently accepted. Defaults to <see langword="false"/> and is available to direct rewrite-engine
    /// callers; the closure runner does not enable it because a per-assembly pass cannot distinguish an excluded
    /// dependency from a sibling assembly that is rewritten in the same closure. BCL <c>System.*</c> calls are
    /// intentionally not flagged here.
    /// </summary>
    public bool DetectUncontrolledTasks { get; init; }

    /// <summary>
    /// Gets a value indicating whether the rewrite injects fine-grained scheduling and race-access
    /// instrumentation. Defaults to <see langword="false"/> so controlled mode has no memory or
    /// control-flow instrumentation overhead.
    /// </summary>
    public bool InstrumentRaceExploration { get; init; }

    internal ImmutableArray<string> StrongNameAssemblyNames { get; init; } = [];

    /// <summary>
    /// Gets the simple names of the assemblies the closure rewrites with the same rule set and options. A member
    /// reference whose signature the rewrite substitutes, but whose declaring type it keeps, binds only where that
    /// type's definition is rewritten too; one declared in another assembly is admitted only if that assembly is in
    /// this set, and otherwise fails the rewrite. The closure runner supplies the set; for a direct engine caller that
    /// does not, no other assembly is rewritten, so every such reference fails closed.
    /// </summary>
    internal ImmutableArray<string> RewrittenAssemblyNames { get; init; } = [];

    /// <summary>
    /// Computes a canonical fingerprint of every option which can affect rewritten output,
    /// diagnostics, or manifest content. Set-like exclusions are sorted so equivalent orderings
    /// produce the same fingerprint; resolver path order is preserved because it controls precedence.
    /// </summary>
    public string ComputeSemanticFingerprint()
    {
        var canonical = new CanonicalEncoding(nameof(RewriteOptions));
        AppendPaths(canonical, nameof(ReplacementAssemblyPaths), ReplacementAssemblyPaths, sort: false);
        AppendPaths(canonical, nameof(ReferenceSearchDirectories), ReferenceSearchDirectories, sort: false);
        canonical.AddString(nameof(TargetRuntime), TargetRuntime.ToString());
        AppendPaths(canonical, nameof(ExcludedTypeFullNames), ExcludedTypeFullNames, sort: true);
        canonical.AddBoolean(nameof(WarnOnUnresolvedReferences), WarnOnUnresolvedReferences);
        canonical.AddBoolean(nameof(ComputeOutputHash), ComputeOutputHash);
        canonical.AddBoolean(nameof(HardenExceptionHandlers), HardenExceptionHandlers);
        canonical.AddBoolean(nameof(DetectUncontrolledTasks), DetectUncontrolledTasks);
        canonical.AddBoolean(nameof(InstrumentRaceExploration), InstrumentRaceExploration);
        AppendPaths(canonical, nameof(StrongNameAssemblyNames), StrongNameAssemblyNames, sort: true);
        AppendPaths(canonical, nameof(RewrittenAssemblyNames), RewrittenAssemblyNames, sort: true);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private static void AppendPaths(
        CanonicalEncoding canonical,
        string name,
        ImmutableArray<string> values,
        bool sort)
    {
        IEnumerable<string> normalized = values
            .Select(static value => value.Replace('\\', '/'))
            .Where(static value => value.Length > 0);
        if (sort)
        {
            normalized = normalized.Distinct(StringComparer.Ordinal).OrderBy(static value => value, StringComparer.Ordinal);
        }

        canonical.AddStringSequence(name, normalized);
    }
}
