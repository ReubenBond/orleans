using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Archive.Memory;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Testing;
using static Orleans.Durable.Storage.Engine.Journals.Tests.JournalSamples;

namespace Orleans.Durable.Storage.Engine.Journals.Tests;

/// <summary>
/// Admission on the primary (journals.md §8.1, §6; JS-I1, JS-I2, JS-I4, JS-I5, JS-I7) over a scripted ledger whose
/// proposals' outcomes each test decides: state loaded at first touch; entries admitted at the version the in-flight
/// ones leave, rejected at once when they apply to no possible state, and waiting otherwise until the in-flight entries
/// land or a seal fences them; an <c>Expired</c> judged against the applied state alone, by its version and its
/// tombstone's retention; rows of each entry's native content; batches in order within the window and the batch policy;
/// and every write answered exactly once. The admission runs inline, so each check sees it quiescent.
/// </summary>
public sealed class LedgerAdmissionTests : IDisposable
{
    private static readonly JournalWriteOutcome Fenced = new(JournalWriteOutcome.Fenced.Instance);
    private static readonly JournalWriteOutcome Ambiguous = new(JournalWriteOutcome.Ambiguous.Instance);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Retention = new JournalStoreOptions().TombstoneRetention;

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    /// <summary>The faults <see cref="AFaultOfTheAdmissionsOwnFailsItsBatchAndAbandonsTheWriter"/> injects.</summary>
    public enum Fault
    {
        CreateBatch,
        Propose,
        Outcome,
    }

    public void Dispose() => _leaks.Dispose();

    /// <summary>
    /// The first write of a key loads its state once; an entry at the key's version is proposed, its row advancing the
    /// version by one, and answered <c>Accepted</c> with that version only once its batch is <c>Durable</c> and applied
    /// (JS-I2); a resend of it is then <c>Rejected</c> with the advanced <c>CurrentVersion</c>.
    /// </summary>
    [Fact]
    public async Task AnEntryAtTheKeysVersionIsAcceptedOnlyOnceItsBatchIsDurable()
    {
        await using var ledger = new ScriptedLedger();
        ledger.Holds(Append(V(0)), Append(V(1)), Append(V(2)));
        var write = ledger.WriteAsync(Append(V(3), "four"));
        var row = Assert.Single(ledger.Proposal(0).Rows);
        Assert.Equal(("journal", RowKind.Append, V(4), "four"), (row.Key, row.Kind, row.Version, System.Text.Encoding.UTF8.GetString(row.Body)));
        Assert.False(write.IsCompleted);
        Assert.Equal(new Slot(0), ledger.Admission.AppliedBelow);

        ledger.Durable(0);

        Assert.Equal(Accepted(V(4)), await AnsweredAsync(write));
        Assert.Equal(new Slot(1), ledger.Admission.AppliedBelow);
        Assert.Equal(Rejected(V(4)), await AnsweredAsync(ledger.WriteAsync(Append(V(3), "four"))));
        Assert.Equal(1, ledger.Loads);
    }

    /// <summary>
    /// An entry which applies to no state the in-flight entries can leave is rejected at once with the applied
    /// <c>CurrentVersion</c>: a count past them, or a version of another base, compared for equality only.
    /// </summary>
    [Fact]
    public async Task AnEntryWhichAppliesToNoPossibleStateIsRejectedAtOnce()
    {
        await using var ledger = new ScriptedLedger();
        var first = ledger.WriteAsync(Append(V(0)));

        Assert.Equal(Rejected(V(0)), await AnsweredAsync(ledger.WriteAsync(Append(V(4)))));
        Assert.Equal(Rejected(V(0)), await AnsweredAsync(ledger.WriteAsync(Replace(V(2)))));
        Assert.Equal(Rejected(V(0)), await AnsweredAsync(ledger.WriteAsync(Delete(V(6)))));
        Assert.Equal(Rejected(V(0)), await AnsweredAsync(ledger.WriteAsync(Append(V(9, 1)))));
        Assert.Equal(Rejected(V(0)), await AnsweredAsync(ledger.WriteAsync(Append(V(9, 0)))));
        ledger.Durable(0);
        Assert.Equal(Accepted(V(1)), await AnsweredAsync(first));
    }

    /// <summary>
    /// An entry which applies to a state an in-flight prefix leaves, but not to the last, waits and holds its key's later
    /// entries behind it; once the in-flight entries land it is decided again and rejected, and the later entries are
    /// decided after it.
    /// </summary>
    [Fact]
    public async Task AConflictingEntryWaitsForTheInFlightEntriesAndIsThenRejected()
    {
        await using var ledger = new ScriptedLedger(window: 1);
        var one = ledger.WriteAsync(Append(V(0)));
        var two = ledger.WriteAsync(Append(V(1)));
        var conflicting = ledger.WriteAsync(Append(V(1), "again"));
        var three = ledger.WriteAsync(Append(V(2)));
        Assert.False(conflicting.IsCompleted);
        Assert.False(three.IsCompleted);

        ledger.Durable(0);
        Assert.Equal(Accepted(V(1)), await AnsweredAsync(one));
        Assert.Equal([2UL], ledger.Proposal(1).Counts);
        Assert.False(conflicting.IsCompleted);

        ledger.Durable(1);
        Assert.Equal(Accepted(V(2)), await AnsweredAsync(two));
        Assert.Equal(Rejected(V(2)), await AnsweredAsync(conflicting));
        Assert.Equal([3UL], ledger.Proposal(2).Counts);
        ledger.Durable(2);
        Assert.Equal(Accepted(V(3)), await AnsweredAsync(three));
    }

    /// <summary>
    /// A seal fences the primary (journals.md §6): the fenced proposal's entries are answered as the ledger decided them,
    /// every waiting entry and every entry not yet proposed is <c>Fenced</c>, and so is every later write.
    /// </summary>
    [Fact]
    public async Task AFencedProposalFencesTheWaitingEntriesAndEveryLaterWrite()
    {
        await using var ledger = new ScriptedLedger(window: 1);
        var one = ledger.WriteAsync(Append(V(0)));
        var waiting = ledger.WriteAsync(Append(V(0), "again"));
        var queued = ledger.WriteAsync(Append(V(0), key: KeyOf("other")));

        ledger.Fenced(0);

        Assert.Equal(Fenced, await AnsweredAsync(one));
        Assert.Equal(Fenced, await AnsweredAsync(waiting));
        Assert.Equal(Fenced, await AnsweredAsync(queued));
        Assert.Equal(Fenced, await AnsweredAsync(ledger.WriteAsync(Append(V(0)))));
        _ = Assert.Single(ledger.Proposals);
    }

    /// <summary>An <c>Ambiguous</c> proposal answers its entries <c>Ambiguous</c>, and fences the admission.</summary>
    [Fact]
    public async Task AnAmbiguousProposalAnswersItsEntriesAmbiguous()
    {
        await using var ledger = new ScriptedLedger();
        var write = ledger.WriteAsync(Append(V(0)));

        ledger.Ambiguous(0);

        Assert.Equal(Ambiguous, await AnsweredAsync(write));
        Assert.Equal(Fenced, await AnsweredAsync(ledger.WriteAsync(Append(V(0)))));
    }

    /// <summary>
    /// An <c>Expired</c> applies only once its tombstone's deletion time plus <c>TombstoneRetention</c> is at or before
    /// the admission's clock (JS-I7): rejected at once a tick before, at its own version, so its key, with no row in the
    /// ledger, does not load again; admitted at that instant; and its row is an empty
    /// <c>Delete</c>, after which the journal is absent at its advanced version and an <c>Append</c> goes on counting.
    /// </summary>
    [Fact]
    public async Task AnExpiredIsAdmittedOnlyOnceItsTombstonesRetentionHasPassed()
    {
        await using var ledger = new ScriptedLedger();
        ledger.Holds(Append(V(0)), Delete(V(1), ScriptedLedger.Epoch.UtcTicks));
        await WarmAsync(ledger);

        ledger.Clock.Advance(Retention - TimeSpan.FromTicks(1));
        Assert.Equal(Rejected(V(2)), await AnsweredAsync(ledger.ExpireAsync(V(2))));
        Assert.Empty(ledger.Proposals);
        Assert.Equal(1, ledger.Loads);

        ledger.Clock.Advance(TimeSpan.FromTicks(1));
        var expired = ledger.ExpireAsync(V(2));
        var row = Assert.Single(ledger.Proposal(0).Rows);
        Assert.Equal((RowKind.Delete, V(3), 0), (row.Kind, row.Version, row.Body.Length));
        ledger.Durable(0);
        Assert.Equal(Accepted(V(3), false), await AnsweredAsync(expired));

        var next = ledger.WriteAsync(Append(V(3), "after"));
        ledger.Durable(1);
        Assert.Equal(Accepted(V(4)), await AnsweredAsync(next));
        Assert.Equal(1, ledger.Loads);
    }

    /// <summary>
    /// A stale <c>Expired</c> is rejected at once (JS-I1, JS-I7): one whose deleted state was written again, one of a
    /// version the deleted state does not have, one of a journal never deleted, and one of a journal which is absent. Each
    /// key loads once, for the write which warms it: an <c>Expired</c> never loads its key again, though its version is
    /// another than the warm one, and the absent key, never warm, is rejected with the <c>Expired</c>'s own version, unloaded
    /// (journals.md §8.6).
    /// </summary>
    [Fact]
    public async Task AStaleExpiredIsRejectedAtOnce()
    {
        await using var ledger = new ScriptedLedger();
        ledger.Clock.Advance(Retention);
        var rewritten = KeyOf("rewritten");
        var deleted = KeyOf("deleted");
        var existing = KeyOf("existing");
        ledger.Holds(Delete(V(0), key: rewritten), Append(V(1), key: rewritten));
        ledger.Holds(Append(V(0), key: deleted), Delete(V(1), key: deleted));
        ledger.Holds(Append(V(0), key: existing));
        await WarmAsync(ledger, rewritten);
        await WarmAsync(ledger, deleted);
        await WarmAsync(ledger, existing);

        Assert.Equal(Rejected(V(2)), await AnsweredAsync(ledger.ExpireAsync(V(1), rewritten)));
        Assert.Equal(Rejected(V(2)), await AnsweredAsync(ledger.ExpireAsync(V(1), deleted)));
        Assert.Equal(Rejected(V(2)), await AnsweredAsync(ledger.ExpireAsync(V(9, 2), deleted)));
        Assert.Equal(Rejected(V(1)), await AnsweredAsync(ledger.ExpireAsync(V(1), existing)));
        Assert.Equal(Rejected(V(0)), await AnsweredAsync(ledger.ExpireAsync(V(0), KeyOf("absent"))));
        Assert.Empty(ledger.Proposals);
        Assert.Equal(3, ledger.Loads);
    }

    /// <summary>
    /// An <c>Expired</c> is judged against the applied state alone (journals.md §8.1): while any entry of its key is in
    /// flight, its <c>Delete</c> or a later entry, it is rejected at once with the applied <c>CurrentVersion</c>.
    /// </summary>
    [Fact]
    public async Task AnExpiredIsRejectedAtOnceWhileAnyEntryOfItsKeyIsInFlight()
    {
        await using var ledger = new ScriptedLedger();
        ledger.Clock.Advance(Retention);
        var deleting = KeyOf("deleting");
        var deleted = KeyOf("deleted");
        ledger.Holds(Append(V(0), key: deleting));
        ledger.Holds(Append(V(0), key: deleted), Delete(V(1), ScriptedLedger.Epoch.UtcTicks - Retention.Ticks, deleted));
        var delete = ledger.WriteAsync(Delete(V(1), ScriptedLedger.Epoch.UtcTicks - Retention.Ticks, deleting));
        var replace = ledger.WriteAsync(Replace(V(2), key: deleted));

        Assert.Equal(Rejected(V(1)), await AnsweredAsync(ledger.ExpireAsync(V(2), deleting)));
        Assert.Equal(Rejected(V(2)), await AnsweredAsync(ledger.ExpireAsync(V(2), deleted)));
        Assert.Equal(2, ledger.Proposals.Count);
        ledger.Durable(0);
        ledger.Durable(1);
        Assert.Equal(Accepted(V(2), false), await AnsweredAsync(delete));
        Assert.Equal(Accepted(V(3), false), await AnsweredAsync(replace));
    }

    /// <summary>
    /// An <c>Expired</c> is rejected at once while its key has an entry waiting (journals.md §8.1, step 3): behind an
    /// in-flight <c>Append</c> and a conflicting <c>Append</c> waiting on it, it is answered with the applied
    /// <c>CurrentVersion</c> before the in-flight batch lands, and the waiting entry is still decided once it does.
    /// </summary>
    [Fact]
    public async Task AnExpiredIsRejectedAtOnceWhileAnEntryOfItsKeyWaits()
    {
        await using var ledger = new ScriptedLedger();
        ledger.Clock.Advance(Retention);
        ledger.Holds(Append(V(0)), Delete(V(1)));
        var inFlight = ledger.WriteAsync(Append(V(2), "first"));
        var waiting = ledger.WriteAsync(Append(V(2), "second"));
        Assert.False(waiting.IsCompleted);

        Assert.Equal(Rejected(V(2)), await AnsweredAsync(ledger.ExpireAsync(V(2))));

        _ = Assert.Single(ledger.Proposals);
        ledger.Durable(0);
        Assert.Equal(Accepted(V(3)), await AnsweredAsync(inFlight));
        Assert.Equal(Rejected(V(3)), await AnsweredAsync(waiting));
    }

    /// <summary>
    /// An entry of the deleted state's version waits on an in-flight <c>Expired</c>, which a seal then excludes: both end
    /// <c>Fenced</c> (journals.md §10).
    /// </summary>
    [Fact]
    public async Task AnEntryWaitingOnAnInFlightExpiredWhichASealFencesIsFenced()
    {
        await using var ledger = new ScriptedLedger();
        ledger.Clock.Advance(Retention);
        ledger.Holds(Append(V(0)), Delete(V(1)));
        await WarmAsync(ledger);
        var expired = ledger.ExpireAsync(V(2));
        Assert.Equal(RowKind.Delete, Assert.Single(ledger.Proposal(0).Rows).Kind);

        var resumed = ledger.WriteAsync(Append(V(2)));
        Assert.False(resumed.IsCompleted);

        ledger.Fenced(0);
        Assert.Equal(Fenced, await AnsweredAsync(expired));
        Assert.Equal(Fenced, await AnsweredAsync(resumed));
    }

    /// <summary>
    /// An entry at the version an in-flight <c>Expired</c> leaves is admitted behind it, and one of the deleted state's
    /// version waits and is rejected once the <c>Expired</c> lands, which leaves it no possible state, with the version
    /// the <c>Expired</c> left.
    /// </summary>
    [Fact]
    public async Task EntriesBehindAnInFlightExpiredAreDecidedByTheVersionItLeaves()
    {
        await using var ledger = new ScriptedLedger();
        ledger.Clock.Advance(Retention);
        ledger.Holds(Append(V(0)), Delete(V(1)));
        await WarmAsync(ledger);
        var expired = ledger.ExpireAsync(V(2));
        var next = ledger.WriteAsync(Append(V(3), "after"));
        var resumed = ledger.WriteAsync(Append(V(2)));
        Assert.Equal([4UL], ledger.Proposal(1).Counts);
        Assert.False(resumed.IsCompleted);

        ledger.Durable(0);
        Assert.Equal(Accepted(V(3), false), await AnsweredAsync(expired));
        Assert.Equal(Rejected(V(3)), await AnsweredAsync(resumed));
        ledger.Durable(1);
        Assert.Equal(Accepted(V(4)), await AnsweredAsync(next));
    }

    /// <summary>
    /// journals.md §8.6: an <c>Expired</c> never loads its key. One whose key holds no warm state, never loaded or evicted,
    /// is rejected at once with its own expected version, proposing nothing and loading nothing; once a write has loaded
    /// the key, the same <c>Expired</c> is admitted.
    /// </summary>
    [Fact]
    public async Task AnExpiredOfAKeyWithNoWarmStateIsRejectedWithoutALoad()
    {
        await using var ledger = new ScriptedLedger();
        ledger.Clock.Advance(Retention);
        var evicted = KeyOf("evicted");
        ledger.Holds(Append(V(0)), Delete(V(1), ScriptedLedger.Epoch.UtcTicks));

        Assert.Equal(Rejected(V(2)), await AnsweredAsync(ledger.ExpireAsync(V(2))));
        Assert.Equal((0, 0), (ledger.Loads, ledger.Proposals.Count));

        await WarmAsync(ledger);
        var expired = ledger.ExpireAsync(V(2));
        ledger.Durable(0);
        Assert.Equal(Accepted(V(3), false), await AnsweredAsync(expired));
        Assert.Equal(1, ledger.Loads);

        await using var budgetless = new ScriptedLedger(options: new JournalStoreOptions { WarmStateBytes = 0 });
        budgetless.Clock.Advance(Retention);
        budgetless.Holds(Delete(V(0), ScriptedLedger.Epoch.UtcTicks, evicted));
        await WarmAsync(budgetless, evicted);
        Assert.Null(budgetless.Admission.Warm(evicted));
        Assert.Equal(Rejected(V(1)), await AnsweredAsync(budgetless.ExpireAsync(V(1), evicted)));
        Assert.Equal((1, 0), (budgetless.Loads, budgetless.Proposals.Count));
    }

    /// <summary>
    /// journals.md §8.6: an <c>Expired</c> is no use of its key. With room for two keys, a deleted key and then a live one
    /// warm, an <c>Expired</c> of the deleted key, accepted or rejected, returns it to the least recently used end, so a
    /// third key evicts it rather than the live key.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnExpiredLeavesItsKeyLeastRecentlyUsed(bool retentionPassed)
    {
        var (deleted, live, third) = (KeyOf("d"), KeyOf("l"), KeyOf("t"));
        var budget = 2 * LedgerAdmission.LoadedKeyBytes(deleted.Length);
        await using var ledger = new ScriptedLedger(options: new JournalStoreOptions { WarmStateBytes = budget });
        ledger.Holds(Delete(V(0), ScriptedLedger.Epoch.UtcTicks, deleted));
        ledger.Holds(Append(V(0), key: live));
        ledger.Holds(Append(V(0), key: third));
        await WarmAsync(ledger, deleted);
        await WarmAsync(ledger, live);
        ledger.Clock.Advance(retentionPassed ? Retention : Retention - TimeSpan.FromTicks(1));

        var expired = ledger.ExpireAsync(V(1), deleted);
        if (retentionPassed)
        {
            ledger.Durable(0);
        }

        Assert.Equal(retentionPassed ? Accepted(V(2), false) : Rejected(V(1)), await AnsweredAsync(expired));
        await WarmAsync(ledger, third);

        Assert.Null(ledger.Admission.Warm(deleted));
        Assert.NotNull(ledger.Admission.Warm(live));
        Assert.NotNull(ledger.Admission.Warm(third));
    }

    /// <summary>
    /// journals.md §8.6: the expiry scan's candidates are the loaded keys with nothing waiting or in flight. A deleted key
    /// whose retention has passed is one, until a stale write of it waits for its reload, and a fenced admission gives
    /// none, though its warm state holds the key deleted.
    /// </summary>
    [Fact]
    public async Task AKeyWaitingForItsReloadAndAFencedAdmissionGiveNoCandidates()
    {
        await using var ledger = new ScriptedLedger();
        var other = KeyOf("other");
        ArchiveRow[] deleted = [Live(At(1), RowKind.Append, V(1), [1]), Live(At(2), RowKind.Delete, V(2), Tombstone(ScriptedLedger.Epoch.UtcTicks))];
        try
        {
            var hold = Task.CompletedTask;
            ledger.LoadsFrom(Key, async _ =>
            {
                await hold;
                return JournalState.Fold(null, deleted);
            });
            await WarmAsync(ledger);
            ledger.Clock.Advance(Retention);
            Assert.Equal([(Key, V(2))], Candidates(ledger));

            var reload = new TaskCompletionSource();
            hold = reload.Task;
            var stale = ledger.WriteAsync(Append(V(0xee, 0)));
            Assert.Equal(2, ledger.Loads);
            Assert.Empty(Candidates(ledger));
            reload.SetResult();
            Assert.Equal(Rejected(V(2)), await AnsweredAsync(stale));
            Assert.Equal([(Key, V(2))], Candidates(ledger));

            var write = ledger.WriteAsync(Append(V(0), key: other));
            ledger.Fenced(0);
            Assert.Equal(Fenced, await AnsweredAsync(write));
            Assert.Empty(Candidates(ledger));
        }
        finally
        {
            Release(deleted);
        }
    }

    /// <summary>The expiry scan's candidates in <paramref name="ledger"/>'s admission.</summary>
    private static List<(JournalRowKey Key, JournalVersion Version)> Candidates(ScriptedLedger ledger)
    {
        var candidates = new List<(JournalRowKey Key, JournalVersion Version)>();
        ledger.Admission.AddExpiryCandidates(candidates);
        return candidates;
    }

    /// <summary>
    /// Loads <paramref name="key"/>'s held state into warm state with a write of another base, which no state admits and
    /// which its first load rejects without loading again.
    /// </summary>
    private static async Task WarmAsync(ScriptedLedger ledger, JournalRowKey? key = null) =>
        _ = Assert.IsType<JournalWriteOutcome.Rejected>((await AnsweredAsync(ledger.WriteAsync(Append(V(0xee, 0), key: key)))).Value);

    /// <summary>Only the key's primary writes an <c>Expired</c>, through its expiry scan (JS-I7): a writer's <c>Expired</c> and a default record are refused.</summary>
    [Fact]
    public async Task AWriterCannotAdmitAnExpiredOrADefaultRecord()
    {
        await using var ledger = new ScriptedLedger();
        using var expired = Expired(V(2));

        _ = Assert.Throws<ArgumentException>(() => { _ = ledger.Admit(expired); });
        _ = Assert.Throws<ArgumentException>(() => { _ = ledger.Admit(default); });

        Assert.Equal(0, ledger.Loads);
    }

    /// <summary>
    /// A state whose version's count is the <c>u64</c> maximum is one no row can advance: an entry which would apply to
    /// it, an <c>Expired</c> of a deleted one among them, is failed with <see cref="ArgumentException"/>, as
    /// <c>BeginRow</c> would refuse it, and nothing is proposed; the entry which reaches the maximum is admitted. The
    /// refusal is the entry's alone: the writer is not abandoned, and another key's write is accepted.
    /// </summary>
    [Fact]
    public async Task AnEntryAtAStateNoRowCanAdvanceIsRefused()
    {
        await using var ledger = new ScriptedLedger();
        ledger.Clock.Advance(Retention);
        var last = V(ulong.MaxValue - 1);
        var deleted = KeyOf("deleted");
        ArchiveRow[] live = [Live(At(1), RowKind.Append, last, [1])];
        ArchiveRow[] tombstoned = [Live(At(1), RowKind.Delete, V(ulong.MaxValue), Tombstone(DefaultTicks))];
        try
        {
            ledger.LoadsFrom(Key, _ => ValueTask.FromResult(JournalState.Fold(null, live)));
            ledger.LoadsFrom(deleted, _ => ValueTask.FromResult(JournalState.Fold(null, tombstoned)));
            var reaching = ledger.WriteAsync(Append(last));
            ledger.Durable(0);
            Assert.Equal(Accepted(V(ulong.MaxValue)), await AnsweredAsync(reaching));

            _ = await Assert.ThrowsAsync<ArgumentException>(() => AnsweredAsync(ledger.WriteAsync(Append(V(ulong.MaxValue)))));
            Assert.Equal(Rejected(V(ulong.MaxValue)), await AnsweredAsync(ledger.WriteAsync(Append(V(0xee, 0), key: deleted))));
            _ = await Assert.ThrowsAsync<ArgumentException>(() => AnsweredAsync(ledger.ExpireAsync(V(ulong.MaxValue), deleted)));
            _ = Assert.Single(ledger.Proposals);

            var other = ledger.WriteAsync(Append(V(0), key: KeyOf("other")));
            ledger.Durable(1);
            Assert.Equal(Accepted(V(1)), await AnsweredAsync(other));
            Assert.Equal(0, ledger.Abandoned);
        }
        finally
        {
            Release(live);
            Release(tombstoned);
        }
    }

    /// <summary>
    /// journals.md §8.8: each of the store's settings out of its range is refused at startup, naming the setting, before
    /// anything is built from it.
    /// </summary>
    [Theory]
    [InlineData(nameof(JournalStoreOptions.MaxBodyBytes))]
    [InlineData(nameof(JournalStoreOptions.MaxBatchEntries))]
    [InlineData(nameof(JournalStoreOptions.MaxBatchBytes))]
    [InlineData(nameof(JournalStoreOptions.MaxQueuedBytes))]
    [InlineData(nameof(JournalStoreOptions.WarmStateBytes))]
    [InlineData(nameof(JournalStoreOptions.TombstoneRetention))]
    [InlineData(nameof(JournalStoreOptions.ExpiryInterval))]
    [InlineData(nameof(JournalStoreOptions.MaxConcurrentHistoryReads))]
    public void EachSettingOutOfItsRangeIsRefusedNamingIt(string setting)
    {
        var options = setting switch
        {
            nameof(JournalStoreOptions.MaxBodyBytes) => new JournalStoreOptions { MaxBodyBytes = 0 },
            nameof(JournalStoreOptions.MaxBatchEntries) => new JournalStoreOptions { MaxBatchEntries = 0 },
            nameof(JournalStoreOptions.MaxBatchBytes) => new JournalStoreOptions { MaxBatchBytes = 0 },
            nameof(JournalStoreOptions.MaxQueuedBytes) => new JournalStoreOptions { MaxQueuedBytes = 0 },
            nameof(JournalStoreOptions.WarmStateBytes) => new JournalStoreOptions { WarmStateBytes = -1 },
            nameof(JournalStoreOptions.TombstoneRetention) => new JournalStoreOptions { TombstoneRetention = TimeSpan.FromTicks(-1) },
            nameof(JournalStoreOptions.ExpiryInterval) => new JournalStoreOptions { ExpiryInterval = TimeSpan.Zero },
            nameof(JournalStoreOptions.MaxConcurrentHistoryReads) => new JournalStoreOptions { MaxConcurrentHistoryReads = 0 },
            _ => throw new ArgumentOutOfRangeException(nameof(setting), setting, "No such setting."),
        };

        Assert.Equal(setting, Assert.Throws<ArgumentOutOfRangeException>(() => LedgerAdmission.CheckRanges(options)).ParamName);
    }

    /// <summary>
    /// <c>TombstoneRetention</c> is at least zero (journals.md §8.4): a negative one is refused at startup, and at zero an
    /// <c>Expired</c> applies as soon as its tombstone's deletion time is at or before the admission's clock.
    /// </summary>
    [Fact]
    public async Task TheTombstoneRetentionIsAtLeastZero()
    {
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new ScriptedLedger(options: new JournalStoreOptions { TombstoneRetention = TimeSpan.FromTicks(-1) }));

        await using var ledger = new ScriptedLedger(options: new JournalStoreOptions { TombstoneRetention = TimeSpan.Zero });
        ledger.Holds(Append(V(0)), Delete(V(1), ScriptedLedger.Epoch.UtcTicks));
        await WarmAsync(ledger);
        var expired = ledger.ExpireAsync(V(2));
        ledger.Durable(0);
        Assert.Equal(Accepted(V(3), false), await AnsweredAsync(expired));
    }

    /// <summary>
    /// A key outside the ledger's range is answered <c>Moved</c> without an owner, before any state loads or any batch
    /// holds it, so no row of a shared batch can be refused for its key.
    /// </summary>
    [Fact]
    public async Task AKeyOutsideTheLedgersRangeIsMoved()
    {
        var range = KeyRange.Containing(Key.Span.ToArray(), 1);
        var outside = Enumerable.Range(0, 64).Select(static index => KeyOf($"k{index}")).First(key => !range.Contains(key.Span));
        await using var ledger = new ScriptedLedger(range: range);

        Assert.Equal(new JournalWriteOutcome(new JournalMoved(Owner: null)), await AnsweredAsync(ledger.WriteAsync(Append(V(0), key: outside))));

        Assert.Equal(0, ledger.Loads);
        Assert.Empty(ledger.Proposals);
        var inside = ledger.WriteAsync(Append(V(0)));
        ledger.Durable(0);
        Assert.Equal(Accepted(V(1)), await AnsweredAsync(inside));
    }

    /// <summary>
    /// An <c>Append</c>'s or a <c>Replace</c>'s body is at most <c>MaxBodyBytes</c> (journals.md §8.4): one a byte
    /// longer is refused with <see cref="ArgumentException"/> before anything is loaded or proposed, and one of exactly
    /// <c>MaxBodyBytes</c> is admitted. The limit is 16 MiB by default.
    /// </summary>
    [Theory]
    [InlineData(JournalEntryKind.Append)]
    [InlineData(JournalEntryKind.Replace)]
    public async Task ABodyOfMaxBodyBytesIsAdmittedAndOneAByteLongerIsRefused(JournalEntryKind kind)
    {
        Assert.Equal(16 << 20, new JournalStoreOptions().MaxBodyBytes);
        await using var ledger = new ScriptedLedger(options: new JournalStoreOptions { MaxBodyBytes = 4 });
        JournalEntryRecord Entry(string body) => kind == JournalEntryKind.Append ? Append(V(0), body) : Replace(V(0), body);

        using (var longer = Entry("fives"))
        {
            _ = Assert.Throws<ArgumentException>(() => { _ = ledger.WriteAsync(longer); });
        }

        Assert.Equal(0, ledger.Loads);
        Assert.Empty(ledger.Proposals);
        var write = ledger.WriteAsync(Entry("four"));
        ledger.Durable(0);
        Assert.Equal(Accepted(V(1), kind == JournalEntryKind.Append), await AnsweredAsync(write));
    }

    /// <summary>
    /// The startup check's boundary (journals.md §8.4): the replica's payload limit holds a batch of one row of the
    /// longest key and a <c>MaxBodyBytes</c> body, which is the batch's header at its longest and its CRC-32C (34 bytes:
    /// 9 fixed bytes, 10-byte segment offset and ordinal base varints, a 1-byte row count, and 4), the row's flags and
    /// key length (4 bytes), its 65,535-byte key, its version before and token at their longest with 32-byte bases (76
    /// bytes), its body length (1 or 3 bytes), and its body, the payload (ledgers.md §7). At exactly that limit the
    /// admission starts and takes such an <c>Append</c> whole; at one byte less it refuses to start.
    /// </summary>
    [Theory]
    [InlineData(20, 34 + 4 + 65_535 + 76 + 1 + 20)]
    [InlineData(65_536, 34 + 4 + 65_535 + 76 + 3 + 65_536)]
    public async Task ThePayloadLimitMustHoldTheLongestKeyAndBody(int maxBodyBytes, int limit)
    {
        Assert.Equal(limit, LedgerAdmission.LongestBatch(maxBodyBytes));
        var options = new JournalStoreOptions { MaxBodyBytes = maxBodyBytes };
        _ = Assert.Throws<ArgumentException>("maxBatchLength", () => new ScriptedLedger(maxBatchLength: limit - 1, options: options));

        await using var ledger = new ScriptedLedger(maxBatchLength: limit, options: options);
        var journal = new string('k', JournalRowKey.MaxKeyBytes);
        var write = ledger.WriteAsync(Append(V(0), new string('x', maxBodyBytes), KeyOf(journal)));
        var row = Assert.Single(ledger.Proposal(0).Rows);
        Assert.Equal((journal, maxBodyBytes), (row.Key, row.Body.Length));
        ledger.Durable(0);
        Assert.Equal(Accepted(V(1)), await AnsweredAsync(write));
    }

    /// <summary>
    /// The startup check bounds the longest row body, a <c>Delete</c>'s 8-byte tombstone included, whatever
    /// <c>MaxBodyBytes</c> says: under a body limit of 1 byte, a payload limit which holds the longest key and a
    /// 1-byte body but not a tombstone is refused, and one which holds the tombstone takes a <c>Delete</c> of that key.
    /// </summary>
    [Fact]
    public async Task ThePayloadLimitMustHoldADeleteOfTheLongestKey()
    {
        var options = new JournalStoreOptions { MaxBodyBytes = 1 };
        var limit = (int)(LedgerBatch.MaxHeaderLength(1) + LedgerBatch.MaxRowLength(KeyRange.MaxKeyLength, 32, 32, JournalEntry.TombstoneLength));
        Assert.Equal(limit, LedgerAdmission.LongestBatch(1));
        _ = Assert.Throws<ArgumentException>("maxBatchLength", () => new ScriptedLedger(maxBatchLength: limit - 1, options: options));

        await using var ledger = new ScriptedLedger(maxBatchLength: limit, options: options);
        var write = ledger.WriteAsync(Delete(V(0), key: KeyOf(new string('k', JournalRowKey.MaxKeyBytes))));
        ledger.Durable(0);
        Assert.Equal(Accepted(V(1), false), await AnsweredAsync(write));
    }

    /// <summary>
    /// Each row holds its entry's native content, written from the verified record (journals.md §4.2, JS-I5): an
    /// <c>Append</c>'s payload across several pages, an empty <c>Replace</c>'s nothing, a forwarded record's payload, a
    /// <c>Delete</c>'s 8-byte tombstone on a <c>Delete</c> row, and an <c>Expired</c>'s empty <c>Delete</c> row.
    /// </summary>
    [Fact]
    public async Task EachRowHoldsItsEntrysNativeContent()
    {
        await using var ledger = new ScriptedLedger();
        ledger.Clock.Advance(Retention);
        var bytes = new byte[100_000];
        new Random(5).NextBytes(bytes);
        using var paged = PagesOf(bytes);
        Assert.False(paged.AsReadOnlySequence().IsSingleSegment, "The payload spans pages.");
        using var multiPage = JournalEntryRecord.Append(Key, V(0), paged);
        using var empty = JournalEntryRecord.Replace(Key, V(1), default);
        using var encoded = JournalEntryRecord.Append(Key, V(2), paged);
        using var forwardedBytes = PagesOf(BytesOf(encoded));
        Assert.Null(JournalEntryRecord.Decode(Key, forwardedBytes, out var forwarded));
        using var delete = Delete(V(3), DefaultTicks);

        Task<JournalWriteOutcome>[] writes;
        using (forwarded)
        {
            writes = [ledger.Admit(multiPage), ledger.Admit(empty), ledger.Admit(forwarded), ledger.Admit(delete)];
        }

        var rows = ledger.Proposals.SelectMany(static proposal => proposal.Rows).ToArray();
        Assert.Equal([RowKind.Append, RowKind.Replace, RowKind.Append, RowKind.Delete], rows.Select(static row => row.Kind));
        byte[][] bodies = [bytes, [], bytes, Tombstone(DefaultTicks)];
        Assert.Equal(bodies, rows.Select(static row => row.Body));
        Assert.Equal([V(1), V(2), V(3), V(4)], rows.Select(static row => row.Version));
        for (var index = 0; index < ledger.Proposals.Count; index++)
        {
            ledger.Durable(index);
        }

        for (var index = 0; index < writes.Length; index++)
        {
            Assert.Equal(Accepted(V((ulong)index + 1), index is 0 or 2), await AnsweredAsync(writes[index]));
        }

        var expired = ledger.ExpireAsync(V(4));
        var expiredRow = Assert.Single(ledger.Proposals[^1].Rows);
        Assert.Equal((RowKind.Delete, 0), (expiredRow.Kind, expiredRow.Body.Length));
        ledger.Durable(ledger.Proposals.Count - 1);
        Assert.Equal(Accepted(V(5), false), await AnsweredAsync(expired));
    }

    /// <summary>
    /// A body of 2 MiB and 17 bytes, across many buffer pages, is proposed whole in its row, both from a record built
    /// where the write began and from one decoded from the paged bytes which reached the primary (journals.md §8.4).
    /// </summary>
    [Fact]
    public async Task ABodyOfManyPagesIsProposedWhole()
    {
        await using var ledger = new ScriptedLedger();
        var bytes = new byte[(2 << 20) + 17];
        new Random(17).NextBytes(bytes);
        using var paged = PagesOf(bytes);
        Assert.False(paged.AsReadOnlySequence().IsSingleSegment, "The body spans pages.");
        using var local = JournalEntryRecord.Append(Key, V(0), paged);
        using var encoded = JournalEntryRecord.Replace(Key, V(1), paged);
        using var forwardedBytes = PagesOf(BytesOf(encoded));
        Assert.Null(JournalEntryRecord.Decode(Key, forwardedBytes, out var forwarded));

        Task<JournalWriteOutcome>[] writes;
        using (forwarded)
        {
            writes = [ledger.Admit(local), ledger.Admit(forwarded)];
        }

        var rows = ledger.Proposals.SelectMany(static proposal => proposal.Rows).ToArray();
        Assert.All(rows, row => Assert.Equal(bytes, row.Body));
        for (var index = 0; index < ledger.Proposals.Count; index++)
        {
            ledger.Durable(index);
        }

        foreach (var write in writes)
        {
            _ = Assert.IsType<JournalWriteOutcome.Accepted>((await AnsweredAsync(write)).Value);
        }
    }

    /// <summary>
    /// Entries admitted while the window is full share the next batch, in admission order, within the batch's entry
    /// count; each row is its entry's kind and native content.
    /// </summary>
    [Fact]
    public async Task EntriesQueuedBehindAFullWindowShareTheNextBatchInAdmissionOrder()
    {
        await using var ledger = new ScriptedLedger(window: 1, options: new JournalStoreOptions { MaxBatchEntries = 3 });
        var b = KeyOf("b");
        var c = KeyOf("c");
        var first = ledger.WriteAsync(Append(V(0)));
        JournalEntryRecord[] records = [Append(V(0), "b1", b), Replace(V(0), "c1", c), Delete(V(1), 5, c), Append(V(1), "b2", b)];

        var writes = records.Select(record => ledger.WriteAsync(record)).ToArray();
        ledger.Durable(0);

        var second = ledger.Proposal(1);
        Assert.Equal(["b", "c", "c"], second.Rows.Select(static row => row.Key));
        Assert.Equal([RowKind.Append, RowKind.Replace, RowKind.Delete], second.Rows.Select(static row => row.Kind));
        byte[][] bodies = ["b1"u8.ToArray(), "c1"u8.ToArray(), Tombstone(5)];
        Assert.Equal(bodies, second.Rows.Select(static row => row.Body));
        ledger.Durable(1);
        Assert.Equal("b2"u8.ToArray(), Assert.Single(ledger.Proposal(2).Rows).Body);
        ledger.Durable(2);
        Assert.Equal(Accepted(V(1)), await AnsweredAsync(first));
        Assert.Equal([Accepted(V(1)), Accepted(V(1), false), Accepted(V(2), false), Accepted(V(2))], await Task.WhenAll(writes));
    }

    /// <summary>
    /// The replica's payload limit bounds a batch (journals.md §8.1): two queued rows which exactly fill it, as admission
    /// estimates them, share a batch, and the third waits for the next. The bodies are long enough that two short-keyed
    /// rows pass the startup check's one row of the longest key.
    /// </summary>
    [Fact]
    public async Task RowsWhichTogetherFillThePayloadLimitShareABatch()
    {
        const int Payload = 70_000;
        var keys = new[] { KeyOf("b"), KeyOf("c"), KeyOf("d") };
        var limit = (int)(LedgerBatch.MaxHeaderLength(1) + 2 * RowLength(keys[0], Payload));
        await using var ledger = new ScriptedLedger(window: 1, maxBatchLength: limit, options: new JournalStoreOptions { MaxBodyBytes = Payload });
        var first = ledger.WriteAsync(Append(V(0)));
        var writes = keys.Select(key => ledger.WriteAsync(Append(V(0), new string('x', Payload), key))).ToArray();

        ledger.Durable(0);

        Assert.Equal(["b", "c"], ledger.Proposal(1).Rows.Select(static row => row.Key));
        ledger.Durable(1);
        Assert.Equal(["d"], ledger.Proposal(2).Rows.Select(static row => row.Key));
        ledger.Durable(2);
        Assert.Equal(Accepted(V(1)), await AnsweredAsync(first));
        foreach (var write in writes)
        {
            Assert.Equal(Accepted(V(1)), await AnsweredAsync(write));
        }
    }

    /// <summary>
    /// <c>MaxBatchBytes</c> bounds a batch's rows (journals.md §8.1), except that a batch takes at least its first row: a
    /// bound below one row, at one row, or a byte short of two proposes each row alone; a bound of two rows pairs them.
    /// </summary>
    [Theory]
    [InlineData(0, 1, new[] { 1, 1, 1 })]
    [InlineData(1, 0, new[] { 1, 1, 1 })]
    [InlineData(2, -1, new[] { 1, 1, 1 })]
    [InlineData(2, 0, new[] { 2, 1 })]
    public async Task MaxBatchBytesBoundsABatchButNeverItsFirstRow(int rows, int extra, int[] expected)
    {
        var keys = new[] { KeyOf("b"), KeyOf("c"), KeyOf("d") };
        var rowLength = (int)RowLength(keys[0], 1);
        await using var ledger = new ScriptedLedger(window: 1, options: new JournalStoreOptions { MaxBatchBytes = rows * rowLength + extra });
        var first = ledger.WriteAsync(Append(V(0)));
        var writes = keys.Select(key => ledger.WriteAsync(Append(V(0), key: key))).ToArray();

        for (var index = 0; index <= expected.Length; index++)
        {
            ledger.Durable(index);
        }

        Assert.Equal(expected, ledger.Proposals.Skip(1).Select(static proposal => proposal.Rows.Count));
        Assert.Equal(Accepted(V(1)), await AnsweredAsync(first));
        foreach (var write in writes)
        {
            Assert.Equal(Accepted(V(1)), await AnsweredAsync(write));
        }
    }

    /// <summary>
    /// The entries accepted and not yet proposed are bounded in bytes (journals.md §8.8): a write past the bound waits,
    /// unloaded and undecided, until a batch takes entries, and is then accepted.
    /// </summary>
    [Fact]
    public async Task AWriteWaitsForRoomAmongTheEntriesNotYetProposed()
    {
        var a = KeyOf("a");
        var b = KeyOf("b");
        await using var ledger = new ScriptedLedger(window: 1, options: new JournalStoreOptions { MaxQueuedBytes = RowLength(a, 1) });
        var first = ledger.WriteAsync(Append(V(0)));
        var queued = ledger.WriteAsync(Append(V(0), key: a));
        var waiting = ledger.WriteAsync(Append(V(0), key: b));
        Assert.Equal(2, ledger.Loads);

        ledger.Durable(0);

        Assert.Equal(3, ledger.Loads);
        Assert.Equal(["a"], ledger.Proposal(1).Rows.Select(static row => row.Key));
        ledger.Durable(1);
        Assert.Equal(["b"], ledger.Proposal(2).Rows.Select(static row => row.Key));
        ledger.Durable(2);
        Assert.Equal(Accepted(V(1)), await AnsweredAsync(first));
        Assert.Equal(Accepted(V(1)), await AnsweredAsync(queued));
        Assert.Equal(Accepted(V(1)), await AnsweredAsync(waiting));
    }

    /// <summary>A write waiting for room is answered <c>Fenced</c> when the admission fences.</summary>
    [Fact]
    public async Task AWriteWaitingForRoomIsFencedWithTheAdmission()
    {
        await using var ledger = new ScriptedLedger(window: 1, options: new JournalStoreOptions { MaxQueuedBytes = RowLength(KeyOf("a"), 1) });
        var first = ledger.WriteAsync(Append(V(0)));
        var queued = ledger.WriteAsync(Append(V(0), key: KeyOf("a")));
        var waiting = ledger.WriteAsync(Append(V(0), key: KeyOf("b")));

        ledger.Fenced(0);

        Assert.Equal(Fenced, await AnsweredAsync(first));
        Assert.Equal(Fenced, await AnsweredAsync(queued));
        Assert.Equal(Fenced, await AnsweredAsync(waiting));
    }

    /// <summary>
    /// Warm state is bounded (journals.md §8.8): a key with entries in flight stays, and once it is idle a budget it
    /// exceeds evicts it, after which its next write loads again a state which includes its landed entries (JS-I9).
    /// </summary>
    [Theory]
    [InlineData(0L, 2)]
    [InlineData(1L << 20, 1)]
    public async Task AnIdleKeyPastTheWarmStateBudgetIsEvictedAndLoadsAgain(long budget, int loads)
    {
        await using var ledger = new ScriptedLedger(options: new JournalStoreOptions { WarmStateBytes = budget });
        var one = ledger.WriteAsync(Append(V(0)));
        var two = ledger.WriteAsync(Append(V(1)));
        Assert.Equal(1, ledger.Loads);
        ledger.Durable(0);
        ledger.Durable(1);
        Assert.Equal(Accepted(V(1)), await AnsweredAsync(one));
        Assert.Equal(Accepted(V(2)), await AnsweredAsync(two));

        Assert.Equal(Rejected(V(2)), await AnsweredAsync(ledger.WriteAsync(Append(V(1)))));

        Assert.Equal(loads, ledger.Loads);
        var three = ledger.WriteAsync(Append(V(2)));
        ledger.Durable(2);
        Assert.Equal(Accepted(V(3)), await AnsweredAsync(three));
    }

    /// <summary>
    /// A rejection of a warm key with no recorded segment in the admission's ledger, whose warm version came from rows
    /// another ledger holds, waits for the key to load again, since a read may name its state by a rebased version
    /// (journals.md §8.3), unless its latest load started after the entry arrived: a burst of stale writes, the first of
    /// which starts a reload which the rest arrive during, loads at most twice, and once the key has a row in the
    /// ledger its warm version is its state's only name, and a rejection reads nothing.
    /// </summary>
    [Fact]
    public async Task ABurstOfStaleWritesOfAWarmKeyWithNoRowInTheLedgerReloadsItAtMostTwice()
    {
        await using var ledger = new ScriptedLedger();
        ArchiveRow[] live = [Live(At(1), RowKind.Append, V(1), [1])];
        try
        {
            var hold = Task.CompletedTask;
            ledger.LoadsFrom(Key, async _ =>
            {
                await hold;
                return JournalState.Fold(null, live);
            });
            Assert.Equal(Rejected(V(1)), await AnsweredAsync(ledger.WriteAsync(Append(V(5)))));
            Assert.Equal(1, ledger.Loads);

            var reload = new TaskCompletionSource();
            hold = reload.Task;
            var burst = Enumerable.Range(0, 4).Select(_ => ledger.WriteAsync(Append(V(5)))).ToArray();
            Assert.Equal(2, ledger.Loads);
            Assert.All(burst, static write => Assert.False(write.IsCompleted));
            reload.SetResult();

            foreach (var write in burst)
            {
                Assert.Equal(Rejected(V(1)), await AnsweredAsync(write));
            }

            Assert.Equal(3, ledger.Loads);
            var two = ledger.WriteAsync(Append(V(1)));
            ledger.Durable(0);
            Assert.Equal(Accepted(V(2)), await AnsweredAsync(two));
            Assert.Equal(Rejected(V(2)), await AnsweredAsync(ledger.WriteAsync(Append(V(5)))));
            Assert.Equal(3, ledger.Loads);
        }
        finally
        {
            Release(live);
        }
    }

    /// <summary>
    /// A stale write of a warm key with an entry in flight is rejected without a reload, which JS-I6 forbids beside an
    /// entry in flight: with the window held by another key's batch, the key's entry at its version is admitted and waits
    /// unbatched, a stale write is rejected with the applied version and reads nothing, and once both batches are
    /// <c>Durable</c> the entry is accepted and each key's rows follow one another.
    /// </summary>
    [Fact]
    public async Task AStaleWriteBesideAnEntryInFlightIsRejectedWithoutAReload()
    {
        await using var ledger = new ScriptedLedger(window: 1);
        var other = ledger.WriteAsync(Append(V(0), key: KeyOf("other")));
        var admitted = ledger.WriteAsync(Append(V(0)));
        Assert.Equal(2, ledger.Loads);
        _ = Assert.Single(ledger.Proposals);

        Assert.Equal(Rejected(V(0)), await AnsweredAsync(ledger.WriteAsync(Append(V(5)))));
        Assert.Equal(2, ledger.Loads);

        ledger.Durable(0);
        ledger.Durable(1);
        Assert.Equal(Accepted(V(1)), await AnsweredAsync(other));
        Assert.Equal(Accepted(V(1)), await AnsweredAsync(admitted));
        ledger.CheckVersions();
    }

    /// <summary>
    /// JS-I6's per-row-key count catches the mutant which evicts a key with entries in flight (<c>evict-with-inflight</c>):
    /// in a Debug build, the count's assertion fails where the key is evicted. Release builds compile the count away.
    /// </summary>
    [Fact]
    public async Task EvictingAKeyWithEntriesInFlightFailsTheCountsAssertion()
    {
        Assert.SkipUnless(TestDiagnostics.Configuration == "Debug", "Release builds compile JS-I6's per-row-key count away.");
        var test = TestContext.Current.Test;
        var since = DebugAssertions.RaisedCount;
        await using var ledger = new ScriptedLedger(options: new JournalStoreOptions { WarmStateBytes = 0 });
        ledger.Admission.EvictionMutation = LedgerAdmission.EvictionMutant.EvictWithInFlight;
        try
        {
            _ = ledger.WriteAsync(Append(V(0)));
        }
        catch (DebugAssertionException)
        {
            // The assertion fails at its site, within the write's admission, and is recorded.
        }

        var started = TimeProvider.System.GetTimestamp();
        while (DebugAssertions.FirstRaisedBy(test, since) is null)
        {
            Assert.True(TimeProvider.System.GetElapsedTime(started) < Timeout, "The eviction raised a debug assertion.");
            await Task.Delay(TimeSpan.FromMilliseconds(1), TimeProvider.System, TestContext.Current.CancellationToken);
        }

        Assert.Contains(DebugAssertions.TakeRaisedBy(test, since), static raised => raised.Message.Contains("evicted only with nothing in flight", StringComparison.Ordinal));
    }

    /// <summary>
    /// An owner read's wait for its observed tail ends once every <c>Durable</c> batch of the admission below it is applied
    /// (journals.md §8.2), and ends <see langword="false"/> when the admission fences first. With no batch of the admission
    /// outstanding, a wait ends at once; once the admission is fenced, every wait ends <see langword="false"/>, whatever it
    /// applied.
    /// </summary>
    [Fact]
    public async Task AWaitForAppliedBatchesEndsOnceTheyAreAppliedOrTheAdmissionFences()
    {
        await using var ledger = new ScriptedLedger();
        Assert.True(await AnsweredAsync(ledger.WaitApplied(new Slot(5))));
        var one = ledger.WriteAsync(Append(V(0)));
        var two = ledger.WriteAsync(Append(V(1)));
        var applied = ledger.WaitApplied(new Slot(1));
        var fenced = ledger.WaitApplied(new Slot(2));
        Assert.False(applied.IsCompleted);
        Assert.Equal(2, ledger.Admission.ApplyWaiters);

        ledger.Durable(0);
        Assert.True(await AnsweredAsync(applied));
        Assert.False(fenced.IsCompleted);

        ledger.Fenced(1);
        Assert.False(await AnsweredAsync(fenced));
        Assert.Equal(Accepted(V(1)), await AnsweredAsync(one));
        Assert.Equal(Fenced, await AnsweredAsync(two));
        Assert.False(await AnsweredAsync(ledger.WaitApplied(new Slot(0))));
        Assert.Equal(0, ledger.Admission.ApplyWaiters);
    }

    /// <summary>
    /// A tail past slots which are not the admission's, such as a writer's segment markers, needs no batch there: the wait
    /// ends once every batch the admission took before it is answered, though the admission applied below the tail.
    /// </summary>
    [Fact]
    public async Task AWaitPastSlotsNotTheAdmissionsEndsOnceItsBatchesAreAnswered()
    {
        await using var ledger = new ScriptedLedger();
        var write = ledger.WriteAsync(Append(V(0)));
        var waiting = ledger.WaitApplied(new Slot(9));
        Assert.False(waiting.IsCompleted);

        ledger.Durable(0);

        Assert.True(await AnsweredAsync(waiting));
        Assert.Equal(new Slot(1), ledger.Admission.AppliedBelow);
        Assert.Equal(Accepted(V(1)), await AnsweredAsync(write));
    }

    /// <summary>A wait whose caller cancels it ends with the cancellation and leaves the admission's waiters.</summary>
    [Fact]
    public async Task ACancelledWaitLeavesTheAdmissionsWaiters()
    {
        await using var ledger = new ScriptedLedger();
        var write = ledger.WriteAsync(Append(V(0)));
        using var cancellation = new CancellationTokenSource();
        var waiting = ledger.Admission.WaitAppliedAsync(new Slot(1), cancellation.Token);
        Assert.Equal(1, ledger.Admission.ApplyWaiters);

        await cancellation.CancelAsync();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(Timeout, TimeProvider.System, TestContext.Current.CancellationToken));
        Assert.Equal(0, ledger.Admission.ApplyWaiters);
        ledger.Durable(0);
        Assert.Equal(Accepted(V(1)), await AnsweredAsync(write));
    }

    /// <summary>
    /// A load whose read is not the admission's to answer, as one the map moved past mid-load, answers every write waiting
    /// for it <c>Moved</c> with the owner the loader names; nothing is proposed, and the key's next write loads again.
    /// </summary>
    [Fact]
    public async Task ALoadAnsweredMovedAnswersItsWaitingWritesMovedAndTheNextWriteLoadsAgain()
    {
        await using var ledger = new ScriptedLedger();
        var owner = new NodeId(new Guid("00000000-0000-0000-0000-0000000000b0"));
        var load = new TaskCompletionSource<JournalLoadOutcome>();
        ledger.LoadsOutcomeFrom(Key, token => new ValueTask<JournalLoadOutcome>(load.Task.WaitAsync(token)));
        var first = ledger.WriteAsync(Append(V(0)));
        var second = ledger.WriteAsync(Append(V(1)));

        load.SetResult(new JournalLoadOutcome(new JournalMoved(owner)));

        var moved = new JournalWriteOutcome(new JournalMoved(owner));
        Assert.Equal(moved, await AnsweredAsync(first));
        Assert.Equal(moved, await AnsweredAsync(second));
        Assert.Empty(ledger.Proposals);
        ledger.LoadsFrom(Key, _ => ValueTask.FromResult<JournalState?>(null));
        var again = ledger.WriteAsync(Append(V(0)));
        Assert.Equal(2, ledger.Loads);
        ledger.Durable(0);
        Assert.Equal(Accepted(V(1)), await AnsweredAsync(again));
    }

    /// <summary>A key's first touch loads its state once for every write which waits for it, which are then decided in order.</summary>
    [Fact]
    public async Task OneLoadServesEveryWriteWaitingForIt()
    {
        await using var ledger = new ScriptedLedger();
        var load = new TaskCompletionSource<JournalState?>();
        ledger.LoadsFrom(Key, _ => new ValueTask<JournalState?>(load.Task));
        var one = ledger.WriteAsync(Append(V(0)));
        var two = ledger.WriteAsync(Append(V(1)));
        Assert.Empty(ledger.Proposals);

        load.SetResult(null);

        Assert.Equal(1, ledger.Loads);
        Assert.Equal([1UL, 2UL], ledger.Proposal(0).Counts);
        ledger.Durable(0);
        Assert.Equal(Accepted(V(1)), await AnsweredAsync(one));
        Assert.Equal(Accepted(V(2)), await AnsweredAsync(two));
    }

    /// <summary>
    /// A loaded state's version is the key's, of whatever base: a rebased state at <c>(VersionBase, 0)</c> admits an entry
    /// at that version and no other (JS-I10).
    /// </summary>
    [Fact]
    public async Task ALoadedRebasedStateAdmitsOnlyItsOwnVersion()
    {
        await using var ledger = new ScriptedLedger();
        using var prefix = new ArchivePrefix(default, new RowToken([0xab]), hasBase: true, null, default, null);
        ledger.LoadsFrom(Key, _ => ValueTask.FromResult(JournalState.Fold(prefix, [])));

        Assert.Equal(Rejected(V(0xab, 0)), await AnsweredAsync(ledger.WriteAsync(Append(V(0)))));
        var write = ledger.WriteAsync(Append(V(0xab, 0)));
        Assert.Equal([V(0xab, 1)], ledger.Proposal(0).Rows.Select(static row => row.Version));
        ledger.Durable(0);
        Assert.Equal(Accepted(V(0xab, 1)), await AnsweredAsync(write));
    }

    /// <summary>A load which fails fails every write waiting for it, and the key's next write loads again.</summary>
    [Fact]
    public async Task AFailedLoadFailsEveryWriteWaitingForItAndTheNextWriteLoadsAgain()
    {
        await using var ledger = new ScriptedLedger();
        var load = new TaskCompletionSource<JournalState?>();
        ledger.LoadsFrom(Key, _ => ledger.Loads == 1 ? new ValueTask<JournalState?>(load.Task) : ValueTask.FromResult<JournalState?>(null));
        var one = ledger.WriteAsync(Append(V(0)));
        var two = ledger.WriteAsync(Append(V(1)));

        load.SetException(new IOException("unavailable"));

        _ = await Assert.ThrowsAsync<IOException>(() => AnsweredAsync(one));
        _ = await Assert.ThrowsAsync<IOException>(() => AnsweredAsync(two));
        var write = ledger.WriteAsync(Append(V(0)));
        ledger.Durable(0);
        Assert.Equal(Accepted(V(1)), await AnsweredAsync(write));
        Assert.Equal(2, ledger.Loads);
    }

    /// <summary>
    /// The writer's disposal before a proposal took a slot appends nothing (ledgers.md §2.4): the admission answers that
    /// batch's entries <c>Fenced</c>, fences every entry waiting and every later write, and proposes nothing more.
    /// </summary>
    [Fact]
    public async Task AWriterDisposedBeforeAProposalFencesTheAdmission()
    {
        await using var ledger = new ScriptedLedger(window: 1);
        var b = KeyOf("b");
        var first = ledger.WriteAsync(Append(V(0)));
        ledger.NextProposeFailure = new ObjectDisposedException("LedgerWriter");
        var refused = ledger.WriteAsync(Append(V(0), key: b));
        var waiting = ledger.WriteAsync(Append(V(0), "again", b));

        ledger.Durable(0);

        Assert.Equal(Accepted(V(1)), await AnsweredAsync(first));
        Assert.Equal(Fenced, await AnsweredAsync(refused));
        Assert.Equal(Fenced, await AnsweredAsync(waiting));
        Assert.Equal(Fenced, await AnsweredAsync(ledger.WriteAsync(Append(V(1)))));
        _ = Assert.Single(ledger.Proposals);
        Assert.Equal(0, ledger.Abandoned);
    }

    /// <summary>
    /// A proposal ended by the writer's disposal after it took a slot may have landed, so its entries are
    /// <c>Ambiguous</c> (JS-I4), and the admission fences.
    /// </summary>
    [Fact]
    public async Task AProposalEndedByTheWritersDisposalIsAmbiguous()
    {
        await using var ledger = new ScriptedLedger();
        ledger.NextOutcomeFailure = new ObjectDisposedException("LedgerWriter");

        var write = ledger.WriteAsync(Append(V(0)));

        Assert.Equal(Ambiguous, await AnsweredAsync(write));
        Assert.Equal(Fenced, await AnsweredAsync(ledger.WriteAsync(Append(V(0)))));
        Assert.Equal(0, ledger.Abandoned);
    }

    /// <summary>
    /// A fault of the admission's own while it builds or proposes a batch, or in its proposal's outcome, fails that
    /// batch's entries with the fault, which asserts nothing; fences every later write; abandons the writer, so a takeover
    /// replaces the ledger; and raises a debug assertion. The admission proposes nothing after it.
    /// </summary>
    [Theory]
    [InlineData(Fault.CreateBatch)]
    [InlineData(Fault.Propose)]
    [InlineData(Fault.Outcome)]
    public async Task AFaultOfTheAdmissionsOwnFailsItsBatchAndAbandonsTheWriter(Fault fault)
    {
        var since = DebugAssertions.RaisedCount;
        var injected = new InvalidOperationException("Injected fault.");
        await using var ledger = new ScriptedLedger();
        switch (fault)
        {
            case Fault.CreateBatch:
                ledger.NextCreateBatchFailure = injected;
                break;
            case Fault.Propose:
                ledger.NextProposeFailure = injected;
                break;
            case Fault.Outcome:
                ledger.NextOutcomeFailure = injected;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(fault));
        }

        var write = ledger.WriteAsync(Append(V(0)));

        Assert.Same(injected, await Assert.ThrowsAsync<InvalidOperationException>(() => AnsweredAsync(write)));
        Assert.Equal(Fenced, await AnsweredAsync(ledger.WriteAsync(Append(V(0)))));
        Assert.Equal(1, ledger.Abandoned);
        Assert.Equal(fault == Fault.Outcome ? 1 : 0, ledger.Proposals.Count);
        await TakeDefectAssertionAsync(since);
    }

    /// <summary>
    /// A fault of the archive store's folds is one of the admission's own: thrown while it decides an entry, as candidate
    /// 2 begins a write from the joined write's <c>Plan</c>, the entry fails with it; thrown while it plans a batch's
    /// tokens, as a roll begins the folds, the batch's entries fail with it (journals.md §8.1). Either way the admission
    /// fences every later write, abandons the writer, and raises a debug assertion.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFaultOfTheStoresFoldsFailsItsEntryAndAbandonsTheWriter(bool planning)
    {
        var since = DebugAssertions.RaisedCount;
        var memory = new InMemoryArchiveStore();
        var store = new ThrowingStore(memory);
        await using var ledger = new ScriptedLedger(store: store, memory: memory);
        Task<JournalWriteOutcome> write;
        if (planning)
        {
            // The key's first entry joins an empty fold, which begins no write; its batch rolls, which begins both.
            ledger.LoadsFrom(Key, _ =>
            {
                var load = new JournalLoad(null, JournalPlanning.Unrecorded(store, default));
                store.Throws = true;
                return ValueTask.FromResult(load);
            });
            write = ledger.WriteAsync(Replace(V(0)));
        }
        else
        {
            var first = ledger.WriteAsync(Append(V(0)));
            ledger.Durable(0);
            Assert.Equal(Accepted(V(1)), await AnsweredAsync(first));
            store.Throws = true;
            write = ledger.WriteAsync(Append(V(1)));
        }

        Assert.Same(store.Injected, await Assert.ThrowsAsync<InvalidOperationException>(() => AnsweredAsync(write)));
        store.Throws = false;
        Assert.Equal(Fenced, await AnsweredAsync(ledger.WriteAsync(Append(V(0), key: KeyOf("other")))));
        Assert.Equal(1, ledger.Abandoned);
        Assert.Equal(planning ? 0 : 1, ledger.Proposals.Count);
        await TakeDefectAssertionAsync(since);
    }

    /// <summary>
    /// Warm state which no longer folds the ledger's decided rows is a defect which stops the process (conventions.md
    /// §14): here a loader keeps and changes the state it handed over, so a <c>Durable</c> entry does not apply. The
    /// entry and every later write fail with the corruption.
    /// </summary>
    [Fact]
    public async Task WarmStateWhichLeftTheLogsFoldStopsTheProcess()
    {
        var since = FatalErrors.ReportedCount;
        await using var ledger = new ScriptedLedger();
        var state = JournalState.Apply(null, Entry(JournalEntryKind.Append, V(0), payloadLength: 1), V(1));
        ledger.LoadsFrom(Key, _ => ValueTask.FromResult<JournalState?>(state));
        var write = ledger.WriteAsync(Append(V(1)));
        _ = JournalState.Apply(state, Entry(JournalEntryKind.Append, V(1), payloadLength: 1), V(2));

        ledger.Durable(0);

        _ = await Assert.ThrowsAsync<JournalStoreCorruptException>(() => AnsweredAsync(write));
        _ = await Assert.ThrowsAsync<JournalStoreCorruptException>(() => AnsweredAsync(ledger.WriteAsync(Append(V(0), key: KeyOf("other")))));
        Assert.Equal("JournalsWarmState", Assert.Single(FatalErrors.TakeMine(since)).Failure.Defect);
    }

    /// <summary>
    /// Disposal answers every entry it has not proposed <c>Fenced</c>, including those waiting for their key's load, and
    /// waits for the outstanding proposals to be decided.
    /// </summary>
    [Fact]
    public async Task DisposalFencesWhatItHasNotProposedAndWaitsForTheOutstandingProposals()
    {
        await using var ledger = new ScriptedLedger(window: 1);
        var load = new TaskCompletionSource<JournalState?>();
        ledger.LoadsFrom(KeyOf("slow"), token => new ValueTask<JournalState?>(load.Task.WaitAsync(token)));
        var proposed = ledger.WriteAsync(Append(V(0)));
        var queued = ledger.WriteAsync(Append(V(1)));
        var loading = ledger.WriteAsync(Append(V(0), key: KeyOf("slow")));

        var disposal = ledger.Admission.DisposeAsync().AsTask();

        Assert.Equal(Fenced, await AnsweredAsync(queued));
        Assert.Equal(Fenced, await AnsweredAsync(loading));
        Assert.False(disposal.IsCompleted);
        ledger.Durable(0);
        await disposal.WaitAsync(Timeout, TimeProvider.System, TestContext.Current.CancellationToken);
        Assert.Equal(Accepted(V(1)), await AnsweredAsync(proposed));
        Assert.Equal(Fenced, await AnsweredAsync(ledger.WriteAsync(Append(V(1)))));
    }

    /// <summary>A caller's cancellation stops its wait and asserts nothing: the admitted entry is decided as usual (journals.md §2.2).</summary>
    [Fact]
    public async Task CancellationStopsTheCallerWaitingAndTheEntryIsDecidedAsUsual()
    {
        await using var ledger = new ScriptedLedger();
        using var cancellation = new CancellationTokenSource();
        var write = ledger.WriteCancellablyAsync(Append(V(0)), cancellation.Token);

        await cancellation.CancelAsync();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write.WaitAsync(Timeout, TimeProvider.System, TestContext.Current.CancellationToken));

        ledger.Durable(0);
        Assert.Equal(Rejected(V(1)), await AnsweredAsync(ledger.WriteAsync(Append(V(0)))));
    }

    /// <summary>
    /// A write whose caller disposes its <see cref="JournalEntryRecord"/> as soon as <see cref="LedgerAdmission.AdmitAsync"/>
    /// returns keeps its multi-page payload intact while queued behind a full window, and releases it without leaking when
    /// another queued entry is fenced before proposal.
    /// </summary>
    [Fact]
    public async Task ABorrowedPayloadSurvivesCallerDisposalAcrossQueuedAndFencedPaths()
    {
        await using var ledger = new ScriptedLedger(window: 1);
        var bytes = new byte[ArcBufferWriter.MinimumPageSize * 3 + 29];
        new Random(31).NextBytes(bytes);
        var keyB = KeyOf("b");
        var keyC = KeyOf("c");

        // Occupy the single in-flight slot so the next two writes queue behind it and must retain their borrowed payloads.
        var first = ledger.WriteAsync(Append(V(0)));
        Task<JournalWriteOutcome> queuedDurable;
        using (var paged = PagesOf(bytes))
        using (var record = JournalEntryRecord.Append(keyB, V(0), paged))
        {
            queuedDurable = ledger.Admit(record);
        }

        Task<JournalWriteOutcome> queuedFenced;
        using (var paged = PagesOf(bytes))
        using (var record = JournalEntryRecord.Append(keyC, V(0), paged))
        {
            queuedFenced = ledger.Admit(record);
        }

        // Complete batch 0 so batch 1 (keyB + keyC) is built from the retained payloads after caller disposal.
        ledger.Durable(0);
        var secondProposal = ledger.Proposal(1);
        Assert.Equal(2, secondProposal.Rows.Count);
        Assert.Equal(bytes, secondProposal.Rows[0].Body);
        Assert.Equal(bytes, secondProposal.Rows[1].Body);

        // Queue one more retained payload behind batch 1, then fence batch 1 so the queued entry is fenced without leaking.
        Task<JournalWriteOutcome> fencedWhileQueued;
        using (var paged = PagesOf(bytes))
        using (var record = JournalEntryRecord.Append(KeyOf("d"), V(0), paged))
        {
            fencedWhileQueued = ledger.Admit(record);
        }

        ledger.Fenced(1);

        Assert.Equal(Accepted(V(1)), await AnsweredAsync(first));
        Assert.Equal(Fenced, await AnsweredAsync(queuedDurable));
        Assert.Equal(Fenced, await AnsweredAsync(queuedFenced));
        Assert.Equal(Fenced, await AnsweredAsync(fencedWhileQueued));
    }

    /// <summary>
    /// Admission transitions cleanly between the uncontended inline single-entry fast path, a single queued entry in the
    /// inline queue slot, and multi-entry queue/batch spill, preserving exact admission order and versions.
    /// </summary>
    [Fact]
    public async Task InlineSingleEntryFastPathTransitionsCleanlyToMultiEntryQueueAndBatch()
    {
        await using var ledger = new ScriptedLedger(window: 1, options: new JournalStoreOptions { MaxBatchEntries = 4 });

        // Uncontended single-entry batch (inline fast path).
        var single = ledger.WriteAsync(Append(V(0), "s0"));
        ledger.Durable(0);
        Assert.Equal(Accepted(V(1)), await AnsweredAsync(single));

        // Hold window with one entry, then queue 1 (inline slot) and 2 more (spills to Queue<Pending> and Pending[]).
        var holding = ledger.WriteAsync(Append(V(1), "s1"));
        var q1 = ledger.WriteAsync(Append(V(2), "s2"));
        var q2 = ledger.WriteAsync(Append(V(3), "s3"));
        var q3 = ledger.WriteAsync(Append(V(4), "s4"));

        ledger.Durable(1);
        Assert.Equal([3UL, 4UL, 5UL], ledger.Proposal(2).Counts);
        ledger.Durable(2);

        Assert.Equal(Accepted(V(2)), await AnsweredAsync(holding));
        Assert.Equal(Accepted(V(3)), await AnsweredAsync(q1));
        Assert.Equal(Accepted(V(4)), await AnsweredAsync(q2));
        Assert.Equal(Accepted(V(5)), await AnsweredAsync(q3));
        ledger.CheckVersions();
    }

    private static JournalWriteOutcome Accepted(JournalVersion version, bool replaceRequested = true) => new(new JournalWriteOutcome.Accepted(version, ReplaceRequested: replaceRequested));

    private static JournalWriteOutcome Rejected(JournalVersion currentVersion) => new(new JournalWriteOutcome.Rejected(currentVersion));

    private static JournalRowKey KeyOf(string journal) => JournalRowKey.Encode(journal);

    /// <summary>
    /// Returns the length admission counts for a row of <paramref name="key"/> at an <c>Origin</c> version with a body of
    /// <paramref name="bodyLength"/> bytes: a defining row with the empty base and a token at its longest, which admission
    /// reserves, since a batch plans the token only once it holds the entry.
    /// </summary>
    private static long RowLength(JournalRowKey key, int bodyLength) => LedgerBatch.MaxRowLength(key.Length, 0, RowToken.MaxLength, bodyLength);

    /// <summary>Returns the outcome of a write the admission has answered, which completed when it answered.</summary>
    private static async Task<T> AnsweredAsync<T>(Task<T> write)
    {
        Assert.True(write.IsCompleted, "The admission answered the write.");
        return await write;
    }

    /// <summary>Returns <paramref name="bytes"/> in a buffer of several pages; the caller disposes it.</summary>
    private static ArcBuffer PagesOf(byte[] bytes)
    {
        using var writer = new ArcBufferWriter();
        writer.Write(bytes);
        return writer.ConsumeSlice(writer.Length);
    }

    /// <summary>A store with a memory store's members whose <c>Begin</c> throws while the test says so.</summary>
    private sealed class ThrowingStore(InMemoryArchiveStore inner) : IArchiveStore
    {
        public InvalidOperationException Injected { get; } = new("Injected fault of the store's folds.");

        public bool Throws { get; set; }

        public ArchiveStoreKind Kind => inner.Kind;

        public int? MaxAppendBytes => inner.MaxAppendBytes;

        public ValueTask<ArchiveStoreRead> ReadAsync(ReadOnlyMemory<byte> key, LedgerMapValue map, CancellationToken cancellationToken) => inner.ReadAsync(key, map, cancellationToken);

        public ValueTask<ArchiveStoreRecord> StoreRecordAsync(IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken) => inner.StoreRecordAsync(parameters, cancellationToken);

        public ArchivePlan Begin(RowToken token) => Throws ? throw Injected : inner.Begin(token);

        public bool Holds(ArchivePrefix prefix, ArchiveWrite write, int count) => inner.Holds(prefix, write, count);

        public bool IsReplaceRequested(RowToken token) => inner.IsReplaceRequested(token);

        public bool AdmitsKey(ReadOnlySpan<byte> key) => inner.AdmitsKey(key);
    }

    /// <summary>
    /// Takes the debug assertion a fault of the admission's own raises on a worker of its own, in a Debug build, once it is
    /// raised.
    /// </summary>
    private static async Task TakeDefectAssertionAsync(long since)
    {
        Assert.SkipUnless(TestDiagnostics.Configuration == "Debug", "Release builds compile JS-I6's per-row-key count away.");
        var test = TestContext.Current.Test;
        var started = TimeProvider.System.GetTimestamp();
        while (DebugAssertions.FirstRaisedBy(test, since) is null)
        {
            Assert.True(TimeProvider.System.GetElapsedTime(started) < Timeout, "The fault raised a debug assertion.");
            await Task.Delay(TimeSpan.FromMilliseconds(1), TimeProvider.System, TestContext.Current.CancellationToken);
        }

        Assert.Contains("fault of its own", Assert.Single(DebugAssertions.TakeRaisedBy(test, since)).Message, StringComparison.Ordinal);
    }
}
