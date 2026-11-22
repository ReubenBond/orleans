using System.Text;
using CsCheck;
using Orleans.Durable.Storage.Engine.Archive.Memory;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Archive.Tests;

/// <summary>
/// The memory store (archive.md §9, §10, HI-I3): every position's token decodes back to it and re-encodes to its own
/// bytes, a token it could not have minted throws, the last generation or append count among them; over generated limits,
/// <c>Plan</c> stays within them for every write <c>Admits</c> grew, with <c>IsReplaceRequested</c> answering at its
/// threshold, which lies strictly below the limit; its drain checks each write's token and the store's limits and skips
/// a write it holds; and its record's bytes are pinned.
/// </summary>
public sealed class InMemoryArchiveStoreTests : IDisposable
{
    private static readonly LedgerSegmentId Segment = new(new LedgerId(1), Slot.Zero);

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    [Fact]
    public void EveryTokenDecodesToItsPositionAndReencodesToItsOwnBytes() =>
        Gen.Select(Gen.ULong, Gen.ULong).Sample(static (generation, appends) =>
        {
            var position = new InMemoryArchivePosition(generation, 0, appends);
            var token = InMemoryArchiveTokens.Encode(position);
            Assert.Equal(position, InMemoryArchiveTokens.Decode(token));
            Assert.Equal(token, InMemoryArchiveTokens.Encode(InMemoryArchiveTokens.Decode(token)));
            Assert.Equal(position.IsOrigin, token.IsEmpty);
        });

    /// <summary>A token with another version, a varint not in its shortest form, an unterminated varint, trailing bytes, or <c>Origin</c> in bytes throws.</summary>
    [Theory]
    [InlineData("020101")]
    [InlineData("01800001")]
    [InlineData("010180")]
    [InlineData("0101010000")]
    [InlineData("010000")]
    [InlineData("01")]
    public void ATokenTheStoreCouldNotHaveMintedThrows(string hex)
    {
        using var store = new InMemoryArchiveStore();

        _ = Assert.Throws<ArchiveCorruptException>(() => InMemoryArchiveTokens.Decode(new RowToken(Convert.FromHexString(hex))));
        _ = Assert.Throws<ArchiveCorruptException>(() => store.Begin(new RowToken(Convert.FromHexString(hex))));
        _ = Assert.Throws<ArchiveCorruptException>(() => store.IsReplaceRequested(new RowToken(Convert.FromHexString(hex))));
    }

    /// <summary>
    /// Over generated limits and commands, each write grown only while <c>Admits</c> admits its next command: an append
    /// run's <c>Plan</c> holds at most the limit's appends, an opener's appends land in its base whatever their number, the
    /// incremental fold equals <c>Plan</c>, and <c>IsReplaceRequested</c> answers exactly from the threshold.
    /// </summary>
    [Fact]
    public void PlanStaysWithinTheLimitsOfEveryWriteAdmitsGrew()
    {
        var cases =
            from limit in Gen.Int[2, 6]
            from threshold in Gen.Int[1, limit - 1]
            from commands in Gen.Frequency((5, Gen.Const(RowKind.Append)), (1, Gen.Const(RowKind.Replace)), (1, Gen.Const(RowKind.Delete))).Array[1, 30]
            select (limit, threshold, commands);
        cases.Sample((limit, threshold, commands) => _leaks.InScope(() =>
        {
            using var store = new InMemoryArchiveStore(maxAppends: limit, replaceRequestAppends: threshold);
            IArchiveStore pure = store;
            var token = default(RowToken);
            var plan = pure.Begin(token);
            var written = new List<ArchiveCommand>();
            foreach (var kind in commands)
            {
                var command = kind == RowKind.Append ? ArchiveCommand.Append(1) : kind == RowKind.Replace ? ArchiveCommand.Replace(1) : ArchiveCommand.Delete(default);
                if (command.IsOpener || !plan.Admits(command))
                {
                    // The write ends, as at a segment's end or a ReplaceRequired; the next starts from its token.
                    token = End(pure, token, written, plan);
                    (plan, written) = (pure.Begin(token), []);
                    command = command.IsOpener ? command : ArchiveCommand.Replace(1);
                }

                plan.Add(command);
                written.Add(command);
            }

            _ = End(pure, token, written, plan);

            RowToken End(IArchiveStore members, RowToken from, List<ArchiveCommand> write, ArchivePlan fold)
            {
                var end = fold.End();
                Assert.Equal(members.Plan(new ArchiveWrite(Segment, from, [.. write])), end);
                var position = InMemoryArchiveTokens.Decode(end);
                if (write.Count > 0 && !write[0].IsOpener)
                {
                    Assert.True(position.Appends <= (ulong)limit, $"An append run Admits grew holds {position.Appends} appends, above {limit}.");
                }

                Assert.Equal(position.Appends >= (ulong)threshold, members.IsReplaceRequested(end));
                return end;
            }
        }));
    }

    /// <summary>
    /// HI-I5: a threshold above the limit, from which the store would refuse an <c>Append</c> without asking for a
    /// <c>Replace</c>, is refused, and so is a limit with no threshold; a threshold at the limit is taken.
    /// </summary>
    [Fact]
    public void AStoreWhichWouldRefuseAnAppendWithoutAskingForAReplaceIsRefused()
    {
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new InMemoryArchiveStore(maxAppends: 2, replaceRequestAppends: 3));
        Assert.Equal("replaceRequestAppends", Assert.Throws<ArgumentException>(() => new InMemoryArchiveStore(maxAppends: 2)).ParamName);
        using var atTheLimit = new InMemoryArchiveStore(maxAppends: 2, replaceRequestAppends: 2);
    }

    /// <summary>A token at the last generation or append count, which no chain reaches, throws as corruption when planned past.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PlanningPastTheLastPositionThrowsAsCorruption(bool opener)
    {
        using var store = new InMemoryArchiveStore();
        var token = InMemoryArchiveTokens.Encode(opener ? new InMemoryArchivePosition(ulong.MaxValue, 0, 0) : new InMemoryArchivePosition(1, 0, ulong.MaxValue));
        var command = opener ? ArchiveCommand.Replace(1) : ArchiveCommand.Append(1);

        _ = Assert.Throws<ArchiveCorruptException>(() => store.Begin(token).Add(command));
    }

    /// <summary>
    /// The drain refuses a write whose token is not its key's position, as one whose predecessor it never applied, a write
    /// it holds in part, a command the store's limits refuse, and rows of two segments, and skips a write it holds, as a
    /// straggler drain's.
    /// </summary>
    [Fact]
    public async Task TheDrainChecksEachWritesTokenAndLimits()
    {
        using var store = new InMemoryArchiveStore(maxAppends: 2, replaceRequestAppends: 1);
        var first = new LedgerSegmentId(new LedgerId(1), Slot.Zero);
        var second = new LedgerSegmentId(new LedgerId(1), new Slot(10));
        var afterFirst = store.Plan(new ArchiveWrite(first, default, [ArchiveCommand.Replace(1)]));
        var skipped = store.Plan(new ArchiveWrite(second, afterFirst, [ArchiveCommand.Replace(1)]));
        using var opener = Row(first, 0, RowKind.Replace, default, 1);
        using var stray = Row(second, 10, RowKind.Append, skipped, 2);
        using var appendA = Row(second, 10, RowKind.Append, afterFirst, 2);
        using var appendB = Row(second, 11, RowKind.Append, afterFirst, 3);
        using var appendC = Row(second, 12, RowKind.Append, afterFirst, 4);

        store.Drain(Key, [opener]);
        _ = Assert.Throws<ArchiveCorruptException>(() => store.Drain(Key, [stray]));
        _ = Assert.Throws<ArgumentException>(() => store.Drain(Key, [opener, stray]));
        _ = Assert.Throws<ArchiveCorruptException>(() => store.Drain(Key, [appendA, appendB, appendC]));
        store.Drain(Key, [appendA]);
        _ = Assert.Throws<ArchiveCorruptException>(() => store.Drain(Key, [appendA, appendB]));
        store.Drain(Key, [appendA]);
        store.Drain(Key, [opener]);

        var read = Assert.IsType<ArchiveStoreRead.Read>((await store.ReadAsync(Key, LedgerMapValue.Empty, TestContext.Current.CancellationToken)).Value);
        using (read)
        {
            Assert.Equal(new byte[] { 1, 1 }, read.Prefix.Content.ToArray());
            Assert.True(read.Prefix.HasBase);
        }
    }

    /// <summary>The memory store's record, a golden vector: its layout, distinct from every archive-log layout, and its fields.</summary>
    [Fact]
    public async Task TheRecordsBytesArePinned()
    {
        using var store = new InMemoryArchiveStore(() => 0x0123456789abcdefUL, maxAppendBytes: 16, maxAppends: 4, replaceRequestAppends: 3);

        _ = await store.CheckedIdAsync(new Dictionary<string, string> { ["format"] = "json" }, TestContext.Current.CancellationToken);

        Assert.Equal(
            "id=00000000000000000123456789abcdef\nlayout=memory-store\nmax-append-bytes=16\nmax-appends=4\nreplace-request-appends=3\ncaller.format=json",
            Encoding.UTF8.GetString(store.RecordBytes.Span));
        _ = await Assert.ThrowsAsync<ArchiveStoreMismatchException>(async () => await store.CheckedIdAsync(new Dictionary<string, string>(), TestContext.Current.CancellationToken));
    }

    public void Dispose() => _leaks.Dispose();

    private static readonly byte[] Key = [1];

    private static ArchiveRow Row(LedgerSegmentId segment, long slot, RowKind kind, RowToken token, ulong count)
    {
        using var body = ArcBuffer.Wrap(new byte[] { 1 });
        return ArchiveRow.Live(new Tag(segment.Ledger, new Slot(slot), 0), kind, new RowVersion(default, count), segment, token, body);
    }
}
