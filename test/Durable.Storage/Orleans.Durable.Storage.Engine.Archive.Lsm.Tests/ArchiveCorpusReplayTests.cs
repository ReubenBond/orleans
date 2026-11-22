using Orleans.Durable.Storage.Fuzz.Archive;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Archive.Lsm.Tests;

/// <summary>
/// Replays the fuzz target of the archive's manifest format through its oracle (verification.md §6.1).
/// </summary>
public sealed class ArchiveCorpusReplayTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public static TheoryData<string> Targets => CorpusReplay.Names(ArchiveTargets.All);

    [Theory]
    [MemberData(nameof(Targets))]
    public Task EverySeedFromTheGoldenVectorsSatisfiesTheOracle(string target) => CorpusReplay.ReplaySeedsAsync(ArchiveTargets.All, target, _leaks.InScopeAsync);

    [Theory]
    [MemberData(nameof(Targets))]
    public Task EveryCommittedCorpusInputSatisfiesTheOracle(string target) => CorpusReplay.ReplayCorpusAsync(ArchiveTargets.All, target, _leaks.InScopeAsync);

    public void Dispose() => _leaks.Dispose();
}
