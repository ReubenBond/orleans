using Orleans.Testing.Simulation.Runtime.Random;

namespace Orleans.Testing.Simulation.Runtime.Tests.Random;

/// <summary>
/// Covers <see cref="SimulationSeedAuthority"/>: domain-seed stability/independence, order- and
/// count-independent per-site derivation, deterministic child authorities, and input validation.
/// </summary>
public sealed class SimulationSeedAuthorityTests
{
    [Fact]
    public void GetDomainSeedIsStableForTheSameSimulationSeedAndDomain()
    {
        var authority = new SimulationSeedAuthority(42);
        Assert.Equal(authority.GetDomainSeed(SimulationSeedDomain.Network), authority.GetDomainSeed(SimulationSeedDomain.Network));
    }

    [Fact]
    public void GetDomainSeedIsStableAcrossIndependentAuthorityInstances()
    {
        // "Stable across processes" - modeled here as stable across two entirely independent
        // SimulationSeedAuthority instances constructed with the same simulation seed.
        var first = new SimulationSeedAuthority(1234);
        var second = new SimulationSeedAuthority(1234);

        Assert.Equal(first.GetDomainSeed(SimulationSeedDomain.Scheduler), second.GetDomainSeed(SimulationSeedDomain.Scheduler));
    }

    [Theory]
    [InlineData(SimulationSeedDomain.Scheduler)]
    [InlineData(SimulationSeedDomain.Network)]
    [InlineData(SimulationSeedDomain.Application)]
    [InlineData(SimulationSeedDomain.Identity)]
    [InlineData(SimulationSeedDomain.Buggify)]
    [InlineData(SimulationSeedDomain.Model)]
    public void EveryKnownDomainProducesADeterministicSeed(SimulationSeedDomain domain)
    {
        var authority = new SimulationSeedAuthority(7);
        var seed = authority.GetDomainSeed(domain);
        Assert.Equal(seed, authority.GetDomainSeed(domain));
    }

    [Fact]
    public void AllKnownDomainsProduceMutuallyDistinctSeedsForTheSameSimulationSeed()
    {
        var authority = new SimulationSeedAuthority(99);
        var domains = Enum.GetValues<SimulationSeedDomain>();
        var seeds = domains.Select(authority.GetDomainSeed).ToArray();

        Assert.Equal(seeds.Length, seeds.Distinct().Count());
    }

    [Fact]
    public void ConsumingASeedInOneDomainConceptuallyNeverAffectsAnotherDomainsSeed()
    {
        // Domain seeds are pure functions of (SimulationSeed, domain) computed independently - "deriving
        // more from one domain" has no representation here that could perturb another domain, but
        // we assert the closest observable proxy: re-deriving one domain's seed after deriving
        // several others (including many site seeds within it) never changes.
        var authority = new SimulationSeedAuthority(2024);
        var networkSeedBefore = authority.GetDomainSeed(SimulationSeedDomain.Network);

        _ = authority.GetDomainSeed(SimulationSeedDomain.Scheduler);
        for (var i = 0; i < 25; i++)
        {
            _ = authority.GetSiteSeed(SimulationSeedDomain.Network, $"site-{i}");
        }

        var networkSeedAfter = authority.GetDomainSeed(SimulationSeedDomain.Network);
        Assert.Equal(networkSeedBefore, networkSeedAfter);
    }

    [Fact]
    public void GetSiteSeedIsStableForTheSameDomainAndSiteId()
    {
        var authority = new SimulationSeedAuthority(5);
        Assert.Equal(
            authority.GetSiteSeed(SimulationSeedDomain.Network, "node-A"),
            authority.GetSiteSeed(SimulationSeedDomain.Network, "node-A"));
    }

    [Fact]
    public void GetSiteSeedDiffersForDifferentSiteIdsInTheSameDomain()
    {
        var authority = new SimulationSeedAuthority(5);
        Assert.NotEqual(
            authority.GetSiteSeed(SimulationSeedDomain.Network, "node-A"),
            authority.GetSiteSeed(SimulationSeedDomain.Network, "node-B"));
    }

    [Fact]
    public void GetSiteSeedDiffersAcrossDomainsForTheSameSiteId()
    {
        var authority = new SimulationSeedAuthority(5);
        Assert.NotEqual(
            authority.GetSiteSeed(SimulationSeedDomain.Network, "node-A"),
            authority.GetSiteSeed(SimulationSeedDomain.Application, "node-A"));
    }

    [Fact]
    public void GetSiteSeedIsIndependentOfDerivationOrderOrCount()
    {
        // Requirement: stable per-node/per-site derivation "without relying on mutable fork order".
        // Deriving node-A's seed first, or deriving nine other sites first, must not change it.
        var early = new SimulationSeedAuthority(11);
        var earlySeedForA = early.GetSiteSeed(SimulationSeedDomain.Network, "node-A");

        var late = new SimulationSeedAuthority(11);
        for (var i = 0; i < 9; i++)
        {
            _ = late.GetSiteSeed(SimulationSeedDomain.Network, $"unrelated-{i}");
        }

        var lateSeedForA = late.GetSiteSeed(SimulationSeedDomain.Network, "node-A");

        Assert.Equal(earlySeedForA, lateSeedForA);
    }

    [Fact]
    public void GetSiteSeedThrowsForNullOrEmptySiteId()
    {
        var authority = new SimulationSeedAuthority(1);
        Assert.Throws<ArgumentException>(() => authority.GetSiteSeed(SimulationSeedDomain.Network, string.Empty));
    }

    [Fact]
    public void CreateChildAuthorityIsDeterministicForTheSameStableId()
    {
        var parent = new SimulationSeedAuthority(3);
        var childA = parent.CreateChildAuthority("node-A");
        var childAAgain = parent.CreateChildAuthority("node-A");

        Assert.Equal(childA.SimulationSeed, childAAgain.SimulationSeed);
    }

    [Fact]
    public void CreateChildAuthorityDiffersForDifferentStableIds()
    {
        var parent = new SimulationSeedAuthority(3);
        var childA = parent.CreateChildAuthority("node-A");
        var childB = parent.CreateChildAuthority("node-B");

        Assert.NotEqual(childA.SimulationSeed, childB.SimulationSeed);
    }

    [Fact]
    public void CreateChildAuthorityIsIndependentOfCreationOrder()
    {
        var early = new SimulationSeedAuthority(3);
        var earlyChildA = early.CreateChildAuthority("node-A");

        var late = new SimulationSeedAuthority(3);
        for (var i = 0; i < 9; i++)
        {
            _ = late.CreateChildAuthority($"unrelated-{i}");
        }

        var lateChildA = late.CreateChildAuthority("node-A");

        Assert.Equal(earlyChildA.SimulationSeed, lateChildA.SimulationSeed);
    }

    [Fact]
    public void CreateChildAuthorityThrowsForNullOrEmptyStableId()
    {
        var authority = new SimulationSeedAuthority(1);
        Assert.Throws<ArgumentException>(() => authority.CreateChildAuthority(string.Empty));
    }

    [Fact]
    public void ChildAuthoritysDomainSeedIsIndependentFromTheParentsSameDomainSeed()
    {
        var parent = new SimulationSeedAuthority(3);
        var child = parent.CreateChildAuthority("node-A");

        Assert.NotEqual(
            parent.GetDomainSeed(SimulationSeedDomain.Network),
            child.GetDomainSeed(SimulationSeedDomain.Network));
    }
}
