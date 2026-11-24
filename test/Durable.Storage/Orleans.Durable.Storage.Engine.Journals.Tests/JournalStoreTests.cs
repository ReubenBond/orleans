using System.Buffers;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Archive.Memory;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.MetaStore;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Journals.Tests;

/// <summary>
/// <see cref="JournalStore"/> over live ledgers and the memory archive store (journals.md §2, §8): writes with every
/// outcome admission answers on a live primary, with the archive store's replace request; reads which fold one history
/// read, the version with the content and its length; loads which continue a key's version across ledgers and rebase it
/// once every ledger which held its rows has dropped, read and reloaded between a drain and its drop; a key warm from a
/// dropped parent's rows, which accepts a write at either name of its state; a load and a read
/// the map moved past mid-read, a held write whose ledger is replaced, a writer whose lifetime stops, a follower which
/// starts only after a split, and a ledger
/// handed to another node; the wait for admission's own batches below a read's tail; the registry's adoption raced by
/// two routes; disposal of the store and of the registry with writes in flight; the expiry scan, which proposes an
/// <c>Expired</c> of each warm journal whose tombstone's retention has passed, in rounds; the preconditions, the startup checks,
/// corruption, and a quorum out of reach, the store's unavailability. Every key's rows drain into the memory store, which checks the token chain admission planned (HI-I4).
/// Warm state's budget is zero where a test needs each touch to load the key afresh. Rare paths are held by construction:
/// a gated archive read, a gated replica opening or write answer, or a gated sweep.
/// </summary>
public sealed class JournalStoreTests : IDisposable
{
    private static readonly JournalVersion Origin = JournalVersion.Origin;
    private static readonly NodeId OtherNode = new(new Guid("00000000-0000-0000-0000-00000000000b"));

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _leaks.Dispose();

    /// <summary>
    /// A write is accepted only at its journal's version, which every accepted entry advances, and is otherwise rejected
    /// with the version; a read answers the fold of the history, its version the version of its content: a
    /// <c>Replace</c> supersedes, an <c>Append</c> adds, and a <c>Delete</c> leaves the journal absent and empty. A journal
    /// never written reads as absent at <c>Origin</c>.
    /// </summary>
    [Fact]
    public async Task AWriteAppliesOnlyAtItsJournalsVersionAndAReadFoldsTheHistory()
    {
        await using var cluster = await JournalCluster.StartAsync();

        var one = Accepted(await cluster.AppendAsync("a", Origin, "x"));
        Assert.Equal(new JournalWriteOutcome(new JournalWriteOutcome.Rejected(one)), await cluster.AppendAsync("a", Origin, "y"));
        var two = Accepted(await cluster.ReplaceAsync("a", one, "snap"));
        var three = Accepted(await cluster.AppendAsync("a", two, "+"));

        Assert.Equal((three, true, "snap+"), await cluster.ReadAsync("a"));
        var four = Accepted(await cluster.DeleteAsync("a", three));
        Assert.Equal((four, false, string.Empty), await cluster.ReadAsync("a"));
        Assert.Equal((Origin, false, string.Empty), await cluster.ReadAsync("never"));
    }

    /// <summary>
    /// orleans.md §5.9: the store answers no applied slot for a ledger before it admits a write there, and once an owner
    /// read has waited for its admission's batches, the slot past every accepted write.
    /// </summary>
    [Fact]
    public async Task TheAppliedSlotPassesEveryWriteAnOwnerReadWaitedFor()
    {
        await using var cluster = await JournalCluster.StartAsync();
        var ledger = cluster.OpenLedger("a");
        Assert.Null(cluster.Journals.AppliedBelow(ledger));

        var one = Accepted(await cluster.AppendAsync("a", Origin, "x"));
        _ = Accepted(await cluster.AppendAsync("a", one, "y"));
        _ = await cluster.ReadAsync("a");

        Assert.Equal(new Slot(2), cluster.Journals.AppliedBelow(ledger));
    }

    /// <summary>
    /// The archive store's limits and its replace request (journals.md §2.4, §8.1 step 4): an accepted write carries the
    /// store's request at the key's token, and a read its own, which its content past twice its base's bytes also gives;
    /// an <c>Append</c> past the store's per-generation limits is answered <c>ReplaceRequired</c>, with no effect, and a
    /// <c>Replace</c> then opens a generation, whose request starts afresh.
    /// </summary>
    [Fact]
    public async Task AWriteAndAReadCarryTheStoresReplaceRequestAndItsLimitsAnswerReplaceRequired()
    {
        await using var cluster = await JournalCluster.StartAsync(memory: new InMemoryArchiveStore(maxAppends: 3, replaceRequestAppends: 2));

        // On an empty base (0 bytes), the very first non-empty Append passes twice the base (journals.md §8.7) and
        // requests a Replace on Accepted as well as on Read, even below the store's append threshold.
        var one = AcceptedHint(await cluster.AppendAsync("a", Origin, "1"));
        var two = AcceptedHint(await cluster.AppendAsync("a", one.Version, "2"));
        var full = AcceptedHint(await cluster.AppendAsync("a", two.Version, "3"));
        Assert.Equal((true, true, true), (one.ReplaceRequested, two.ReplaceRequested, full.ReplaceRequested));

        Assert.Equal(new JournalWriteOutcome(JournalWriteOutcome.ReplaceRequired.Instance), await cluster.AppendAsync("a", full.Version, "4"));
        Assert.Equal((full.Version, true, "123", true), await ReadHintAsync(cluster, "a"));

        var three = AcceptedHint(await cluster.ReplaceAsync("a", full.Version, "SSSS"));
        Assert.False(three.ReplaceRequested);
        Assert.Equal((three.Version, true, "SSSS", false), await ReadHintAsync(cluster, "a"));

        // Within twice its base's bytes, both the write and the read ask for a replace by the store's request alone.
        var four = AcceptedHint(await cluster.AppendAsync("a", three.Version, "4"));
        var five = AcceptedHint(await cluster.AppendAsync("a", four.Version, "5"));
        Assert.Equal((false, true), (four.ReplaceRequested, five.ReplaceRequested));
        Assert.Equal((five.Version, true, "SSSS45", true), await ReadHintAsync(cluster, "a"));

        // Past twice its base's bytes, both warm state on Accepted and the read ask by size alone (journals.md §8.7).
        var six = AcceptedHint(await cluster.ReplaceAsync("a", five.Version, "SS"));
        Assert.False(six.ReplaceRequested);
        var within = AcceptedHint(await cluster.AppendAsync("a", six.Version, "12"));
        Assert.False(within.ReplaceRequested);
        Assert.Equal((within.Version, true, "SS12", false), await ReadHintAsync(cluster, "a"));

        var reset = AcceptedHint(await cluster.ReplaceAsync("a", within.Version, "SS"));
        var seven = AcceptedHint(await cluster.AppendAsync("a", reset.Version, "123"));
        Assert.True(seven.ReplaceRequested);
        Assert.Equal((seven.Version, true, "SS123", true), await ReadHintAsync(cluster, "a"));
    }

    /// <summary>
    /// A process which serves no open ledger over a key answers its writes and reads <c>Moved</c>, naming no owner while
    /// the map lists none, as before bootstrap; a write which breaks a precondition is still refused.
    /// </summary>
    [Fact]
    public async Task AKeyNoServedLedgerHoldsIsMoved()
    {
        await using var cluster = await JournalCluster.StartAsync(new JournalStoreOptions { MaxBodyBytes = 4 }, bootstrap: false);
        var moved = new JournalMoved(Owner: null);

        Assert.Equal(new JournalWriteOutcome(moved), await cluster.AppendAsync("a", Origin, "x"));
        Assert.Equal(moved, (await cluster.Journals.ReadAsync(JournalRowKey.Encode("a"), Token)).Value);
        _ = await Assert.ThrowsAsync<ArgumentException>(() => cluster.AppendAsync("a", Origin, "fives"));
        using var fives = ArcBuffer.Wrap("fives"u8.ToArray());
        using var oversize = JournalEntryRecord.Append(JournalRowKey.Encode("a"), Origin, fives);
        _ = Assert.Throws<ArgumentException>(() => { _ = cluster.Journals.WriteAsync(oversize, Token); });
    }

    /// <summary>
    /// A write which breaks a precondition of journals.md §2.2 is refused with <see cref="ArgumentException"/> before it
    /// leaves the process, whether the store encodes it or receives it encoded: a payload past <c>MaxBodyBytes</c>, an
    /// empty <c>Append</c>, a key the archive store does not admit, an <c>Expired</c>, which only the expiry scan writes,
    /// and the default record.
    /// </summary>
    [Fact]
    public async Task AWriteWhichBreaksAPreconditionIsRefused()
    {
        await using var cluster = await JournalCluster.StartAsync(new JournalStoreOptions { MaxBodyBytes = 4 });
        var refused = JournalRowKey.Encode("refused");
        using var fives = ArcBuffer.Wrap("fives"u8.ToArray());
        using var four = ArcBuffer.Wrap("four"u8.ToArray());
        using var oversize = JournalEntryRecord.Append(JournalRowKey.Encode("a"), Origin, fives);
        using var expired = JournalEntryRecord.Expired(JournalRowKey.Encode("a"), Origin);
        using var toRefused = JournalEntryRecord.Append(refused, Origin, four);
        cluster.Store.RefusedKey = refused.Span.ToArray();

        _ = Assert.Throws<ArgumentException>(() => cluster.Journals.EncodeAppend(JournalRowKey.Encode("a"), Origin, fives).Dispose());
        _ = Assert.Throws<ArgumentException>(() => cluster.Journals.EncodeReplace(JournalRowKey.Encode("a"), Origin, fives).Dispose());
        _ = Assert.Throws<ArgumentException>(() => cluster.Journals.EncodeAppend(JournalRowKey.Encode("a"), Origin, default).Dispose());
        _ = Assert.Throws<ArgumentException>(() => cluster.Journals.EncodeAppend(refused, Origin, four).Dispose());
        _ = Assert.Throws<ArgumentException>(() => cluster.Journals.EncodeReplace(refused, Origin, four).Dispose());
        _ = Assert.Throws<ArgumentException>(() => cluster.Journals.EncodeDelete(refused, Origin).Dispose());
        foreach (var record in (JournalEntryRecord[])[oversize, expired, toRefused, default])
        {
            _ = Assert.Throws<ArgumentException>(() => { _ = cluster.Journals.WriteAsync(record, Token); });
        }

        Assert.Equal((Origin, false, string.Empty), await cluster.ReadAsync("a"));
        _ = Accepted(await cluster.AppendAsync("a", Origin, "four"));
    }

    /// <summary>
    /// A key's version continues across ledgers: once its ledger is replaced, the successor's admission loads it from the
    /// closed ledger's rows, in one history read, and accepts the writer's next entry at the version it holds; with no warm
    /// state each write and each read reads the history once.
    /// </summary>
    [Fact]
    public async Task AKeysVersionContinuesAcrossLedgers()
    {
        await using var cluster = await JournalCluster.StartAsync(new JournalStoreOptions { WarmStateBytes = 0 });
        var one = Accepted(await cluster.AppendAsync("a", Origin, "x"));
        _ = await cluster.ReplaceAsync("a");
        var reads = cluster.Store.Reads;

        var two = Accepted(await cluster.AppendAsync("a", one, "y"));
        Assert.Equal(reads + 1, cluster.Store.Reads);
        var three = Accepted(await cluster.AppendAsync("a", two, "z"));
        Assert.Equal(reads + 2, cluster.Store.Reads);

        Assert.Equal((three, true, "xyz"), await cluster.ReadAsync("a"));
        Assert.Equal(reads + 3, cluster.Store.Reads);
    }

    /// <summary>
    /// Once every ledger which held a key's rows has drained and dropped, its next load rebases (JS-I10): a writer holding
    /// the version from before the drop is rejected once, with the rebased version, from one history read, which a read
    /// also gives with the same content; the writer's next entry at it is accepted, and a read then folds the prefix's
    /// content and the live row's after it.
    /// </summary>
    [Fact]
    public async Task AKeyWhoseLedgersDroppedRebasesAndAStaleWriterIsRejectedOnce()
    {
        await using var cluster = await JournalCluster.StartAsync(new JournalStoreOptions { WarmStateBytes = 0 });
        var one = Accepted(await cluster.AppendAsync("a", Origin, "x"));
        var closed = await cluster.ReplaceAsync("a");
        await cluster.DrainAsync(closed);
        await cluster.DropAsync(closed);
        var reads = cluster.Store.Reads;

        var rejected = Assert.IsType<JournalWriteOutcome.Rejected>((await cluster.AppendAsync("a", one, "y")).Value).CurrentVersion;
        Assert.Equal(reads + 1, cluster.Store.Reads);
        Assert.NotEqual(one, rejected);
        Assert.Equal((rejected, true, "x"), await cluster.ReadAsync("a"));

        var two = Accepted(await cluster.AppendAsync("a", rejected, "y"));
        Assert.Equal(reads + 3, cluster.Store.Reads);
        Assert.Equal((two, true, "xy"), await cluster.ReadAsync("a"));
    }

    /// <summary>
    /// A key warm in a split's child from its parent's rows keeps its version as a name of its state once the parent has
    /// drained and dropped, while a read names the same state by the rebased version (journals.md §8.3): a write at either
    /// name is accepted. A write at the rebased name, which the warm version is not, loads the key again, in exactly one
    /// history read, before admission decides it; a write at the warm version reads nothing.
    /// </summary>
    /// <param name="atRebasedName">Whether the write expects the rebased version the read gave, or the warm one.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AWarmKeyWhoseRowsAllDroppedAcceptsAWriteAtEitherNameOfItsState(bool atRebasedName)
    {
        var (cluster, warm, rebased) = await WarmKeyWhoseRowsAllDroppedAsync(LedgerAdmission.ReloadMutant.None);
        await using (cluster)
        {
            var reads = cluster.Store.Reads;

            var two = Accepted(await cluster.AppendAsync("a", atRebasedName ? rebased : warm, "y"));

            Assert.Equal(reads + (atRebasedName ? 1 : 0), cluster.Store.Reads);
            Assert.Equal((two, true, "xy"), await cluster.ReadAsync("a"));
        }
    }

    /// <summary>
    /// The mutant which rejects a warm key's entry without loading it again rejects a write at the rebased name a read gave
    /// (journals.md §10), with the warm version, which no read answers: a caller which reads again before writing again
    /// is rejected until another write lands.
    /// </summary>
    [Fact]
    public async Task RejectingWithoutAReloadRejectsAWriteAtTheRebasedName()
    {
        var (cluster, warm, rebased) = await WarmKeyWhoseRowsAllDroppedAsync(LedgerAdmission.ReloadMutant.RejectsWithoutReload);
        await using (cluster)
        {
            var reads = cluster.Store.Reads;

            Assert.Equal(new JournalWriteOutcome(new JournalWriteOutcome.Rejected(warm)), await cluster.AppendAsync("a", rebased, "y"));

            Assert.Equal(reads, cluster.Store.Reads);
            Assert.Equal((rebased, true, "x"), await cluster.ReadAsync("a"));
        }
    }

    /// <summary>
    /// Existence follows the latest opener (journals.md §2.1) before the rows drain, between their drain and their
    /// ledger's drop, when the archive holds them and the map still lists their ledger, and after the drop: an empty
    /// <c>Replace</c> leaves the journal existing and empty; a <c>Delete</c> and then an <c>Append</c> leaves it existing;
    /// and an <c>Expired</c> after a <c>Delete</c>, of the key warm from it, leaves it absent. Every row is in the replaced
    /// ledger, so a stale write at each stage, which no row in the open ledger exempts, reloads its key and is rejected with
    /// the version the read gives.
    /// </summary>
    [Fact]
    public async Task ExistenceFollowsTheLatestOpenerBeforeAndAfterTheRowsDrain()
    {
        await using var cluster = await JournalCluster.StartAsync(new JournalStoreOptions { TombstoneRetention = TimeSpan.Zero });
        _ = Accepted(await cluster.ReplaceAsync("emptied", Origin, string.Empty));
        var deleted = Accepted(await cluster.DeleteAsync("revived", Origin));
        _ = Accepted(await cluster.AppendAsync("revived", deleted, "back"));
        var gone = Accepted(await cluster.DeleteAsync("expired", Origin));
        _ = Accepted(await cluster.Journals.ExpireAsync(JournalRowKey.Encode("expired"), gone, Token));
        var closed = await cluster.ReplaceAsync("emptied");

        await AssertExistenceAsync(cluster);
        await cluster.DrainAsync(closed);
        await AssertExistenceAsync(cluster);
        await cluster.DropAsync(closed);
        await AssertExistenceAsync(cluster);

        static async Task AssertExistenceAsync(JournalCluster cluster)
        {
            Assert.Equal((true, string.Empty), await ReadAndReloadAsync(cluster, "emptied"));
            Assert.Equal((true, "back"), await ReadAndReloadAsync(cluster, "revived"));
            Assert.Equal((false, string.Empty), await ReadAndReloadAsync(cluster, "expired"));
        }
    }

    /// <summary>
    /// An <c>Expired</c> whose <c>Delete</c> has drained and dropped applies to the rebased state, by the prefix's
    /// tombstone and the rebased version, and leaves the journal absent (journals.md §8.6), once a stale write has loaded
    /// the key's rebased state into the successor's warm state, since an <c>Expired</c> never loads its key.
    /// </summary>
    [Fact]
    public async Task AnExpiredAfterItsDeleteDroppedAppliesToTheRebasedState()
    {
        await using var cluster = await JournalCluster.StartAsync(new JournalStoreOptions { TombstoneRetention = TimeSpan.Zero });
        var one = Accepted(await cluster.AppendAsync("a", Origin, "x"));
        _ = Accepted(await cluster.DeleteAsync("a", one));
        var closed = await cluster.ReplaceAsync("a");
        await cluster.DrainAsync(closed);
        await cluster.DropAsync(closed);
        var (rebased, exists, _) = await cluster.ReadAsync("a");
        Assert.False(exists);
        Assert.Equal(new JournalWriteOutcome(new JournalWriteOutcome.Rejected(rebased)), await cluster.Journals.ExpireAsync(JournalRowKey.Encode("a"), rebased, Token));
        Assert.Equal(new JournalWriteOutcome(new JournalWriteOutcome.Rejected(rebased)), await cluster.AppendAsync("a", Origin, "z"));

        var expired = Accepted(await cluster.Journals.ExpireAsync(JournalRowKey.Encode("a"), rebased, Token));

        Assert.Equal((expired, false, string.Empty), await cluster.ReadAsync("a"));
    }

    /// <summary>
    /// journals.md §8.6, JS-I7: the expiry scan proposes the <c>Expired</c> of a warm journal deleted with its tombstone's
    /// retention passed, at the deleted state's version, so the journal reads absent at the version after it; a tick
    /// before the retention passes it proposes nothing, and nothing once the journal is absent. A live journal is never a
    /// candidate.
    /// </summary>
    [Fact]
    public async Task TheExpiryScanExpiresAWarmJournalOnceItsTombstonesRetentionHasPassed()
    {
        var clock = new FakeTimeProvider();
        var retention = TimeSpan.FromDays(2);
        await using var cluster = await JournalCluster.StartAsync(new JournalStoreOptions { TombstoneRetention = retention }, clock: clock);
        var deleted = Accepted(await cluster.DeleteAsync("a", Accepted(await cluster.AppendAsync("a", Origin, "x"))));
        var live = Accepted(await cluster.AppendAsync("live", Origin, "y"));

        clock.Advance(retention - TimeSpan.FromTicks(1));
        Assert.Equal(default, await cluster.Journals.ExpireTombstonesAsync(Token));
        Assert.Equal((deleted, false, string.Empty), await cluster.ReadAsync("a"));

        clock.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(new JournalExpiryScanResult(Expired: 1, 0, 0, 0, 0, 0), await cluster.Journals.ExpireTombstonesAsync(Token));
        Assert.Equal((deleted.Next(), false, string.Empty), await cluster.ReadAsync("a"));
        Assert.Equal((live, true, "y"), await cluster.ReadAsync("live"));
        Assert.Equal(default, await cluster.Journals.ExpireTombstonesAsync(Token));
    }

    /// <summary>
    /// journals.md §8.6: the expiry scan proposes nothing for a journal written since its <c>Delete</c>, nor for one deleted
    /// again until the later tombstone's retention has passed, whatever the earlier one's.
    /// </summary>
    [Fact]
    public async Task TheExpiryScanLeavesAJournalWrittenOrDeletedAgainSinceItsDelete()
    {
        var clock = new FakeTimeProvider();
        var retention = TimeSpan.FromDays(2);
        await using var cluster = await JournalCluster.StartAsync(new JournalStoreOptions { TombstoneRetention = retention }, clock: clock);
        var rewritten = Accepted(await cluster.AppendAsync("rewritten", Accepted(await cluster.DeleteAsync("rewritten", Origin)), "back"));
        var first = Accepted(await cluster.DeleteAsync("redeleted", Origin));
        clock.Advance(retention / 2);
        var again = Accepted(await cluster.DeleteAsync("redeleted", Accepted(await cluster.AppendAsync("redeleted", first, "back"))));

        clock.Advance(retention / 2);
        Assert.Equal(default, await cluster.Journals.ExpireTombstonesAsync(Token));

        clock.Advance(retention / 2);
        Assert.Equal(new JournalExpiryScanResult(Expired: 1, 0, 0, 0, 0, 0), await cluster.Journals.ExpireTombstonesAsync(Token));
        Assert.Equal((rewritten, true, "back"), await cluster.ReadAsync("rewritten"));
        Assert.Equal((again.Next(), false, string.Empty), await cluster.ReadAsync("redeleted"));
    }

    /// <summary>
    /// journals.md §8.6, JS-I7: the expiry scan proposes nothing for a deleted key whose retention has passed while an entry
    /// of it is in flight, here an <c>Append</c> its writer decided and admission has not yet applied, which then lands.
    /// </summary>
    [Fact]
    public async Task TheExpiryScanLeavesAKeyWithAnEntryInFlight()
    {
        var clock = new FakeTimeProvider();
        var retention = TimeSpan.FromDays(2);
        await using var cluster = await JournalCluster.StartAsync(new JournalStoreOptions { TombstoneRetention = retention }, clock: clock);
        var deleted = Accepted(await cluster.DeleteAsync("a", Origin));
        clock.Advance(retention);
        var decided = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var apply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cluster.Journals.BeforeApply = () =>
        {
            _ = decided.TrySetResult();
            return apply.Task;
        };

        var append = cluster.AppendAsync("a", deleted, "back");
        try
        {
            await decided.Task.WaitAsync(Token);
            Assert.Equal(default, await cluster.Journals.ExpireTombstonesAsync(Token));
        }
        finally
        {
            apply.SetResult();
        }

        Assert.Equal((Accepted(await append), true, "back"), await cluster.ReadAsync("a"));
    }

    /// <summary>
    /// journals.md §8.6: the expiry scan's candidates are hints, proposed in rounds of at most <c>MaxBatchEntries</c>, each
    /// ended before the next. With rounds of one, a write to each of two candidates while the first round's <c>Expired</c>
    /// is in flight waits behind it on its key, and is then rejected, and is admitted on the other key, whose
    /// <c>Expired</c>, proposed only once the first round has ended, is rejected against its state (JS-I1).
    /// </summary>
    [Fact]
    public async Task AnExpiredOfAJournalWrittenSinceTheScanFoundItIsRejected()
    {
        await using var cluster = await JournalCluster.StartAsync(new JournalStoreOptions { MaxBatchEntries = 1, TombstoneRetention = TimeSpan.Zero });
        var a = Accepted(await cluster.DeleteAsync("a", Origin));
        var b = Accepted(await cluster.DeleteAsync("b", Origin));
        var decided = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var apply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cluster.Journals.BeforeApply = () =>
        {
            _ = decided.TrySetResult();
            return apply.Task;
        };

        var scan = cluster.Journals.ExpireTombstonesAsync(Token);
        Task<JournalWriteOutcome>[] writes;
        try
        {
            await decided.Task.WaitAsync(Token);
            writes = [cluster.AppendAsync("a", a, "back"), cluster.AppendAsync("b", b, "back")];
        }
        finally
        {
            apply.SetResult();
        }

        Assert.Equal(new JournalExpiryScanResult(Expired: 1, Rejected: 1, 0, 0, 0, 0), await scan);
        var outcomes = await Task.WhenAll(writes);
        _ = Assert.Single(outcomes, static outcome => outcome.Value is JournalWriteOutcome.Accepted);
        _ = Assert.Single(outcomes, static outcome => outcome.Value is JournalWriteOutcome.Rejected);
    }

    /// <summary>
    /// journals.md §8.6: an <c>Expired</c> never loads its key. With warm state's room for two keys and rounds of one, a
    /// third key loaded while the first round's <c>Expired</c> is in flight evicts the other candidate, whose
    /// <c>Expired</c> is then rejected with its own expected version, reading no history, and the journal stays deleted.
    /// </summary>
    [Fact]
    public async Task ACandidateEvictedBeforeItsRoundIsRejectedAndReadsNoHistory()
    {
        var budget = 2 * LedgerAdmission.LoadedKeyBytes(JournalRowKey.Encode("a").Length);
        await using var cluster = await JournalCluster.StartAsync(new JournalStoreOptions { MaxBatchEntries = 1, TombstoneRetention = TimeSpan.Zero, WarmStateBytes = budget });
        Dictionary<string, JournalVersion> deleted = new(StringComparer.Ordinal)
        {
            ["a"] = Accepted(await cluster.DeleteAsync("a", Origin)),
            ["b"] = Accepted(await cluster.DeleteAsync("b", Origin)),
        };
        var decided = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var apply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cluster.Journals.BeforeApply = () =>
        {
            _ = decided.TrySetResult();
            return apply.Task;
        };

        var scan = cluster.Journals.ExpireTombstonesAsync(Token);
        int reads;
        try
        {
            await decided.Task.WaitAsync(Token);
            Assert.Equal(new JournalWriteOutcome(new JournalWriteOutcome.Rejected(Origin)), await cluster.AppendAsync("c", deleted["a"], "x"));
            reads = cluster.Store.Reads;
        }
        finally
        {
            apply.SetResult();
        }

        Assert.Equal(new JournalExpiryScanResult(Expired: 1, Rejected: 1, 0, 0, 0, 0), await scan);
        Assert.Equal(reads, cluster.Store.Reads);
        var unchanged = 0;
        foreach (var (key, version) in deleted)
        {
            unchanged += (await cluster.ReadAsync(key)).Version == version ? 1 : 0;
        }

        Assert.Equal(1, unchanged);
    }

    /// <summary>
    /// journals.md §8.6: the expiry scan takes its candidates from every admission the store holds: after a split, a deleted
    /// journal warm in each child's admission is expired.
    /// </summary>
    [Fact]
    public async Task TheExpiryScanTakesCandidatesFromEveryAdmission()
    {
        await using var cluster = await JournalCluster.StartAsync(new JournalStoreOptions { TombstoneRetention = TimeSpan.Zero });
        _ = await cluster.SplitAsync("a");
        await cluster.ReconciledAsync();
        var keys = Enumerable.Range(0, 1000).Select(static index => string.Create(CultureInfo.InvariantCulture, $"k{index}"))
            .GroupBy(cluster.OpenLedger)
            .Select(static ledger => ledger.First())
            .ToArray();
        Assert.Equal(2, keys.Length);
        foreach (var key in keys)
        {
            _ = Accepted(await cluster.DeleteAsync(key, Origin));
        }

        Assert.Equal(2, cluster.Journals.ServedLedgers.Count);
        Assert.Equal(new JournalExpiryScanResult(Expired: 2, 0, 0, 0, 0, 0), await cluster.Journals.ExpireTombstonesAsync(Token));
    }

    /// <summary>
    /// journals.md §8.6: a cancellation ends the scan once its round ends, and proposes no later round: with rounds of one,
    /// cancelled while the first round's <c>Expired</c> is in flight, or once it is answered and before the next round
    /// starts, the other candidate stays deleted.
    /// </summary>
    /// <param name="duringTheRound">Whether the cancellation comes while the first round's <c>Expired</c> is in flight.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ACancelledScanProposesNoLaterRound(bool duringTheRound)
    {
        await using var cluster = await JournalCluster.StartAsync(new JournalStoreOptions { MaxBatchEntries = 1, TombstoneRetention = TimeSpan.Zero });
        Dictionary<string, JournalVersion> deleted = new(StringComparer.Ordinal)
        {
            ["a"] = Accepted(await cluster.DeleteAsync("a", Origin)),
            ["b"] = Accepted(await cluster.DeleteAsync("b", Origin)),
        };
        var decided = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var apply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cluster.Journals.BeforeApply = () =>
        {
            _ = decided.TrySetResult();
            return apply.Task;
        };

        using var cancellation = new CancellationTokenSource();
        if (!duringTheRound)
        {
            cluster.Journals.BeforeApply = null;
            cluster.Journals.OnExpiryProposed = (_, _) => _ => cancellation.Cancel();
        }

        var scan = cluster.Journals.ExpireTombstonesAsync(cancellation.Token);
        try
        {
            if (duringTheRound)
            {
                await decided.Task.WaitAsync(Token);
                await cancellation.CancelAsync();
            }

            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scan.WaitAsync(Patience, Token));
        }
        finally
        {
            apply.SetResult();
        }

        var unchanged = 0;
        foreach (var (key, version) in deleted)
        {
            var (read, exists, _) = await cluster.ReadAsync(key);
            Assert.False(exists);
            unchanged += read == version ? 1 : 0;
        }

        Assert.Equal(1, unchanged);
    }

    /// <summary>
    /// journals.md §8.6, §2.2: a scan racing the store's disposal ends with it rather than hanging: with rounds of one, the
    /// first round's <c>Expired</c> held as the store is disposed is answered as its ledger decided it, and the next
    /// round meets the disposed store, which ends the scan with <see cref="ObjectDisposedException"/>.
    /// </summary>
    [Fact]
    public async Task AScanRacingTheStoresDisposalEndsWithIt()
    {
        await using var cluster = await JournalCluster.StartAsync(new JournalStoreOptions { MaxBatchEntries = 1, TombstoneRetention = TimeSpan.Zero });
        _ = Accepted(await cluster.DeleteAsync("a", Origin));
        _ = Accepted(await cluster.DeleteAsync("b", Origin));
        var decided = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var apply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cluster.Journals.BeforeApply = () =>
        {
            _ = decided.TrySetResult();
            return apply.Task;
        };

        var scan = cluster.Journals.ExpireTombstonesAsync(Token);
        Task disposal;
        try
        {
            await decided.Task.WaitAsync(Token);
            disposal = cluster.Journals.DisposeAsync().AsTask();
        }
        finally
        {
            apply.SetResult();
        }

        _ = await Assert.ThrowsAsync<ObjectDisposedException>(() => scan.WaitAsync(Patience, Token));
        await disposal.WaitAsync(Patience, Token);
        _ = await Assert.ThrowsAsync<ObjectDisposedException>(() => cluster.Journals.ExpireTombstonesAsync(Token));
    }

    /// <summary>
    /// journals.md §8.6: a proposal which fails counts as <c>Failed</c> and ends nothing but itself: with rounds of one, a
    /// candidate whose proposal faults, here one with no key, is counted, and the next round's candidate is expired.
    /// </summary>
    [Fact]
    public async Task AFailedProposalIsCountedAndTheScanGoesOn()
    {
        await using var cluster = await JournalCluster.StartAsync(new JournalStoreOptions { TombstoneRetention = TimeSpan.Zero });
        var deleted = Accepted(await cluster.DeleteAsync("a", Origin));

        var result = await JournalExpiryScan.RunAsync(cluster.Journals, [(null!, deleted), (JournalRowKey.Encode("a"), deleted)], round: 1, Token);

        Assert.Equal(new JournalExpiryScanResult(Expired: 1, 0, 0, 0, 0, Failed: 1), result);
        Assert.Equal(2, result.Proposed);
    }

    /// <summary>
    /// A read yields the archive prefix's content and then the live rows', each chunk the caller's; disposing the result
    /// partway releases the chunks not yet yielded, while a chunk yielded stays the caller's to read and dispose, and the
    /// disposed result yields nothing more.
    /// </summary>
    [Fact]
    public async Task AReadDisposedPartwayReleasesWhatItHasNotYielded()
    {
        await using var cluster = await JournalCluster.StartAsync();
        var one = Accepted(await cluster.AppendAsync("a", Origin, "one"));
        var closed = await cluster.ReplaceAsync("a");
        await cluster.DrainAsync(closed);
        await cluster.DropAsync(closed);
        var rebased = Assert.IsType<JournalWriteOutcome.Rejected>((await cluster.AppendAsync("a", one, "two")).Value).CurrentVersion;
        _ = Accepted(await cluster.AppendAsync("a", rebased, "two"));

        var result = Assert.IsType<JournalReadResult>((await cluster.Journals.ReadAsync(JournalRowKey.Encode("a"), Token)).Value);
        Assert.Equal(6, result.ContentLength);
        var chunks = result.ReadPayloadsAsync(Token).GetAsyncEnumerator(Token);
        ArcBuffer first;
        try
        {
            Assert.True(await chunks.MoveNextAsync());
            first = chunks.Current;
            result.Dispose();
            _ = await Assert.ThrowsAsync<ObjectDisposedException>(async () => await chunks.MoveNextAsync());
        }
        finally
        {
            await chunks.DisposeAsync();
            result.Dispose();
        }

        using (first)
        {
            Assert.Equal("one", Encoding.UTF8.GetString(first.ToArray()));
        }
    }

    /// <summary>
    /// A load which waits at the archive while its ledger splits, and whose admission the store's follower retires once the
    /// registry has reconciled the split, answers its waiting write <c>Fenced</c> before the load returns: the admission
    /// proposed nothing (JS-I4), and the same record sent again is accepted on the child.
    /// </summary>
    [Fact]
    public async Task AWriteWhoseAdmissionRetiresMidLoadIsFenced()
    {
        await using var cluster = await JournalCluster.StartAsync();
        var parent = cluster.OpenLedger("k");
        using var x = ArcBuffer.Wrap("x"u8.ToArray());
        using var record = cluster.Journals.EncodeAppend(JournalRowKey.Encode("k"), Origin, x);
        cluster.Store.HoldNextRead();
        var write = cluster.Journals.WriteAsync(record, Token);
        await cluster.Store.ReadHeld.WaitAsync(Token);

        _ = await cluster.SplitAsync("k");

        Assert.Equal(new JournalWriteOutcome(JournalWriteOutcome.Fenced.Instance), await write);
        await cluster.Store.HeldReadEnded.WaitAsync(Token);
        Assert.DoesNotContain(parent, cluster.Journals.ServedLedgers);
        var one = Accepted(await cluster.Journals.WriteAsync(record, Token));
        Assert.Equal((one, true, "x"), await cluster.ReadAsync("k"));
    }

    /// <summary>
    /// The store's follower takes its baseline, the map view's epoch and the unserved ledgers, as the store is constructed,
    /// before any admission exists: a follower which starts only once a write's load waits at the archive and its ledger
    /// has split and the registry reconciled the split still retires the parent's admission, so the write is answered
    /// <c>Fenced</c> and the held load ends. A baseline taken as the follower starts would be the split's epoch, which it
    /// never sweeps, and the write would wait forever.
    /// </summary>
    [Fact]
    public async Task AFollowerWhichStartsAfterASplitStillRetiresTheParentsAdmission()
    {
        var following = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var cluster = await JournalCluster.StartAsync(beforeFollowing: () => following.Task);
        var parent = cluster.OpenLedger("k");
        using var x = ArcBuffer.Wrap("x"u8.ToArray());
        using var record = cluster.Journals.EncodeAppend(JournalRowKey.Encode("k"), Origin, x);
        cluster.Store.HoldNextRead();
        var write = cluster.Journals.WriteAsync(record, Token);
        await cluster.Store.ReadHeld.WaitAsync(Token);
        _ = await cluster.SplitAsync("k");
        await cluster.ReconciledAsync();

        following.SetResult();

        Assert.Equal(new JournalWriteOutcome(JournalWriteOutcome.Fenced.Instance), await write.WaitAsync(TimeSpan.FromSeconds(30), TimeProvider.System, Token));
        await cluster.Store.HeldReadEnded.WaitAsync(Token);
        Assert.DoesNotContain(parent, cluster.Journals.ServedLedgers);
    }

    /// <summary>
    /// A load which waits at the archive while its ledger splits, with the store's sweep held, reads the map listing the
    /// child over the key: the read is not its admission's (JS-I3), so the load answers <c>Moved</c> naming this node, the
    /// child's primary, and nothing is proposed; the store routes the write again once the registry has reconciled, and
    /// the child's admission accepts it. The parent's admission retires once the sweep runs.
    /// </summary>
    [Fact]
    public async Task ALoadTheMapMovedPastMidReadIsMovedAndRoutedAgainToTheChild()
    {
        await using var cluster = await JournalCluster.StartAsync();
        var parent = cluster.OpenLedger("k");
        var sweep = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cluster.Journals.BeforeSweep = () => sweep.Task;
        cluster.Store.HoldNextRead();
        var write = cluster.AppendAsync("k", Origin, "x");
        await cluster.Store.ReadHeld.WaitAsync(Token);
        Assert.Equal(1, cluster.Journals.AdmissionsCreated);

        _ = await cluster.SplitAsync("k");
        cluster.Store.Release();

        var one = Accepted(await write);
        Assert.Equal(2, cluster.Journals.AdmissionsCreated);
        Assert.Contains(parent, cluster.Journals.ServedLedgers);
        Assert.Equal((one, true, "x"), await cluster.ReadAsync("k"));

        sweep.SetResult();
        await UntilAsync(() => !cluster.Journals.ServedLedgers.Contains(parent), "the parent ledger's admission to retire");
    }

    /// <summary>
    /// A read which waits at the archive while its ledger splits finds its seal check sealed and reads again through the
    /// map listing the child: the read is not its admission's, and the store routes it again, to the child, which answers
    /// the key's content.
    /// </summary>
    [Fact]
    public async Task AReadTheMapMovedPastMidReadIsRoutedAgainToTheChild()
    {
        await using var cluster = await JournalCluster.StartAsync();
        var one = Accepted(await cluster.AppendAsync("k", Origin, "x"));
        cluster.Store.HoldNextRead();
        var read = cluster.ReadAsync("k");
        await cluster.Store.ReadHeld.WaitAsync(Token);

        _ = await cluster.SplitAsync("k");
        cluster.Store.Release();

        Assert.Equal((one, true, "x"), await read);
    }

    /// <summary>
    /// A write held at its load when its ledger is replaced is <c>Fenced</c>: the replace retires its admission, which
    /// proposed nothing; the same record sent again is accepted on the successor, which continues the key's version.
    /// </summary>
    [Fact]
    public async Task AWriteHeldWhileItsLedgerIsReplacedIsFenced()
    {
        await using var cluster = await JournalCluster.StartAsync(new JournalStoreOptions { WarmStateBytes = 0 });
        var one = Accepted(await cluster.AppendAsync("k", Origin, "x"));
        using var y = ArcBuffer.Wrap("y"u8.ToArray());
        using var record = cluster.Journals.EncodeAppend(JournalRowKey.Encode("k"), one, y);
        cluster.Store.HoldNextRead();
        var write = cluster.Journals.WriteAsync(record, Token);
        await cluster.Store.ReadHeld.WaitAsync(Token);

        _ = await cluster.ReplaceAsync("k");

        Assert.Equal(new JournalWriteOutcome(JournalWriteOutcome.Fenced.Instance), await write);
        var two = Accepted(await cluster.Journals.WriteAsync(record, Token));
        Assert.Equal((two, true, "xy"), await cluster.ReadAsync("k"));
    }

    /// <summary>
    /// A writer whose lifetime stops under an open ledger the map still lists, here mid-read, with the recovery the stop
    /// starts held before its seal, leaves the key unserved here: the read, of the admission's ledger and linearizable, is
    /// not its admission's once the registry no longer serves its writer (JS-I3), the store routes it once more after the
    /// registry reconciles, finds no writer, and answers <c>Moved</c> naming no owner, so the caller reads the map again;
    /// a write is answered alike, and the stale admission retires.
    /// </summary>
    [Fact]
    public async Task AWriterWhoseLifetimeStopsMidReadIsMovedNamingNoOwner()
    {
        await using var cluster = await JournalCluster.StartAsync();
        _ = Accepted(await cluster.AppendAsync("k", Origin, "x"));
        var ledger = cluster.OpenLedger("k");
        var seal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cluster.Ledgers.Transport.BeforeSeal = _ => seal.Task;
        try
        {
            cluster.Store.HoldNextRead();
            var read = cluster.Journals.ReadAsync(JournalRowKey.Encode("k"), Token);
            await cluster.Store.ReadHeld.WaitAsync(Token);

            cluster.Ledgers.Registry.TryGet(ledger)!.Abandon();
            cluster.Store.Release();

            var moved = new JournalMoved(Owner: null);
            Assert.Equal(moved, (await read).Value);
            Assert.Equal(new JournalWriteOutcome(moved), await cluster.AppendAsync("k", Origin, "y"));
            Assert.DoesNotContain(ledger, cluster.Journals.ServedLedgers);
        }
        finally
        {
            seal.SetResult();
        }
    }

    /// <summary>
    /// The store's follower retires an admission whose writer the registry stops serving while the map still lists its
    /// ledger open, here a writer whose lifetime stops with its recovery's seal held, with no later call to the store:
    /// the registry's unserved writer wakes the follower, whose sweep retires the admission.
    /// </summary>
    [Fact]
    public async Task TheFollowerRetiresAnAdmissionWhoseWriterStopsWithNoLaterCall()
    {
        await using var cluster = await JournalCluster.StartAsync();
        _ = Accepted(await cluster.AppendAsync("k", Origin, "x"));
        var ledger = cluster.OpenLedger("k");
        Assert.Contains(ledger, cluster.Journals.ServedLedgers);
        var seal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cluster.Ledgers.Transport.BeforeSeal = _ => seal.Task;
        try
        {
            var epoch = cluster.Ledgers.Map.Current.Epoch;
            cluster.Ledgers.Registry.TryGet(ledger)!.Abandon();

            await UntilAsync(() => !cluster.Journals.ServedLedgers.Contains(ledger), "the ledger's admission to retire");
            Assert.Equal(epoch, cluster.Ledgers.Map.Current.Epoch);
            Assert.Equal(ledger, cluster.OpenLedger("k"));
        }
        finally
        {
            seal.SetResult();
        }
    }

    /// <summary>
    /// Once the key's ledger is handed to another node, whose claim the map lists over the successor, a write and a read
    /// are answered <c>Moved</c> naming that node.
    /// </summary>
    [Fact]
    public async Task AKeyHandedToAnotherNodeIsMovedNamingIt()
    {
        await using var cluster = await JournalCluster.StartAsync();
        _ = Accepted(await cluster.AppendAsync("k", Origin, "x"));

        _ = await cluster.Ledgers.HandOffAsync(cluster.OpenLedger("k"), OtherNode, Token);

        var moved = new JournalMoved(OtherNode);
        Assert.Equal(new JournalWriteOutcome(moved), await cluster.AppendAsync("k", Origin, "y"));
        Assert.Equal(moved, (await cluster.Journals.ReadAsync(JournalRowKey.Encode("k"), Token)).Value);
    }

    /// <summary>
    /// A read which sees a batch its writer has decided, which admission has not yet applied, waits for admission to apply
    /// it before it answers (journals.md §8.2), so a write at the version the read gives is not rejected for the batch in
    /// flight: here the <c>Expired</c> of the <c>Delete</c> the read saw is accepted.
    /// </summary>
    [Fact]
    public async Task AReadWaitsForItsAdmissionsBatchesBelowItsTailSoAWriteAtItsVersionIsAccepted()
    {
        await using var cluster = await JournalCluster.StartAsync(new JournalStoreOptions { TombstoneRetention = TimeSpan.Zero });
        var one = Accepted(await cluster.AppendAsync("a", Origin, "x"));
        var decided = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var apply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cluster.Journals.BeforeApply = () =>
        {
            _ = decided.TrySetResult();
            return apply.Task;
        };

        var delete = cluster.DeleteAsync("a", one);
        Task<(JournalVersion Version, bool Exists, string Content)> read;
        try
        {
            await decided.Task.WaitAsync(Token);
            read = cluster.ReadAsync("a");
            await UntilAsync(() => cluster.Journals.ApplyWaiters == 1 || read.IsCompleted, "the read to wait for its key's apply");
            Assert.False(read.IsCompleted);
        }
        finally
        {
            apply.SetResult();
        }

        var (version, exists, _) = await read;
        Assert.False(exists);
        _ = Accepted(await cluster.Journals.ExpireAsync(JournalRowKey.Encode("a"), version, Token));
        Assert.Equal(version, Accepted(await delete));
    }

    /// <summary>
    /// Routes racing the registry's adoption of a split's child, held at the child's first opening under the registry's
    /// lock, create one admission for the child's writer, through which both writes are accepted (LP-I1, JS-I1).
    /// </summary>
    [Fact]
    public async Task RoutesRacingTheRegistrysAdoptionCreateOneAdmissionPerWriter()
    {
        await using var cluster = await JournalCluster.StartAsync();
        var parent = cluster.OpenLedger("k");
        using var opening = new ManualResetEventSlim();
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cluster.Ledgers.Transport.BeforeOpen = ledger =>
        {
            if (ledger != parent)
            {
                _ = opened.TrySetResult();
                opening.Wait(Token);
            }
        };

        var split = Task.Run(() => cluster.SplitAsync("k"), Token);
        await opened.Task.WaitAsync(Token);
        var child = cluster.OpenLedger("k");
        Assert.NotEqual(parent, child);
        var other = Enumerable.Range(0, 64).Select(static index => $"k{index}").First(key => cluster.OpenLedger(key) == child);
        var first = Task.Run(() => cluster.AppendAsync("k", Origin, "x"), Token);
        var second = Task.Run(() => cluster.AppendAsync(other, Origin, "y"), Token);
        opening.Set();
        _ = await split;

        _ = Accepted(await first);
        _ = Accepted(await second);
        Assert.Equal(1, cluster.Journals.AdmissionsCreated);
        Assert.Equal(child, Assert.Single(cluster.Journals.ServedLedgers));
    }

    /// <summary>
    /// Disposing the store with a write whose decided batch admission has yet to apply waits for that admission: the
    /// disposal stays pending while the apply is held and completes once it is released, the write accepted, and every
    /// later write or read, of any key, throws <see cref="ObjectDisposedException"/>.
    /// </summary>
    [Fact]
    public async Task DisposingTheStoreWaitsForItsWritesAndThenRefusesEveryCall()
    {
        await using var cluster = await JournalCluster.StartAsync();
        var decided = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var apply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cluster.Journals.BeforeApply = () =>
        {
            _ = decided.TrySetResult();
            return apply.Task;
        };

        var write = cluster.AppendAsync("k", Origin, "x");
        Task disposing;
        try
        {
            await decided.Task.WaitAsync(Token);
            disposing = cluster.Journals.DisposeAsync().AsTask();
            var pause = Task.Delay(TimeSpan.FromMilliseconds(100), TimeProvider.System, Token);
            Assert.Same(pause, await Task.WhenAny(disposing, pause));
        }
        finally
        {
            apply.SetResult();
        }

        await disposing;
        _ = Accepted(await write);
        using var x = ArcBuffer.Wrap("x"u8.ToArray());
        using var record = JournalEntryRecord.Append(JournalRowKey.Encode("never"), Origin, x);
        _ = await Assert.ThrowsAsync<ObjectDisposedException>(() => cluster.Journals.WriteAsync(record, Token));
        _ = await Assert.ThrowsAsync<ObjectDisposedException>(() => cluster.Journals.ReadAsync(JournalRowKey.Encode("k"), Token));
        _ = await Assert.ThrowsAsync<ObjectDisposedException>(() => cluster.Journals.ReadAsync(JournalRowKey.Encode("never"), Token));
    }

    /// <summary>
    /// journals.md §2.2: the store throws <see cref="ObjectDisposedException"/> only for a call which had no effect, so a
    /// caller may report it as having none. A write accepted before the disposal is in its key's history; a write whose
    /// proposal is in flight, which the writer's disposal ends while the store's waits for it, as a stop whose bounded wait
    /// passes does (orleans.md §5.2 step 4), is answered <c>Ambiguous</c>, not with the exception; a write whose load the
    /// disposal meets is answered as the fenced admission answers it, not with the exception, and leaves no row; and a
    /// write which reaches the disposed store throws it and leaves no row.
    /// </summary>
    [Fact]
    public async Task TheStoreThrowsObjectDisposedExceptionOnlyForACallWithoutEffect()
    {
        await using var cluster = await JournalCluster.StartAsync();
        _ = Accepted(await cluster.AppendAsync("accepted", Origin, "x"));
        var (stored, acknowledge) = HoldWriteAnswers(cluster);
        var inFlight = cluster.AppendAsync("in-flight", Origin, "x");
        await stored.WaitAsync(Token);
        cluster.Store.HoldNextRead();
        var loading = cluster.AppendAsync("loading", Origin, "x");
        await cluster.Store.ReadHeld.WaitAsync(Token);

        var disposing = cluster.Journals.DisposeAsync().AsTask();
        try
        {
            await cluster.Ledgers.Registry.DisposeAsync();
            Assert.Equal(new JournalWriteOutcome(JournalWriteOutcome.Ambiguous.Instance), await inFlight);
        }
        finally
        {
            acknowledge.SetResult();
            cluster.Store.Release();
        }

        await disposing;

        var held = await Record.ExceptionAsync(() => loading);
        Assert.IsNotType<ObjectDisposedException>(held);
        _ = await Assert.ThrowsAsync<ObjectDisposedException>(() => cluster.AppendAsync("late", Origin, "x"));
        var history = new ArchiveReader(cluster.Ledgers.Registry.Reader, cluster.Store, TimeProvider.System);
        foreach (var (key, exists) in new[] { ("accepted", true), ("loading", false), ("late", false) })
        {
            using var read = await history.ReadAsync(JournalRowKey.Encode(key).Memory, Token);
            Assert.Equal(exists, JournalState.Fold(read) is { Exists: true });
        }
    }

    /// <summary>
    /// journals.md §8.8: at most <c>MaxConcurrentHistoryReads</c> history reads run at once, a write's load and an owner read
    /// alike: with every archive read held at the store and a bound of 2, a third waits for a slot without reaching the
    /// archive, and runs once a held read ends.
    /// </summary>
    [Fact]
    public async Task AtMostMaxConcurrentHistoryReadsRunAtOnce()
    {
        await using var cluster = await JournalCluster.StartAsync(new JournalStoreOptions { MaxConcurrentHistoryReads = 2 });
        cluster.Store.HoldEveryRead();
        var first = cluster.Journals.ReadAsync(JournalRowKey.Encode("first"), Token);
        var load = cluster.AppendAsync("load", Origin, "x");
        await UntilAsync(() => cluster.Store.HeldArrivals == 2, "two history reads to reach the archive");
        var third = cluster.Journals.ReadAsync(JournalRowKey.Encode("third"), Token);
        await UntilAsync(() => cluster.Journals.HistoryReads.Waiting == 1 || cluster.Store.HeldArrivals > 2, "the third read to wait for a slot or reach the archive");

        Assert.Equal(2, cluster.Journals.HistoryReads.OutstandingCalls);
        Assert.Equal(2, cluster.Store.HeldArrivals);
        Assert.False(third.IsCompleted);

        cluster.Store.ReleaseEveryRead();
        foreach (var read in new[] { first, third })
        {
            Assert.IsType<JournalReadResult>((await read.WaitAsync(Patience, Token)).Value).Dispose();
        }

        _ = Accepted(await load.WaitAsync(Patience, Token));
        Assert.Equal(0, cluster.Journals.HistoryReads.OutstandingCalls);
    }

    /// <summary>
    /// journals.md §8.8: a history read holds its slot only while it reads the archive and the replicas: with a bound of 1,
    /// a write whose load has read its history and whose proposal waits for its replicas holds no slot, and a read of
    /// another key runs meanwhile.
    /// </summary>
    [Fact]
    public async Task AHistoryReadHoldsItsSlotOnlyWhileItReads()
    {
        await using var cluster = await JournalCluster.StartAsync(new JournalStoreOptions { MaxConcurrentHistoryReads = 1 });
        var (stored, acknowledge) = HoldWriteAnswers(cluster);
        var write = cluster.AppendAsync("written", Origin, "x");
        await stored.WaitAsync(Patience, Token);
        try
        {
            Assert.Equal(0, cluster.Journals.HistoryReads.OutstandingCalls);
            var read = await cluster.Journals.ReadAsync(JournalRowKey.Encode("other"), Token).WaitAsync(Patience, Token);
            Assert.IsType<JournalReadResult>(read.Value).Dispose();
            Assert.False(write.IsCompleted);
        }
        finally
        {
            acknowledge.SetResult();
        }

        _ = Accepted(await write.WaitAsync(Patience, Token));
    }

    /// <summary>
    /// journals.md §8.8: a history read returns its slot however it ends: with a bound of 1, a read which finds corruption
    /// and a read cancelled at the archive each leave the slot free for the next read.
    /// </summary>
    [Fact]
    public async Task ACorruptOrCancelledHistoryReadReturnsItsSlot()
    {
        await using var cluster = await JournalCluster.StartAsync(new JournalStoreOptions { MaxConcurrentHistoryReads = 1 });
        cluster.Store.NextReadFailure = new ArchiveCorruptException("A test's corruption.");
        _ = await Assert.ThrowsAsync<JournalStoreCorruptException>(() => cluster.Journals.ReadAsync(JournalRowKey.Encode("corrupt"), Token));
        Assert.Equal(0, cluster.Journals.HistoryReads.OutstandingCalls);

        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        cluster.Store.HoldNextRead();
        var cancelled = cluster.Journals.ReadAsync(JournalRowKey.Encode("cancelled"), cancel.Token);
        await cluster.Store.ReadHeld.WaitAsync(Patience, Token);
        await cancel.CancelAsync();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.WaitAsync(Patience, Token));
        Assert.Equal(0, cluster.Journals.HistoryReads.OutstandingCalls);

        Assert.IsType<JournalReadResult>((await cluster.Journals.ReadAsync(JournalRowKey.Encode("next"), Token).WaitAsync(Patience, Token)).Value).Dispose();
    }

    /// <summary>
    /// journals.md §2.2, §8.8: the store's disposal withdraws a history read which waits for a slot, which throws
    /// <see cref="ObjectDisposedException"/>, the store's answer for a call without effect, without reaching the archive.
    /// </summary>
    [Fact]
    public async Task TheStoresDisposalWithdrawsAReadWaitingForAHistorySlot()
    {
        await using var cluster = await JournalCluster.StartAsync(new JournalStoreOptions { MaxConcurrentHistoryReads = 1 });
        cluster.Store.HoldNextRead();
        var holding = cluster.Journals.ReadAsync(JournalRowKey.Encode("holding"), Token);
        await cluster.Store.ReadHeld.WaitAsync(Patience, Token);
        var waiting = cluster.Journals.ReadAsync(JournalRowKey.Encode("waiting"), Token);
        await UntilAsync(() => cluster.Journals.HistoryReads.Waiting == 1, "the second read to wait for the slot");
        var reads = cluster.Store.Reads;

        var disposing = cluster.Journals.DisposeAsync().AsTask();
        _ = await Assert.ThrowsAsync<ObjectDisposedException>(() => waiting.WaitAsync(Patience, Token));
        Assert.Equal(reads, cluster.Store.Reads);
        cluster.Store.Release();
        _ = await Record.ExceptionAsync(() => holding.WaitAsync(Patience, Token));
        await disposing.WaitAsync(Patience, Token);
    }

    /// <summary>
    /// journals.md §8.8: a caller cancelling while waiting for a history-read slot withdraws cleanly without reaching the
    /// archive or disturbing the read holding the slot, and a pre-cancelled token cancels before taking a slot.
    /// </summary>
    [Fact]
    public async Task CallerCancellationWithdrawsAReadWaitingForAHistorySlot()
    {
        await using var cluster = await JournalCluster.StartAsync(new JournalStoreOptions { MaxConcurrentHistoryReads = 1 });

        using (var preCancelled = new CancellationTokenSource())
        {
            await preCancelled.CancelAsync();
            var readsBefore = cluster.Store.Reads;
            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cluster.Journals.ReadAsync(JournalRowKey.Encode("pre"), preCancelled.Token));
            Assert.Equal(readsBefore, cluster.Store.Reads);
            Assert.Equal(0, cluster.Journals.HistoryReads.OutstandingCalls);
        }

        cluster.Store.HoldNextRead();
        var holding = cluster.Journals.ReadAsync(JournalRowKey.Encode("holding"), Token);
        await cluster.Store.ReadHeld.WaitAsync(Patience, Token);

        using var waiterCts = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var waiting = cluster.Journals.ReadAsync(JournalRowKey.Encode("waiting"), waiterCts.Token);
        await UntilAsync(() => cluster.Journals.HistoryReads.Waiting == 1, "the second read to wait for the slot");
        var readsWhileHeld = cluster.Store.Reads;

        await waiterCts.CancelAsync();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(Patience, Token));
        Assert.Equal(readsWhileHeld, cluster.Store.Reads);
        Assert.Equal(1, cluster.Journals.HistoryReads.OutstandingCalls);

        cluster.Store.Release();
        Assert.IsType<JournalReadResult>((await holding.WaitAsync(Patience, Token)).Value).Dispose();
        Assert.Equal(0, cluster.Journals.HistoryReads.OutstandingCalls);
    }

    /// <summary>
    /// Disposing the writer registry before the store, with a write whose acknowledgement is held, ends the writer's
    /// proposal while the view lists its ledger open, which asserts nothing: the write is <c>Ambiguous</c>, the order the
    /// composition avoids by disposing the store first (journals.md §8.1).
    /// </summary>
    [Fact]
    public async Task DisposingTheRegistryWithAWriteInFlightLeavesItAmbiguous()
    {
        await using var cluster = await JournalCluster.StartAsync();
        var (stored, acknowledge) = HoldWriteAnswers(cluster);
        var write = cluster.AppendAsync("k", Origin, "x");
        await stored.WaitAsync(Token);

        try
        {
            await cluster.Ledgers.Registry.DisposeAsync();
            Assert.Equal(new JournalWriteOutcome(JournalWriteOutcome.Ambiguous.Instance), await write);
        }
        finally
        {
            acknowledge.SetResult();
        }
    }

    /// <summary>
    /// The archive's corruption is the journal's (journals.md §6): a read whose history read finds it throws
    /// <see cref="JournalStoreCorruptException"/>, and so does a write whose load finds it, which leaves the key unloaded,
    /// so its next write loads it again.
    /// </summary>
    [Fact]
    public async Task TheArchivesCorruptionIsTheJournals()
    {
        await using var cluster = await JournalCluster.StartAsync();
        cluster.Store.NextReadFailure = new ArchiveCorruptException("A test's corruption.");
        var read = await Assert.ThrowsAsync<JournalStoreCorruptException>(() => cluster.Journals.ReadAsync(JournalRowKey.Encode("k"), Token));
        _ = Assert.IsType<ArchiveCorruptException>(read.InnerException);

        cluster.Store.NextReadFailure = new ArchiveCorruptException("A test's corruption.");
        var write = await Assert.ThrowsAsync<JournalStoreCorruptException>(() => cluster.AppendAsync("k", Origin, "x"));
        _ = Assert.IsType<ArchiveCorruptException>(write.InnerException);

        _ = Accepted(await cluster.AppendAsync("k", Origin, "x"));
    }

    /// <summary>
    /// The largest batch admission builds (journals.md §8.4), pinned by hand: a header of 34 bytes for one row, or of 35
    /// for up to 16,383; and a defining row of the longest key's 65,535 bytes with 81 bytes of lengths, version, and
    /// reserved token when its body's length takes one byte, or 83 when it takes three, and the body, at least a
    /// tombstone's 8 bytes.
    /// </summary>
    [Fact]
    public void TheLargestBatchIsTheLongerOfOneLongestRowAndAFullBatch()
    {
        Assert.Equal(35 + (1L << 20), LedgerAdmission.LargestBatch(new JournalStoreOptions { MaxBodyBytes = 20 }));
        Assert.Equal(34 + 65_535 + 83 + 65_536, LedgerAdmission.LargestBatch(new JournalStoreOptions { MaxBodyBytes = 65_536, MaxBatchEntries = 1, MaxBatchBytes = 1 }));
        Assert.Equal(34 + 65_535 + 81 + 8, LedgerAdmission.LargestBatch(new JournalStoreOptions { MaxBodyBytes = 1, MaxBatchEntries = 1, MaxBatchBytes = 1 }));
    }

    /// <summary>
    /// The startup checks of journals.md §8.4, which constructing the store runs, each at its boundary: the largest batch
    /// within the replica's payload limit, <c>MaxBodyBytes</c> at most the store's <c>MaxAppendBytes</c>, and
    /// <c>MaxQueuedBytes</c> and the writers' <c>MaxProposalBytes</c> each at least the largest batch. Each refusal is a
    /// <see cref="JournalStoreLimitException"/> naming its bound. The registry holds <c>MaxProposalBytes</c> at least its
    /// payload limit, which the first check bounds the largest batch by, so only the check itself reaches that refusal.
    /// </summary>
    [Fact]
    public async Task TheStartupChecksRefuseEachLimitPastItsBoundary()
    {
        await using var ledgers = await LiveLedgers.StartAsync(new InMemoryConditionalRegister(), TimeProvider.System, Token);
        using var unbounded = new InMemoryArchiveStore();
        using var bounded = new InMemoryArchiveStore(maxAppendBytes: 64);
        var options = new JournalStoreOptions { MaxBodyBytes = 64, MaxBatchBytes = 1 << 20, MaxQueuedBytes = 2 << 20 };
        var largest = LedgerAdmission.LargestBatch(options);

        await new JournalStore(ledgers.Registry, bounded, options, TimeProvider.System).DisposeAsync();
        await new JournalStore(ledgers.Registry, unbounded, new JournalStoreOptions { MaxBodyBytes = 64, MaxBatchBytes = 1 << 20, MaxQueuedBytes = largest }, TimeProvider.System).DisposeAsync();
        AssertRefused(JournalStoreLimit.PayloadLimit, () => _ = new JournalStore(ledgers.Registry, unbounded, new JournalStoreOptions { MaxBatchBytes = ledgers.Registry.MaxBatchLength, MaxQueuedBytes = 1L << 30 }, TimeProvider.System));
        AssertRefused(JournalStoreLimit.MaxAppendBytes, () => _ = new JournalStore(ledgers.Registry, bounded, new JournalStoreOptions { MaxBodyBytes = 65, MaxBatchBytes = 1 << 20, MaxQueuedBytes = 2 << 20 }, TimeProvider.System));
        AssertRefused(JournalStoreLimit.MaxQueuedBytes, () => _ = new JournalStore(ledgers.Registry, unbounded, new JournalStoreOptions { MaxBodyBytes = 64, MaxBatchBytes = 1 << 20, MaxQueuedBytes = largest - 1 }, TimeProvider.System));
        JournalStore.CheckStartup(options, unbounded, (int)largest, largest);
        AssertRefused(JournalStoreLimit.PayloadLimit, () => JournalStore.CheckStartup(options, unbounded, (int)largest - 1, largest));
        AssertRefused(JournalStoreLimit.MaxProposalBytes, () => JournalStore.CheckStartup(options, unbounded, (int)largest, largest - 1));

        static void AssertRefused(JournalStoreLimit limit, Action start)
        {
            var refusal = Assert.Throws<JournalStoreLimitException>(start);
            Assert.Equal(limit, refusal.Limit);
            Assert.Equal("options", refusal.ParamName);
        }
    }

    /// <summary>
    /// The store's parameters for the archive store's record (archive.md §2.4) are the <c>MaxBodyBytes</c> it enforces,
    /// from its own copy of the options, under its pinned key and in invariant digits.
    /// </summary>
    [Fact]
    public async Task TheStoresParametersAreTheBodyLimitItEnforces()
    {
        var options = new JournalStoreOptions { MaxBodyBytes = 1_234_567 };
        await using var cluster = await JournalCluster.StartAsync(options);
        options.MaxBodyBytes = 1;

        Assert.Equal(
            new Dictionary<string, string>
            {
                ["max-body-bytes"] = "1234567",
                ["local-streams"] = string.Empty,
                ["max-local-appends"] = options.MaxLocalAppendsPerWrite.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["max-local-append-bytes"] = options.MaxLocalAppendBytesPerWrite.ToString(System.Globalization.CultureInfo.InvariantCulture),
            },
            cluster.Journals.StoreParameters);
        _ = Accepted(await cluster.AppendAsync("a", Origin, "more than one byte"));
    }

    /// <summary>
    /// archive.md §2.4: a composition records the store's parameters before it builds a store, so the parameters of options
    /// out of their range are refused, naming the setting, and never reach a store's record.
    /// </summary>
    [Fact]
    public void TheParametersOfABodyLimitOutOfItsRangeAreRefused() =>
        Assert.Equal(
            nameof(JournalStoreOptions.MaxBodyBytes),
            Assert.Throws<ArgumentOutOfRangeException>(() => JournalStore.StoreParametersOf(new JournalStoreOptions { MaxBodyBytes = 0 })).ParamName);

    /// <summary>
    /// journals.md §2.2, §2.3, §6: while the key's ledger's quorum is out of reach, a read and a write whose key loads fail
    /// with <see cref="JournalStoreUnavailableException"/>, carrying the ledger's aggregate of its replicas' failures, never
    /// the aggregate itself; the write was never proposed, so once the replicas answer again the journal reads as it was and
    /// a write at its version is accepted.
    /// </summary>
    [Fact]
    public async Task AQuorumOutOfReachFailsAReadAndAnUnproposedWriteAsUnavailable()
    {
        await using var cluster = await JournalCluster.StartAsync(new JournalStoreOptions { WarmStateBytes = 0 });
        var one = Accepted(await cluster.AppendAsync("a", Origin, "x"));

        cluster.Ledgers.Transport.Unreachable = true;
        var read = await Assert.ThrowsAsync<JournalStoreUnavailableException>(() => cluster.Journals.ReadAsync(JournalRowKey.Encode("a"), Token));
        var write = await Assert.ThrowsAsync<JournalStoreUnavailableException>(() => cluster.AppendAsync("a", one, "y"));
        cluster.Ledgers.Transport.Unreachable = false;

        foreach (var failure in new[] { read, write })
        {
            var quorum = Assert.IsType<AggregateException>(failure.InnerException);
            Assert.All(quorum.Flatten().InnerExceptions, static replica => Assert.IsType<TimeoutException>(replica));
        }

        Assert.Equal((one, true, "x"), await cluster.ReadAsync("a"));
        var two = Accepted(await cluster.AppendAsync("a", one, "y"));
        Assert.Equal((two, true, "xy"), await cluster.ReadAsync("a"));
    }

    /// <summary>
    /// OR-I24: cancelling <see cref="JournalStore.PrepareReleaseAsync"/> while <see cref="LedgerAdmission"/> is still
    /// draining an in-flight load clears the admission's draining state instead of leaving it permanently fenced for
    /// future writes while its writer stays open.
    /// </summary>
    [Fact]
    public async Task PrepareReleaseCancellationDuringAdmissionDrainRestoresAdmission()
    {
        await using var cluster = await JournalCluster.StartAsync();
        var ledger = cluster.OpenLedger("k");
        cluster.Store.HoldNextRead();
        var first = cluster.AppendAsync("k", Origin, "x");
        await cluster.Store.ReadHeld.WaitAsync(Patience, Token);

        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var prepare = cluster.Journals.PrepareReleaseAsync(ledger, KeyRange.Root, TimeSpan.FromSeconds(10), cancel.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(50), TimeProvider.System, Token);
        Assert.False(prepare.IsCompleted);
        await cancel.CancelAsync();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => prepare.WaitAsync(Patience, Token));

        cluster.Store.Release();
        var one = Accepted(await first.WaitAsync(Patience, Token));
        var two = Accepted(await cluster.AppendAsync("k", one, "y").WaitAsync(Patience, Token));
        Assert.Equal((two, true, "xy"), await cluster.ReadAsync("k"));
    }

    /// <summary>
    /// OR-I24: cancelling <see cref="JournalStore.PrepareReleaseAsync"/> after the writer has closed its proposal gate
    /// stops the writer cleanly as <see cref="SequencerStopReason.Sealing"/> and retires the admission rather than
    /// leaving a half-closed writer with an active admission that permanently answers <c>Fenced</c>.
    /// </summary>
    [Fact]
    public async Task PrepareReleaseCancellationDuringWriterDrainStopsWriterAndRetiresAdmission()
    {
        await using var cluster = await JournalCluster.StartAsync();
        var one = Accepted(await cluster.AppendAsync("k", Origin, "x"));
        var ledger = cluster.OpenLedger("k");
        var writer = cluster.Ledgers.Registry.TryGet(ledger)!;
        var (stored, acknowledge) = HoldWriteAnswers(cluster);

        Task<ProposalOutcome> rawProposal;
        try
        {
            rawProposal = Task.Run(
                async () =>
                {
                    var key = JournalRowKey.Encode("other");
                    using var batch = await writer.CreateBatchAsync(1, (int)(LedgerBatch.MaxHeaderLength(1) + LedgerBatch.MaxRowLength(key.Length, 0, 0, 1)), Token);
                    _ = batch.AddRow(key.Span, RowKind.Append, default, default, new ReadOnlySequence<byte>([1]));
                    return await writer.ProposeAsync(batch, Token);
                },
                Token);
            await stored.WaitAsync(Patience, Token);

            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
            var prepare = cluster.Journals.PrepareReleaseAsync(ledger, KeyRange.Root, TimeSpan.FromSeconds(10), cancel.Token);
            await Task.Delay(TimeSpan.FromMilliseconds(50), TimeProvider.System, Token);
            Assert.False(prepare.IsCompleted);
            await cancel.CancelAsync();
            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => prepare.WaitAsync(Patience, Token));
        }
        finally
        {
            acknowledge.SetResult();
        }

        Assert.True(writer.Sequencer.IsStopped);
        Assert.Equal(SequencerStopReason.Sealing, writer.Sequencer.StopReason);
        Assert.DoesNotContain(ledger, cluster.Journals.ServedLedgers);
        Assert.Equal(new JournalWriteOutcome(new JournalMoved(Owner: null)), await cluster.AppendAsync("k", one, "y").WaitAsync(Patience, Token));

        _ = await cluster.ReplaceAsync("k");
        _ = Assert.IsType<ProposalOutcome.Durable>((await rawProposal.WaitAsync(Patience, Token)).Value);
        var two = Accepted(await cluster.AppendAsync("k", one, "y").WaitAsync(Patience, Token));
        Assert.Equal((two, true, "xy"), await cluster.ReadAsync("k"));
    }

    /// <summary>
    /// OR-I24: when <see cref="JournalStore.PrepareReleaseAsync"/> starts before any admission exists for the ledger,
    /// concurrent writes arriving while release is in flight do not create a new admission; they observe
    /// <see cref="JournalMoved"/> while the writer drains and stops cleanly.
    /// </summary>
    [Fact]
    public async Task PrepareReleaseWithoutExistingAdmissionBlocksNewAdmissionsWhileInFlight()
    {
        await using var cluster = await JournalCluster.StartAsync();
        var ledger = cluster.OpenLedger("k");
        Assert.Equal(0, cluster.Journals.AdmissionsCreated);
        var (stored, acknowledge) = HoldWriteAnswers(cluster);

        Task<ProposalOutcome> rawProposal;
        Task<LedgerPrepareReleaseOutcome> prepare;
        try
        {
            var writer = cluster.Ledgers.Registry.TryGet(ledger)!;
            rawProposal = Task.Run(
                async () =>
                {
                    var key = JournalRowKey.Encode("other");
                    using var batch = await writer.CreateBatchAsync(1, (int)(LedgerBatch.MaxHeaderLength(1) + LedgerBatch.MaxRowLength(key.Length, 0, 0, 1)), Token);
                    _ = batch.AddRow(key.Span, RowKind.Append, default, default, new ReadOnlySequence<byte>([1]));
                    return await writer.ProposeAsync(batch, Token);
                },
                Token);
            await stored.WaitAsync(Patience, Token);

            prepare = cluster.Journals.PrepareReleaseAsync(ledger, KeyRange.Root, TimeSpan.FromSeconds(10), Token);
            await Task.Delay(TimeSpan.FromMilliseconds(50), TimeProvider.System, Token);
            Assert.False(prepare.IsCompleted);

            var outcome = await cluster.AppendAsync("k", Origin, "during-release").WaitAsync(Patience, Token);
            Assert.Equal(new JournalWriteOutcome(new JournalMoved(Owner: null)), outcome);
            Assert.Equal(0, cluster.Journals.AdmissionsCreated);
        }
        finally
        {
            acknowledge.SetResult();
        }

        _ = Assert.IsType<ProposalOutcome.Durable>((await rawProposal.WaitAsync(Patience, Token)).Value);
        var released = Assert.IsType<LedgerPrepareReleaseOutcome.Released>((await prepare.WaitAsync(Patience, Token)).Value);
        Assert.True(released.ContiguousTail >= new Slot(1));
        Assert.Equal(0, cluster.Journals.AdmissionsCreated);
    }

    private static JournalVersion Accepted(JournalWriteOutcome outcome) =>
        Assert.IsType<JournalWriteOutcome.Accepted>(outcome.Value).Version;

    private static JournalWriteOutcome.Accepted AcceptedHint(JournalWriteOutcome outcome) =>
        Assert.IsType<JournalWriteOutcome.Accepted>(outcome.Value);

    /// <summary>Reads <paramref name="key"/>, with the read's replace request.</summary>
    private static async Task<(JournalVersion Version, bool Exists, string Content, bool ReplaceRequested)> ReadHintAsync(JournalCluster cluster, string key)
    {
        using var result = Assert.IsType<JournalReadResult>((await cluster.Journals.ReadAsync(JournalRowKey.Encode(key), Token)).Value);
        return (result.CurrentVersion, result.Exists, await JournalCluster.ContentAsync(result), result.ReplaceRequested);
    }

    /// <summary>
    /// Reads <paramref name="key"/>, and reloads it with a write at <c>Origin</c>, which the key's state, never at
    /// <c>Origin</c>, rejects with the read's version, from a history read of its own; returns the read's existence and
    /// content.
    /// </summary>
    private static async Task<(bool Exists, string Content)> ReadAndReloadAsync(JournalCluster cluster, string key)
    {
        var (version, exists, content) = await cluster.ReadAsync(key);
        var reads = cluster.Store.Reads;
        Assert.Equal(new JournalWriteOutcome(new JournalWriteOutcome.Rejected(version)), await cluster.AppendAsync(key, Origin, "z"));
        Assert.Equal(reads + 1, cluster.Store.Reads);
        return (exists, content);
    }

    /// <summary>
    /// Writes "x" to the journal "a" and splits its ledger; makes the key warm in the child from the parent's rows with a
    /// write at <c>Origin</c>, which its load serves and rejects with the parent's version; then drains and drops the
    /// parent, so no listed ledger holds a row of the key, and reads it. Returns the cluster, whose admissions reject as
    /// <paramref name="mutation"/>, the key's warm version, and the rebased version the read gives for the same state.
    /// </summary>
    private static async Task<(JournalCluster Cluster, JournalVersion Warm, JournalVersion Rebased)> WarmKeyWhoseRowsAllDroppedAsync(LedgerAdmission.ReloadMutant mutation)
    {
        var cluster = await JournalCluster.StartAsync();
        try
        {
            cluster.Journals.ReloadMutation = mutation;
            var warm = Accepted(await cluster.AppendAsync("a", Origin, "x"));
            var parent = await cluster.SplitAsync("a");
            await cluster.ReconciledAsync();
            var reads = cluster.Store.Reads;
            Assert.Equal(new JournalWriteOutcome(new JournalWriteOutcome.Rejected(warm)), await cluster.AppendAsync("a", Origin, "z"));
            Assert.Equal(reads + 1, cluster.Store.Reads);

            await cluster.DrainAsync(parent);
            await cluster.DropAsync(parent);
            var (rebased, exists, content) = await cluster.ReadAsync("a");
            Assert.NotEqual(warm, rebased);
            Assert.True(exists);
            Assert.Equal("x", content);
            return (cluster, warm, rebased);
        }
        catch
        {
            await cluster.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Holds the answer of each replica write from now on once the replica has stored it; returns a task which completes
    /// at the first stored write, and the source whose completion releases every held answer.
    /// </summary>
    private static (Task Stored, TaskCompletionSource Acknowledge) HoldWriteAnswers(JournalCluster cluster)
    {
        var stored = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acknowledge = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cluster.Ledgers.Transport.BeforeWriteAnswer = (_, _) =>
        {
            _ = stored.TrySetResult();
            return acknowledge.Task;
        };

        return (stored.Task, acknowledge);
    }

    /// <summary>
    /// Waits until <paramref name="condition"/> holds, which a step the test does not hold makes true, failing with
    /// <paramref name="what"/> once the test's patience passes.
    /// </summary>
    private static async Task UntilAsync(Func<bool> condition, string what)
    {
        var deadline = TimeProvider.System.GetTimestamp();
        while (!condition())
        {
            Assert.True(TimeProvider.System.GetElapsedTime(deadline) < Patience, $"Waited {Patience} for {what}.");
            await Task.Delay(TimeSpan.FromMilliseconds(1), TimeProvider.System, Token);
        }
    }
}
