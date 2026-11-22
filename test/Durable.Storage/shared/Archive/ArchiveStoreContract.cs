using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using CsCheck;
using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Testing;

/// <summary>
/// The <see cref="IArchiveStore"/> conformance suite (archive.md §3, §10), which every archive store passes: <c>Absent</c>
/// only when the store's prefix of the key would hold no command; a prefix whose content, base, and tombstone are the fold
/// of exactly the writes its <c>Holds</c> holds; every write a drain applied held; a straggler drain which changes
/// nothing; a version base which advances with every write and never repeats; the laws of the pure members; and a stable
/// store ID in a record created once, which the store refuses when its own fields differ, and answers whatever the
/// caller's parameters, which the caller's own check refuses when they differ.
/// </summary>
/// <remarks>
/// Each store's suite derives from it, giving the store and a drain which lands a ledger's one segment as the store's own
/// drain would, and completes the ledger. Each write lands in a ledger of its own. A read takes a map value which drops
/// every ledger a write landed in, so every store holds each write whole (HI-I2), or, for the two-way property, a value
/// generated over the landed ledgers: one which still lists a suffix of them, closed, as a drop in lineage order leaves
/// it, and one older than the store, whose next ledger ID lies at or below ledgers which landed after it was read.
/// </remarks>
public abstract class ArchiveStoreContract : IDisposable
{
    /// <summary>The key every case's rows hold.</summary>
    protected static readonly byte[] Key = Encoding.UTF8.GetBytes("history-key");

    /// <summary>Another key.</summary>
    protected static readonly byte[] OtherKey = Encoding.UTF8.GetBytes("other-key");

    private static readonly ImmutableArray<NodeId> Replicas = [new NodeId(new Guid("00000000-0000-0000-0000-000000000001"))];

    private static readonly Gen<(RowKind Kind, int Length)> Commands = Gen.Frequency(
        (6, Gen.Int[1, 3].Select(static length => (RowKind.Append, length))),
        (2, Gen.Int[0, 3].Select(static length => (RowKind.Replace, length))),
        (1, Gen.OneOfConst(0, 8).Select(static length => (RowKind.Delete, length))));

    /// <summary>A history of a key, one write a ledger, each with the choices of the map value a read after it takes.</summary>
    private static readonly Gen<((RowKind Kind, int Length)[] Write, int Listed, int Next)[]> Histories =
        Gen.Select(Commands.Array[1, 4], Gen.Int[0, 1000], Gen.Int[0, 1000]).Array[1, 6];

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();
    private readonly Dictionary<string, KeyChain> _chains = [with(StringComparer.Ordinal)];
    private readonly Dictionary<LedgerId, Slot> _ends = [];
    private ulong _nextLedger = 1;
    private ulong _epoch;
    private int _lawKeys;

    /// <summary>Gets the store under test.</summary>
    protected abstract IArchiveStore Store { get; }

    /// <summary>
    /// Gets the store's order of each key's chain, where its <c>Holds</c> reads tokens, which then strictly order the
    /// chain (HI-I3); or <see langword="null"/> for a store whose coverage is the read's map value and whose token is empty.
    /// </summary>
    protected abstract IComparer<RowToken>? ChainOrder { get; }

    [Fact]
    public async Task AStoreHoldingNothingOfTheKeyAnswersAbsent()
    {
        Assert.Same(ArchiveStoreRead.Absent.Instance, (await Store.ReadAsync(Key, Map(), Token)).Value);
        await LandAsync((OtherKey, RowKind.Append, "a"));

        Assert.Same(ArchiveStoreRead.Absent.Instance, (await Store.ReadAsync(Key, Map(), Token)).Value);
    }

    /// <summary>
    /// A prefix holds the fold of the commands it holds (archive.md §2.1): its content, whether a <c>Replace</c> opened
    /// its base, an empty one included, and the tombstone of a <c>Delete</c> only while the key is deleted; across
    /// openers, append runs, a tombstoned <c>Delete</c>, an empty <c>Delete</c>, and an <c>Append</c> after a <c>Delete</c>.
    /// Each segment, separated by <c>|</c>, lands in a ledger of its own.
    /// </summary>
    [Theory]
    [InlineData("A:a A:b | A:c")]
    [InlineData("A:a | R:b A:c | A:d")]
    [InlineData("A:a R:b A:c | R: | A:d")]
    [InlineData("A:a D:tomb0001 | A:b")]
    [InlineData("A:a | D:tomb0001")]
    [InlineData("R:a | D: | A:b")]
    [InlineData("D: | R:")]
    [InlineData("A:a D:tomb0001 A:b | D:tomb0002 A:c R:d")]
    public async Task APrefixIsTheFoldOfTheCommandsItHolds(string history)
    {
        List<(RowKind Kind, byte[] Body)> landed = [];
        foreach (var segment in history.Split('|'))
        {
            var rows = segment.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(static row => (Key, row[0] switch
            {
                'A' => RowKind.Append,
                'R' => RowKind.Replace,
                _ => RowKind.Delete,
            }, row[2..])).ToArray();
            using (var built = Build(rows))
            {
                await LandAsync(built);
            }

            landed.AddRange(rows.Select(static row => (row.Item2, Encoding.UTF8.GetBytes(row.Item3))));

            using var read = await ReadAsync(Key);
            var expected = ArchiveOracle.Fold(landed, static row => row.Kind, static row => row.Body);
            var actual = ArchiveFold.Of(read.Prefix);
            Assert.True(expected.SameAs(actual), $"After [{segment}], the prefix holds {actual}, not {expected}.");
        }
    }

    /// <summary>
    /// A read with a map value older than the store, whose next ledger ID lies at the ledger of a write which landed after
    /// it was read, holds none of that write and answers only the writes before it (archive.md §3).
    /// </summary>
    [Fact]
    public async Task AReadWhoseMapIsOlderThanTheStoreHoldsNoWriteOfALedgerPastIt()
    {
        using var first = Build((Key, RowKind.Append, "a"));
        await LandAsync(first);
        var older = MapOf(listedFrom: _nextLedger, next: _nextLedger);
        using var second = Build((Key, RowKind.Append, "b"));
        await LandAsync(second);

        var answer = await Store.ReadAsync(Key, older, Token);
        using var read = Assert.IsType<ArchiveStoreRead.Read>(answer.Value);
        var expected = ChainOrder is null ? "a" : "ab";
        Assert.Equal(expected, Encoding.UTF8.GetString(read.Prefix.Content.ToArray()));
        Assert.Equal(ChainOrder is not null, Store.Holds(read.Prefix, second.Writes[Hex(Key)], 1));
        Assert.True(Store.Holds(read.Prefix, first.Writes[Hex(Key)], 1));
    }

    /// <summary>A straggler drain of a write the store holds changes neither its prefix nor its version base.</summary>
    [Fact]
    public async Task AStragglerDrainChangesNothing()
    {
        using var first = Build((Key, RowKind.Append, "a"), (Key, RowKind.Append, "b"));
        await LandAsync(first);
        await LandAsync((Key, RowKind.Replace, "c"), (Key, RowKind.Append, "d"));
        ArchiveFold before;
        RowToken token, versionBase;
        using (var read = await ReadAsync(Key))
        {
            (before, token, versionBase) = (ArchiveFold.Of(read.Prefix), read.Prefix.Token, read.Prefix.VersionBase);
        }

        await DrainAsync(first.Segment, first.End, first.Rows, Token);

        using var after = await ReadAsync(Key);
        Assert.True(before.SameAs(ArchiveFold.Of(after.Prefix)));
        Assert.Equal((token, versionBase), (after.Prefix.Token, after.Prefix.VersionBase));
    }

    /// <summary>
    /// The pure members' laws (archive.md §10, HI-I3, HI-I4) over random histories of a key, one write per ledger: a write
    /// is not held before it lands; after it lands, a read whose map value drops every ledger holds it and every earlier
    /// one, and a read whose map value is generated holds exactly the writes of the ledgers that value no longer lists, or,
    /// where tokens order the chain, every landed write, and folds exactly them (the two-way property); <c>Holds</c> is
    /// true for 0 and monotone in the count and in the coverage; <c>Plan</c> of an empty write returns its token and, where
    /// tokens order the chain, lies strictly above it for every non-empty write <c>Admits</c> grew; the incremental fold
    /// equals <c>Plan</c>; and each version base a read with every ledger dropped answers differs from every earlier one
    /// (the rebase-freshness property), while every version base answers one state.
    /// </summary>
    [Fact]
    public Task ThePureMembersKeepTheirLawsAndVersionBasesNeverRepeat() =>
        Histories.SampleAsync(CheckLawsAsync, iter: 40, threads: 1);

    /// <summary>
    /// Where tokens order the chain, every state a key's chain passes through has its own token (archive.md §3): over
    /// random histories of openers and appends, the write a reader forms from each prefix of each segment's rows, as one
    /// whose tail stops inside the segment forms it, plans a token no other prefix of the chain plans. A token which named
    /// two states would let a reader whose tail lags inside a segment take a prefix holding the whole segment's write as
    /// its own cut (archive-logs.md §7.2).
    /// </summary>
    [Fact]
    public void DistinctStatesOfAChainHaveDistinctTokens()
    {
        if (ChainOrder is null)
        {
            return;
        }

        Histories.Sample(history =>
        {
            var planned = new Dictionary<RowToken, string>();
            RowToken next = default;
            for (var index = 0; index < history.Length; index++)
            {
                var segment = new LedgerSegmentId(new LedgerId(1 + (ulong)index), Slot.Zero);
                var rows = history[index].Write;
                for (var cut = 1; cut <= rows.Length; cut++)
                {
                    var token = Store.Plan(WriteOf(segment, next, rows[..cut]));
                    var state = string.Create(CultureInfo.InvariantCulture, $"segment {index} through row {cut}");
                    Assert.True(planned.TryAdd(token, state), $"{state} plans {token}, which {planned.GetValueOrDefault(token)} plans too.");
                }

                next = Store.Plan(WriteOf(segment, next, rows));
            }
        });

        ArchiveWrite WriteOf(LedgerSegmentId segment, RowToken token, (RowKind Kind, int Length)[] rows)
        {
            var opener = Array.FindLastIndex(rows, static row => row.Kind != RowKind.Append);
            var commands = rows[Math.Max(opener, 0)..].Select(static row => ArchiveCommand.Of(row.Kind, new byte[row.Length]));
            return new ArchiveWrite(segment, token, [.. commands], Math.Max(opener, 0));
        }
    }

    /// <summary>
    /// HI-I5: a store asks for a <c>Replace</c> from every token at which a fold refuses an <c>Append</c> within
    /// <c>MaxAppendBytes</c>, so a writer refused learns of the request from its next read. Along a chain of one-byte and
    /// of largest <c>Append</c>s, one write each, until the store refuses one, and at every cut of random histories, at a
    /// fresh write's refusal and at one mid-write, each refusal finds the request.
    /// </summary>
    [Fact]
    public void AStoreWhichRefusesAnAppendAsksForAReplace()
    {
        foreach (var length in (int[])[1, Store.MaxAppendBytes ?? 1 << 20])
        {
            RowToken token = default;
            for (var write = 0; write < (ChainOrder is null ? 16 : 100_000); write++)
            {
                if (!Store.Begin(token).Admits(ArchiveCommand.Append(length)))
                {
                    Assert.True(Store.IsReplaceRequested(token), $"The store refuses an Append of {length} bytes from the token after {write} writes, and asks for no Replace.");
                    break;
                }

                token = Store.Plan(new ArchiveWrite(new LedgerSegmentId(new LedgerId(1 + (ulong)write), Slot.Zero), token, [ArchiveCommand.Append(length)]));
            }
        }

        Histories.Sample(history =>
        {
            RowToken token = default;
            for (var index = 0; index < history.Length; index++)
            {
                var segment = new LedgerSegmentId(new LedgerId(1 + (ulong)index), Slot.Zero);
                var rows = history[index].Write;
                var opener = Array.FindLastIndex(rows, static row => row.Kind != RowKind.Append);
                ImmutableArray<ArchiveCommand> commands = [.. rows[Math.Max(opener, 0)..].Select(static row => ArchiveCommand.Of(row.Kind, new byte[row.Length]))];
                var write = new ArchiveWrite(segment, token, commands, Math.Max(opener, 0));
                for (var cut = 0; cut <= commands.Length; cut++)
                {
                    var plan = Store.Begin(token);
                    var admitted = 0;
                    while (admitted < cut && plan.Admits(commands[admitted]))
                    {
                        plan.Add(commands[admitted++]);
                    }

                    var reached = plan.End();
                    if (admitted < cut && commands[admitted].Kind == RowKind.Append)
                    {
                        // The fold refused an Append mid-write, as admission's joined candidate does.
                        Assert.True(Store.IsReplaceRequested(reached), $"The store refuses an Append after {admitted} commands of a write, at {reached}, and asks for no Replace.");
                    }

                    if (!Store.Begin(reached).Admits(ArchiveCommand.Append(1)))
                    {
                        Assert.True(Store.IsReplaceRequested(reached), $"The store refuses an Append from {reached}, and asks for no Replace.");
                    }
                }

                token = Store.Plan(write);
            }
        });
    }

    /// <summary>
    /// A store whose only token is empty, the LSM's, keeps no per-key limit: it admits every key and command, bounds no
    /// body, and never asks for a <c>Replace</c> (archive-lsm.md §2.8).
    /// </summary>
    [Fact]
    public void AStoreOfEmptyTokensAdmitsEveryKeyAndCommand()
    {
        if (ChainOrder is not null)
        {
            return;
        }

        Assert.Null(Store.MaxAppendBytes);
        Assert.True(Store.AdmitsKey(Key));
        Assert.False(Store.IsReplaceRequested(default));
        Assert.Throws<ArchiveCorruptException>(() => Store.IsReplaceRequested(new RowToken([1])));
        var write = new ArchiveWrite(new LedgerSegmentId(new LedgerId(1), Slot.Zero), default, [.. Enumerable.Repeat(ArchiveCommand.Append(1 << 20), 100)]);
        Assert.True(Store.Admits(write, ArchiveCommand.Append(int.MaxValue)));
    }

    /// <summary>
    /// A fold's state is within <see cref="ArchivePlan.MaxStateBytes"/> (archive.md §4), which admission counts per warm
    /// key: <c>Begin</c> from <c>Origin</c> and from the token after a write allocates at most that, and taking an
    /// <c>Append</c> allocates nothing.
    /// </summary>
    [Fact]
    public void AFoldsStateIsWithinTheContractsBound()
    {
        var write = new ArchiveWrite(new LedgerSegmentId(new LedgerId(1), Slot.Zero), default, [ArchiveCommand.Replace(3), ArchiveCommand.Append(1)]);
        var after = Store.Plan(write);
        foreach (var token in (RowToken[])[default, after])
        {
            _ = Store.Begin(token);
            var before = GC.GetAllocatedBytesForCurrentThread();
            var plan = Store.Begin(token);
            var begun = GC.GetAllocatedBytesForCurrentThread() - before;
            before = GC.GetAllocatedBytesForCurrentThread();
            plan.Add(ArchiveCommand.Append(1));
            var added = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.InRange(begun, 1, ArchivePlan.MaxStateBytes);
            Assert.Equal(0, added);
        }
    }

    [Fact]
    public void ATokenTheStoreCouldNotHaveMintedThrows() =>
        Assert.Throws<ArchiveCorruptException>(() => Store.Begin(new RowToken([0xff, 0xff, 0xff])));

    [Fact]
    public async Task HoldsRefusesACountOutsideTheWrite()
    {
        using var segment = Build((Key, RowKind.Append, "a"));
        await LandAsync(segment);
        using var read = await ReadAsync(Key);
        var write = segment.Writes[Hex(Key)];

        Assert.Throws<ArgumentOutOfRangeException>(() => Store.Holds(read.Prefix, write, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Store.Holds(read.Prefix, write, write.Commands.Length + 1));
    }

    [Fact]
    public async Task TheStoreIdIsStableNonZeroAndReadsBack()
    {
        var parameters = Parameters("format", "json");
        var id = await Store.CheckedIdAsync(parameters, Token);

        Assert.Equal(id, await Store.CheckedIdAsync(Parameters("format", "json"), Token));
        Assert.NotEqual(UInt128.Zero, id.Value);
        Assert.Equal(id, ArchiveStoreId.TryParse(id.ToString()));
    }

    /// <summary>
    /// The store's record, created by the first call with its parameters, is answered whole to every later call, whatever
    /// that call's parameters: the store compares its own fields and never its caller's (archive.md §2.4).
    /// </summary>
    [Fact]
    public async Task TheStoresRecordIsCreatedOnceAndAnsweredWhateverTheParameters()
    {
        var created = await Store.StoreRecordAsync(Parameters("format", "json", "max-body-bytes", "1"), Token);

        var (layout, fields) = ExpectedStoreFields;
        Assert.Equal(layout, created.Layout);
        Assert.Equal(fields.OrderBy(static pair => pair.Key, StringComparer.Ordinal), created.StoreFields);
        var again = await Store.StoreRecordAsync(Parameters("format", "xml", "zone", "a"), Token);
        Assert.NotEqual(UInt128.Zero, created.Id.Value);
        Assert.Equal(created.Id, again.Id);
        Assert.Null(ArchiveStoreRecord.FirstDifference(created, again));
        Assert.Equal(Parameters("format", "json", "max-body-bytes", "1"), again.Parameters);
        Assert.Equal(created.Id, await Store.CheckedIdAsync(Parameters("format", "json", "max-body-bytes", "1"), Token));
    }

    /// <summary>
    /// A record whose caller parameters differ from the caller's in any one is answered by the store, and refused by the
    /// caller's own check, naming the parameter and both values (archive.md §2.4).
    /// </summary>
    [Theory]
    [InlineData("format=xml max-body-bytes=1", "format", "json", "xml")]
    [InlineData("format=json", "max-body-bytes", "1", null)]
    [InlineData("format=json max-body-bytes=1 zone=a", "zone", null, "a")]
    [InlineData("max-body-bytes=1", "format", "json", null)]
    public async Task ARecordWhoseCallerParametersDifferIsAnsweredAndRefusedByTheCaller(string parameters, string field, string? stored, string? caller)
    {
        var created = await Store.StoreRecordAsync(Parameters("format", "json", "max-body-bytes", "1"), Token);
        var other = parameters.Split(' ').Select(static pair => pair.Split('=')).ToDictionary(static pair => pair[0], static pair => pair[1]);

        var record = await Store.StoreRecordAsync(other, Token);
        Assert.Equal(created.Id, record.Id);
        var mismatch = Assert.Throws<ArchiveStoreMismatchException>(() => record.CheckParametersMatch(other));
        Assert.Equal((field, true), Field(mismatch));
        Assert.Equal((stored, caller), (mismatch.StoredValue, mismatch.CallerValue));
        Assert.Equal((field, true), Field(await Assert.ThrowsAsync<ArchiveStoreMismatchException>(async () => await Store.CheckedIdAsync(other, Token))));
        Assert.Equal(created.Encode(), await StoredRecordBytesAsync());
    }

    /// <summary>
    /// A record whose store fields differ from the store's own in any one, its layout, a field it lacks, or a field it holds
    /// with another value, is refused by the store itself, naming the field and both values, whatever the caller's
    /// parameters: the store's half of the record is checked apart from the caller's (archive.md §2.4, HI-I3).
    /// </summary>
    [Fact]
    public async Task ARecordWhoseStoreFieldsDifferIsRefusedByTheStore()
    {
        var parameters = Parameters("format", "json");
        var created = await Store.StoreRecordAsync(parameters, Token);
        var others = new List<(ArchiveStoreRecord Record, string Field, string? Stored, string? Caller)>
        {
            (ArchiveStoreRecord.Create(created.Id, "another-layout", created.StoreFields, created.Parameters), ArchiveStoreRecord.LayoutField, "another-layout", created.Layout),
            (ArchiveStoreRecord.Create(created.Id, created.Layout, created.StoreFields.SetItem("added-field", "1"), created.Parameters), "added-field", "1", null),
        };
        foreach (var (key, value) in created.StoreFields)
        {
            others.Add((ArchiveStoreRecord.Create(created.Id, created.Layout, created.StoreFields.SetItem(key, value + "0"), created.Parameters), key, value + "0", value));
            others.Add((ArchiveStoreRecord.Create(created.Id, created.Layout, created.StoreFields.Remove(key), created.Parameters), key, null, value));
        }

        foreach (var (record, field, stored, caller) in others)
        {
            await ReplaceRecordAsync(record);
            // The second call's parameters differ too: the store's half, checked first, names the store field.
            foreach (var call in new[] { parameters, Parameters("format", "xml") })
            {
                var mismatch = await Assert.ThrowsAsync<ArchiveStoreMismatchException>(async () => await Store.StoreRecordAsync(call, Token));
                Assert.Equal((field, false, stored, caller), (mismatch.Field, mismatch.IsParameter, mismatch.StoredValue, mismatch.CallerValue));
                Assert.Equal(record.Encode(), await StoredRecordBytesAsync());
            }
        }

        await ReplaceRecordAsync(created);
        Assert.Equal(created.Id, (await Store.StoreRecordAsync(parameters, Token)).Id);
    }

    /// <summary>
    /// A first call which loses the record's creation to another process's, whose caller parameters differ from the
    /// loser's, answers the winner's record, the stored bytes, which the caller's check then refuses; a winner whose store
    /// fields differ is refused by the store, which leaves the winner's bytes as they are; a create which lands and
    /// answers that the record exists, as a retried attempt does, answers the call's own record; and a record whose create
    /// answers that it exists while it reads absent is corruption: the race meets the same two checks as a later call
    /// (archive.md §2.4, HI-I3).
    /// </summary>
    [Theory]
    [InlineData("parameters")]
    [InlineData("store fields")]
    [InlineData("own attempt")]
    [InlineData("absent")]
    public async Task ACallWhichLosesTheRecordsCreationMeetsTheWinnersRecord(string winner)
    {
        var (layout, fields) = ExpectedStoreFields;
        var parameters = Parameters("format", "json");
        var id = ArchiveStoreId.NewRandom();
        ArchiveStoreRecord? competing = winner switch
        {
            "parameters" => ArchiveStoreRecord.Create(id, layout, fields, Parameters("format", "xml")),
            "store fields" => ArchiveStoreRecord.Create(id, layout, new Dictionary<string, string>(fields) { ["added-field"] = "1" }, Parameters("format", "xml")),
            _ => null,
        };
        var races = winner == "own attempt" ? LandNextRecordCreateAsExisting() : RaceNextRecordCreate(competing);
        Assert.SkipUnless(races, "The store creates its record under a lock of its own, which no other process races.");

        switch (winner)
        {
            case "parameters":
                var answered = await Store.StoreRecordAsync(parameters, Token);
                Assert.Equal(competing!.Encode(), answered.Encode());
                Assert.Equal(await StoredRecordBytesAsync(), answered.Encode());
                Assert.Equal(("format", true), Field(Assert.Throws<ArchiveStoreMismatchException>(() => answered.CheckParametersMatch(parameters))));
                Assert.Equal(("format", true), Field(await Assert.ThrowsAsync<ArchiveStoreMismatchException>(async () => await Store.CheckedIdAsync(parameters, Token))));
                break;
            case "store fields":
                // Both halves differ: the store's half is checked first, and names a store field.
                Assert.Equal(("added-field", false), Field(await Assert.ThrowsAsync<ArchiveStoreMismatchException>(async () => await Store.StoreRecordAsync(parameters, Token))));
                Assert.Equal(competing!.Encode(), await StoredRecordBytesAsync());
                break;
            case "own attempt":
                var own = await Store.StoreRecordAsync(parameters, Token);
                Assert.Equal(await StoredRecordBytesAsync(), own.Encode());
                Assert.Equal((layout, parameters.Count), (own.Layout, own.Parameters.Count));
                own.CheckParametersMatch(parameters);
                break;
            default:
                _ = await Assert.ThrowsAsync<ArchiveCorruptException>(async () => await Store.StoreRecordAsync(parameters, Token));
                Assert.Null(await StoredRecordBytesAsync());
                break;
        }
    }
    /// <summary>
    /// Another instance of the store over the same objects, as a restarted process opens, answers the persisted record
    /// byte for byte, whatever its own caller parameters.
    /// </summary>
    [Fact]
    public async Task AReopenedStoreAnswersThePersistedRecord()
    {
        var created = await Store.StoreRecordAsync(Parameters("format", "json"), Token);

        var reopened = await Reopen().StoreRecordAsync(Parameters("format", "xml"), Token);

        Assert.Equal(created.Encode(), reopened.Encode());
    }

    /// <summary>A caller parameter which takes a store field's name is the caller's own, apart from the store's field.</summary>
    [Fact]
    public async Task ACallerParameterNamedAsAStoreFieldStaysApart()
    {
        var id = await Store.CheckedIdAsync(Parameters("layout", "x", "id", "y"), Token);

        Assert.Equal(id, await Store.CheckedIdAsync(Parameters("layout", "x", "id", "y"), Token));
        Assert.Equal(("layout", true), Field(await Assert.ThrowsAsync<ArchiveStoreMismatchException>(async () => await Store.CheckedIdAsync(Parameters("id", "y"), Token))));
    }

    [Fact]
    public async Task AParameterWhichIsNoFieldIsRefused()
    {
        _ = await Assert.ThrowsAsync<ArgumentException>(async () => await Store.StoreRecordAsync(Parameters("Format", "json"), Token));
        _ = await Store.StoreRecordAsync(Parameters("format", "json"), Token);
        _ = await Assert.ThrowsAsync<ArgumentException>(async () => await Store.StoreRecordAsync(Parameters("Format", "json"), Token));
    }

    /// <summary>A command whose body spans buffer pages, as every body is inline, reads back whole and byte for byte.</summary>
    [Fact]
    public async Task ABodySpanningPagesReadsBackWhole()
    {
        var body = new byte[200_017];
        new Random(17).NextBytes(body);
        using (var built = Build((Key, RowKind.Replace, body)))
        {
            await LandAsync(built);
        }

        using var read = await ReadAsync(Key);
        Assert.Equal(body, read.Prefix.Content.ToArray());
        Assert.False(read.Prefix.Content.AsReadOnlySequence().IsSingleSegment, "The content spans pages.");
    }

    public void Dispose()
    {
        DisposeStore();
        _leaks.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Lands <paramref name="rows"/>, the one segment of ledger <paramref name="segment"/>'s ledger, which ends at
    /// <paramref name="end"/>, its <c>T</c>, as the store's drain would, and completes the ledger; a drain of rows the store
    /// holds changes nothing. The drain borrows the rows.
    /// </summary>
    protected abstract ValueTask DrainAsync(LedgerSegmentId segment, Slot end, KeyedRow[] rows, CancellationToken cancellationToken);

    /// <summary>Replaces the store's record with <paramref name="record"/>, as another process's first call would have created it.</summary>
    protected abstract ValueTask ReplaceRecordAsync(ArchiveStoreRecord record);

    /// <summary>
    /// Gets the layout and the other store fields the spec gives the store's record for the store under test: the oracle of
    /// its own half of the record (archive.md §9, archive-lsm.md §2.8, archive-logs.md §7.5).
    /// </summary>
    protected abstract (string Layout, IReadOnlyDictionary<string, string> Fields) ExpectedStoreFields { get; }

    /// <summary>
    /// Makes the store's next create of its record race another process's, whose record <paramref name="winner"/> lands
    /// first, or, when it is <see langword="null"/>, whose create answers that the record exists while it reads absent;
    /// returns false for a store whose record no other process races.
    /// </summary>
    protected abstract bool RaceNextRecordCreate(ArchiveStoreRecord? winner);

    /// <summary>
    /// Makes the store's next create of its record land and then answer that the record exists, as a client which retried
    /// its own landed attempt does; returns false for a store whose record no other process races.
    /// </summary>
    protected abstract bool LandNextRecordCreateAsExisting();

    /// <summary>Returns the bytes of the store's record as the store holds them, or <see langword="null"/> when it holds none.</summary>
    protected abstract ValueTask<byte[]?> StoredRecordBytesAsync();

    /// <summary>Returns another instance of the store over the same objects, as a restarted process opens it.</summary>
    protected abstract IArchiveStore Reopen();

    /// <summary>Releases the store, before the leak check ends.</summary>
    protected abstract void DisposeStore();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string Hex(ReadOnlySpan<byte> key) => Convert.ToHexString(key);

    private static (string, bool) Field(ArchiveStoreMismatchException mismatch) => (mismatch.Field ?? string.Empty, mismatch.IsParameter);

    private static Dictionary<string, string> Parameters(params string[] pairs) =>
        Enumerable.Range(0, pairs.Length / 2).ToDictionary(index => pairs[2 * index], index => pairs[2 * index + 1]);

    private static byte[] Body(int length, int seed) => [.. Enumerable.Range(0, length).Select(index => unchecked((byte)(seed * 31 + index)))];

    /// <summary>Checks the laws over one history of a fresh key, one write per ledger.</summary>
    private async Task CheckLawsAsync(((RowKind Kind, int Length)[] Write, int Listed, int Next)[] history)
    {
        var key = Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"law-{_lawKeys++}"));
        var landed = new List<(ArchiveWrite Write, (RowKind Kind, byte[] Body)[] Commands)>();
        var bases = new VersionChecker();
        var firstLedger = _nextLedger;
        ArchivePrefix? before = null;
        try
        {
            foreach (var (segment, listed, next) in history)
            {
                var rows = segment.Select((command, index) => (key, command.Kind, Body(command.Length, landed.Count * 8 + index))).ToArray();
                using var built = Build(rows);
                var write = built.Writes[Hex(key)];
                CheckPlan(write);
                if (before is not null)
                {
                    Assert.False(Store.Holds(before, write, 1), $"A write is held before it lands: {write}.");
                    Assert.True(Store.Holds(before, write, 0));
                }

                await LandAsync(built);
                landed.Add((write, [.. built.Rows.Select(static row => (row.Row.Kind, row.Row.Body.ToArray()))]));
                using var read = await ReadAsync(key);
                var after = read.Prefix;
                CheckHolds(after, landed, static _ => true, before);
                Assert.Null(bases.VersionBase(key, after.VersionBase, ArchiveFold.Of(after).ToString()));

                // The two-way property: a generated map value which still lists a suffix of the landed ledgers, or predates
                // some of them.
                var listedFrom = firstLedger + (ulong)(listed % (int)(_nextLedger - firstLedger + 1));
                var nextId = listedFrom + (ulong)(next % (int)(_nextLedger + 2 - listedFrom));
                var map = MapOf(listedFrom, Math.Max(nextId, 1));
                await CheckGeneratedReadAsync(key, map, landed, bases);
                before?.Dispose();
                before = after.Retain();
            }
        }
        finally
        {
            before?.Dispose();
        }
    }

    /// <summary>
    /// Reads <paramref name="key"/> with <paramref name="map"/> and checks the two-way property: <c>Holds</c> holds exactly
    /// the writes of the ledgers the map no longer lists, or, where tokens order the chain, every landed one; and the
    /// prefix followed by the commands of the writes it does not hold folds to the key's history, so it holds no command of
    /// a write <c>Holds</c> does not hold, as one of a ledger past the map's next ID, and lacks none of one it holds but those
    /// a later write supersedes.
    /// </summary>
    private async Task CheckGeneratedReadAsync(byte[] key, LedgerMapValue map, List<(ArchiveWrite Write, (RowKind Kind, byte[] Body)[] Commands)> landed, VersionChecker bases)
    {
        Func<ArchiveWrite, bool> expected = ChainOrder is null ? write => map.NoLongerLists(write.Segment.Ledger) : static _ => true;
        var history = ArchiveOracle.Fold(landed.SelectMany(static write => write.Commands), static row => row.Kind, static row => row.Body);
        var notHeld = landed.Where(write => !expected(write.Write)).SelectMany(static write => write.Commands);
        var answer = await Store.ReadAsync(key, map, Token);
        if (answer.Value is ArchiveStoreRead.Stale)
        {
            // A store may refuse a map value which lists open a ledger a drain since completed: the map predates the
            // store's view (archive.md §3), and its reader reads the map again.
            Assert.Contains(map.Ledgers, ledger => ledger.Status.Value is LedgerStatus.Open && ledger.Id.Value < _nextLedger);
            return;
        }

        if (answer.Value is not ArchiveStoreRead.Read read)
        {
            var absent = Assert.IsType<ArchiveStoreRead.Absent>(answer.Value);
            foreach (var (ledger, bound) in absent.Bounds)
            {
                Assert.True(bound > Slot.Zero);
                Assert.DoesNotContain(landed, write => write.Write.Segment.Ledger == ledger && write.Write.Segment.Start < bound);
            }

            var composed = ArchiveFold.Nothing.Apply(notHeld, static row => row.Kind, static row => row.Body);
            Assert.True(history.SameAs(composed), $"With a map value of next ledger {map.NextLedgerId}, Absent and the writes not held fold to {composed}, not {history}.");
            return;
        }

        using (read)
        {
            CheckHolds(read.Prefix, landed, expected, before: null);
            var composed = ArchiveFold.Of(read.Prefix).Apply(notHeld, static row => row.Kind, static row => row.Body);
            Assert.True(history.SameAs(composed), $"With a map value of next ledger {map.NextLedgerId}, the prefix {ArchiveFold.Of(read.Prefix)} and the writes not held fold to {composed}, not {history}.");
            Assert.Null(bases.State(key, read.Prefix.VersionBase, ArchiveFold.Of(read.Prefix).ToString()));
        }
    }

    /// <summary>
    /// Checks that <paramref name="prefix"/> holds each landed write wholly when <paramref name="held"/> says so and none
    /// of it otherwise, monotone in the count, and, against <paramref name="before"/>, monotone in coverage.
    /// </summary>
    private void CheckHolds(ArchivePrefix prefix, List<(ArchiveWrite Write, (RowKind Kind, byte[] Body)[] Commands)> landed, Func<ArchiveWrite, bool> held, ArchivePrefix? before)
    {
        foreach (var (write, _) in landed)
        {
            var expected = held(write);
            Assert.True(Store.Holds(prefix, write, 0));
            for (var count = 1; count <= write.Commands.Length; count++)
            {
                Assert.True(expected == Store.Holds(prefix, write, count), $"A landed write is {(expected ? "not " : string.Empty)}held through command {count}: {write}.");
                Assert.True(before is null || !Store.Holds(before, write, count) || Store.Holds(prefix, write, count), "Holds shrank as coverage grew.");
            }
        }
    }

    /// <summary>
    /// Checks <c>Plan</c>'s laws on <paramref name="write"/>: the incremental fold equals it, an empty write returns its
    /// token, and, where tokens order the chain, a non-empty write's lies strictly above it.
    /// </summary>
    private void CheckPlan(ArchiveWrite write)
    {
        var plan = Store.Begin(write.Token);
        plan.Supersede(write.Superseded);
        foreach (var command in write.Commands)
        {
            plan.Add(command);
        }

        Assert.Equal(Store.Plan(write), plan.End());
        Assert.Equal(write.Token, Store.Plan(new ArchiveWrite(write.Segment, write.Token, [])));
        Assert.Equal(write.Token, Store.Begin(write.Token).End());
        if (ChainOrder is { } order)
        {
            Assert.True(order.Compare(Store.Plan(write), write.Token) > 0, $"Plan({write}) does not lie above its token in the store's order (HI-I3).");
        }
    }

    /// <summary>
    /// A map value which lists only the next ledger a write would take, open over the whole keyspace, so every ledger a
    /// write landed in is dropped; each value takes a new epoch.
    /// </summary>
    private LedgerMapValue Map() => MapOf(_nextLedger, _nextLedger + 1);

    /// <summary>
    /// A map value whose next ledger ID is <paramref name="next"/>, which lists, closed at its end, every landed ledger from
    /// <paramref name="listedFrom"/> below it, and, when <paramref name="next"/> passes the next ledger a write would take,
    /// that ledger, open over the whole keyspace; each value takes a new epoch. A value which predates the next ledger
    /// lists its last ledger open, as it was then, since an open ledger holds every key a listed ledger holds (LG-I1).
    /// </summary>
    private LedgerMapValue MapOf(ulong listedFrom, ulong next)
    {
        var claim = new PrimaryClaim(Replicas[0], SequencerInstanceId.New());
        var ledgers = ImmutableArray.CreateBuilder<LedgerMetadata>();
        for (var id = listedFrom; id < next && id < _nextLedger; id++)
        {
            LedgerStatus status = id + 1 == next ? LedgerStatus.Open.Instance : new LedgerStatus.Closed(_ends[new LedgerId(id)]);
            ledgers.Add(new LedgerMetadata(new LedgerId(id), KeyRange.Root, Replicas, 4, claim, status));
        }

        if (next > _nextLedger)
        {
            ledgers.Add(new LedgerMetadata(new LedgerId(_nextLedger), KeyRange.Root, Replicas, 4, claim, LedgerStatus.Open.Instance));
        }

        return new LedgerMapValue(++_epoch, new LedgerId(next), 0, ledgers.ToImmutable());
    }

    private async Task<ArchiveStoreRead.Read> ReadAsync(byte[] key) =>
        Assert.IsType<ArchiveStoreRead.Read>((await Store.ReadAsync(key, Map(), Token)).Value);

    private Built Build(params (byte[] Key, RowKind Kind, string Body)[] rows) =>
        Build([.. rows.Select(static row => (row.Key, row.Kind, Encoding.UTF8.GetBytes(row.Body)))]);

    private Built Build(params (byte[] Key, RowKind Kind, byte[] Body)[] rows) =>
        Build(rows.AsEnumerable());

    /// <summary>
    /// Builds the next ledger's one segment of <paramref name="rows"/>, one a slot: each key's defining row stores the
    /// token its previous write leaves, by the store's <c>Plan</c>, and each row the version after its key's previous row.
    /// An <c>Append</c> the store's limits refuse becomes a <c>Replace</c>, as its writer's <c>ReplaceRequired</c> leads.
    /// </summary>
    private Built Build(IEnumerable<(byte[] Key, RowKind Kind, byte[] Body)> rows)
    {
        var ledger = new LedgerId(_nextLedger);
        var segment = new LedgerSegmentId(ledger, Slot.Zero);
        var built = new List<KeyedRow>();
        var writes = new Dictionary<string, (RowToken Start, ArchivePlan Plan, List<ArchiveCommand> Commands)>(StringComparer.Ordinal);
        try
        {
            foreach (var (key, given, body) in rows)
            {
                var name = Hex(key);
                var chain = _chains.TryGetValue(name, out var found) ? found : _chains[name] = new KeyChain();
                if (!writes.TryGetValue(name, out var write))
                {
                    writes[name] = write = (chain.Next, Store.Begin(chain.Next), []);
                }

                var kind = given;
                var command = ArchiveCommand.Of(kind, body);
                if (!command.IsOpener && !write.Plan.Admits(command))
                {
                    (kind, command) = (RowKind.Replace, ArchiveCommand.Replace(body.Length));
                }

                if (command.IsOpener && write.Plan.Count > 0)
                {
                    // The opener supersedes the key's rows before it in the segment, which the store's plan may count.
                    var plan = Store.Begin(write.Start);
                    plan.Supersede(write.Plan.Superseded + write.Plan.Count);
                    write.Commands.Clear();
                    write = writes[name] = (write.Start, plan, write.Commands);
                }

                write.Plan.Add(command);
                write.Commands.Add(command);
                chain.Version = chain.Version.Next();
                using var bytes = ArcBuffer.Wrap(body);
                built.Add(new KeyedRow(key, ArchiveRow.Live(new Tag(ledger, new Slot(built.Count), 0), kind, chain.Version, segment, write.Start, bytes)));
            }

            var result = new Built(segment, new Slot(built.Count), [.. built], writes.ToDictionary(static pair => pair.Key, pair => new ArchiveWrite(segment, pair.Value.Start, [.. pair.Value.Commands], pair.Value.Plan.Superseded), StringComparer.Ordinal));
            foreach (var (name, write) in writes)
            {
                _chains[name].Next = write.Plan.End();
            }

            return result;
        }
        catch
        {
            KeyedRow.Release(built);
            throw;
        }
    }

    /// <summary>Lands a built segment, which it borrows: the suite's next map value drops its ledger.</summary>
    private async Task LandAsync(Built built)
    {
        await DrainAsync(built.Segment, built.End, built.Rows, Token);
        _ends[built.Segment.Ledger] = built.End;
        _nextLedger++;
    }

    /// <summary>Builds and lands a segment, and releases its rows.</summary>
    private async Task LandAsync(params (byte[] Key, RowKind Kind, string Body)[] rows)
    {
        using var built = Build(rows);
        await LandAsync(built);
    }

    /// <summary>A key's chain: the token its next write starts from, and the version after its last row.</summary>
    private sealed class KeyChain
    {
        public RowToken Next { get; set; }

        public RowVersion Version { get; set; }
    }

    /// <summary>A built segment: its identity, its end, its rows, which it owns, and each key's write, by its key in hexadecimal.</summary>
    private sealed record Built(LedgerSegmentId Segment, Slot End, KeyedRow[] Rows, Dictionary<string, ArchiveWrite> Writes) : IDisposable
    {
        public void Dispose() => KeyedRow.Release(Rows);
    }
}
