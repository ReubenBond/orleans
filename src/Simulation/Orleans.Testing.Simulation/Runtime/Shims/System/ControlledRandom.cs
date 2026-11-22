using System.ComponentModel;

namespace Orleans.Testing.Simulation.Shims.System;

/// <summary>
/// <para>
/// The deterministic replacements for <see cref="global::System.Random.Shared"/> and the <see cref="global::System.Random"/>
/// constructors. Instrumented code has <c>Random.Shared</c>, <c>new Random()</c>, and
/// <c>new Random(int)</c> redirected here. Each method requires an active simulation.
/// </para>
/// <para><b>Stream lifetime and isolation semantics.</b></para>
/// <list type="bullet">
/// <item><description>
/// <see cref="GetShared"/> (<c>Random.Shared</c>): one stable <see cref="global::System.Random"/> per node for the
/// life of the simulation, seeded from the application seed domain. Successive draws advance that one
/// per-node stream; the instance is isolated per node so nodes never share mutable state. The returned
/// wrapper synchronizes every mutable draw so an accidentally escaped concurrent caller cannot corrupt
/// the deterministic stream.
/// </description></item>
/// <item><description>
/// <see cref="CreateUnseeded"/> (<c>new Random()</c>): a fresh independent <see cref="global::System.Random"/> per
/// construction, seeded from a per-node monotonic construction counter. The sequence a program sees is
/// reproducible under a fixed schedule, and distinct constructions never share state.
/// </description></item>
/// <item><description>
/// <see cref="CreateSeeded"/> (<c>new Random(int)</c>): the caller's explicit seed is preserved
/// exactly - a seeded <see cref="global::System.Random"/> is already deterministic and reproducible, so the shim does
/// not reseed it. Consequently two nodes that both call <c>new Random(42)</c> observe identical
/// sequences <em>by the caller's explicit choice</em>; this is the documented compatibility policy.
/// </description></item>
/// </list>
/// <para>
/// All simulated randomness here is drawn from the application seed domain only, so consuming it never
/// perturbs the scheduler, network, or fault-injection ("Buggify") domains.
/// </para>
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class ControlledRandom
{
    /// <summary>Deterministic replacement for <see cref="global::System.Random.Shared"/>.</summary>
    /// <returns>The node's shared deterministic stream.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static global::System.Random GetShared()
    {
        var (_, env, node) = SimulationRuntimeDispatch.RequireEnvironment("System.Random.Shared");
        return env.GetSharedRandom(node);
    }

    /// <summary>Deterministic replacement for the parameterless <see cref="global::System.Random"/> constructor.</summary>
    /// <returns>A fresh deterministic stream.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static global::System.Random CreateUnseeded()
    {
        var (_, env, node) = SimulationRuntimeDispatch.RequireEnvironment("System.Random..ctor()");
        return env.CreateUnseededRandom(node);
    }

    /// <summary>
    /// Deterministic replacement for the seeded <see cref="global::System.Random"/> constructor. The caller's
    /// seed is preserved exactly per the documented compatibility policy.
    /// </summary>
    /// <param name="seed">The seed supplied by the caller.</param>
    /// <returns>A <see cref="global::System.Random"/> seeded with <paramref name="seed"/>.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static global::System.Random CreateSeeded(int seed)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation("System.Random..ctor(Int32)");
        // An explicitly-seeded Random is already deterministic and reproducible, so it draws no ambient
        // time or randomness: the deterministic contract only requires that we not perturb the caller's
        // chosen seed. We therefore honour the seed verbatim and deliberately
        // do not require an active runtime (there is nothing irreproducible to guard against).
        return new global::System.Random(seed);
    }
}
