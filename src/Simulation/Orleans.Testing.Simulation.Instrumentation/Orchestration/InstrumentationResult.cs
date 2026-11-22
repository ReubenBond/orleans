using System.Collections.Immutable;
using Orleans.Testing.Simulation.Instrumentation.Diagnostics;

namespace Orleans.Testing.Simulation.Instrumentation.Orchestration;

/// <summary>
/// The outcome of an <see cref="InstrumentationRunner"/> run over an application closure: whether it
/// succeeded, whether it was satisfied incrementally from cache (no work performed), the staging and
/// manifest locations, and the per-assembly results plus the verbatim assets copied.
/// </summary>
public sealed record InstrumentationResult
{
    /// <summary>Gets a value indicating whether the run completed without error diagnostics.</summary>
    public required bool Succeeded { get; init; }

    /// <summary>Gets a value indicating whether the run was a verified incremental no-op (cache hit).</summary>
    public required bool WasIncrementalHit { get; init; }

    /// <summary>Gets the feature version of the target runtime the rules were evaluated against.</summary>
    public required Version TargetRuntime { get; init; }

    /// <summary>Gets where <see cref="TargetRuntime"/> came from, such as <c>from 'App.runtimeconfig.json'</c>.</summary>
    public required string TargetRuntimeOrigin { get; init; }

    /// <summary>Gets the staging directory the instrumented closure was written to.</summary>
    public required string StagingDirectory { get; init; }

    /// <summary>Gets the path the closure manifest was written to (when a full run occurred).</summary>
    public required string ManifestPath { get; init; }

    /// <summary>Gets the per-assembly instrumentation results.</summary>
    public ImmutableArray<AssemblyInstrumentationResult> Assemblies { get; init; } = [];

    /// <summary>Gets the closure-relative paths of the assets copied verbatim into staging.</summary>
    public ImmutableArray<string> CopiedAssets { get; init; } = [];

    /// <summary>Gets the top-level diagnostics not attributable to a single assembly (e.g. key loading).</summary>
    public ImmutableArray<RewriteDiagnostic> Diagnostics { get; init; } = [];

    /// <summary>Gets every error-severity diagnostic across the run (top-level and per-assembly).</summary>
    public IEnumerable<RewriteDiagnostic> Errors =>
        Diagnostics.Where(d => d.IsError).Concat(Assemblies.SelectMany(a => a.Errors));

    /// <summary>Gets the number of assemblies whose IL was rewritten.</summary>
    public int RewrittenCount => Assemblies.Count(a => a.WasRewritten);

    /// <summary>Gets the number of assemblies that were verified idempotent no-ops.</summary>
    public int NoOpCount => Assemblies.Count(a => a.WasNoOp);

    /// <summary>
    /// Throws an <see cref="InstrumentationException"/> if the run did not succeed; otherwise returns
    /// this result for fluent use.
    /// </summary>
    public InstrumentationResult EnsureSuccess()
    {
        if (!Succeeded)
        {
            throw new InstrumentationException(this);
        }

        return this;
    }
}
