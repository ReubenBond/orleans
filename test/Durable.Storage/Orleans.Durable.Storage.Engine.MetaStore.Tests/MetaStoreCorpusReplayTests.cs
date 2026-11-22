using Orleans.Durable.Storage.Fuzz.MetaStore;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.MetaStore.Tests;

/// <summary>
/// Replays the fuzz targets of the MetaStore's formats through their oracles (verification.md §6.1).
/// </summary>
public sealed class MetaStoreCorpusReplayTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public static TheoryData<string> Targets => CorpusReplay.Names(MetaStoreTargets.All);

    [Theory]
    [MemberData(nameof(Targets))]
    public Task EverySeedFromTheGoldenVectorsSatisfiesTheOracle(string target) => CorpusReplay.ReplaySeedsAsync(MetaStoreTargets.All, target, _leaks.InScopeAsync);

    [Theory]
    [MemberData(nameof(Targets))]
    public Task EveryCommittedCorpusInputSatisfiesTheOracle(string target) => CorpusReplay.ReplayCorpusAsync(MetaStoreTargets.All, target, _leaks.InScopeAsync);

    public void Dispose() => _leaks.Dispose();
}
