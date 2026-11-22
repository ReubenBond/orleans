using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Durable.Storage.Engine.Archive.Lsm.Control;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Archive.Lsm.Tests;

/// <summary>
/// Examples of <see cref="Sweeper"/> (archive §2.7, §8.5): drain garbage, a superseded upload, an abandoned one, and the
/// uploads of a ledger completed meanwhile, deleted one sweep after a sweep first finds it deletable (LSM-I6), and never
/// by the first sweep after a restart; an upload in flight, whose condition holds, never marked; compaction outputs
/// decided by their trailers' inputs, each trailer read once across sweeps; and unreadable objects left in place,
/// reported, and counted, while the rest of the garbage goes.
/// </summary>
public sealed class SweeperTests : IDisposable
{
    private static readonly UInt128 Id = UInt128.Parse("0123456789abcdef0123456789abcdef", NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _leaks.Dispose();

    /// <summary>
    /// An upload of a segment whose commit found another drain's of the same segment applied, an abandoned one whose commit
    /// failed and was never submitted again, and an upload of a ledger which another drain then completed are garbage: a
    /// sweep marks them, the next deletes them, and the referenced drain outputs stay. A sweeper which restarts holds no
    /// marks, so its first sweep deletes nothing.
    /// </summary>
    [Fact]
    public async Task DrainGarbageIsDeletedOneSweepAfterASweepFindsIt()
    {
        await using var world = await ArchiveWorld.StartAsync(Token);
        var ledger = world.Ledgers.OpenContaining([0]).Id;
        _ = await world.Ledgers.ProposeAsync(ledger, [LiveRow.Of("a", RowKind.Append, "a0")], Token);
        _ = await world.Ledgers.EndSegmentAsync(ledger, Token);
        _ = await world.Ledgers.ProposeAsync(ledger, [LiveRow.Of("a", RowKind.Append, "a1")], Token);
        var tail = await world.Ledgers.EndSegmentAsync(ledger, Token) + 1;

        // The abandoned upload's commit fails; the duplicate one's waits while a third drain commits the same segment.
        _ = await Assert.ThrowsAsync<IOException>(() => world.Worker().DrainAsync(world.Ledgers.Ledger(ledger), tail, static (_, _) => throw new IOException("lost"), Token));
        var duplicate = await world.Worker().DrainAsync(
            world.Ledgers.Ledger(ledger),
            tail,
            async (change, cancellationToken) =>
            {
                _ = await world.DrainAndCommitAsync(world.Worker(), world.Ledgers.Ledger(ledger), tail, cancellationToken);
                return await world.CommitAsync(change, cancellationToken);
            },
            Token);
        var drained = Assert.IsType<DrainOutcome.Drained>(duplicate.Value);
        _ = Assert.IsType<ManifestCommitOutcome.Applied>(drained.Outcome.Value);
        Assert.DoesNotContain(Assert.Single(drained.Change.Sections), (await world.Manifest.ReadAsync(Token)).Value.Sections);

        // An upload of the closed ledger from its watermark, abandoned before another drain completes the ledger.
        await world.Ledgers.ReplaceAsync(ledger, Token);
        var closed = world.Ledgers.Ledger(ledger);
        var end = Assert.IsType<LedgerStatus.Closed>(closed.Status.Value).T;
        _ = await Assert.ThrowsAsync<IOException>(() => world.Worker().DrainAsync(closed, end, static (_, _) => throw new IOException("lost"), Token));
        _ = await world.CompleteAsync(world.Worker(), ledger, Token);

        var referenced = (await world.Manifest.ReadAsync(Token)).Value.Sections.Select(static section => section.Object).ToHashSet();
        var garbage = (await ListAsync(world.Store)).Except(referenced).ToHashSet();
        Assert.Equal(3, garbage.Count);
        var sweeper = NewSweeper(world);

        Assert.Equal(new SweepResult(Referenced: 2, Pending: 0, Marked: 3, Deleted: 0, Unreadable: 0), await sweeper.SweepAsync(Token));
        Assert.Equal(new SweepResult(2, 0, 3, 0, 0), await NewSweeper(world).SweepAsync(Token));
        Assert.Equal(new SweepResult(2, 0, 0, 3, 0), await sweeper.SweepAsync(Token));
        Assert.Equal(referenced.Order(), await ListAsync(world.Store));
        Assert.Equal(new SweepResult(2, 0, 0, 0, 0), await sweeper.SweepAsync(Token));
    }

    /// <summary>
    /// An upload whose change the committer has not yet decided is unreferenced while its condition holds: sweeps keep it
    /// unmarked, however many run, and once the change applies it is referenced and stays.
    /// </summary>
    [Fact]
    public async Task AnUploadInFlightIsNeverMarked()
    {
        await using var world = await ArchiveWorld.StartAsync(Token);
        var ledger = world.Ledgers.OpenContaining([0]).Id;
        _ = await world.Ledgers.ProposeAsync(ledger, [LiveRow.Of("a", RowKind.Append, "a0")], Token);
        var tail = await world.Ledgers.EndSegmentAsync(ledger, Token) + 1;
        var sweeper = NewSweeper(world);
        var during = new List<SweepResult>();

        var drained = await world.Worker().DrainAsync(
            world.Ledgers.Ledger(ledger),
            tail,
            async (change, cancellationToken) =>
            {
                for (var sweep = 0; sweep < 3; sweep++)
                {
                    during.Add(await sweeper.SweepAsync(cancellationToken));
                }

                return await world.CommitAsync(change, cancellationToken);
            },
            Token);

        _ = Assert.IsType<ManifestCommitOutcome.Applied>(Assert.IsType<DrainOutcome.Drained>(drained.Value).Outcome.Value);
        Assert.All(during, static result => Assert.Equal(new SweepResult(0, 1, 0, 0, 0), result));
        Assert.Equal(new SweepResult(1, 0, 0, 0, 0), await sweeper.SweepAsync(Token));
        Assert.Single(await ListAsync(world.Store));
    }

    /// <summary>
    /// The deleting sweep evaluates a marked object again against its own manifest read: under the mutant which marks an
    /// upload in flight, the commit which lands between the marking sweep and the next leaves the object referenced, and
    /// the next sweep keeps it.
    /// </summary>
    [Fact]
    public async Task TheDeletingSweepEvaluatesAMarkedObjectAgain()
    {
        await using var world = await ArchiveWorld.StartAsync(Token);
        var ledger = world.Ledgers.OpenContaining([0]).Id;
        _ = await world.Ledgers.ProposeAsync(ledger, [LiveRow.Of("a", RowKind.Append, "a0")], Token);
        var tail = await world.Ledgers.EndSegmentAsync(ledger, Token) + 1;
        var sweeper = NewSweeper(world);
        sweeper.SweepDrainMutant = true;
        SweepResult? marking = null;

        var drained = await world.Worker().DrainAsync(
            world.Ledgers.Ledger(ledger),
            tail,
            async (change, cancellationToken) =>
            {
                marking = await sweeper.SweepAsync(cancellationToken);
                return await world.CommitAsync(change, cancellationToken);
            },
            Token);

        _ = Assert.IsType<ManifestCommitOutcome.Applied>(Assert.IsType<DrainOutcome.Drained>(drained.Value).Outcome.Value);
        Assert.Equal(new SweepResult(Referenced: 0, Pending: 0, Marked: 1, Deleted: 0, Unreadable: 0), marking);
        Assert.Equal(new SweepResult(Referenced: 1, Pending: 0, Marked: 0, Deleted: 0, Unreadable: 0), await sweeper.SweepAsync(Token));
        Assert.Single(await ListAsync(world.Store));
    }

    /// <summary>
    /// A sweep reads the manifest after it lists, through the view the process's committer shares, so the manifest holds
    /// every commit made before the listing: a drain committed during the listing is referenced, and a compaction output
    /// whose input was committed during the listing is pending. The mutant which reads the manifest first finds the drain
    /// output pending and marks the compaction output, which its commit can still reference (LSM-I6).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ASweepReadsTheManifestAfterItLists(bool snapshotBeforeList)
    {
        await using var world = await ArchiveWorld.StartAsync(Token);
        var ledger = world.Ledgers.OpenContaining([0]).Id;
        _ = await world.Ledgers.ProposeAsync(ledger, [LiveRow.Of("a", RowKind.Append, "a0")], Token);
        var tail = await world.Ledgers.EndSegmentAsync(ledger, Token) + 1;
        ManifestChange uploaded = default;
        _ = await Assert.ThrowsAsync<IOException>(() => world.Worker().DrainAsync(
            world.Ledgers.Ledger(ledger),
            tail,
            (change, _) =>
            {
                uploaded = change;
                throw new IOException("held");
            },
            Token));
        var sweeper = new Sweeper(world.Store, world.Manifest, TimeProvider.System, NullLogger<Sweeper>.Instance) { SnapshotBeforeListMutant = snapshotBeforeList };
        world.Store.BeforeList = async prefix =>
        {
            world.Store.BeforeList = null;
            _ = Assert.IsType<ManifestCommitOutcome.Applied>((await world.CommitAsync(uploaded, Token)).Value);
        };

        var drain = await sweeper.SweepAsync(Token);

        Assert.Equal(snapshotBeforeList ? new SweepResult(0, 1, 0, 0, 0) : new SweepResult(1, 0, 0, 0, 0), drain);

        // A compaction over an input which a drain commits during the next listing.
        _ = await world.Ledgers.ProposeAsync(ledger, [LiveRow.Of("b", RowKind.Append, "b0")], Token);
        var next = await world.Ledgers.EndSegmentAsync(ledger, Token) + 1;
        ObjectName? compaction = null;
        world.Store.BeforeList = async prefix =>
        {
            world.Store.BeforeList = null;
            var input = Assert.Single((await world.DrainAndCommitAsync(world.Worker(), world.Ledgers.Ledger(ledger), next, Token)).Sections);
            compaction = await UploadCompactionAsync(world.Store, [new SectionPosition(input.Object, input.Offset)], Id);
        };

        var compacted = await sweeper.SweepAsync(Token);

        Assert.Equal(snapshotBeforeList ? new SweepResult(1, 1, 1, 0, 0) : new SweepResult(2, 1, 0, 0, 0), compacted);
        Assert.Contains(compaction, await ListAsync(world.Store));
    }

    /// <summary>
    /// A compaction output's condition is its trailer's inputs, every one of which the manifest must list at its
    /// position: one whose input is listed stays; one with an input no longer listed, one of whose two inputs is gone,
    /// and one naming a listed object at another offset go one sweep after they are found. The sweeper reads each
    /// trailer once across its sweeps, in one read of the object's tail.
    /// </summary>
    [Fact]
    public async Task CompactionOutputsAreDecidedByTheirInputsReadOnce()
    {
        await using var world = await ArchiveWorld.StartAsync(Token);
        var ledger = world.Ledgers.OpenContaining([0]).Id;
        _ = await world.Ledgers.ProposeAsync(ledger, [LiveRow.Of("a", RowKind.Append, "a0")], Token);
        var drain = await world.DrainOpenAsync(world.Worker(), ledger, Token);
        var input = Assert.Single(drain.Sections);
        var listed = new SectionPosition(input.Object, input.Offset);
        var gone = new SectionPosition(new ObjectName("d/9/0/gone"), 0);
        var pending = await UploadCompactionAsync(world.Store, [listed], Id);
        var lost = await UploadCompactionAsync(world.Store, [gone], Id + 1);
        var half = await UploadCompactionAsync(world.Store, [listed, gone], Id + 2);
        var moved = await UploadCompactionAsync(world.Store, [listed with { Offset = listed.Offset + 1 }], Id + 3);
        var sweeper = NewSweeper(world);

        Assert.Equal(new SweepResult(1, 1, 3, 0, 0), await sweeper.SweepAsync(Token));
        Assert.Equal(new SweepResult(1, 1, 0, 3, 0), await sweeper.SweepAsync(Token));
        Assert.Equal(new SweepResult(1, 1, 0, 0, 0), await sweeper.SweepAsync(Token));

        Assert.Equal([pending, input.Object], await ListAsync(world.Store));
        Assert.All<ObjectName>([pending, lost, half, moved], name => Assert.Equal(1, world.RangedReads(name)));
        Assert.Equal(0, world.DrainOutputReads());
    }

    /// <summary>
    /// Unreadable objects are left in place, logged, and counted in the sweep's result and the gauge, and the sweep
    /// decides the rest of its listing: a name under <c>d/</c> which names no drain, one listed first; a compaction output
    /// whose trailer fails its CRC-32C; one whose trailer holds a drain's condition; and one whose listed length passes the
    /// longest run object. Garbage listed after them is marked, and deleted by the next sweep, which reads each corrupt
    /// trailer again.
    /// </summary>
    [Fact]
    public async Task UnreadableObjectsStayCountedWhileTheRestOfTheGarbageGoes()
    {
        await using var world = await ArchiveWorld.StartAsync(Token);
        using var gauge = new UnreadableGauge();
        var sweeper = NewSweeper(world);
        Assert.Equal(new SweepResult(0, 0, 0, 0, 0), await sweeper.SweepAsync(Token));

        var first = new ObjectName("d/00/0/a");
        var unnamed = new ObjectName("d/x/0/a");
        await CreateAsync(world.Store, first, [1]);
        await CreateAsync(world.Store, unnamed, [1]);
        var gone = new SectionPosition(new ObjectName("d/9/0/gone"), 0);
        var damaged = await UploadCompactionAsync(world.Store, [gone], Id, damage: true);
        var mismatched = await UploadCompactionAsync(world.Store, [gone], Id + 1, condition: new RunObjectCondition.Drain(new LedgerId(1), Slot.Zero));
        var huge = await UploadCompactionAsync(world.Store, [gone], Id + 2);
        var garbage = await UploadCompactionAsync(world.Store, [gone], Id + 3);
        world.Store.Listed = listed => listed.Name == huge ? listed with { Length = (long)RunObjectFormat.MaxObjectLength + 1 } : listed;

        Assert.Equal(new SweepResult(0, 0, 1, 0, 5), await sweeper.SweepAsync(Token));
        Assert.Equal(new SweepResult(0, 0, 0, 1, 5), await sweeper.SweepAsync(Token));

        Assert.DoesNotContain(garbage, await ListAsync(world.Store));
        Assert.Equal(5, (await ListAsync(world.Store)).Count);
        Assert.Equal([0L, 5L, 5L], gauge.Values);
        Assert.Equal(2, world.RangedReads(damaged));
        Assert.Equal(2, world.RangedReads(mismatched));
        Assert.Equal(0, world.RangedReads(huge));
    }

    /// <summary>
    /// An object another sweeper deletes between this sweep's listing and its read of the object's trailer is gone: the
    /// sweep counts it nowhere, and reports no corruption.
    /// </summary>
    [Fact]
    public async Task AnObjectDeletedAfterTheListingIsGone()
    {
        await using var world = await ArchiveWorld.StartAsync(Token);
        var compaction = await UploadCompactionAsync(world.Store, [new SectionPosition(new ObjectName("d/9/0/gone"), 0)], Id);
        world.Store.AfterList = async prefix =>
        {
            if (prefix == RunObjectNames.CompactionPrefix)
            {
                await world.Objects.DeleteAsync(compaction, Token);
            }
        };

        Assert.Equal(new SweepResult(0, 0, 0, 0, 0), await NewSweeper(world).SweepAsync(Token));
        Assert.Equal(1, world.RangedReads(compaction));
    }

    /// <summary>
    /// A failure of the object store ends the sweep and leaves its marks standing: an object whose delete failed stays
    /// marked, and the next sweep deletes it.
    /// </summary>
    [Fact]
    public async Task MarksStandWhenADeleteFails()
    {
        await using var world = await ArchiveWorld.StartAsync(Token);
        var garbage = await UploadCompactionAsync(world.Store, [new SectionPosition(new ObjectName("d/9/0/gone"), 0)], Id);
        var sweeper = NewSweeper(world);
        Assert.Equal(new SweepResult(0, 0, 1, 0, 0), await sweeper.SweepAsync(Token));

        world.Store.FailDeletes(1);
        _ = await Assert.ThrowsAsync<IOException>(() => sweeper.SweepAsync(Token));

        Assert.Equal(new SweepResult(0, 0, 0, 1, 0), await sweeper.SweepAsync(Token));
        Assert.DoesNotContain(garbage, await ListAsync(world.Store));
    }

    /// <summary>
    /// A sweeper runs one sweep at a time: a sweep begun while another runs is refused, and the first completes.
    /// </summary>
    [Fact]
    public async Task ASweeperRunsOneSweepAtATime()
    {
        await using var world = await ArchiveWorld.StartAsync(Token);
        var sweeper = NewSweeper(world);
        world.Store.HoldReads();
        var first = sweeper.SweepAsync(Token);

        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => sweeper.SweepAsync(Token));
        world.Store.ReleaseReads();
        Assert.Equal(new SweepResult(0, 0, 0, 0, 0), await first);
    }

    private static Sweeper NewSweeper(ArchiveWorld world) => new(world.Store, new ManifestView(world.Store), TimeProvider.System, NullLogger<Sweeper>.Instance);

    private static async Task<List<ObjectName>> ListAsync(IObjectStore store) =>
        [.. (await store.ListAsync(string.Empty, cancellationToken: Token).ToListAsync(Token)).Select(static listed => listed.Name).Where(static name => name != ObjectName.Manifest)];

    private static async Task CreateAsync(IObjectStore store, ObjectName name, byte[] content)
    {
        using var buffer = ArcBuffer.Wrap(content);
        _ = await store.CreateAsync(name, buffer, null, Token);
    }

    /// <summary>
    /// Uploads a compaction output of one row whose inputs are <paramref name="inputs"/>, or whose trailer holds
    /// <paramref name="condition"/> under a compaction output's name, with its trailer's CRC-32C damaged when
    /// <paramref name="damage"/>, and returns its name.
    /// </summary>
    private static async Task<ObjectName> UploadCompactionAsync(
        IObjectStore store,
        SectionPosition[] inputs,
        UInt128 id,
        bool damage = false,
        RunObjectCondition? condition = null)
    {
        var data = new RunObjectData(
            [new RunSectionData(KeyRange.Root, [new RunRowData([1], new Tag(new LedgerId(1), Slot.Zero, 0), RowKind.Append, [1])])],
            condition ?? new RunObjectCondition.Compaction([.. inputs.Order()]),
            new ArchiveOptions().SectionSettings);
        var name = RunObjectNames.Compaction(KeyRange.Root, id);
        using var encoded = data.Encode();
        var bytes = encoded.Content.ToArray();
        if (damage)
        {
            // The trailer's CRC-32C covers the byte before its length.
            bytes[^9] ^= 0xff;
        }

        await CreateAsync(store, name, bytes);
        return name;
    }

    /// <summary>
    /// Records the values of the gauge of unreadable objects which the sweeps of its creator's flow record. A listener
    /// hears every sweep of the process, and a sweep records its measurements on its caller's flow, so the gauge keeps
    /// only those made where it is current, and sweeps of tests running alongside leave it unchanged.
    /// </summary>
    private sealed class UnreadableGauge : IDisposable
    {
        private static readonly AsyncLocal<UnreadableGauge?> Current = new();
        private readonly ConcurrentQueue<long> _values = [];
        private readonly MeterListener _listener;

        public UnreadableGauge()
        {
            Current.Value = this;
            _listener = new MeterListener
            {
                InstrumentPublished = static (instrument, listener) =>
                {
                    if (instrument.Meter.Name == ArchiveMetrics.MeterName && instrument.Name == SweepMetrics.UnreadableObjectsName)
                    {
                        listener.EnableMeasurementEvents(instrument);
                    }
                },
            };
            _listener.SetMeasurementEventCallback<long>((_, value, _, _) =>
            {
                if (Current.Value == this)
                {
                    _values.Enqueue(value);
                }
            });
            _listener.Start();
        }

        public long[] Values => [.. _values];

        public void Dispose() => _listener.Dispose();
    }
}
