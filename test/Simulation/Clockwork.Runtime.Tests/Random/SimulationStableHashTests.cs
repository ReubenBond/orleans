using Orleans.Testing.Simulation.Runtime.Random;

namespace Orleans.Testing.Simulation.Runtime.Tests.Random;

/// <summary>
/// Covers <see cref="SimulationStableHash"/>: stability, non-triviality relative to
/// <see cref="object.GetHashCode()"/>, distinctness between <see cref="SimulationStableHash.ToInt32(string)"/>
/// and <see cref="SimulationStableHash.ToInt64(string)"/>, and that the component separator prevents
/// simple concatenation collisions.
/// </summary>
public sealed class SimulationStableHashTests
{
    [Fact]
    public void ToInt32IsStableAcrossCalls()
    {
        Assert.Equal(SimulationStableHash.ToInt32("clockwork"), SimulationStableHash.ToInt32("clockwork"));
    }

    [Fact]
    public void ToInt32ProducesAKnownVectorSoARegressionInTheAlgorithmIsCaught()
    {
        // Pinned known-vector: SHA-256("clockwork") first four bytes as big-endian int32. If this
        // ever changes, every derived seed across the whole codebase silently changes with it, so
        // this is deliberately asserted against a literal rather than only self-consistency.
        Assert.Equal(-85322183, SimulationStableHash.ToInt32("clockwork"));
    }

    [Fact]
    public void ToInt32DiffersForDifferentInputs()
    {
        Assert.NotEqual(SimulationStableHash.ToInt32("a"), SimulationStableHash.ToInt32("b"));
    }

    [Fact]
    public void ToInt32MultiComponentOverloadsAgreeWithManualSeparatorJoin()
    {
        var viaParams = SimulationStableHash.ToInt32("scheduler", "node-1");
        var viaEnumerable = SimulationStableHash.ToInt32((IEnumerable<string>)["scheduler", "node-1"]);
        var viaManualJoin = SimulationStableHash.ToInt32($"scheduler{SimulationStableHash.ComponentSeparator}node-1");

        Assert.Equal(viaParams, viaEnumerable);
        Assert.Equal(viaParams, viaManualJoin);
    }

    [Fact]
    public void ComponentSeparatorPreventsNaiveConcatenationCollisions()
    {
        // Without a separator, ("ab", "c") and ("a", "bc") would hash identically because both
        // concatenate to "abc". The separator must keep them distinct.
        var first = SimulationStableHash.ToInt32("ab", "c");
        var second = SimulationStableHash.ToInt32("a", "bc");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void ToInt64DiffersFromToInt32ForTheSameInput()
    {
        // ToInt64 reads a different (wider) slice of the same digest, so it must not just be a
        // sign/width-extended copy of ToInt32's result for the same input.
        var asInt32 = SimulationStableHash.ToInt32("same-input");
        var asInt64 = SimulationStableHash.ToInt64("same-input");

        Assert.NotEqual((long)asInt32, asInt64);
    }

    [Fact]
    public void ToInt64IsStableAcrossCalls()
    {
        Assert.Equal(SimulationStableHash.ToInt64("clockwork"), SimulationStableHash.ToInt64("clockwork"));
    }

    [Fact]
    public void ToInt32ThrowsForNullSingleValue()
    {
        Assert.Throws<ArgumentNullException>(() => SimulationStableHash.ToInt32((string)null!));
    }

    [Fact]
    public void ToInt32ThrowsForNullValuesSequence()
    {
        Assert.Throws<ArgumentNullException>(() => SimulationStableHash.ToInt32((IEnumerable<string>)null!));
    }

    [Fact]
    public void ToInt64ThrowsForNullValue()
    {
        Assert.Throws<ArgumentNullException>(() => SimulationStableHash.ToInt64(null!));
    }
}
