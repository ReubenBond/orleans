using System.Collections.Immutable;
using Orleans.Testing.Simulation.Instrumentation.Diagnostics;
using Orleans.Testing.Simulation.Instrumentation.Manifest;

namespace Orleans.Testing.Simulation.Instrumentation.Orchestration;

/// <summary>
/// The per-assembly outcome of an instrumentation run: which closure-relative assembly was processed,
/// whether it was rewritten (versus a verified idempotent no-op), whether it was re-signed, and the
/// deterministic manifest and diagnostics the engine produced for it.
/// </summary>
/// <param name="RelativePath">The assembly's path relative to the staging/source root.</param>
/// <param name="WasRewritten">Whether output IL was written by the engine (false for a verified no-op).</param>
/// <param name="WasNoOp">Whether the input was already rewritten with a matching signature.</param>
/// <param name="WasReSigned">Whether the staged output was re-signed with the configured strong-name key.</param>
/// <param name="ReadyToRunStripped">Whether a ReadyToRun native image was stripped to produce IL-only output.</param>
/// <param name="Manifest">The engine manifest for this assembly, or <see langword="null"/> if it was rejected before rewriting.</param>
/// <param name="Diagnostics">The diagnostics produced for this assembly (engine plus orchestrator diagnostics).</param>
public sealed record AssemblyInstrumentationResult(
    string RelativePath,
    bool WasRewritten,
    bool WasNoOp,
    bool WasReSigned,
    bool ReadyToRunStripped,
    InstrumentationManifest? Manifest,
    ImmutableArray<RewriteDiagnostic> Diagnostics)
{
    /// <summary>Gets the error-severity diagnostics for this assembly.</summary>
    public IEnumerable<RewriteDiagnostic> Errors => Diagnostics.Where(d => d.IsError);
}
