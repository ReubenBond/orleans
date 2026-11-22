namespace Orleans.Testing.Simulation.Runtime.Random;

/// <summary>
/// <para>
/// A deterministic seed/decision service: derives independent, named-domain seeds (see
/// <see cref="SimulationSeedDomain"/>) from one simulation seed, and derives per-node/per-site child
/// seeds within a domain from a <em>stable string identity</em> rather than from mutable
/// construction/fork order.
/// </para>
/// <para>
/// <b>Why not just keep calling <c>Random.Fork()</c>/<c>Random.Next()</c>?</b> Forking a shared
/// <see cref="System.Random"/> in registration order (as e.g. <c>SimulationCluster.ForkRandom()</c>
/// does today) makes every derived seed depend on the exact order and count of every earlier
/// derivation - reordering two <c>AddNode</c> calls, or adding an unrelated derivation in between,
/// silently reseeds every node that comes after it. <see cref="GetSiteSeed(SimulationSeedDomain, string)"/>
/// instead hashes <c>(SimulationSeed, domain, stableId)</c> directly: the result depends only on the
/// domain and the caller-supplied stable identity (e.g. a node's network address), not on when or
/// how many times it - or any other id - was derived. This is "stable per-node/per-site
/// derivation" as required by the design: renaming, reordering, or adding unrelated nodes never
/// perturbs an existing node's seed.
/// </para>
/// <para>
/// <b>Domain independence.</b> <see cref="GetDomainSeed(SimulationSeedDomain)"/> hashes
/// <c>(SimulationSeed, domain)</c>, so each domain's seed is an independent function of the simulation seed and
/// that domain's name alone - consuming randomness from the resulting stream (however many draws,
/// however many further forks) never touches any other domain's seed or stream, because domain
/// seeds are computed once, up front, from immutable inputs rather than being threaded through a
/// single shared generator.
/// </para>
/// <para>
/// This service deals only in <see cref="int"/> seeds, not <see cref="System.Random"/> instances:
/// callers (e.g. the Orleans.Testing.Simulation.Simulation package) wrap a returned seed in whatever random
/// type is appropriate for their layer (<c>SimulationRandom</c> today).
/// </para>
/// </summary>
public sealed class SimulationSeedAuthority
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SimulationSeedAuthority"/> class.
    /// </summary>
    /// <param name="simulationSeed">The deterministic simulation seed all domain/site seeds derive from.</param>
    public SimulationSeedAuthority(int simulationSeed)
    {
        SimulationSeed = simulationSeed;
    }

    /// <summary>
    /// Gets the deterministic simulation seed this authority was created with.
    /// </summary>
    public int SimulationSeed { get; }

    /// <summary>
    /// Derives the seed for an entire named domain. Stable across processes: the same
    /// <see cref="SimulationSeed"/> and <paramref name="domain"/> always produce the same result.
    /// </summary>
    /// <param name="domain">The domain to derive a seed for.</param>
    /// <returns>A deterministic seed, independent of every other domain's seed.</returns>
    public int GetDomainSeed(SimulationSeedDomain domain) =>
        SimulationStableHash.ToInt32(SimulationSeedComponent, domain.ToString());

    /// <summary>
    /// Derives a per-node/per-site child seed within a domain, keyed by a caller-supplied stable
    /// identity (e.g. a node's network address, or a stable call-site tag) rather than by
    /// construction/fork order. See this type's remarks for why order-independence matters.
    /// </summary>
    /// <param name="domain">The domain the site belongs to.</param>
    /// <param name="siteId">
    /// A stable identity for the site (e.g. a node's network address). Must be non-empty and
    /// should be stable across runs for the same conceptual node/site.
    /// </param>
    /// <returns>
    /// A deterministic seed depending only on <see cref="SimulationSeed"/>, <paramref name="domain"/>,
    /// and <paramref name="siteId"/> - never on registration order or on any other site's identity.
    /// </returns>
    public int GetSiteSeed(SimulationSeedDomain domain, string siteId)
    {
        ArgumentException.ThrowIfNullOrEmpty(siteId);
        return SimulationStableHash.ToInt32(SimulationSeedComponent, domain.ToString(), siteId);
    }

    /// <summary>
    /// Creates an independent child <see cref="SimulationSeedAuthority"/> for a stable identity
    /// (e.g. one authority per node), whose own domain/site seeds are derived from a simulation seed that
    /// depends only on this authority's <see cref="SimulationSeed"/> and <paramref name="stableId"/> -
    /// not on how many other child authorities have been created, or in what order.
    /// </summary>
    /// <param name="stableId">A stable identity for the child authority.</param>
    /// <returns>A new, independent <see cref="SimulationSeedAuthority"/>.</returns>
    public SimulationSeedAuthority CreateChildAuthority(string stableId)
    {
        ArgumentException.ThrowIfNullOrEmpty(stableId);
        return new SimulationSeedAuthority(SimulationStableHash.ToInt32(SimulationSeedComponent, "Child", stableId));
    }

    private string SimulationSeedComponent =>
        SimulationSeed.ToString("x8", System.Globalization.CultureInfo.InvariantCulture);
}
