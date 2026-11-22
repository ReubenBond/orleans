using CsCheck;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Journals.Tests;

/// <summary>
/// The cold-primary property (journals.md §10; JS-I6, JS-I10): a primary restarted at any cut loads what the warm primary
/// holds. One key is written in a live ledger across segments, and its ended segments drain into the memory store one at a
/// time; at every cut, a load from one history read, as a restarted primary's, gives the warm primary's state, its version
/// among it, and its planning state: the recorded segment and its token while the key's newest segment is live and not
/// held, and the token its next write starts from at every cut. Once the ledger closes, drains, and drops, the load
/// rebases: content, existence, tombstone, and that token are the warm primary's, at the version base's count 0. Examples
/// show the two mutants the property catches: a replay without <c>Holds</c>, and no lazy roll. The cuts fall at the
/// start, while the key's newest segment is still open, and between segments: a drain holds a key's write of a segment
/// whole, so no cut within a segment is reachable here; a partly held newest write, which the archive's merge allows, is
/// <c>JournalFoldTests</c>'s, its planning state from the read's next token.
/// </summary>
public sealed class JournalColdPrimaryTests : IDisposable
{
    private const string Key = "k";

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    /// <summary>A step of a generated history: an entry kind chosen among those which apply, or a segment's end.</summary>
    public enum Step
    {
        Append,
        Replace,
        EmptyReplace,
        Delete,
        Expire,
        EndSegment,
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Gets a generator of histories which begin with an <c>Append</c>; an <c>Expired</c> follows only a <c>Delete</c>.</summary>
    private static Gen<Step[]> Histories { get; } = Gen.Enum<Step>().Array[0, 14].Select(static choices =>
    {
        var steps = new List<Step> { Step.Append };
        var deleted = false;
        foreach (var choice in choices)
        {
            var step = choice == Step.Expire && !deleted ? Step.Append : choice;
            steps.Add(step);
            deleted = step == Step.Delete || deleted && step == Step.EndSegment;
        }

        return steps.ToArray();
    });

    public void Dispose() => _leaks.Dispose();

    /// <summary>At every cut of a partly drained live ledger, and after it drops, a cold load gives the warm primary's state.</summary>
    [Fact]
    public Task AColdLoadAtEveryCutIsTheWarmPrimarysState() =>
        Histories.SampleAsync(
            steps => _leaks.InScopeAsync(async () => Assert.Null(await CheckAsync(steps, JournalLoadMutant.None))),
            iter: 25,
            threads: 1);

    /// <summary>
    /// Each mutant fails the property on a history of appends across three segments: a replay without <c>Holds</c> at a
    /// cut whose prefix holds an append, and no lazy roll at a cut whose newest segment is live and not held.
    /// </summary>
    [Theory]
    [InlineData(nameof(JournalLoadMutant.ReplayWithoutHolds), "After 1 of")]
    [InlineData(nameof(JournalLoadMutant.NoLazyRoll), "After 0 of")]
    public async Task EachMutantFailsTheProperty(string mutant, string cut) =>
        Assert.StartsWith(cut, await CheckAsync([Step.Append, Step.EndSegment, Step.Append, Step.EndSegment, Step.Append], Enum.Parse<JournalLoadMutant>(mutant)), StringComparison.Ordinal);

    /// <summary>
    /// Writes <paramref name="steps"/> to the key in a live ledger, ends its last segment, and compares a cold load with the
    /// warm primary at every cut and after the ledger drops; returns the first difference, in words, or null.
    /// </summary>
    private static async Task<string?> CheckAsync(Step[] steps, JournalLoadMutant mutant)
    {
        await using var cluster = await JournalCluster.StartAsync(new JournalStoreOptions { TombstoneRetention = TimeSpan.Zero });
        var key = JournalRowKey.Encode(Key);
        var ledger = cluster.OpenLedger(Key);
        var version = JournalVersion.Origin;
        foreach (var (step, index) in steps.Select(static (step, index) => (step, index)))
        {
            var outcome = step switch
            {
                Step.Append => await cluster.AppendAsync(Key, version, $"a{index}"),
                Step.Replace => await cluster.ReplaceAsync(Key, version, $"r{index}"),
                Step.EmptyReplace => await cluster.ReplaceAsync(Key, version, string.Empty),
                Step.Delete => await cluster.DeleteAsync(Key, version),
                Step.Expire => await cluster.Journals.ExpireAsync(key, version, Token),
                Step.EndSegment => (JournalWriteOutcome?)null,
                _ => throw new ArgumentOutOfRangeException(nameof(steps)),
            };
            if (outcome is { } answered)
            {
                version = Assert.IsType<JournalWriteOutcome.Accepted>(answered.Value).Version;
            }
            else
            {
                _ = await cluster.Ledgers.EndSegmentAsync(ledger, Token);
            }
        }

        // A cut while the key's newest segment is the ledger's open one, before any drain.
        cluster.Journals.LoadMutation = mutant;
        var writing = cluster.Journals.Warm(key) ?? throw new InvalidOperationException("The key is not warm with nothing in flight.");
        var opened = await LoadColdAsync(cluster, key);
        if (opened.Failure is not null || opened.State != writing)
        {
            return $"After 0 of the segments drained, with the newest segment open, the cold load gives {opened.State}, not {writing}: {opened.Failure}";
        }

        _ = await cluster.Ledgers.EndSegmentAsync(ledger, Token);
        var warm = cluster.Journals.Warm(key) ?? throw new InvalidOperationException("The key is not warm with nothing in flight.");
        var content = (await cluster.ReadAsync(Key)).Content;
        var segments = await cluster.ScanSegmentsAsync(ledger, await cluster.Ledgers.ConfirmOpenAsync(ledger, Token));
        try
        {
            for (var drained = 0; drained <= segments.Count; drained++)
            {
                if (drained > 0)
                {
                    cluster.Drain(segments[drained - 1]);
                }

                var cold = await LoadColdAsync(cluster, key);
                if (cold.Failure is { } failure)
                {
                    return $"After {drained} of {segments.Count} segments drained, the cold load failed: {failure}";
                }

                var newestLive = drained < segments.Count;
                var expected = newestLive ? warm : warm with { Segment = null, Token = warm.NextToken };
                if (cold.State != expected)
                {
                    return $"After {drained} of {segments.Count} segments drained, the cold load gives {cold.State}, not {expected}.";
                }
            }
        }
        finally
        {
            foreach (var segment in segments)
            {
                KeyedRow.Release(segment);
            }
        }

        var closed = await cluster.ReplaceAsync(Key);
        await cluster.DrainAsync(closed);
        await cluster.DropAsync(closed);
        var rebased = await LoadColdAsync(cluster, key);
        if (rebased.Failure is { } dropped)
        {
            return $"Once the ledger dropped, the cold load failed: {dropped}";
        }

        var after = rebased.State;
        if (after.Version.Count != 0 || (after.Exists, after.DeletedAt, after.ContentLength, after.Segment, after.Token) != (warm.Exists, warm.DeletedAt, warm.ContentLength, null, warm.NextToken))
        {
            return $"Once the ledger dropped, the cold load gives {after}, not the warm primary's {warm} rebased.";
        }

        var read = await cluster.ReadAsync(Key);
        return read.Content == content ? null : $"Once the ledger dropped, a read gives the content '{read.Content}', not '{content}'.";
    }

    /// <summary>Loads the key cold, or returns the failure of the load, which a mutant may cause.</summary>
    private static async Task<(JournalWarm State, string? Failure)> LoadColdAsync(JournalCluster cluster, JournalRowKey key)
    {
        try
        {
            return (await cluster.Journals.LoadColdAsync(key, Token), null);
        }
        catch (JournalStoreCorruptException exception)
        {
            return (default, exception.Message);
        }
    }
}
