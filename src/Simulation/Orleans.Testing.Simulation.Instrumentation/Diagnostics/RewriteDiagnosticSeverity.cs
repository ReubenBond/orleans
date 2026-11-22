namespace Orleans.Testing.Simulation.Instrumentation.Diagnostics;

/// <summary>
/// The severity of a <see cref="RewriteDiagnostic"/> emitted by the rewrite engine.
/// </summary>
public enum RewriteDiagnosticSeverity
{
    /// <summary>Informational; does not affect success.</summary>
    Info,

    /// <summary>A non-fatal concern (e.g. an unrelated optional reference could not be resolved).</summary>
    Warning,

    /// <summary>A fatal problem that fails the rewrite (e.g. a targeted member could not be resolved).</summary>
    Error,
}
