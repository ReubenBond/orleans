using System.Globalization;

namespace Orleans.Testing.Simulation;

/// <summary>
/// <para>
/// Configures the escalating iteration budget used by the adaptive
/// <see cref="SimulationCluster.RunUntil(Func{bool}, AdaptiveExecutionBudget, CancellationToken)"/> and
/// <see cref="SimulationCluster.RunUntilIdle(AdaptiveExecutionBudget, CancellationToken, TimeSpan?)"/> methods. These entry points spare
/// callers from having to pick a <c>maxIterations</c> value sized to their specific scenario:
/// instead of one fixed budget, the drive loop is run in successive batches, starting at
/// <see cref="InitialMaxIterations"/> and multiplying by <see cref="GrowthFactor"/> after each
/// batch that stops only because it ran out of iterations.
/// </para>
/// <para>
/// <see cref="MaxTotalIterations"/> is a hard ceiling on the sum of iterations across every batch:
/// escalation never removes the safety cap that <c>maxIterations</c> already provides on the
/// non-adaptive <c>RunUntil</c>/<c>RunUntilIdle</c> APIs - it just removes the need to guess its
/// value up front.
/// </para>
/// </summary>
public sealed class AdaptiveExecutionBudget
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AdaptiveExecutionBudget"/> class.
    /// </summary>
    /// <param name="initialMaxIterations">The iteration budget for the first batch. Defaults to 1,000.</param>
    /// <param name="growthFactor">
    /// The multiplier applied to the batch budget each time a batch stops only because it reached
    /// its iteration limit (<see cref="SimulationExecutionReason.MaxIterationsReached"/>). Must be
    /// greater than 1.0 so each round meaningfully escalates. Defaults to 4.0.
    /// </param>
    /// <param name="maxTotalIterations">
    /// The hard ceiling on the total number of iterations across every batch, regardless of how
    /// many rounds of escalation occur. Defaults to 10,000,000.
    /// </param>
    public AdaptiveExecutionBudget(int initialMaxIterations = 1_000, double growthFactor = 4.0, int maxTotalIterations = 10_000_000)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(initialMaxIterations, 1);
        if (growthFactor <= 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(growthFactor), growthFactor, "Growth factor must be greater than 1.0 so each escalation round meaningfully increases the budget.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(maxTotalIterations, initialMaxIterations);

        InitialMaxIterations = initialMaxIterations;
        GrowthFactor = growthFactor;
        MaxTotalIterations = maxTotalIterations;
    }

    /// <summary>
    /// Gets the default adaptive budget: an initial batch of 1,000 iterations, escalating by 4x
    /// each round it is exhausted, up to a hard ceiling of 10,000,000 total iterations.
    /// </summary>
    public static AdaptiveExecutionBudget Default { get; } = new();

    /// <summary>
    /// Gets the iteration budget for the first batch.
    /// </summary>
    public int InitialMaxIterations { get; }

    /// <summary>
    /// Gets the multiplier applied to the batch budget after each batch that stops only because it
    /// reached its iteration limit.
    /// </summary>
    public double GrowthFactor { get; }

    /// <summary>
    /// Gets the hard ceiling on the total number of iterations across every batch.
    /// </summary>
    public int MaxTotalIterations { get; }

    /// <inheritdoc />
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"InitialMaxIterations={InitialMaxIterations}, GrowthFactor={GrowthFactor}, MaxTotalIterations={MaxTotalIterations}");
}
