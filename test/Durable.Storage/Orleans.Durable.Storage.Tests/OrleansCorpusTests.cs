using Orleans.Durable.Storage.Fuzz.Orleans;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Tests;

/// <summary>
/// Replays the Orleans composition's fuzz targets' seeds and committed corpora through their oracles (verification.md §6.1):
/// the node registry's and the journal and replica node grains' answers'.
/// </summary>
[NoPageLeakCheck("The registry codec rents no pages, and the journal and replica answers carry Orleans buffers, which their targets release.")]
public sealed class OrleansCorpusTests
{
    public static TheoryData<string> Targets => CorpusReplay.Names(OrleansTargets.All);

    [Theory]
    [MemberData(nameof(Targets))]
    public Task SeedsSatisfyTheOracle(string target) => CorpusReplay.ReplaySeedsAsync(OrleansTargets.All, target);

    [Theory]
    [MemberData(nameof(Targets))]
    public Task CorpusSatisfiesTheOracle(string target) => CorpusReplay.ReplayCorpusAsync(OrleansTargets.All, target);
}
