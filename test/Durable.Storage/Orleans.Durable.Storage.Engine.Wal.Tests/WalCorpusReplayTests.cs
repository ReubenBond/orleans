using Orleans.Durable.Storage.Fuzz.Wal;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Wal.Tests;

/// <summary>
/// Replays the fuzz targets of the WAL's formats through their oracles (verification.md §6.1).
/// </summary>
public sealed class WalCorpusReplayTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public static TheoryData<string> Targets => CorpusReplay.Names(WalTargets.All);

    [Theory]
    [MemberData(nameof(Targets))]
    public Task EverySeedFromTheGoldenVectorsSatisfiesTheOracle(string target) => CorpusReplay.ReplaySeedsAsync(WalTargets.All, target, _leaks.InScopeAsync);

    [Theory]
    [MemberData(nameof(Targets))]
    public Task EveryCommittedCorpusInputSatisfiesTheOracle(string target) => CorpusReplay.ReplayCorpusAsync(WalTargets.All, target, _leaks.InScopeAsync);

    public void Dispose() => _leaks.Dispose();
}
