namespace Orleans.Testing.Simulation.Instrumentation.Configuration;

/// <summary>
/// Selects the granularity of IL instrumentation applied to a controlled application closure.
/// </summary>
public enum InstrumentationMode
{
    /// <summary>
    /// Rewrites only configured controlled APIs. No fine-grained memory or control-flow scheduling
    /// points are injected.
    /// </summary>
    Controlled,

    /// <summary>
    /// Adds fine-grained scheduling and race-access instrumentation for systematic race exploration.
    /// This mode is explicit because it increases rewritten IL size and runtime scheduling frequency.
    /// </summary>
    RaceExploration,
}
