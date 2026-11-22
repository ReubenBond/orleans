using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Journals.Tests;

/// <summary>
/// The rebase cases against the version checker (journals.md §8.3, §10; JS-I10): one writer goes on writing a key while
/// its ledgers close, drain into the memory store, and drop, so each ledger's admission loads the key afresh, as a
/// restarted primary does: loads which meet a drained ledger the map still lists, two rebases once every ledger holding
/// the key's rows has dropped, and a load whose ledger drains and drops between its store read and its scans. Each accepted version goes to the version checker, which finds none repeated;
/// each of the loader's three rebase mutants, which rebase with live rows, to a version base an earlier load saw, or
/// from a map taken after the archive, repeats one, or fails the scenario's load first.
/// </summary>
public sealed class JournalRebaseTests : IDisposable
{
    private const string Key = "k";

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    /// <summary>A break of the loader's rebase, which the scenario runs under.</summary>
    public enum Rebase
    {
        /// <summary>The loader of journals.md §8.3.</summary>
        None,

        /// <summary><c>rebase-with-live-rows</c>.</summary>
        WithLiveRows,

        /// <summary><c>rebase-on-stale-token</c>.</summary>
        OnStaleToken,

        /// <summary><c>rebase-from-map-after-archive</c>, the history reader's read-order mutant.</summary>
        FromMapAfterArchive,
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _leaks.Dispose();

    /// <summary>
    /// The loader's rebase keeps a key's versions unique (JS-I10): through every step the version checker finds no
    /// accepted version repeated, and the key rebased at least twice, each to a version base it never held. The last load
    /// rebases a third time unless its scan of the closed ledger ended before the ledger dropped, which keeps its live row.
    /// </summary>
    [Fact]
    public async Task ARebasedKeysVersionsNeverRepeat()
    {
        var since = DebugAssertions.RaisedCount;
        var run = await RunAsync(Rebase.None);

        Assert.Null(run.Failure);
        Assert.Empty(DebugAssertions.TakeRaisedBy(TestContext.Current.Test, since));
        Assert.True(run.Bases.Count >= 3, $"The key took the version bases [{string.Join(", ", run.Bases)}]: Origin and at least two rebases.");
    }

    /// <summary>
    /// Each of the loader's rebase mutants fails its own check (journals.md §10): the version checker finds an accepted
    /// version of <c>rebase-on-stale-token</c> and of <c>rebase-from-map-after-archive</c> repeated, and the history read's
    /// chain check (HI-I4) refuses <c>rebase-with-live-rows</c>'s load first, or, in a Debug build, the load-side assertion
    /// that each live row takes the version after its predecessor's.
    /// </summary>
    /// <param name="mutant">The loader's mutant.</param>
    /// <param name="check">A fragment of the failure the mutant's own check reports.</param>
    [Theory]
    [InlineData(Rebase.WithLiveRows, "(HI-I4)")]
    [InlineData(Rebase.OnStaleToken, "JS-I10: the version checker found")]
    [InlineData(Rebase.FromMapAfterArchive, "JS-I10: the version checker found")]
    public async Task EachRebaseMutantFailsItsOwnCheck(Rebase mutant, string check)
    {
        var since = DebugAssertions.RaisedCount;
        var run = await RunAsync(mutant);
        var asserted = DebugAssertions.TakeRaisedBy(TestContext.Current.Test, since);

        if (mutant == Rebase.WithLiveRows && asserted.Count > 0)
        {
            Assert.Contains(asserted, static raised => raised.Message.Contains("JS-I10 at load", StringComparison.Ordinal));
            return;
        }

        Assert.Empty(asserted);
        Assert.NotNull(run.Failure);
        Assert.Contains(check, run.Failure, StringComparison.Ordinal);
        if (mutant != Rebase.WithLiveRows)
        {
            Assert.Contains("repeats", run.Failure, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Runs the scenario under <paramref name="mutant"/>: the first violation or failure, in words, and the version bases
    /// the key's accepted versions took, in order.
    /// </summary>
    private static async Task<(string? Failure, List<string> Bases)> RunAsync(Rebase mutant)
    {
        await using var cluster = await JournalCluster.StartAsync();
        cluster.Journals.LoadMutation = mutant switch
        {
            Rebase.WithLiveRows => JournalLoadMutant.RebaseWithLiveRows,
            Rebase.OnStaleToken => JournalLoadMutant.RebaseOnStaleToken,
            Rebase.None or Rebase.FromMapAfterArchive => JournalLoadMutant.None,
            _ => throw new ArgumentOutOfRangeException(nameof(mutant)),
        };
        cluster.Journals.ArchiveReader.MapAfterStoreMutant = mutant == Rebase.FromMapAfterArchive;
        var writer = new Writer(cluster);
        try
        {
            // Each ledger's admission loads the key afresh. Two loads meet a drained ledger the map still lists, whose row
            // of the key is live and held by the prefix; the second also meets the key's live rows of another ledger.
            await writer.WriteAsync("a");
            var first = await cluster.ReplaceAsync(Key);
            await cluster.DrainAsync(first);
            await writer.WriteAsync("b");
            var second = await cluster.ReplaceAsync(Key);
            await writer.WriteAsync("c");

            // Two rebases, once every ledger holding the key's rows has dropped.
            await cluster.DropAsync(first);
            await DrainAndDropAsync(cluster, second);
            await DrainAndDropAsync(cluster, await cluster.ReplaceAsync(Key));
            await writer.WriteAsync("d");
            await DrainAndDropAsync(cluster, await cluster.ReplaceAsync(Key));
            await writer.WriteAsync("e");

            // A load whose store read answers before the key's ledger drains and drops, and whose scans begin after.
            var last = await cluster.ReplaceAsync(Key);
            cluster.Store.HoldNextRead(afterAnswer: true);
            var write = writer.WriteAsync("f");
            await cluster.Store.ReadHeld.WaitAsync(Token);
            await DrainAndDropAsync(cluster, last);
            cluster.Store.Release();
            await write;
            await writer.WriteAsync("g");
        }
        catch (Exception exception) when (writer.Violation is null && mutant != Rebase.None && exception is JournalStoreCorruptException or DebugAssertionException)
        {
            return (exception.Message, writer.Bases);
        }

        return (writer.Violation, writer.Bases);
    }

    private static async Task DrainAndDropAsync(JournalCluster cluster, LedgerId ledger)
    {
        await cluster.DrainAsync(ledger);
        await cluster.DropAsync(ledger);
    }

    /// <summary>
    /// A writer of the key which writes at the version its last answer gave, and once more at a rejection's; each accepted
    /// version goes to the version checker.
    /// </summary>
    private sealed class Writer(JournalCluster cluster)
    {
        private readonly VersionChecker _versions = new();
        private JournalVersion _known = JournalVersion.Origin;

        public string? Violation { get; private set; }

        public List<string> Bases { get; } = [JournalVersion.Origin.Base.ToString()];

        public async Task WriteAsync(string text)
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                switch ((await cluster.AppendAsync(Key, _known, text)).Value)
                {
                    case JournalWriteOutcome.Accepted accepted:
                        _known = accepted.Version;
                        if (accepted.Version.Base.ToString() is var name && name != Bases[^1])
                        {
                            Bases.Add(name);
                        }

                        if (_versions.Version(JournalRowKey.Encode(Key).Span, accepted.Version.ToRowVersion()) is { } repeated)
                        {
                            Violation ??= $"JS-I10: the version checker found {repeated}";
                        }

                        return;
                    case JournalWriteOutcome.Rejected rejected:
                        _known = rejected.CurrentVersion;
                        break;
                    case var other:
                        throw new InvalidOperationException($"A write of the key at {_known} answered {other}.");
                }
            }

            throw new InvalidOperationException($"A write of the key at the version its rejection gave, {_known}, was rejected.");
        }
    }
}
