namespace Orleans.Testing.Simulation.Instrumentation.Manifest;

/// <summary>
/// The outcome recorded for a site the engine considered while applying a rule.
/// </summary>
public enum TransformationOutcome
{
    /// <summary>The site was rewritten (redirected, wrapped, or type-substituted).</summary>
    Transformed,

    /// <summary>A deterministic rejection was injected at the site.</summary>
    Rejected,
}
