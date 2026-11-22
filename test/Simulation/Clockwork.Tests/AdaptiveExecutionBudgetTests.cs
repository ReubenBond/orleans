using System.Globalization;

namespace Orleans.Testing.Simulation.Tests;

/// <summary>
/// Covers construction validation, defaults, and formatting for <see cref="AdaptiveExecutionBudget"/>.
/// </summary>
public sealed class AdaptiveExecutionBudgetTests
{
    [Fact]
    public void DefaultHasExpectedValues()
    {
        var budget = AdaptiveExecutionBudget.Default;

        Assert.Equal(1_000, budget.InitialMaxIterations);
        Assert.Equal(4.0, budget.GrowthFactor);
        Assert.Equal(10_000_000, budget.MaxTotalIterations);
    }

    [Fact]
    public void ParameterlessConstructorMatchesDefault()
    {
        var budget = new AdaptiveExecutionBudget();

        Assert.Equal(AdaptiveExecutionBudget.Default.InitialMaxIterations, budget.InitialMaxIterations);
        Assert.Equal(AdaptiveExecutionBudget.Default.GrowthFactor, budget.GrowthFactor);
        Assert.Equal(AdaptiveExecutionBudget.Default.MaxTotalIterations, budget.MaxTotalIterations);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ConstructorRejectsNonPositiveInitialMaxIterations(int initialMaxIterations)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AdaptiveExecutionBudget(initialMaxIterations: initialMaxIterations));
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(0.5)]
    [InlineData(0.0)]
    [InlineData(-2.0)]
    public void ConstructorRejectsGrowthFactorAtOrBelowOne(double growthFactor)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AdaptiveExecutionBudget(growthFactor: growthFactor));
    }

    [Fact]
    public void ConstructorRejectsMaxTotalIterationsBelowInitialMaxIterations()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AdaptiveExecutionBudget(initialMaxIterations: 100, maxTotalIterations: 99));
    }

    [Fact]
    public void ConstructorAllowsMaxTotalIterationsEqualToInitialMaxIterations()
    {
        var budget = new AdaptiveExecutionBudget(initialMaxIterations: 50, maxTotalIterations: 50);

        Assert.Equal(50, budget.InitialMaxIterations);
        Assert.Equal(50, budget.MaxTotalIterations);
    }

    [Fact]
    public void ToStringUsesInvariantCultureRegardlessOfCurrentThreadCulture()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            var budget = new AdaptiveExecutionBudget(initialMaxIterations: 5, growthFactor: 2.5, maxTotalIterations: 500);

            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            var invariant = budget.ToString();

            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var german = budget.ToString();

            Assert.Equal(invariant, german);
            Assert.Contains("InitialMaxIterations=5", invariant, StringComparison.Ordinal);
            Assert.Contains("GrowthFactor=2.5", invariant, StringComparison.Ordinal);
            Assert.Contains("MaxTotalIterations=500", invariant, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }
}
