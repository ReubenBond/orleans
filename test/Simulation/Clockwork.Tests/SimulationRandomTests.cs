namespace Orleans.Testing.Simulation.Tests;

public sealed class SimulationRandomTests
{
    [Fact]
    public void SameSeedProducesSameSequence()
    {
        var first = new SimulationRandom(42);
        var second = new SimulationRandom(42);

        var firstValues = Enumerable.Range(0, 100).Select(_ => first.Next()).ToArray();
        var secondValues = Enumerable.Range(0, 100).Select(_ => second.Next()).ToArray();

        Assert.Equal(firstValues, secondValues);
    }

    [Fact]
    public void ShuffleIsReproducible()
    {
        var first = new SimulationRandom(42);
        var second = new SimulationRandom(42);
        var firstValues = Enumerable.Range(0, 20).ToList();
        var secondValues = Enumerable.Range(0, 20).ToList();

        first.Shuffle(firstValues);
        second.Shuffle(secondValues);

        Assert.Equal(firstValues, secondValues);
    }

    [Fact]
    public void ForkProducesAReproducibleIndependentStream()
    {
        var first = new SimulationRandom(42).Fork();
        var second = new SimulationRandom(42).Fork();

        Assert.Equal(first.Next(), second.Next());
        Assert.Equal(first.NextGuid(), second.NextGuid());
    }
}
