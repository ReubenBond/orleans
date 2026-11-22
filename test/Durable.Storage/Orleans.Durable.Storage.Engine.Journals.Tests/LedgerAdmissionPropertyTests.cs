using CsCheck;
using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Archive.Memory;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Testing;
using static Orleans.Durable.Storage.Engine.Journals.Tests.JournalSamples;

namespace Orleans.Durable.Storage.Engine.Journals.Tests;

/// <summary>
/// A model-based property of admission (journals.md §8.1; JS-I1, JS-I2, JS-I4, JS-I6, JS-I7): writes to two keys, at
/// expected versions at or near each key's current one or of another base, and the expiry scan's <c>Expired</c>s, current
/// and stale, against a scripted ledger whose proposals the property decides <c>Durable</c> in order, interleaved with the
/// writes, with advances of the admission's clock, and with segment ends, under a warm-state budget which keeps every key
/// or evicts each idle one, so keys reload mid-segment.
/// </summary>
/// <remarks>
/// The admission runs inline, so the property knows the cut at which each write was answered: the number of proposals
/// decided. Once every proposal is decided, every write has its one answer (the progress check). The oracle is the
/// accepted writes alone, written here apart from the code: each key's accepted entries chain from <c>Origin</c>, each at
/// the version after its predecessor's; an accepted <c>Expired</c> follows an accepted <c>Delete</c> whose stamp plus the
/// retention is at or before the clock when it was issued, with nothing of its key in flight then. The proposed rows of
/// each key, in tag order, are exactly the accepted entries in write order, each a row of its entry's kind at the version
/// after its expected one; each accepted entry was answered only once its proposal was decided (JS-I2), with its row's
/// version; each rejection reports the state the accepted entries decided by its cut give, which the fold of the rows
/// agrees with, and its entry applies to none of the states possible then: that state, and the state after each prefix
/// of the key's in-flight entries, an <c>Expired</c> only to the first with nothing in flight and its retention passed
/// (JS-I1). Warm state equals the fold of the history, which final probes check: a write of another base is rejected
/// with the fold's <c>CurrentVersion</c>, and a key whose fold is deleted expires at that version once its retention
/// passes. <c>Delete</c>s are stamped at the clock's time, and the retention is two days, which the clock's advances of
/// one to four days cross, sometimes exactly. The memory store limits an append run to one append per generation, so
/// <c>Append</c>s meet its limits: a white-box oracle checks each <c>ReplaceRequired</c> (JS-I4), which came with nothing
/// of its key in flight at its cut, for an <c>Append</c> one of whose two candidate writes the store refuses
/// (journals.md §8.1 step 4): the key's write in the segment of its last decided row, and an empty write from that
/// write's <c>Plan</c>. Each key's rows follow one another, and every <c>Durable</c> row drains into the store, which
/// checks each key's token chain and its limits (HI-I4). Mutants which answer <c>ReplaceRequired</c> with entries in
/// flight, or when both candidates admit, fail the oracle, each with its own message.
/// </remarks>
public sealed class LedgerAdmissionPropertyTests : IDisposable
{
    private static readonly JournalRowKey[] Keys = [JournalRowKey.Encode("a"), JournalRowKey.Encode("b")];
    private static readonly JournalVersion OtherBase = V(0xee, 0);
    private static readonly TimeSpan Retention = TimeSpan.FromDays(2);

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    private enum Step
    {
        Append,
        Replace,
        Delete,
        Expire,
        ExpireStale,
        Decide,
        Advance,
        EndSegment,
    }

    public void Dispose() => _leaks.Dispose();

    [Fact]
    public Task EveryWriteIsAnsweredOnceAndTheAcceptedEntriesAreTheFoldOfTheHistory() => CheckPropertyAsync(LedgerAdmission.ReplaceRequiredMutant.None);

    /// <summary>
    /// Regression for #422: a warm-rejected <c>Expired</c> (while an entry of its key is in flight) followed, after
    /// eviction, by a cold-rejected <c>Expired</c> at the same expected version must each be checked against its own
    /// rejection kind rather than matching the later cold rejection to the earlier warm write.
    /// </summary>
    [Fact]
    public Task WarmThenColdExpiredRejectionAtTheSameExpectedVersionIsMatchedInOrder() =>
        _leaks.InScopeAsync(() => CheckAsync(
            window: 1,
            evict: true,
            [
                (Step.Delete, 0, 0),
                (Step.Decide, 0, 0),
                (Step.Delete, 0, 0),
                (Step.ExpireStale, 0, -1),
                (Step.Decide, 0, 0),
                (Step.Expire, 0, 0),
            ],
            LedgerAdmission.ReplaceRequiredMutant.None));

    /// <summary>Each mutant of <c>ReplaceRequired</c> fails the property, by its own check of the white-box oracle (journals.md §10).</summary>
    [Theory]
    [InlineData(nameof(LedgerAdmission.ReplaceRequiredMutant.AnswersWithEntriesInFlight), "was in flight")]
    [InlineData(nameof(LedgerAdmission.ReplaceRequiredMutant.AnswersWhenBothAdmit), "both candidate writes admit")]
    public async Task EachReplaceRequiredMutantFailsTheOracle(string mutant, string check)
    {
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => CheckPropertyAsync(Enum.Parse<LedgerAdmission.ReplaceRequiredMutant>(mutant)));

        Assert.Contains(check, failure.ToString(), StringComparison.Ordinal);
    }

    private Task CheckPropertyAsync(LedgerAdmission.ReplaceRequiredMutant mutant) =>
        Gen.Select(Gen.Int[1, 3], Gen.Bool, Gen.Select(Gen.Enum<Step>(), Gen.Int[0, 1], Gen.OneOf(Gen.Const(0), Gen.Int[-1, 2])).Array[1, 40])
            .SampleAsync((window, evict, steps) => _leaks.InScopeAsync(() => CheckAsync(window, evict, steps, mutant)), iter: 200, threads: 1);

    private static async Task CheckAsync(int window, bool evict, (Step Step, int Key, int Offset)[] steps, LedgerAdmission.ReplaceRequiredMutant mutant)
    {
        var options = new JournalStoreOptions { MaxBatchEntries = 3, TombstoneRetention = Retention };
        if (evict)
        {
            options.WarmStateBytes = 0;
        }

        await using var ledger = new ScriptedLedger(window, options: options, memory: new InMemoryArchiveStore(maxAppends: 1, replaceRequestAppends: 1)) { StartEachSegment = false };
        ledger.Admission.ReplaceRequiredMutation = mutant;
        var run = new Run(ledger);
        ledger.Admission.OnRejected = run.Rejected;
        foreach (var (step, key, offset) in steps)
        {
            run.Take(step, key, offset);
        }

        run.DecideUntilAnswered();
        for (var key = 0; key < Keys.Length; key++)
        {
            await run.AssertKeyAsync(key);
        }

        ledger.CheckVersions();
        ledger.DrainDurable();
    }

    /// <summary>
    /// Returns whether an entry applies to a possible state (journals.md §2.1, §8.1), written apart from the code under
    /// test: by version equality, and an <c>Expired</c> only to the applied state, deleted, with nothing in flight, once
    /// its tombstone's stamp plus the retention is at or before <paramref name="now"/>.
    /// </summary>
    private static bool AppliesTo(Write entry, PossibleJournal state, bool appliedWithNothingInFlight, long now) => entry.Kind == JournalEntryKind.Expired
        ? appliedWithNothingInFlight && state.DeletionTicks is { } deleted && state.Version == entry.Expected && deleted + Retention.Ticks <= now
        : state.Version == entry.Expected;

    /// <summary>A write the property made: its key, fields, outcome, when it was issued, and when it was answered.</summary>
    private sealed class Write(int key, JournalEntryKind kind, JournalVersion expected, long? deletionTicks, int issuedAt, long issuedTicks, Task<JournalWriteOutcome> outcome, int payloadLength)
    {
        /// <summary>Gets the length of an <c>Append</c>'s or a <c>Replace</c>'s payload.</summary>
        public int PayloadLength { get; } = payloadLength;

        public int Key { get; } = key;

        public JournalEntryKind Kind { get; } = kind;

        public JournalVersion Expected { get; } = expected;

        /// <summary>Gets a <c>Delete</c>'s stamp; null for every other kind.</summary>
        public long? DeletionTicks { get; } = deletionTicks;

        /// <summary>Gets the number of proposals decided when the write was issued.</summary>
        public int IssuedAt { get; } = issuedAt;

        /// <summary>Gets the admission's clock when the write was issued.</summary>
        public long IssuedTicks { get; } = issuedTicks;

        public Task<JournalWriteOutcome> Outcome { get; } = outcome;

        /// <summary>Gets or sets the number of proposals decided when the write was answered.</summary>
        public int? AnsweredAt { get; set; }

        /// <summary>Gets or sets the admission's clock when the write was answered, which a clock's advance answers nothing at.</summary>
        public long AnsweredTicks { get; set; }
    }

    /// <summary>A state an entry may meet: a <c>CurrentVersion</c>, and its tombstone's stamp when it is deleted.</summary>
    private readonly record struct PossibleJournal(JournalVersion Version, long? DeletionTicks);

    /// <summary>One run of the property: its ledger, its writes, and the proposals decided.</summary>
    private sealed class Run(ScriptedLedger ledger)
    {
        private readonly List<Write> _writes = [];
        private readonly Dictionary<(JournalRowKey Key, JournalVersion Expected), Queue<bool>> _expiredRejections = [];
        private readonly ulong[] _highest = new ulong[Keys.Length];
        private int _decided;

        public void Take(Step step, int key, int offset)
        {
            var expected = V((ulong)Math.Max(0, (long)_highest[key] + offset));
            var now = ledger.Clock.GetUtcNow().UtcTicks;
            var payload = $"{key}{expected.Count}";
            switch (step)
            {
                case Step.Append:
                    Add(key, JournalEntryKind.Append, expected, null, ledger.WriteAsync(JournalSamples.Append(expected, payload, Keys[key])), payload.Length);
                    break;
                case Step.Replace:
                    Add(key, JournalEntryKind.Replace, expected, null, ledger.WriteAsync(JournalSamples.Replace(expected, payload, Keys[key])), payload.Length);
                    break;
                case Step.Delete:
                    Add(key, JournalEntryKind.Delete, expected, now, ledger.WriteAsync(JournalSamples.Delete(expected, now, Keys[key])));
                    break;
                case Step.Expire when LastDecidedDelete(key) is { } deleted:
                    Add(key, JournalEntryKind.Expired, deleted, null, ledger.ExpireAsync(deleted, Keys[key]));
                    break;
                case Step.ExpireStale when LastDecidedDelete(key) is { } deleted:
                    var stale = offset < 0 ? deleted.Next() : new JournalVersion(new RowToken([0xee]), deleted.Count);
                    Add(key, JournalEntryKind.Expired, stale, null, ledger.ExpireAsync(stale, Keys[key]));
                    break;
                case Step.Expire or Step.ExpireStale:
                    break;
                case Step.Decide when ledger.Proposals.Count > _decided:
                    Decide();
                    break;
                case Step.Decide:
                    break;
                case Step.Advance:
                    ledger.Clock.Advance(TimeSpan.FromDays(offset + 2));
                    break;
                case Step.EndSegment:
                    ledger.EndSegment();
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(step));
            }

            if (step is Step.Append or Step.Replace or Step.Delete)
            {
                _highest[key] = Math.Max(_highest[key], expected.Count + 1);
            }
        }

        /// <summary>
        /// Decides every proposal until every write is answered: an admission which left a write waiting with nothing in
        /// flight would make no further proposal, and the property would fail here.
        /// </summary>
        public void DecideUntilAnswered()
        {
            while (_writes.Any(static write => write.AnsweredAt is null))
            {
                Assert.True(ledger.Proposals.Count > _decided, "A write waits while nothing is in flight.");
                Decide();
            }
        }

        public async Task AssertKeyAsync(int key)
        {
            var writes = _writes.Where(write => write.Key == key).ToList();
            var landed = LandedRows(key);
            var accepted = writes.Where(static write => write.Outcome.Result.Value is JournalWriteOutcome.Accepted).ToList();
            AssertAcceptedChain(key, accepted);
            Assert.Equal(
                accepted.Select(static write => (JournalEntryKinds.RowKindOf(write.Kind), write.Expected.Next())),
                landed.Select(static row => (row.Row.Kind, row.Row.Version)));
            for (var index = 0; index < accepted.Count; index++)
            {
                Assert.True(accepted[index].AnsweredAt > landed[index].Slot, $"An entry of key {key} was accepted before its proposal {landed[index].Slot} was decided (JS-I2).");
                // Its replace request is the store's at the key's admitted token, which entries still in flight move;
                // JournalStoreTests and LedgerAdmissionPlanningTests check it.
                Assert.Equal(landed[index].Row.Version, Assert.IsType<JournalWriteOutcome.Accepted>(accepted[index].Outcome.Result.Value).Version);
            }

            var final = FoldAt(key, _decided);
            for (var index = 0; index < writes.Count; index++)
            {
                var write = writes[index];
                var outcome = write.Outcome.Result;
                var unexpected = outcome switch
                {
                    JournalWriteOutcome.Accepted => false,
                    JournalWriteOutcome.Rejected rejected when write.Kind == JournalEntryKind.Expired && TakeCold(Keys[key], write.Expected) => AssertColdRejection(write, rejected.CurrentVersion),
                    JournalWriteOutcome.Rejected rejected => AssertRejection(key, write, rejected.CurrentVersion, [.. accepted.Where(earlier => writes.IndexOf(earlier) < index)], landed),
                    JournalWriteOutcome.ReplaceRequired => AssertReplaceRequired(write, [.. accepted.Where(earlier => writes.IndexOf(earlier) < index)], landed),
                    JournalMoved or JournalWriteOutcome.Fenced or JournalWriteOutcome.Ambiguous => true,
                    null => throw new InvalidOperationException("A write's outcome holds a case."),
                };
                Assert.False(unexpected, $"A write of key {key} at {write.Expected} was answered {outcome.Value} while every proposal was Durable.");
            }

            Assert.All(_expiredRejections.Where(entry => entry.Key.Key == Keys[key]), static entry => Assert.Empty(entry.Value));

            var current = final?.CurrentVersion ?? JournalVersion.Origin;
            var probe = await ledger.WriteAsync(JournalSamples.Append(OtherBase, key: Keys[key]));
            Assert.Equal(new JournalWriteOutcome(new JournalWriteOutcome.Rejected(current)), probe);
            if (final?.DeletedAt is not null)
            {
                // An Expired never loads its key (journals.md §8.6): with no warm state, as a budget of none leaves the key
                // once the probe is answered, it is rejected at once with its own version.
                ledger.Clock.Advance(Retention);
                var warm = ledger.Admission.Warm(Keys[key]) is not null;
                var expired = ledger.ExpireAsync(current, Keys[key]);
                if (warm)
                {
                    Decide();
                }

                Assert.Equal(warm ? new JournalWriteOutcome(new JournalWriteOutcome.Accepted(current.Next(), ReplaceRequested: false)) : new JournalWriteOutcome(new JournalWriteOutcome.Rejected(current)), await expired);
            }
        }

        /// <summary>
        /// Records whether each rejected <c>Expired</c> met a cold key, in decision order: an <c>Expired</c> never waits on
        /// its key and waits only in the FIFO room queue, so rejected <c>Expired</c>s of a <c>(Key, Expected)</c> are
        /// decided in submission order.
        /// </summary>
        public void Rejected(JournalRejection rejection)
        {
            if (rejection.Kind != JournalEntryKind.Expired)
            {
                return;
            }

            var pair = (rejection.Key, rejection.Expected);
            if (!_expiredRejections.TryGetValue(pair, out var queue))
            {
                _expiredRejections[pair] = queue = new Queue<bool>();
            }

            queue.Enqueue(rejection.Cold);
        }

        /// <summary>Claims the recorded coldness of the next rejected <c>Expired</c> of <paramref name="key"/> at <paramref name="expected"/>.</summary>
        private bool TakeCold(JournalRowKey key, JournalVersion expected)
        {
            Assert.True(_expiredRejections.TryGetValue((key, expected), out var queue), $"Every rejected Expired of {key} at {expected} is recorded by OnRejected.");
            Assert.True(queue.TryDequeue(out var cold), $"Every rejected Expired of {key} at {expected} is recorded by OnRejected.");
            return cold;
        }

        /// <summary>
        /// Checks the rejection of an <c>Expired</c> whose key held no warm state (journals.md §8.6): decided from no state, it
        /// reports its own expected version.
        /// </summary>
        /// <returns><see langword="false"/>, once the check passes.</returns>
        private static bool AssertColdRejection(Write write, JournalVersion currentVersion)
        {
            Assert.Equal(write.Expected, currentVersion);
            return false;
        }

        /// <summary>
        /// Checks the accepted entries of a key, in write order, against the oracle: they chain from <c>Origin</c>, and an
        /// <c>Expired</c> follows a <c>Delete</c> whose retention passed by its issue, with nothing of its key in flight
        /// then: every earlier accepted entry decided.
        /// </summary>
        private static void AssertAcceptedChain(int key, List<Write> accepted)
        {
            var version = JournalVersion.Origin;
            for (var index = 0; index < accepted.Count; index++)
            {
                var entry = accepted[index];
                Assert.True(entry.Expected == version, $"Key {key}'s accepted entry {index} expected {entry.Expected}, not {version}, the version its predecessors leave.");
                version = entry.Expected.Next();
                if (entry.Kind != JournalEntryKind.Expired)
                {
                    continue;
                }

                Assert.True(index > 0 && accepted[index - 1].Kind == JournalEntryKind.Delete, $"Key {key}'s accepted Expired {index} follows no accepted Delete.");
                Assert.True(accepted[index - 1].DeletionTicks + Retention.Ticks <= entry.IssuedTicks, $"Key {key}'s accepted Expired {index} was issued before its tombstone's retention passed.");
                Assert.True(accepted[index - 1].AnsweredAt <= entry.IssuedAt, $"Key {key}'s accepted Expired {index} was issued while its Delete was in flight.");
            }
        }

        private void Add(int key, JournalEntryKind kind, JournalVersion expected, long? deletionTicks, Task<JournalWriteOutcome> outcome, int payloadLength = 0)
        {
            _writes.Add(new Write(key, kind, expected, deletionTicks, _decided, ledger.Clock.GetUtcNow().UtcTicks, outcome, payloadLength));
            Observe();
        }

        private void Decide()
        {
            ledger.Durable(_decided++);
            Observe();
        }

        /// <summary>Records the cut of each write answered since the last step: the admission answered it within the step.</summary>
        private void Observe()
        {
            foreach (var write in _writes)
            {
                if (write.AnsweredAt is null && write.Outcome.IsCompleted)
                {
                    write.AnsweredAt = _decided;
                    write.AnsweredTicks = ledger.Clock.GetUtcNow().UtcTicks;
                }
            }
        }

        /// <summary>Returns the version after the key's last decided row when it is a tombstoned <c>Delete</c>.</summary>
        private JournalVersion? LastDecidedDelete(int key) =>
            LandedRows(key).LastOrDefault(row => row.Slot < _decided) is { Row: { Kind: RowKind.Delete, Body.Length: JournalEntry.TombstoneLength } row } ? row.Version : null;

        /// <summary>
        /// Checks a rejection against the key's possible states at its cut: the state the accepted entries decided by then
        /// leave, which the fold of the rows decided by then agrees with, and the state after each prefix of the entries it
        /// accepted earlier which had not landed, at the clock when it was answered.
        /// </summary>
        /// <returns><see langword="false"/>, once the rejection's checks pass.</returns>
        private bool AssertRejection(int key, Write write, JournalVersion currentVersion, List<Write> acceptedEarlier, List<LandedRow> landed)
        {
            var cut = write.AnsweredAt!.Value;
            var decided = acceptedEarlier.Where((_, index) => landed[index].Slot < cut).LastOrDefault();
            var applied = new PossibleJournal(decided?.Expected.Next() ?? JournalVersion.Origin, decided?.DeletionTicks);
            var fold = FoldAt(key, cut);
            Assert.Equal(applied.Version, fold?.CurrentVersion ?? JournalVersion.Origin);
            Assert.Equal(applied.DeletionTicks, fold?.DeletedAt?.UtcTicks);
            Assert.Equal(applied.Version, currentVersion);
            var possible = new List<PossibleJournal> { applied };
            for (var index = 0; index < acceptedEarlier.Count; index++)
            {
                if (landed[index].Slot >= cut)
                {
                    var entry = acceptedEarlier[index];
                    possible.Add(new PossibleJournal(entry.Expected.Next(), entry.DeletionTicks));
                }
            }

            var nothingInFlight = possible.Count == 1;
            Assert.DoesNotContain(possible.Select((state, index) => (state, index)), possibility => AppliesTo(write, possibility.state, nothingInFlight && possibility.index == 0, write.AnsweredTicks));
            return false;
        }

        /// <summary>
        /// Checks a <c>ReplaceRequired</c> (journals.md §8.1 step 4): an <c>Append</c>, answered with no entry of its key in
        /// flight at its cut, which the store's limits refuse in one of its two candidate writes: the key's write in the
        /// segment of its last decided row grown by it, and an empty write from that write's <c>Plan</c> grown by it.
        /// </summary>
        /// <returns><see langword="false"/>, once the checks pass.</returns>
        private bool AssertReplaceRequired(Write write, List<Write> acceptedEarlier, List<LandedRow> landed)
        {
            var cut = write.AnsweredAt!.Value;
            Assert.Equal(JournalEntryKind.Append, write.Kind);
            for (var index = 0; index < acceptedEarlier.Count; index++)
            {
                Assert.False(landed[index].Slot >= cut, $"ReplaceRequired at cut {cut} while the entry at {acceptedEarlier[index].Expected} was in flight.");
            }

            var decided = landed.Where(row => row.Slot < cut).ToList();
            var last = decided.Count == 0 ? (LedgerSegmentId?)null : decided[^1].Row.Segment;
            var segment = decided.Where(row => row.Row.Segment == last).Select(static row => Live(row.Tag, row.Row.Kind, row.Row.Version, row.Row.Body, row.Row.Token, row.Row.Segment)).ToArray();
            try
            {
                var store = ledger.Store;
                var fold = store.Begin(segment.Length == 0 ? default : segment[0].Token);
                if (segment.Length > 0)
                {
                    foreach (var command in ArchiveWrite.OfRows(segment).Commands)
                    {
                        fold.Add(command);
                    }
                }

                var append = ArchiveCommand.Append(write.PayloadLength);
                Assert.False(fold.Admits(append) && store.Begin(fold.End()).Admits(append), $"ReplaceRequired at cut {cut} for an Append both candidate writes admit.");
            }
            finally
            {
                Release(segment);
            }

            return false;
        }

        /// <summary>Returns the key's rows of every proposal, in tag order.</summary>
        private List<LandedRow> LandedRows(int key)
        {
            var rows = new List<LandedRow>();
            var proposals = ledger.Proposals;
            for (var slot = 0; slot < proposals.Count; slot++)
            {
                for (var index = 0; index < proposals[slot].Rows.Count; index++)
                {
                    var row = proposals[slot].Rows[index];
                    if (row.Key == Keys[key].ToString())
                    {
                        rows.Add(new LandedRow(slot, new Tag(ScriptedLedger.Id, new Slot(slot), index), row));
                    }
                }
            }

            return rows;
        }

        /// <summary>Folds the key's rows of the first <paramref name="cut"/> proposals, as a history read with no prefix gives them.</summary>
        private JournalState? FoldAt(int key, int cut)
        {
            var rows = LandedRows(key).Where(row => row.Slot < cut).Select(static landed => Live(landed.Tag, landed.Row.Kind, landed.Row.Version, landed.Row.Body)).ToArray();
            try
            {
                return JournalState.Fold(null, rows);
            }
            finally
            {
                Release(rows);
            }
        }
    }

    /// <summary>A row of a key in a proposal: the proposal's slot, the row's tag, and the row.</summary>
    private sealed record LandedRow(int Slot, Tag Tag, ScriptedRow Row);
}
