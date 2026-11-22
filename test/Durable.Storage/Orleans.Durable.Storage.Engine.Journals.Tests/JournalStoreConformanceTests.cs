using System.Text.RegularExpressions;
using CsCheck;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Journals.Tests;

/// <summary>
/// Checks <see cref="JournalStore"/> over live ledgers on memory replicas and the memory archive store against
/// <see cref="JournalStoreSpec"/>, the Journals spec, whose state is <see cref="JournalModelState"/> (verification.md
/// §3.1, §4.1; journals.md §10): random sequences and concurrent groups which CsCheck generates and shrinks, and every
/// short sequence Accordant generates; with the environment's steps, a takeover, a write it fences, and a crash which
/// leaves a write <c>Ambiguous</c>; and a mutant per contract clause, each a deliberately wrong store which the spec
/// catches at the call which breaks the clause.
/// </summary>
public sealed class JournalStoreConformanceTests : IDisposable
{
    /// <summary>The memory store's limit on a generation's appends, which the spec judges.</summary>
    private const int MaxAppends = 3;

    /// <summary>The memory store's threshold of a generation's appends for its replace request.</summary>
    private const int ReplaceRequestAppends = 2;

    private static readonly JournalStoreSpecOptions Limits = new(MaxAppends, ReplaceRequestAppends);

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _leaks.Dispose();

    [Fact]
    public async Task TheStoreConformsOverMemoryReplicas()
    {
        var kit = Kit(static seed => JournalStoreTarget.StartAsync(seed, MaxAppends, ReplaceRequestAppends));
        await kit.SampleAsync(kit.Sequences(30), iterations: 300);
        await kit.SampleAsync(kit.ConcurrentSequences(15), iterations: 200);
    }

    /// <summary>
    /// Every sequence of three of the calls below conforms: writes at the newest and at a stale version, each kind of
    /// write, a read, the environment's steps, and the expiry scan, so each step meets each other in each order. The calls
    /// are enumerated rather than taken from Accordant's generation, which explores the spec's states without a system:
    /// its placeholder answers name no version, so it reaches only whether the process serves the journals.
    /// </summary>
    [Fact]
    public async Task TheStoreConformsInEverySequenceOfThreeCalls()
    {
        object[] requests =
        [
            new JournalWrite(0, JournalWriteKind.Append, 0, 1),
            new JournalWrite(0, JournalWriteKind.Append, 1, 2),
            new JournalWrite(0, JournalWriteKind.Replace, 0, 3),
            new JournalWrite(0, JournalWriteKind.Delete, 0, 0),
            new JournalWrite(0, JournalWriteKind.Append, 0, 4, JournalDisruption.Takeover),
            new JournalWrite(0, JournalWriteKind.Append, 0, 5, JournalDisruption.Crash),
            new JournalRead(0),
            JournalTakeover.Instance,
            JournalScan.Instance,
        ];
        var calls = requests.Select(static request => new ConformanceCall(JournalStoreSpec.OperationOf(request), request)).ToArray();
        var kit = Kit(static seed => JournalStoreTarget.StartAsync(seed, MaxAppends, ReplaceRequestAppends));
        var seed = 0;
        foreach (var first in calls)
        {
            foreach (var second in calls)
            {
                foreach (var third in calls)
                {
                    await kit.RunAsync(new ConformanceCase([new([first]), new([second]), new([third])], Seed: seed++));
                }
            }
        }
    }

    /// <summary>
    /// The spec catches a store which breaks each clause of the contract, at the call which shows it: an entry accepted at
    /// a stale version (JS-I1); <c>ReplaceRequired</c> answered where the store admits the append, or an append accepted past
    /// the limits (journals.md §2.2); a <c>Moved</c>, a <c>Fenced</c>, or an <c>Ambiguous</c> write with more effect than it
    /// may have (JS-I4, §2.2); an <c>Accepted</c> or a <c>Rejected</c> version which names no state of the journal (§2.1,
    /// §2.2); a read which misses an acknowledged entry or reflects one never decided (JS-I3); and a delete or a replace
    /// folded wrongly (§2.1). The limits' two mutants are memory stores whose limits are one below and one above those the
    /// spec judges. A takeover's race leaves a case's answers unreplayable at times, when the kit reports CsCheck's shrunk
    /// case rather than a minimized one, so the round the spec refuses need not be the case's last.
    /// </summary>
    [Theory]
    [InlineData(JournalStoreMutant.AcceptsAtStaleVersion, 0, @"^Append\([^)]*\)( during a \w+)? → append\([^)]*\) -> Accepted")]
    [InlineData(JournalStoreMutant.None, MaxAppends - 1, @"→ append\([^)]*\) -> ReplaceRequired$")]
    [InlineData(JournalStoreMutant.None, MaxAppends + 1, @"→ append\([^)]*\) -> Accepted")]
    [InlineData(JournalStoreMutant.MovedEntryLands, 0, @"^(Read|Append|Replace|Delete|ExpiryScan)\b")]
    [InlineData(JournalStoreMutant.FencedEntryLands, 0, @"^(Read|Append|Replace|Delete|ExpiryScan)\b")]
    [InlineData(JournalStoreMutant.AmbiguousEntryWrittenTwice, 0, @"^(Read|Append|Replace|Delete|ExpiryScan)\b")]
    [InlineData(JournalStoreMutant.AcceptedVersionNamesNoState, 0, @"^(Read|Append|Replace|Delete|ExpiryScan)\b")]
    [InlineData(JournalStoreMutant.ReadMissesAcknowledged, 0, @"^Read\(")]
    [InlineData(JournalStoreMutant.ReadReflectsUndecided, 0, @"^Read\(")]
    [InlineData(JournalStoreMutant.DeleteLeavesJournal, 0, @"^Read\([^)]*\) → read\([^)]*\) -> exists")]
    [InlineData(JournalStoreMutant.ReplaceFoldsAsAppend, 0, @"^(Read|Append|Replace)\(")]
    [InlineData(JournalStoreMutant.RejectionNamesNoState, 0, @"-> Rejected\(")]
    public async Task TheSpecCatchesAMutantOfEachClause(JournalStoreMutant mutant, int storeMaxAppends, string failingCall)
    {
        var maxAppends = storeMaxAppends == 0 ? MaxAppends : storeMaxAppends;
        var kit = Kit(seed => JournalStoreTarget.StartAsync(seed, maxAppends, Math.Min(ReplaceRequestAppends, maxAppends), mutant));
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => kit.SampleAsync(kit.Sequences(30), iterations: 400));

        var timeout = TimeSpan.FromSeconds(5);
        var report = Regex.Match(
            failure.Message,
            @"The spec does not allow round (?<round>\d+) of (?<rounds>\d+), seed \d+:.*?^> \k<round>\. (?<call>[^\r\n]*)",
            RegexOptions.Singleline | RegexOptions.Multiline,
            timeout);
        Assert.True(report.Success, failure.Message);
        Assert.Matches(new Regex(failingCall, RegexOptions.None, timeout), report.Groups["call"].Value);
        Assert.Matches(new Regex(@"Replay the shrunk case with CLOG_TEST_SEED=\S+", RegexOptions.None, timeout), failure.Message);
    }

    /// <summary>Returns a kit of the spec's operations over the targets <paramref name="create"/> starts.</summary>
    private ConformanceKit<JournalStoreState, JournalStoreTarget> Kit(Func<int, Task<JournalStoreTarget>> create)
    {
        var keys = Gen.Int[0, JournalStoreSpec.Keys.Length - 1];
        var kinds = Gen.Frequency((5, Gen.Const(JournalWriteKind.Append)), (2, Gen.Const(JournalWriteKind.Replace)), (2, Gen.Const(JournalWriteKind.Delete)));
        var backs = Gen.Frequency((6, Gen.Const(0)), (2, Gen.Const(1)), (1, Gen.Const(2)));
        Gen<JournalWrite> Writes(JournalDisruption disruption) =>
            Gen.Select(keys, kinds, backs, Gen.Byte[1, 255], (key, kind, back, value) => new JournalWrite(key, kind, back, value, disruption));

        return new ConformanceKit<JournalStoreState, JournalStoreTarget>(
                JournalStoreSpec.Create(Limits),
                static () => new JournalStoreState(),
                create,
                static (_, _) => true,
                Token,
                _leaks.InScopeAsync,
                JournalStoreSpec.Conflicts)
            .Operation(JournalStoreSpec.Write, Writes(JournalDisruption.None), static (target, request) => target.WriteAsync(request), weight: 12)
            .Operation(JournalStoreSpec.Read, keys.Select(static key => new JournalRead(key)), static (target, request) => target.ReadAsync(request), weight: 6)
            .Operation(JournalStoreSpec.FencedWrite, Writes(JournalDisruption.Takeover), static (target, request) => target.WriteAsync(request))
            .Operation(JournalStoreSpec.CrashingWrite, Writes(JournalDisruption.Crash), static (target, request) => target.WriteAsync(request))
            .Operation(JournalStoreSpec.Takeover, Gen.Const(JournalTakeover.Instance), static (target, _) => target.TakeOverAsync(), weight: 3)
            .Operation(JournalStoreSpec.ExpiryScan, Gen.Const(JournalScan.Instance), static (target, _) => target.ScanAsync());
    }
}
