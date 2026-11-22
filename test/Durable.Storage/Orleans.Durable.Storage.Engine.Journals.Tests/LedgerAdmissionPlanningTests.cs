using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Archive.Memory;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Testing;
using static Orleans.Durable.Storage.Engine.Journals.Tests.JournalSamples;

namespace Orleans.Durable.Storage.Engine.Journals.Tests;

/// <summary>
/// Token planning and the store's limits in admission (journals.md §8.1 steps 4 and 5, JS-I4, JS-I10), over a scripted
/// ledger whose batches start segments or continue them as each test says: a key's token rolls lazily, to <c>Plan</c> of
/// its write in its recorded segment, when its next row joins a batch of another segment; a roll founds the admitted
/// fold again from the entries admitted against the old segment; a load continues its newest open segment; an
/// <c>Append</c> the store's limits refuse in either candidate write is answered <c>ReplaceRequired</c>, with no effect,
/// only with nothing of its key in flight, and otherwise waits; and a key the store cannot store is refused. Every
/// <c>Durable</c> row drains into the memory store, which checks each key's token chain and its limits (HI-I4).
/// </summary>
public sealed class LedgerAdmissionPlanningTests : IDisposable
{
    private static readonly JournalWriteOutcome ReplaceRequired = new(JournalWriteOutcome.ReplaceRequired.Instance);
    private static readonly LedgerSegmentId First = new(ScriptedLedger.Id, Slot.Zero);

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    /// <summary>
    /// A key's first row stores <c>Origin</c>; its rows in the same segment keep the segment's token; its first row in a
    /// later segment stores <c>Plan</c> of its write in the earlier one (JS-I10).
    /// </summary>
    [Fact]
    public async Task TheTokenRollsLazilyAtASegmentChange()
    {
        await using var ledger = new ScriptedLedger { StartEachSegment = false };
        Accept(ledger, ledger.WriteAsync(Append(V(0), "one")), 0);
        Accept(ledger, ledger.WriteAsync(Append(V(1), "two")), 1);
        ledger.EndSegment();
        Accept(ledger, ledger.WriteAsync(Replace(V(2), "three")), 2);
        Accept(ledger, ledger.WriteAsync(Append(V(3), "four")), 3);

        var planned = ledger.Store.Plan(new ArchiveWrite(First, default, [ArchiveCommand.Append(3), ArchiveCommand.Append(3)]));
        Assert.False(planned.IsEmpty);
        Assert.Equal([default, default, planned, planned], ledger.Proposals.Select(static proposal => Assert.Single(proposal.Rows).Token));
        Assert.Equal([First, First, new(ScriptedLedger.Id, new Slot(2)), new(ScriptedLedger.Id, new Slot(2))], ledger.Proposals.Select(static proposal => Assert.Single(proposal.Rows).Segment));
        ledger.DrainDurable();
    }

    /// <summary>
    /// A later opener in the same segment begins the key's write there again, so the token rolls to <c>Plan</c> of the
    /// write from that opener alone, which the segment's first row's token begins: the roll takes the written fold at the
    /// segment change, where a roll at each batch would begin the second <c>Replace</c>'s write from the first's.
    /// </summary>
    [Fact]
    public async Task ARollTakesTheWriteFromTheSegmentsLastOpener()
    {
        await using var ledger = new ScriptedLedger { StartEachSegment = false };
        Accept(ledger, ledger.WriteAsync(Replace(V(0), "first")), 0);
        Accept(ledger, ledger.WriteAsync(Replace(V(1), "second")), 1);
        ledger.EndSegment();
        Accept(ledger, ledger.WriteAsync(Append(V(2))), 2);

        var planned = ledger.Store.Plan(new ArchiveWrite(First, default, [ArchiveCommand.Replace(6)], superseded: 1));
        var eager = ledger.Store.Plan(new ArchiveWrite(First, planned, [ArchiveCommand.Replace(6)]));
        Assert.NotEqual(eager, planned);
        Assert.Equal(planned, Assert.Single(ledger.Proposal(2).Rows).Token);
        ledger.DrainDurable();
    }

    /// <summary>
    /// Entries admitted while their key's earlier one is in flight are judged against the write they would join in the
    /// recorded segment; when their batch starts another segment, their token rolls to <c>Plan</c> of the key's write in
    /// the old segment, and the admitted fold is founded again on the new one, which a Debug assertion checks equals the
    /// written write once nothing of the key is in flight. An <c>Append</c> the limits then refuse while those entries
    /// are in flight waits, and is answered <c>ReplaceRequired</c> once they land, with nothing proposed.
    /// </summary>
    [Fact]
    public async Task ARollFoundsTheAdmittedFoldOnTheEntriesAdmittedAgainstTheOldSegment()
    {
        await using var ledger = new ScriptedLedger(window: 1, memory: new InMemoryArchiveStore(maxAppends: 3, replaceRequestAppends: 3));
        var one = ledger.WriteAsync(Append(V(0)));
        var two = ledger.WriteAsync(Append(V(1)));
        var three = ledger.WriteAsync(Append(V(2)));
        var four = ledger.WriteAsync(Append(V(3)));

        // The three admitted appends reach the store's limit, from which it asks for a Replace (HI-I5), so each answer,
        // which carries the request at the key's admitted token, asks for one.
        ledger.Durable(0);
        Assert.Equal(Requested(V(1)), await AnsweredAsync(one));
        var second = ledger.Proposal(1);
        var planned = ledger.Store.Plan(new ArchiveWrite(First, default, [ArchiveCommand.Append(1)]));
        Assert.Equal([planned, planned], second.Rows.Select(static row => row.Token));
        Assert.False(four.IsCompleted);

        ledger.Durable(1);
        Assert.Equal(Requested(V(2)), await AnsweredAsync(two));
        Assert.Equal(Requested(V(3)), await AnsweredAsync(three));
        Assert.Equal(ReplaceRequired, await AnsweredAsync(four));
        Assert.Equal(2, ledger.Proposals.Count);
        ledger.DrainDurable();
    }

    /// <summary>
    /// The store's limits at unit size (journals.md §8.1 step 4): with two appends a generation, the second
    /// <c>Append</c> is admitted at the limit and the third is answered <c>ReplaceRequired</c> at once, with no effect;
    /// a <c>Replace</c> then opens a generation, after which an <c>Append</c> is admitted again.
    /// </summary>
    [Fact]
    public async Task AnAppendPastTheStoresLimitsIsAnsweredReplaceRequiredWithNoEffect()
    {
        await using var ledger = new ScriptedLedger(memory: new InMemoryArchiveStore(maxAppends: 2, replaceRequestAppends: 2));
        Accept(ledger, ledger.WriteAsync(Append(V(0))), 0);
        Accept(ledger, ledger.WriteAsync(Append(V(1))), 1);

        Assert.Equal(ReplaceRequired, await AnsweredAsync(ledger.WriteAsync(Append(V(2)))));
        Assert.Equal(2, ledger.Proposals.Count);

        Accept(ledger, ledger.WriteAsync(Replace(V(2))), 2);
        Accept(ledger, ledger.WriteAsync(Append(V(3))), 3);
        ledger.DrainDurable();
    }

    /// <summary>
    /// After an opener in the recorded segment, the write an <c>Append</c> would join, and the empty write from its
    /// <c>Plan</c>, both count the generation the opener began: the third <c>Append</c> past two a generation is answered
    /// <c>ReplaceRequired</c> at once, with nothing in flight.
    /// </summary>
    [Fact]
    public async Task AnAppendPastTheLimitsOfAGenerationAnOpenerBeganIsAnsweredReplaceRequired()
    {
        await using var ledger = new ScriptedLedger(memory: new InMemoryArchiveStore(maxAppends: 2, replaceRequestAppends: 2)) { StartEachSegment = false };
        Accept(ledger, ledger.WriteAsync(Replace(V(0))), 0);
        Accept(ledger, ledger.WriteAsync(Append(V(1))), 1);
        Accept(ledger, ledger.WriteAsync(Append(V(2))), 2);

        Assert.Equal(ReplaceRequired, await AnsweredAsync(ledger.WriteAsync(Append(V(3)))));
        Assert.Equal(3, ledger.Proposals.Count);
        ledger.DrainDurable();
    }

    /// <summary>
    /// While the admitted write has reached the store's replace request, an <c>Append</c> waits for the key's in-flight
    /// entries, since a write split across segments near the limit could pass what the candidates judge (archive.md §3);
    /// once they land it is judged and admitted.
    /// </summary>
    [Fact]
    public async Task AnAppendWaitsForTheInFlightEntriesOnceTheReplaceIsRequested()
    {
        await using var ledger = new ScriptedLedger(memory: new InMemoryArchiveStore(maxAppends: 3, replaceRequestAppends: 1));
        var one = ledger.WriteAsync(Append(V(0)));
        var two = ledger.WriteAsync(Append(V(1)));

        Assert.Single(ledger.Proposals);
        ledger.Durable(0);
        Assert.Equal(new JournalWriteOutcome(new JournalWriteOutcome.Accepted(V(1), ReplaceRequested: true)), await AnsweredAsync(one));
        Accept(ledger, two, 1);
        ledger.DrainDurable();
    }

    /// <summary>
    /// <c>Accepted</c> carries the archive store's replace request for the key's admitted write, a hint the writer latches
    /// (journals.md §2.4): a <c>Replace</c> opens a generation the store does not yet ask to replace, and the
    /// <c>Append</c> which reaches its threshold of one append is answered with the request.
    /// </summary>
    [Fact]
    public async Task AnAcceptedAnswerCarriesTheStoresReplaceRequest()
    {
        await using var ledger = new ScriptedLedger(memory: new InMemoryArchiveStore(maxAppends: 3, replaceRequestAppends: 1));
        var replace = ledger.WriteAsync(Replace(V(0)));
        ledger.Durable(0);
        Assert.Equal(new JournalWriteOutcome(new JournalWriteOutcome.Accepted(V(1), ReplaceRequested: false)), await AnsweredAsync(replace));

        var append = ledger.WriteAsync(Append(V(1)));
        ledger.Durable(1);
        Assert.Equal(new JournalWriteOutcome(new JournalWriteOutcome.Accepted(V(2), ReplaceRequested: true)), await AnsweredAsync(append));
        ledger.DrainDurable();
    }

    /// <summary>
    /// A key whose newest live segment is still open loads its write there as its planning state: its next row in that
    /// segment keeps the segment's token, and its first in a later segment stores <c>Plan</c> of the whole write.
    /// </summary>
    [Fact]
    public async Task ALoadContinuesItsKeysWriteInTheOpenSegment()
    {
        await using var ledger = new ScriptedLedger(options: new JournalStoreOptions { WarmStateBytes = 0 }) { StartEachSegment = false };
        Accept(ledger, ledger.WriteAsync(Append(V(0), "one")), 0);
        Accept(ledger, ledger.WriteAsync(Append(V(1), "two")), 1);
        Assert.Equal(2, ledger.Loads);
        ledger.EndSegment();
        Accept(ledger, ledger.WriteAsync(Append(V(2), "three")), 2);

        Assert.Equal(3, ledger.Loads);
        var planned = ledger.Store.Plan(new ArchiveWrite(First, default, [ArchiveCommand.Append(3), ArchiveCommand.Append(3)]));
        Assert.Equal(planned, Assert.Single(ledger.Proposal(2).Rows).Token);
        ledger.DrainDurable();
    }

    /// <summary>
    /// A key reloaded while its segment is open goes on planning from the loaded write: its in-flight row there keeps the
    /// segment's token, and its next row, in a later segment and with no reload, stores <c>Plan</c> of the write from
    /// the segment's last opener.
    /// </summary>
    [Fact]
    public async Task AReloadedKeyGoesOnPlanningItsWriteInTheOpenSegment()
    {
        await using var ledger = new ScriptedLedger(options: new JournalStoreOptions { WarmStateBytes = 0 }) { StartEachSegment = false };
        Accept(ledger, ledger.WriteAsync(Replace(V(0), "first")), 0);
        var second = ledger.WriteAsync(Replace(V(1), "second"));
        ledger.EndSegment();
        var third = ledger.WriteAsync(Append(V(2)));
        ledger.Durable(1);
        ledger.Durable(2);

        Assert.Equal(Accepted(V(2)), await AnsweredAsync(second));
        Assert.Equal(Accepted(V(3)), await AnsweredAsync(third));
        Assert.Equal(2, ledger.Loads);
        Assert.Equal(default, Assert.Single(ledger.Proposal(1).Rows).Token);
        var planned = ledger.Store.Plan(new ArchiveWrite(First, default, [ArchiveCommand.Replace(6)], superseded: 1));
        Assert.Equal(planned, Assert.Single(ledger.Proposal(2).Rows).Token);
        ledger.DrainDurable();
    }

    /// <summary>
    /// A write answered <c>ReplaceRequired</c> or <c>Rejected</c> before any batch leaves the bytes it queued: with room
    /// for one row queued, each later write is accepted at once.
    /// </summary>
    [Fact]
    public async Task AnAnswerBeforeABatchReleasesTheQueuedBytes()
    {
        var options = new JournalStoreOptions { MaxQueuedBytes = LedgerBatch.MaxRowLength(Key.Length, 0, RowToken.MaxLength, 1) };
        await using var ledger = new ScriptedLedger(options: options, memory: new InMemoryArchiveStore(maxAppends: 1, replaceRequestAppends: 1));
        Accept(ledger, ledger.WriteAsync(Append(V(0))), 0);

        Assert.Equal(ReplaceRequired, await AnsweredAsync(ledger.WriteAsync(Append(V(1)))));
        Assert.Equal(0, ledger.Admission.QueuedBytes);
        Assert.Equal(ReplaceRequired, await AnsweredAsync(ledger.WriteAsync(Append(V(1)))));
        Assert.Equal(new JournalWriteOutcome(new JournalWriteOutcome.Rejected(V(1))), await AnsweredAsync(ledger.WriteAsync(Append(V(5)))));
        Assert.Equal(0, ledger.Admission.QueuedBytes);
        Accept(ledger, ledger.WriteAsync(Replace(V(1), "r")), 1);
        ledger.DrainDurable();
    }

    /// <summary>
    /// Each break of a key's token chain is caught by its check (journals.md §10): a roll which keeps the earlier
    /// segment's token, by the drain's chain check; a defining row whose version before it leaves the fold, by the rows'
    /// version check.
    /// </summary>
    [Theory]
    [InlineData(nameof(LedgerAdmission.ChainMutant.TokenNotRolled), "stores the token")]
    [InlineData(nameof(LedgerAdmission.ChainMutant.VersionOffTheFold), "does not follow")]
    public async Task EachChainMutantIsCaughtByItsCheck(string mutant, string check)
    {
        await using var ledger = new ScriptedLedger { StartEachSegment = false };
        Accept(ledger, ledger.WriteAsync(Append(V(0))), 0);
        Accept(ledger, ledger.WriteAsync(Append(V(1))), 1);
        ledger.Admission.ChainMutation = Enum.Parse<LedgerAdmission.ChainMutant>(mutant);
        ledger.EndSegment();

        // A Replace, which the store cannot hold already: a write of Appends from an earlier token would read as drained.
        _ = ledger.WriteAsync(Replace(V(2)));

        if (mutant == nameof(LedgerAdmission.ChainMutant.TokenNotRolled))
        {
            ledger.CheckVersions();
            ledger.Durable(2);
            Assert.Contains(check, Assert.Throws<ArchiveCorruptException>(ledger.DrainDurable).Message, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains(check, Assert.Throws<InvalidOperationException>(ledger.CheckVersions).Message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Under a limit of six bytes of appends in one write, the write an <c>Append</c> would join refuses it while the
    /// empty write from its <c>Plan</c> admits it: two appends of three bytes reach the limit, and a third of one byte
    /// passes it, so it waits while the key's entries are in flight, and is answered <c>ReplaceRequired</c> once they land.
    /// </summary>
    [Fact]
    public async Task AnAppendTheJoinedWriteAloneRefusesWaitsAndThenIsAnsweredReplaceRequired()
    {
        var memory = new InMemoryArchiveStore();
        await using var ledger = new ScriptedLedger(store: new PerWriteLimitStore(memory, 6), memory: memory) { StartEachSegment = false };
        var one = ledger.WriteAsync(Append(V(0), "abc"));
        var two = ledger.WriteAsync(Append(V(1), "def"));
        var three = ledger.WriteAsync(Append(V(2), "g"));

        Assert.False(three.IsCompleted);
        ledger.Durable(0);
        Assert.False(three.IsCompleted);
        ledger.Durable(1);
        Assert.Equal(Requested(V(1)), await AnsweredAsync(one));
        Assert.Equal(Requested(V(2)), await AnsweredAsync(two));
        Assert.Equal(ReplaceRequired, await AnsweredAsync(three));
        Assert.Equal(2, ledger.Proposals.Count);
        ledger.DrainDurable();
    }

    /// <summary>
    /// Under a limit of six bytes of appends in one write, a roll founds the admitted fold on the entries admitted
    /// against the old segment: an <c>Append</c> which waited on the old segment's write, at the limit, is admitted
    /// against the new segment's, which holds only the entry the roll moved there.
    /// </summary>
    [Fact]
    public async Task ARollFoundsTheJoinedWriteOnTheEntriesItMoved()
    {
        var memory = new InMemoryArchiveStore();
        await using var ledger = new ScriptedLedger(window: 1, store: new PerWriteLimitStore(memory, 6), memory: memory) { StartEachSegment = false };
        var one = ledger.WriteAsync(Append(V(0), "abc"));
        var two = ledger.WriteAsync(Append(V(1), "def"));
        var three = ledger.WriteAsync(Append(V(2), "g"));
        ledger.EndSegment();

        ledger.Durable(0);
        Assert.Equal(Requested(V(1)), await AnsweredAsync(one));
        Assert.False(three.IsCompleted);
        Assert.Equal(new LedgerSegmentId(ScriptedLedger.Id, new Slot(1)), Assert.Single(ledger.Proposal(1).Rows).Segment);
        ledger.Durable(1);
        Assert.Equal(Requested(V(2)), await AnsweredAsync(two));
        ledger.Durable(2);
        Assert.Equal(Requested(V(3)), await AnsweredAsync(three));
        ledger.DrainDurable();
    }

    /// <summary>A key the archive store cannot store (<c>AdmitsKey</c>) is refused with <see cref="ArgumentException"/>, before any load.</summary>
    [Fact]
    public async Task AKeyTheStoreCannotStoreIsRefused()
    {
        var refused = JournalRowKey.Encode("refused");
        var memory = new InMemoryArchiveStore();
        await using var ledger = new ScriptedLedger(store: new RefusingStore(memory, refused), memory: memory);

        using (var record = Append(V(0), key: refused))
        {
            Assert.Contains("AdmitsKey", Assert.Throws<ArgumentException>(() => { _ = ledger.Admit(record); }).Message, StringComparison.Ordinal);
        }

        Assert.Equal(0, ledger.Loads);
        Accept(ledger, ledger.WriteAsync(Append(V(0))), 0);
    }

    private static JournalWriteOutcome Accepted(JournalVersion version) => new(new JournalWriteOutcome.Accepted(version, ReplaceRequested: false));

    private static JournalWriteOutcome Requested(JournalVersion version) => new(new JournalWriteOutcome.Accepted(version, ReplaceRequested: true));

    /// <summary>Decides proposal <paramref name="index"/> <c>Durable</c> and checks the write it holds is accepted.</summary>
    private static void Accept(ScriptedLedger ledger, Task<JournalWriteOutcome> write, int index)
    {
        ledger.Durable(index);
        Assert.True(write.IsCompletedSuccessfully, "The admission answered the write.");
        _ = Assert.IsType<JournalWriteOutcome.Accepted>(write.Result.Value);
    }

    private static async Task<T> AnsweredAsync<T>(Task<T> write)
    {
        Assert.True(write.IsCompleted, "The admission answered the write.");
        return await write;
    }

    /// <summary>A store with a memory store's members which cannot store one key.</summary>
    private sealed class RefusingStore(InMemoryArchiveStore inner, JournalRowKey refused) : IArchiveStore
    {
        public ArchiveStoreKind Kind => inner.Kind;

        public int? MaxAppendBytes => inner.MaxAppendBytes;

        public ValueTask<ArchiveStoreRead> ReadAsync(ReadOnlyMemory<byte> key, LedgerMapValue map, CancellationToken cancellationToken) => inner.ReadAsync(key, map, cancellationToken);

        public ValueTask<ArchiveStoreRecord> StoreRecordAsync(IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken) => inner.StoreRecordAsync(parameters, cancellationToken);

        public ArchivePlan Begin(RowToken token) => inner.Begin(token);

        public bool Holds(ArchivePrefix prefix, ArchiveWrite write, int count) => inner.Holds(prefix, write, count);

        public bool IsReplaceRequested(RowToken token) => inner.IsReplaceRequested(token);

        public bool AdmitsKey(ReadOnlySpan<byte> key) => !key.SequenceEqual(refused.Span) && inner.AdmitsKey(key);
    }

    /// <summary>
    /// A store with a memory store's members whose folds also refuse an <c>Append</c> which would take one write's
    /// appended bytes past <paramref name="limit"/>: a limit an empty write from any token meets only with one longer
    /// append, so only the joined write refuses the tests' appends.
    /// </summary>
    private sealed class PerWriteLimitStore(InMemoryArchiveStore inner, int limit) : IArchiveStore
    {
        public ArchiveStoreKind Kind => inner.Kind;

        public int? MaxAppendBytes => inner.MaxAppendBytes;

        public ValueTask<ArchiveStoreRead> ReadAsync(ReadOnlyMemory<byte> key, LedgerMapValue map, CancellationToken cancellationToken) => inner.ReadAsync(key, map, cancellationToken);

        public ValueTask<ArchiveStoreRecord> StoreRecordAsync(IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken) => inner.StoreRecordAsync(parameters, cancellationToken);

        public ArchivePlan Begin(RowToken token) => new Plan(inner.Begin(token), limit);

        public bool Holds(ArchivePrefix prefix, ArchiveWrite write, int count) => inner.Holds(prefix, write, count);

        public bool IsReplaceRequested(RowToken token) => inner.IsReplaceRequested(token);

        public bool AdmitsKey(ReadOnlySpan<byte> key) => inner.AdmitsKey(key);

        private sealed class Plan(ArchivePlan inner, int limit) : ArchivePlan
        {
            private int _appendedBytes;

            public override RowToken End() => inner.End();

            protected override void Apply(ArchiveCommand command)
            {
                inner.Add(command);
                if (command.Kind == RowKind.Append)
                {
                    _appendedBytes += command.ContentLength;
                }
            }

            protected override bool AdmitsNext(ArchiveCommand command) =>
                inner.Admits(command) && (command.Kind != RowKind.Append || _appendedBytes + command.ContentLength <= limit);
        }
    }
}
