using Orleans.Testing.Simulation.Runtime.Execution;
using Orleans.Testing.Simulation.Runtime.Random;
using Orleans.Testing.Simulation.Runtime.Scheduling.Resources;

namespace Orleans.Testing.Simulation.Runtime.Scheduling.Strategies;

/// <summary>
/// <para>
/// Picks uniformly at random among the runnable operations using a deterministic, seeded stream. The
/// same seed always produces the same schedule (making a race reproducible), while different seeds
/// explore different interleavings (making a race findable). The seed comes from the runtime policy
/// <see cref="SimulationSeedDomain.Scheduler"/> domain via <see cref="ForRuntime"/>, so scheduling
/// randomness is independent of application/network randomness and stable per simulation seed.
/// </para>
/// <para>
/// Because its choice consults hidden state (the random stream), it declares
/// <see cref="RecordsNondeterministicChoices"/> = <see langword="true"/>: the scheduler records each
/// real choice so an exact-replay run can validate it. The stream advances only when there is more
/// than one candidate, so adding a step where only one operation is runnable never perturbs the
/// sequence.
/// </para>
/// </summary>
internal sealed class SeededRandomSchedulingStrategy :
    ISimulationSchedulingStrategy,
    ISimulationSchedulingStrategyRuntimeHooks
{
    // SplitMix64: a tiny, well-distributed, fully specified generator. Implemented inline so the
    // sequence is identical across processes and .NET versions (unlike System.Random, whose
    // algorithm is not contractually stable), which is what makes seed-based replay portable.
    private ulong _state;

    /// <summary>
    /// Initializes a new instance of the <see cref="SeededRandomSchedulingStrategy"/> class from a raw
    /// integer seed. Prefer <see cref="ForRuntime"/>, which derives the seed from the runtime's root
    /// seed within the scheduler decision domain.
    /// </summary>
    /// <param name="seed">The deterministic seed for the scheduling random stream.</param>
    internal SeededRandomSchedulingStrategy(int seed)
    {
        _state = unchecked((ulong)seed * 0x9E3779B97F4A7C15UL + 0x2545F4914F6CDD1DUL);
    }

    /// <inheritdoc/>
    public string Name => "seeded-random";

    /// <inheritdoc/>
    public bool RecordsNondeterministicChoices => true;

    /// <summary>
    /// Creates a strategy whose seed is derived from <paramref name="runtime"/>'s simulation seed within the
    /// <see cref="SimulationSeedDomain.Scheduler"/> domain, so scheduling randomness is independent of
    /// every other decision domain and reproducible from the same simulation seed.
    /// </summary>
    /// <param name="runtime">The runtime whose simulation seed derives the scheduler seed.</param>
    /// <returns>A seeded strategy for that runtime.</returns>
    internal static SeededRandomSchedulingStrategy ForRuntime(SimulationRuntimeIdentity runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        var seed = new SimulationSeedAuthority(runtime.Seed).GetDomainSeed(SimulationSeedDomain.Scheduler);
        return new SeededRandomSchedulingStrategy(seed);
    }

    /// <inheritdoc/>
    public SimulationOperation ChooseNext(SimulationSchedulingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var count = context.Runnable.Count;
        if (count == 1)
        {
            // No real choice: never draw, so the stream is not perturbed by single-candidate steps.
            return context.Runnable[0];
        }

        var index = NextIndex(count);
        return context.Runnable[index];
    }

    /// <summary>Chooses an index from a non-empty deterministic candidate list using this strategy's stream.</summary>
    internal int ChooseIndex(int exclusiveMax)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(exclusiveMax);
        return exclusiveMax == 1 ? 0 : NextIndex(exclusiveMax);
    }

    int ISimulationSchedulingStrategyRuntimeHooks.ChooseResourceWaiter(
        IReadOnlyList<SimulationResourceWaiterInfo> waiters) =>
        ChooseIndex(waiters.Count);

    string ISimulationSchedulingStrategyRuntimeHooks.DecisionSourceId => Name;

    void ISimulationSchedulingStrategyRuntimeHooks.ValidateComplete()
    {
    }

    private int NextIndex(int exclusiveMax)
    {
        // Lemire-style unbiased bounded reduction over a fresh 64-bit draw.
        var draw = NextUInt64();
        return (int)(((UInt128)draw * (uint)exclusiveMax) >> 64);
    }

    private ulong NextUInt64()
    {
        unchecked
        {
            _state += 0x9E3779B97F4A7C15UL;
            var z = _state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }
}
