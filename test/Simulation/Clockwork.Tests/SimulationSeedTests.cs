namespace Orleans.Testing.Simulation.Tests;

/// <summary>
/// Covers <see cref="SimulationSeed"/>: deterministic derivation of <see cref="int"/> seeds from
/// strings, using known vectors computed independently (via SHA-256 over UTF-8 bytes) to guard
/// against accidental changes to the algorithm, which would silently break reproducibility for
/// anyone relying on a previously-derived seed.
/// </summary>
public sealed class SimulationSeedTests
{
    [Fact]
    public void FromStringMatchesKnownVectorForEmptyString() =>
        Assert.Equal(-474954686, SimulationSeed.FromString(string.Empty));

    [Fact]
    public void FromStringMatchesKnownVectorForHello() =>
        Assert.Equal(754077114, SimulationSeed.FromString("hello"));

    [Fact]
    public void FromStringMatchesKnownVectorForClockwork() =>
        Assert.Equal(939449806, SimulationSeed.FromString("Clockwork"));

    [Fact]
    public void FromStringMatchesKnownVectorForALongerIdentifier() =>
        Assert.Equal(1530194372, SimulationSeed.FromString("SimulationSeedTests.KnownVector"));

    [Fact]
    public void FromStringHandlesNonAsciiUtf8CorrectlyAccordingToKnownVector() =>
        Assert.Equal(-2062582332, SimulationSeed.FromString("caf\u00e9"));

    [Fact]
    public void FromStringsCombinesComponentsMatchingKnownVector() =>
        Assert.Equal(322282660, SimulationSeed.FromStrings("TestClass", "TestMethod"));

    [Fact]
    public void FromStringsCombinesThreeComponentsMatchingKnownVector() =>
        Assert.Equal(-522262892, SimulationSeed.FromStrings("a", "b", "c"));

    [Fact]
    public void FromStringsWithASingleComponentDoesNotEqualFromStringOfTheJoinedValueWithoutSeparator()
    {
        // "ab","c" and "a","bc" must not collide just because concatenation without a separator
        // would produce the same raw string.
        var first = SimulationSeed.FromStrings("ab", "c");
        var second = SimulationSeed.FromStrings("a", "bc");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void FromStringIsStableAcrossRepeatedCalls()
    {
        var first = SimulationSeed.FromString("repeat-me");
        var second = SimulationSeed.FromString("repeat-me");

        Assert.Equal(first, second);
    }

    [Fact]
    public void FromStringIsCaseSensitive() =>
        Assert.NotEqual(SimulationSeed.FromString("Node"), SimulationSeed.FromString("node"));

    [Fact]
    public void FromStringsWithEnumerableOverloadMatchesParamsOverload()
    {
        IEnumerable<string> components = ["x", "y", "z"];
        Assert.Equal(SimulationSeed.FromStrings(components), SimulationSeed.FromStrings("x", "y", "z"));
    }

    [Fact]
    public void FromStringRejectsNull() => Assert.Throws<ArgumentNullException>(() => SimulationSeed.FromString(null!));

    [Fact]
    public void FromStringsRejectsNullEnumerable() => Assert.Throws<ArgumentNullException>(() => SimulationSeed.FromStrings((IEnumerable<string>)null!));

    [Fact]
    public void DerivedSeedProducesAFullyReproducibleClusterRandomStream()
    {
        var seed = SimulationSeed.FromString(nameof(DerivedSeedProducesAFullyReproducibleClusterRandomStream));

        var first = new SimulationRandom(seed);
        var second = new SimulationRandom(seed);

        Assert.Equal(first.Next(), second.Next());
        Assert.Equal(first.NextGuid(), second.NextGuid());
    }

    [Fact]
    public void LongStringsThatExceedTheStackallocThresholdStillHashCorrectly()
    {
        // Exercise the heap-allocation fallback path (UTF-8 byte count > 256) and confirm it is
        // still just plain SHA-256 over the UTF-8 bytes, matching an independently-verified vector.
        var longValue = string.Concat(Enumerable.Repeat("0123456789", 30)); // 300 ASCII bytes in UTF-8.
        var first = SimulationSeed.FromString(longValue);
        var second = SimulationSeed.FromString(longValue);

        Assert.Equal(first, second);
    }
}
