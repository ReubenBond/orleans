using Orleans.Durable.Storage.Fuzz.Ledgers;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests;

/// <summary>
/// Replays the fuzz targets of the LedgerStore's formats through their oracles (verification.md §6.1).
/// </summary>
public sealed class LedgerStoreCorpusReplayTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public static TheoryData<string> Targets => CorpusReplay.Names(LedgerStoreTargets.All);

    [Theory]
    [MemberData(nameof(Targets))]
    public Task EverySeedFromTheGoldenVectorsSatisfiesTheOracle(string target) => CorpusReplay.ReplaySeedsAsync(LedgerStoreTargets.All, target, _leaks.InScopeAsync);

    [Theory]
    [MemberData(nameof(Targets))]
    public Task EveryCommittedCorpusInputSatisfiesTheOracle(string target) => CorpusReplay.ReplayCorpusAsync(LedgerStoreTargets.All, target, _leaks.InScopeAsync);

    public void Dispose() => _leaks.Dispose();
}
