using System.Collections.Immutable;
using CsCheck;
using Orleans.Durable.Storage.AzureStorage.Archive;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.ObjectStore.Memory;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Archive.Logs.Tests;

/// <summary>
/// The laws of archive logs' pure members (archive-logs.md §2.2, §10, HI-I3): <c>Plan</c> by its fold, strictly above a
/// non-empty admitted write's token and equal to an empty one's, within <c>MaxUnits</c> for every write <c>Admits</c> grew
/// over generated layouts; the packing which the writer's requests share; and Azure's limits at their unit boundaries,
/// where <c>IsReplaceRequested</c> answers and <c>Admits</c> refuses, as Journals' <c>ReplaceRequired</c> follows them.
/// </summary>
[NoPageLeakCheck("Plans and tokens are values; the tests rent no pages.")]
public sealed class ArchiveLogPlanTests
{
    private static readonly LedgerSegmentId Segment = new(new LedgerId(3), Slot.Zero);

    private static readonly Gen<ArchiveLogLayout> Layouts =
        Gen.Select(Gen.Int[1, 8], Gen.Int[2, 12]).Select(static limits => new ArchiveLogLayout("memory", ArchiveLogPacking.Blocks, limits.Item1, (ulong)limits.Item2, (ulong)limits.Item2 - 1, MemoryArchiveLogStore.NameRule));

    /// <summary>
    /// A key's history over a generated layout, each write grown command by command while <c>Admits</c> admits it, as
    /// admission grows it: the fold's token after each write equals <c>Plan</c>, lies above the write's token, stays within
    /// <c>MaxUnits</c>, and is the position the write's packed units give.
    /// </summary>
    [Fact]
    public void PlanKeepsItsLawsOverGeneratedLayoutsAndHistories() =>
        Gen.Select(Layouts, Gen.Select(Gen.Int[0, 9], Gen.Int[1, 8]).Array[1, 12].Array[1, 6]).Sample(static @case =>
        {
            var (layout, history) = @case;
            var store = new LogArchiveStore(new MemoryArchiveLogStore(layout), new InMemoryObjectStore(), ArchiveLogNames.Hexadecimal);
            RowToken token = default;
            foreach (var commands in history)
            {
                var plan = store.Begin(token);
                var taken = ImmutableArray.CreateBuilder<ArchiveCommand>();
                foreach (var (kind, length) in commands)
                {
                    var command = kind switch
                    {
                        0 => ArchiveCommand.Replace(length),
                        1 => ArchiveCommand.Delete(new byte[8]),
                        _ => ArchiveCommand.Append(Math.Min(length, layout.MaxAppendBytes!.Value)),
                    };
                    if (command.IsOpener && taken.Count > 0 || !plan.Admits(command))
                    {
                        continue;
                    }

                    plan.Add(command);
                    taken.Add(command);
                }

                var write = new ArchiveWrite(Segment, token, taken.ToImmutable());
                var after = plan.End();
                Assert.Equal(store.Plan(write), after);
                var (from, to) = (ArchiveLogTokens.PositionOf(token), ArchiveLogTokens.PositionOf(after));
                Assert.True(write.Commands.IsEmpty ? to == from : to > from, $"Plan({write}) is {to}, not above {from}.");
                Assert.True(to.Units <= layout.MaxUnits, $"Plan({write}) packs {to.Units} units, past {layout.MaxUnits}.");
                if (!write.Commands.IsEmpty && !write.IsOpener)
                {
                    var start = ArchiveLogConditions.RunStart(from);
                    var boundaries = ArchiveLogUnitPacker.Boundaries(layout, start.Length, write.Commands.AsSpan());
                    Assert.Equal(new ArchiveLogPosition(start.Generation, start.BaseRows, boundaries[^1], start.Units + (ulong)(boundaries.Length - 1)), to);
                }

                token = after;
            }
        });

    [Fact]
    public void AnOpenerOpensTheNextGenerationAndItsAppendsLandInItsBase()
    {
        var store = Store(MemoryArchiveLogStore.DefaultLayout);

        Assert.Equal(new ArchiveLogPosition(1, 2, 0, 0), Position(store.Plan(Write(default, ArchiveCommand.Replace(9), ArchiveCommand.Append(5)))));
        Assert.Equal(new ArchiveLogPosition(8, 2, 0, 0), Position(store.Plan(Write(Token(7, 100, 3), ArchiveCommand.Delete(default), ArchiveCommand.Append(5)))));
        Assert.Equal(new ArchiveLogPosition(2, 4, 0, 0), Position(store.Plan(new ArchiveWrite(Segment, Token(1, 9, 2), [ArchiveCommand.Replace(9)], superseded: 3))));
        Assert.Equal(new ArchiveLogPosition(1, 0, 3, 1), Position(store.Plan(Write(default, ArchiveCommand.Append(1), ArchiveCommand.Append(2)))));
        Assert.Equal(Token(4, 9, 2), store.Plan(Write(Token(4, 9, 2))));
    }

    /// <summary>
    /// Azure's layout (archive-logs.md §2.2, §9): <c>IsReplaceRequested</c> answers from 40,000 blocks; an append run is
    /// admitted to 49,900 blocks and refused past them, so its writer answers <c>ReplaceRequired</c>, whose opener always
    /// admits; and <c>MaxAppendBytes</c> is 100 MiB.
    /// </summary>
    [Fact]
    public void AzuresLimitsAnswerAtTheirUnitBoundaries()
    {
        var store = Store(AzureArchiveLogStore.AzureLayout);
        const int Block = AzureArchiveLogStore.MaxBlockBytes;

        Assert.Equal(Block, store.MaxAppendBytes);
        Assert.False(store.IsReplaceRequested(Token(3, 39_999, 39_999)));
        Assert.True(store.IsReplaceRequested(Token(3, 40_000, 40_000)));
        Assert.True(store.Admits(Write(Token(3, 49_899, 49_899)), ArchiveCommand.Append(Block)));
        Assert.False(store.Admits(Write(Token(3, 49_900, 49_900)), ArchiveCommand.Append(1)));
        Assert.True(store.Admits(Write(Token(3, 49_899, 49_899), ArchiveCommand.Append(Block - 1)), ArchiveCommand.Append(1)));
        Assert.False(store.Admits(Write(Token(3, 49_899, 49_899), ArchiveCommand.Append(Block - 1)), ArchiveCommand.Append(2)));
        Assert.True(store.Admits(Write(Token(3, 49_900, 49_900)), ArchiveCommand.Replace(1)));
        Assert.True(store.Admits(Write(Token(3, 49_900, 49_900), ArchiveCommand.Replace(1)), ArchiveCommand.Append(Block)));
        Assert.Throws<ArgumentException>(() => store.Admits(Write(Token(3, 1, 1)), ArchiveCommand.Append(Block + 1)));
    }

    /// <summary>Azure refuses a blob name over 1,024 characters, so a key whose checkpoint's name would pass it is refused.</summary>
    [Fact]
    public void AKeyIsAdmittedWhileItsCheckpointsNameFitsAzuresBound()
    {
        var names = new ArchiveLogNames("test-1", static key => new string('k', key.Length));
        using var service = new FakeAzureBlobService();
        var store = new LogArchiveStore(new AzureArchiveLogStore(service.Client(), "json"), new InMemoryObjectStore(), names);

        Assert.True(store.AdmitsKey(new byte[991]));
        Assert.False(store.AdmitsKey(new byte[992]));
        Assert.Equal(1024, AzureArchiveLogMetadata.CheckpointName(new string('k', 991), ulong.MaxValue).Length);
    }

    /// <summary>
    /// Azure's names admit only a log whose blobs no URI rewrites into another's (DR 7), and whose checkpoint's name an HTTP
    /// header can carry: printable ASCII, with what a URI escapes, but no empty, dot-ended, or backslashed segment, no
    /// control or non-ASCII character, and at most 252 segments, so a checkpoint has 254.
    /// </summary>
    [Theory]
    [InlineData("k/ab", true)]
    [InlineData("k/a.b/c", true)]
    [InlineData("Grain.Type/Key A%2F+#?&=;~", true)]
    [InlineData("k/\U0001F600", false)]
    [InlineData("k/\u00E9", false)]
    [InlineData("k/\u007F", false)]
    [InlineData("k/../y", false)]
    [InlineData("k/./y", false)]
    [InlineData("k//y", false)]
    [InlineData("/k", false)]
    [InlineData("k/", false)]
    [InlineData("k.", false)]
    [InlineData("k\\y", false)]
    [InlineData("k\ty", false)]
    [InlineData("", false)]
    public void AzureAdmitsOnlyNamesNoUriRewrites(string name, bool admitted)
    {
        using var service = new FakeAzureBlobService();
        var logs = new AzureArchiveLogStore(service.Client(), "json");

        Assert.Equal(admitted, logs.AdmitsName(name));
        Assert.True(new MemoryArchiveLogStore().AdmitsName("k/../y"), "The memory store names no URI.");
    }

    [Fact]
    public void AzureAdmitsAtMost252Segments()
    {
        using var service = new FakeAzureBlobService();
        var logs = new AzureArchiveLogStore(service.Client(), "json");

        Assert.False(logs.AdmitsName("k" + '\uD800'), "A lone surrogate is no printable ASCII.");
        Assert.True(logs.AdmitsName(string.Join('/', Enumerable.Repeat("s", 252))));
        Assert.False(logs.AdmitsName(string.Join('/', Enumerable.Repeat("s", 253))));
        Assert.Equal(254, AzureArchiveLogMetadata.CheckpointName(string.Join('/', Enumerable.Repeat("s", 252)), 1).Count(static c => c == '/') + 1);
    }

    /// <summary>
    /// The chunk layout's limits (archive-logs.md §7.4): an append run of any length is one chunk;
    /// <c>IsReplaceRequested</c> answers from 32 chunks; an append run is admitted to 48 chunks and refused past them, so
    /// its writer answers <c>ReplaceRequired</c>, whose opener always admits.
    /// </summary>
    [Fact]
    public void TheChunkLayoutsLimitsAnswerAtTheirUnitBoundaries()
    {
        var store = ChunkStore(new ChunkArchiveLogStore(new InMemoryObjectStore()));

        Assert.Null(store.MaxAppendBytes);
        Assert.False(store.IsReplaceRequested(Token(3, 31, 31)));
        Assert.True(store.IsReplaceRequested(Token(3, 32, 32)));
        Assert.Equal(new ArchiveLogPosition(3, 0, 47 + 12, 48), Position(store.Plan(Write(Token(3, 47, 47), ArchiveCommand.Append(5), ArchiveCommand.Append(7)))));
        Assert.True(store.Admits(Write(Token(3, 47, 47), ArchiveCommand.Append(5)), ArchiveCommand.Append(7)));
        Assert.False(store.Admits(Write(Token(3, 48, 48)), ArchiveCommand.Append(1)));
        Assert.True(store.Admits(Write(Token(3, 48, 48)), ArchiveCommand.Replace(1)));
        Assert.True(store.Admits(Write(Token(3, 48, 48), ArchiveCommand.Replace(1)), ArchiveCommand.Append(1)));
    }

    /// <summary>
    /// A split write at the chunk layout's headroom (archive §3, Split writes): admission judges a key's write from 31
    /// chunks, below the replace request, as one chunk, while the batches which hold its in-flight entries start 16 more
    /// segments. Each segment's write is one chunk, admitted from its own token, so the split writes reach the layout's
    /// 48 chunks and no further, the store holds them all, and one write more is refused, by admission and by the store.
    /// </summary>
    [Fact]
    public async Task ASplitWriteAtTheChunkLayoutsHeadroomStaysWithinItsLimit()
    {
        var token = TestContext.Current.CancellationToken;
        var logs = new ChunkArchiveLogStore(new InMemoryObjectStore());
        var store = ChunkStore(logs);
        var name = new ArchiveLogName("k/split", ArchiveLogNames.KeyHash("split"u8));
        var position = ArchiveLogPosition.Origin;
        async Task AppendAsync(int segment)
        {
            var from = position.IsOrigin ? default : ArchiveLogTokens.Encode(position);
            var write = Write(from, ArchiveCommand.Append(1));
            Assert.True(store.Admits(Write(from), ArchiveCommand.Append(1)), $"Segment {segment}'s write from {position} is refused.");
            var next = Position(store.Plan(write));
            _ = Assert.IsType<ArchiveLogWriteOutcome.Applied>((await logs.AppendAsync(name, position, [new([(byte)segment])], token)).Value);
            position = next;
        }

        for (var segment = 0; segment < 31; segment++)
        {
            await AppendAsync(segment);
        }

        var admitted = ArchiveLogTokens.Encode(position);
        Assert.False(store.IsReplaceRequested(admitted));
        Assert.Equal(32UL, Position(store.Plan(Write(admitted, [.. Enumerable.Repeat(ArchiveCommand.Append(1), 17)]))).Units);
        for (var segment = 31; segment < 48; segment++)
        {
            await AppendAsync(segment);
        }

        Assert.Equal(new ArchiveLogPosition(1, 0, 48, 48), position);
        Assert.False(store.Admits(Write(ArchiveLogTokens.Encode(position)), ArchiveCommand.Append(1)));
        _ = await Assert.ThrowsAsync<ArchiveLogRefusedException>(async () => await logs.AppendAsync(name, position, [new([48])], token));
        var read = Assert.IsType<ArchiveLogRead.Read>((await logs.ReadAsync(name, token)).Value);
        using (read)
        {
            Assert.Equal(position, read.Position);
            Assert.Equal(Enumerable.Range(0, 48).Select(static segment => (byte)segment), read.Content.ToArray());
        }
    }

    /// <summary>
    /// HI-I5: a layout whose replace request does not come before its limit, from which the store would refuse an
    /// <c>Append</c> without asking for a <c>Replace</c>, is refused; one just below the limit is taken.
    /// </summary>
    [Theory]
    [InlineData(6UL)]
    [InlineData(7UL)]
    public void ALayoutWhoseReplaceRequestIsNotBelowItsLimitIsRefused(ulong replaceRequestUnits)
    {
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new ArchiveLogLayout("memory", ArchiveLogPacking.Blocks, 4, 6, replaceRequestUnits, MemoryArchiveLogStore.NameRule));
        Assert.Equal(5UL, new ArchiveLogLayout("memory", ArchiveLogPacking.Blocks, 4, 6, 5, MemoryArchiveLogStore.NameRule).ReplaceRequestUnits);
    }

    [Fact]
    public void AnAppendRunCarriesNoEmptyAppend() =>
        Assert.Throws<ArgumentException>(() => Store(MemoryArchiveLogStore.DefaultLayout).Plan(Write(default, ArchiveCommand.Append(0))));

    [Fact]
    public void ATokenPastTheLargestPositionIsCorrupt()
    {
        var store = Store(MemoryArchiveLogStore.DefaultLayout);

        Assert.Throws<ArchiveCorruptException>(() => store.Plan(Write(Token(ArchiveLogTokens.MaxWide, 0, 0), ArchiveCommand.Replace(1))));
        Assert.Throws<ArchiveCorruptException>(() => store.Plan(Write(Token(1, ArchiveLogTokens.MaxWide, 1), ArchiveCommand.Append(1))));
    }

    private static LogArchiveStore Store(ArchiveLogLayout layout) =>
        new(new MemoryArchiveLogStore(layout), new InMemoryObjectStore(), ArchiveLogNames.Hexadecimal);

    private static LogArchiveStore ChunkStore(ChunkArchiveLogStore logs) => new(logs, new InMemoryObjectStore(), ArchiveLogNames.Hexadecimal);

    private static RowToken Token(ulong generation, ulong length, ulong units) => ArchiveLogTokens.Encode(new(generation, 0, length, units));

    private static ArchiveLogPosition Position(RowToken token) => ArchiveLogTokens.PositionOf(token);

    private static ArchiveWrite Write(RowToken token, params ArchiveCommand[] commands) => new(Segment, token, [.. commands]);
}
