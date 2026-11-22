using Orleans.Durable.Storage.Fuzz.Journals;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Journals.Tests;

/// <summary>
/// Replays the fuzz target of the Journals layer's entry record through its oracle (verification.md §6.1).
/// </summary>
public sealed class JournalsCorpusReplayTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public static TheoryData<string> Targets => CorpusReplay.Names(JournalsTargets.All);

    [Theory]
    [MemberData(nameof(Targets))]
    public Task EverySeedFromTheGoldenVectorsSatisfiesTheOracle(string target) => CorpusReplay.ReplaySeedsAsync(JournalsTargets.All, target, _leaks.InScopeAsync);

    [Theory]
    [MemberData(nameof(Targets))]
    public Task EveryCommittedCorpusInputSatisfiesTheOracle(string target) => CorpusReplay.ReplayCorpusAsync(JournalsTargets.All, target, _leaks.InScopeAsync);

    public void Dispose() => _leaks.Dispose();
}
