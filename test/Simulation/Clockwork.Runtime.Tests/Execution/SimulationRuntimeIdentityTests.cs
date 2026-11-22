using Orleans.Testing.Simulation.Runtime.Execution;

namespace Orleans.Testing.Simulation.Runtime.Tests.Execution;

/// <summary>
/// Verifies that runtime identity is determined only by <see cref="SimulationRuntimeIdentity.Id"/>.
/// </summary>
public sealed class SimulationRuntimeIdentityTests
{
    [Fact]
    public void EqualIdsAreEqualRegardlessOfSeedAndDescription()
    {
        var id = Guid.Parse("b8ff6ff1-e605-4d78-ac93-47d472968d01");
        var first = new SimulationRuntimeIdentity(id, 17, null);
        var second = new SimulationRuntimeIdentity(id, 42, "diagnostic metadata");

        Assert.True(first.Equals(second));
        Assert.True(second.Equals(first));
        Assert.True(first == second);
        Assert.False(first != second);
    }

    [Fact]
    public void EqualIdsProduceEqualHashCodes()
    {
        var id = Guid.Parse("06ef0dc7-727d-412b-818c-45a99a3ddc62");
        var first = new SimulationRuntimeIdentity(id, 17, "same description");
        var second = new SimulationRuntimeIdentity(id, 18, "same description");

        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }

    [Fact]
    public void DifferentIdsAreNotEqualEvenWhenMetadataMatches()
    {
        var first = new SimulationRuntimeIdentity(
            Guid.Parse("fbe61a2e-6b86-42db-8cb1-d03b466422ee"),
            73,
            "shared metadata");
        var second = new SimulationRuntimeIdentity(
            Guid.Parse("cc8f8966-7066-4a45-849a-e0ed5f654185"),
            73,
            "shared metadata");

        Assert.False(first.Equals(second));
        Assert.False(second.Equals(first));
        Assert.False(first == second);
        Assert.True(first != second);
    }

    [Fact]
    public void DictionaryUsesIdOnlyIdentity()
    {
        var id = Guid.Parse("62833bf2-dc7b-4b05-babb-11052e26272c");
        var first = new SimulationRuntimeIdentity(id, 11, "original metadata");
        var equivalent = new SimulationRuntimeIdentity(id, 29, null);
        var identities = new Dictionary<SimulationRuntimeIdentity, string>
        {
            [first] = "original",
        };

        var found = identities.TryGetValue(equivalent, out var originalValue);
        identities[equivalent] = "replacement";

        Assert.True(found);
        Assert.Equal("original", originalValue);
        Assert.Single(identities);
        Assert.Equal("replacement", identities[first]);
    }

    [Fact]
    public void HashSetCollapsesMetadataVariantsOfTheSameId()
    {
        var id = Guid.Parse("64fb78b4-286b-4119-907c-ac866cb416fc");
        var first = new SimulationRuntimeIdentity(id, 3, "first");
        var second = new SimulationRuntimeIdentity(id, 5, "second");
        var probe = new SimulationRuntimeIdentity(id, 7, null);
        var identities = new HashSet<SimulationRuntimeIdentity> { first };

        var added = identities.Add(second);

        Assert.False(added);
        Assert.Single(identities);
        Assert.Contains(probe, identities);
    }
}
