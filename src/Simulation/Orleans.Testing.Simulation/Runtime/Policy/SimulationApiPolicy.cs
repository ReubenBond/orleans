namespace Orleans.Testing.Simulation.Runtime.Policy;

/// <summary>
/// Classifies how a rewrite rule treats a targeted API. Rules are the sole source of API policy;
/// callers leave an API unchanged by omitting its rule or excluding its assembly from instrumentation.
/// </summary>
public enum SimulationApiPolicy
{
    /// <summary>
    /// The API is intercepted and routed through deterministic simulation machinery (e.g. a
    /// virtual clock, a deterministic random source, a simulated network). This is the strict
    /// default for rewrite rules.
    /// </summary>
    Controlled,

    /// <summary>
    /// The API is forbidden while a simulation is active - calling it should fail loudly (e.g. by
    /// throwing) rather than silently doing something non-deterministic. Use this for APIs that
    /// cannot be made deterministic and must never be reached from simulated code (e.g. real
    /// wall-clock/network/filesystem access with no simulated equivalent).
    /// </summary>
    Rejected,
}
