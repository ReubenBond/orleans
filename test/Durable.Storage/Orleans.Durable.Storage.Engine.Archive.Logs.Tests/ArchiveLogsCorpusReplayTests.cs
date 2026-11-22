using Orleans.Durable.Storage.Fuzz.Archive.Logs;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Archive.Logs.Tests;

/// <summary>
/// Replays archive logs' fuzz targets, the token codec and Azure's metadata reader, through their oracles
/// (verification.md §6.1, archive-logs.md §10).
/// </summary>
public sealed class ArchiveLogsCorpusReplayTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public static TheoryData<string> Targets => CorpusReplay.Names(ArchiveLogsTargets.All);

    [Theory]
    [MemberData(nameof(Targets))]
    public Task EverySeedFromTheGoldenVectorsSatisfiesTheOracle(string target) => CorpusReplay.ReplaySeedsAsync(ArchiveLogsTargets.All, target, _leaks.InScopeAsync);

    [Theory]
    [MemberData(nameof(Targets))]
    public Task EveryCommittedCorpusInputSatisfiesTheOracle(string target) => CorpusReplay.ReplayCorpusAsync(ArchiveLogsTargets.All, target, _leaks.InScopeAsync);

    public void Dispose() => _leaks.Dispose();
}
