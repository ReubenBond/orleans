using Orleans.Testing.Simulation.Runtime.Shims;
using Orleans.Testing.Simulation.Runtime.Scheduling;

namespace Orleans.Testing.Simulation.Runtime.Execution;

/// <summary>
/// <para>
/// Identifies one active simulation runtime (a "host" such as a simulation cluster) for the
/// lifetime of the ambient <see cref="SimulationExecutionContext"/>. Two runtimes are the same
/// runtime if and only if their <see cref="Id"/>s are equal - <see cref="Seed"/> and
/// <see cref="Description"/> are diagnostic metadata only and are not used for identity.
/// </para>
/// <para>
/// Instances are created once per simulation host and then threaded through every ambient scope
/// that host installs, so code running anywhere inside that simulation can discover which
/// simulation (and, transitively, which seed) it is executing under.
/// </para>
/// </summary>
/// <param name="Id">A process-unique identifier for this runtime instance.</param>
/// <param name="Seed">The deterministic simulation seed the runtime was created with, for diagnostics.</param>
/// <param name="Description">An optional human-readable description, for diagnostics only.</param>
public sealed record SimulationRuntimeIdentity(Guid Id, int Seed, string? Description = null)
{
    private RuntimeComponents? _components;

    internal ISimulationRuntimeEnvironment Environment =>
        _components?.Environment ?? throw IncompleteRuntime();

    internal SimulationScheduler Scheduler =>
        _components?.Scheduler ?? throw IncompleteRuntime();

    internal bool IsConfigured => _components is not null;

    internal void ConfigureServices(
        ISimulationRuntimeEnvironment environment,
        SimulationScheduler scheduler)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(scheduler);

        var components = new RuntimeComponents(environment, scheduler);
        if (Interlocked.CompareExchange(ref _components, components, null) is not null)
        {
            throw new InvalidOperationException(
                $"Simulation runtime '{Id}' is already configured. Runtime services are immutable once installed.");
        }
    }

    internal void EnsureConfigured()
    {
        if (_components is null)
        {
            throw IncompleteRuntime();
        }
    }

    /// <inheritdoc />
    public bool Equals(SimulationRuntimeIdentity? other) => other is not null && Id == other.Id;

    /// <inheritdoc />
    public override int GetHashCode() => Id.GetHashCode();

    private InvalidOperationException IncompleteRuntime() =>
        new(
            $"Simulation runtime '{Id}' is incomplete. Configure its deterministic environment and task " +
            "scheduler before entering it.");

    private sealed record RuntimeComponents(
        ISimulationRuntimeEnvironment Environment,
        SimulationScheduler Scheduler);
}
