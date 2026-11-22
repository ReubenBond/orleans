using System.Collections.Immutable;
using Orleans.Testing.Simulation.Instrumentation.Diagnostics;
using Orleans.Testing.Simulation.Instrumentation.Manifest;

namespace Orleans.Testing.Simulation.Instrumentation.Rewriting;

/// <summary>
/// The outcome of a single <see cref="RewriteEngine.Rewrite(RewriteRequest)"/> call: whether it
/// succeeded, whether it was a verified idempotent no-op, whether output was written, and the
/// deterministic <see cref="InstrumentationManifest"/> describing exactly what happened. Diagnostics
/// are surfaced both here and inside the manifest.
/// </summary>
public sealed record RewriteResult
{
    /// <summary>Gets a value indicating whether the rewrite completed without error diagnostics.</summary>
    public required bool Succeeded { get; init; }

    /// <summary>Gets a value indicating whether the input was already rewritten with a matching signature.</summary>
    public required bool WasNoOp { get; init; }

    /// <summary>Gets a value indicating whether an output assembly file was written.</summary>
    public required bool WasWritten { get; init; }

    /// <summary>Gets the deterministic manifest describing the rewrite.</summary>
    public required InstrumentationManifest Manifest { get; init; }

    /// <summary>Gets the diagnostics produced during the rewrite.</summary>
    public ImmutableArray<RewriteDiagnostic> Diagnostics => Manifest.Diagnostics;

    /// <summary>Gets the error-severity diagnostics produced during the rewrite.</summary>
    public IEnumerable<RewriteDiagnostic> Errors => Diagnostics.Where(d => d.IsError);

    /// <summary>
    /// Throws a <see cref="RewriteException"/> if the rewrite did not succeed; otherwise returns this
    /// result for fluent use.
    /// </summary>
    public RewriteResult EnsureSuccess()
    {
        if (!Succeeded)
        {
            throw new RewriteException(this);
        }

        return this;
    }
}
