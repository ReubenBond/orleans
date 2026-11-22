namespace Orleans.Testing.Simulation.Runtime.Random;

/// <summary>
/// <para>
/// Independent, named domains for deterministic randomness/decision derivation. Each domain gets
/// its own seed (see <see cref="SimulationSeedAuthority"/>), so that consuming randomness in one
/// domain - drawing more numbers, forking more child streams - never perturbs any other domain's
/// sequence. This is what makes it safe to add, remove, or reorder calls within one domain (say,
/// adding a new network-delay decision) without silently changing the scheduler's or the
/// application's random sequence and invalidating unrelated recorded seeds/replays.
/// </para>
/// <para>
/// The specific domains here correspond to the major independent sources of decision-making
/// identified by the runtime policy design: scheduling order, simulated network behavior, application/node
/// -level randomness (what application code sees via <c>SimulationRandom</c>), stable identity
/// generation (e.g. deterministic GUIDs), fault injection ("Buggify"), and model-level exploration
/// (e.g. schedule exploration).
/// </para>
/// </summary>
public enum SimulationSeedDomain
{
    /// <summary>Decisions made by the scheduler about execution order/interleaving.</summary>
    Scheduler,

    /// <summary>Decisions made by the simulated network (delay, loss, jitter, partitioning).</summary>
    Network,

    /// <summary>Randomness exposed to application/node code (e.g. via <c>SimulationRandom</c>).</summary>
    Application,

    /// <summary>Deterministic identity generation (e.g. stable GUIDs, stable node/site ids).</summary>
    Identity,

    /// <summary>Fault-injection ("Buggify"-style) activation decisions.</summary>
    Buggify,

    /// <summary>Model-level decisions (e.g. race-exploration search strategy choices).</summary>
    Model,
}
