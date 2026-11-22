using System.Text;
using Orleans.Durable.Storage.Diagnostics;
using Orleans.Durable.Storage.Engine.Archive.Control;
using Orleans.Durable.Storage.Engine.Archive.Lsm.Control;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Archive.Lsm.Tests;

/// <summary>
/// Examples of <see cref="DrainWorker"/> (archive §2.3, §8.1): an open ledger's closed segments drained one at a time, in
/// order, each closed by the next segment's start, and a closed one's last segment by its <c>T</c>, which completes it;
/// each drain one object of one section, holding exactly its segment's rows, sorted, at the deepest grid depth containing
/// the ledger's range, or several past the object bound, so every watermark is a segment start or <c>T</c> (LSM-I2); a
/// segment holding only a marker drained without an object; a failure-triggered drain to its marker's slot, reading
/// nothing past it; an open segment, and backpressure before a segment's end, which drain nothing; the next drain from
/// the last applied one's end without a manifest read, from the manifest after a superseded one, and a pending change
/// submitted again after an unknown outcome (LSM-I1); a watermark inside a segment, which is corrupt; and a ledger which
/// is covered or dropped uploads nothing.
/// </summary>
public sealed class DrainWorkerTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _leaks.Dispose();

    /// <summary>
    /// LSM-I2: a drain of an open ledger takes the segment from its watermark once a decided marker closes it below the
    /// tail, uploading one drain output <c>d/{S}/{a}/{id}</c> whose one section holds exactly the segment's rows, by key
    /// and then tag, and commits a change to the marker's slot which is not complete. The next drain starts from there,
    /// without a manifest read, and finds the marker's segment open; once a later marker closes it, it drains. Each upload,
    /// the drain outputs' and the manifest's, carries its writer's CRC-32C of the bytes it verified (archive §3).
    /// </summary>
    [Fact]
    public async Task AnOpenLedgerDrainsEachClosedSegmentAndTheNextDrainStartsFromItsEnd()
    {
        await using var world = await ArchiveWorld.StartAsync(Token);
        var ledger = world.Ledgers.OpenContaining([0]).Id;
        var b = await world.Ledgers.ProposeAsync(ledger, [LiveRow.Of("b", RowKind.Append, "b0")], Token);
        var ac = await world.Ledgers.ProposeAsync(ledger, [LiveRow.Of("c", RowKind.Replace, "c0"), LiveRow.Of("a", RowKind.Append, "a0")], Token);
        var b1 = await world.Ledgers.ProposeAsync(ledger, [LiveRow.Of("b", RowKind.Append, "b1")], Token);
        var marker = await world.Ledgers.EndSegmentAsync(ledger, Token);
        var worker = world.Worker();

        var first = await world.DrainAndCommitAsync(worker, world.Ledgers.Ledger(ledger), await world.Ledgers.ConfirmOpenAsync(ledger, Token), Token);

        Assert.Equal((Slot.Zero, new Slot(3), false), (first.From, first.Below, first.Complete));
        Assert.Equal(new Slot(3), marker);
        var section = Assert.Single(first.Sections);
        Assert.StartsWith("d/1/0/", section.Object.Value, StringComparison.Ordinal);
        Assert.Equal(KeyRange.Root, section.Partition);
        (string, Tag)[] expected = [("a", ac[1]), ("b", b[0]), ("b", b1[0]), ("c", ac[0])];
        Assert.Equal(expected, await RowsAsync(world, section.Object));

        var (reads, calls) = (world.ManifestReads, world.CallMark);
        _ = await world.Ledgers.ProposeAsync(ledger, [LiveRow.Of("d", RowKind.Append, "d0")], Token);
        Assert.Equal(new DrainOutcome(new DrainOutcome.OpenSegment(marker)), await world.DrainOnceAsync(worker, world.Ledgers.Ledger(ledger), await world.Ledgers.ConfirmOpenAsync(ledger, Token), Token));
        Assert.Equal(0, world.CreatesSince(calls));
        _ = await world.Ledgers.EndSegmentAsync(ledger, Token);
        var second = await world.DrainAndCommitAsync(worker, world.Ledgers.Ledger(ledger), await world.Ledgers.ConfirmOpenAsync(ledger, Token), Token);

        Assert.Equal((new Slot(3), new Slot(5)), (second.From, second.Below));
        Assert.StartsWith("d/1/3/", Assert.Single(second.Sections).Object.Value, StringComparison.Ordinal);
        Assert.Equal(reads, world.ManifestReads);
        Assert.Equal(new Slot(5), (await world.Manifest.ReadAsync(Token)).Value.RecordedWatermark(ledger));
        Assert.Equal([Slot.Zero, new Slot(3), new Slot(5)], await world.Ledgers.DecidedStartsAsync(world.Ledgers.Ledger(ledger), await world.Ledgers.ConfirmOpenAsync(ledger, Token), Token));
        Assert.Collection(
            world.Store.Writes,
            static write => AssertCarriesItsCrc32C(write, "Create(d/1/0/"),
            static write => AssertCarriesItsCrc32C(write, "Replace(manifest)"),
            static write => AssertCarriesItsCrc32C(write, "Create(d/1/3/"),
            static write => AssertCarriesItsCrc32C(write, "Replace(manifest)"));
    }

    /// <summary>
    /// A segment the decided batches have not closed below the tail drains nothing: the drain answers
    /// <see cref="DrainOutcome.OpenSegment"/> from its start, uploading and submitting nothing.
    /// </summary>
    [Fact]
    public async Task AnOpenSegmentDrainsNothing()
    {
        await using var world = await ArchiveWorld.StartAsync(Token);
        var ledger = world.Ledgers.OpenContaining([0]).Id;
        _ = await world.Ledgers.ProposeAsync(ledger, [LiveRow.Of("a", RowKind.Append, "a0")], Token);
        _ = await world.Ledgers.ProposeAsync(ledger, [LiveRow.Of("a", RowKind.Append, "a1")], Token);
        var tail = await world.Ledgers.ConfirmOpenAsync(ledger, Token);
        var submitted = false;

        var outcome = await world.Worker().DrainAsync(world.Ledgers.Ledger(ledger), tail, (change, token) =>
        {
            submitted = true;
            return world.CommitAsync(change, token);
        }, Token);

        Assert.Equal(new DrainOutcome(new DrainOutcome.OpenSegment(Slot.Zero)), outcome);
        Assert.False(submitted);
        Assert.Empty(await world.Objects.ListAsync("d/", cancellationToken: Token).ToListAsync(Token));
    }

    /// <summary>
    /// A segment which holds only a marker drains without an object: its change has no sections and advances the
    /// watermark past the marker.
    /// </summary>
    [Fact]
    public async Task ASegmentHoldingOnlyAMarkerDrainsWithoutAnObject()
    {
        await using var world = await ArchiveWorld.StartAsync(Token);
        var ledger = world.Ledgers.OpenContaining([0]).Id;
        _ = await world.Ledgers.ProposeAsync(ledger, [LiveRow.Of("a", RowKind.Append, "a0")], Token);
        var first = await world.Ledgers.EndSegmentAsync(ledger, Token);
        var second = await world.Ledgers.EndSegmentAsync(ledger, Token);
        var tail = await world.Ledgers.ConfirmOpenAsync(ledger, Token);
        var worker = world.Worker();
        _ = await world.DrainAndCommitAsync(worker, world.Ledgers.Ledger(ledger), tail, Token);

        var calls = world.CallMark;
        var marker = await world.DrainAndCommitAsync(worker, world.Ledgers.Ledger(ledger), tail, Token);

        Assert.Equal((first, second, false), (marker.From, marker.Below, marker.Complete));
        Assert.Empty(marker.Sections);
        Assert.Equal(0, world.CreatesSince(calls));
        Assert.Equal(second, (await world.Manifest.ReadAsync(Token)).Value.RecordedWatermark(ledger));
    }

    /// <summary>
    /// A failure-triggered drain (archive §8.1), its caller's loop: once the marker which ends the open segment is decided,
    /// drains bounded by the slot after the marker take each closed segment below the marker's slot, in order, and stop
    /// there, though a later segment is closed too, reading no batch past the marker; the next finds the marker's segment
    /// open.
    /// </summary>
    [Fact]
    public async Task AFailureTriggeredDrainEndsAtItsMarkersSlot()
    {
        await using var world = await ArchiveWorld.StartAsync(Token);
        var ledger = world.Ledgers.OpenContaining([0]).Id;
        _ = await world.Ledgers.ProposeAsync(ledger, [LiveRow.Of("a", RowKind.Append, "a0")], Token);
        _ = await world.Ledgers.EndSegmentAsync(ledger, Token);
        _ = await world.Ledgers.ProposeAsync(ledger, [LiveRow.Of("a", RowKind.Append, "a1")], Token);
        var marker = await world.Ledgers.EndSegmentAsync(ledger, Token);
        _ = await world.Ledgers.ProposeAsync(ledger, [LiveRow.Of("a", RowKind.Append, "a2")], Token);
        _ = await world.Ledgers.EndSegmentAsync(ledger, Token);
        _ = await world.Ledgers.ConfirmOpenAsync(ledger, Token);
        world.Ledgers.Transport.Saturated = (_, slot) => slot > marker;
        var drains = new List<ManifestChange.Drain>();
        var worker = world.Worker();

        DrainOutcome outcome;
        while ((outcome = await world.DrainOnceAsync(worker, world.Ledgers.Ledger(ledger), marker + 1, Token)).Value is DrainOutcome.Drained drained)
        {
            _ = Assert.IsType<ManifestCommitOutcome.Applied>(drained.Outcome.Value);
            drains.Add(drained.Change);
        }

        Assert.Equal([(Slot.Zero, new Slot(1)), (new Slot(1), marker)], drains.Select(static drain => (drain.From, drain.Below)));
        Assert.Equal(new DrainOutcome(new DrainOutcome.OpenSegment(marker)), outcome);
        Assert.Equal(0, world.Ledgers.Transport.Answered);
        Assert.Equal(marker, (await world.Manifest.ReadAsync(Token)).Value.RecordedWatermark(ledger));
    }

    /// <summary>
    /// A closed ledger drains its segments in order, its last to its <c>T</c>, which completes it, and <c>DrainedBelow</c>
    /// passes it. A later drain by the same worker is covered without a create or a manifest read, and so is one by
    /// another worker which reads the manifest.
    /// </summary>
    [Fact]
    public async Task AClosedLedgersSegmentsDrainToItsTailAndThenItIsCovered()
    {
        await using var world = await ArchiveWorld.StartAsync(Token);
        var ledger = world.Ledgers.OpenContaining([0]).Id;
        _ = await world.Ledgers.ProposeAsync(ledger, [LiveRow.Of("a", RowKind.Append, "a0")], Token);
        _ = await world.Ledgers.EndSegmentAsync(ledger, Token);
        _ = await world.Ledgers.ProposeAsync(ledger, [LiveRow.Of("a", RowKind.Append, "a1")], Token);
        await world.Ledgers.ReplaceAsync(ledger, Token);
        var closed = world.Ledgers.Ledger(ledger);
        var worker = world.Worker();

        var first = await world.DrainAndCommitAsync(worker, closed, new Slot(3), Token);
        var last = await world.DrainAndCommitAsync(worker, closed, new Slot(3), Token);

        Assert.Equal((Slot.Zero, new Slot(1), false), (first.From, first.Below, first.Complete));
        Assert.Equal((new Slot(1), new Slot(3), true), (last.From, last.Below, last.Complete));
        var manifest = (await world.Manifest.ReadAsync(Token)).Value;
        Assert.True(manifest.IsComplete(ledger));
        Assert.Equal(new LedgerId(2), manifest.DrainedBelow);

        var calls = world.CallMark;
        Assert.Equal(new DrainOutcome(DrainOutcome.Covered.Instance), await world.DrainOnceAsync(worker, closed, new Slot(3), Token));
        Assert.Equal(calls, world.CallMark);
        Assert.Equal(new DrainOutcome(DrainOutcome.Covered.Instance), await world.DrainOnceAsync(world.Worker(), closed, new Slot(3), Token));
        Assert.Equal(0, world.CreatesSince(calls));
    }

    /// <summary>
    /// A closed ledger whose last segment holds only the marker which ended its predecessor completes by a drain without
    /// an object, and one which holds no batch completes by a drain of no slots, which uploads nothing either.
    /// </summary>
    [Fact]
    public async Task ALedgerWithNoRowsLeftCompletesWithoutAnObject()
    {
        await using var world = await ArchiveWorld.StartAsync(Token);
        var ledger = world.Ledgers.OpenContaining([0]).Id;
        _ = await world.Ledgers.ProposeAsync(ledger, [LiveRow.Of("a", RowKind.Append, "a0")], Token);
        _ = await world.Ledgers.ProposeAsync(ledger, [LiveRow.Of("a", RowKind.Append, "a1")], Token);
        var worker = world.Worker();
        _ = await world.DrainOpenAsync(worker, ledger, Token);
        await world.Ledgers.ReplaceAsync(ledger, Token);
        var empty = world.Ledgers.OpenContaining([0]).Id;
        await world.Ledgers.ReplaceAsync(empty, Token);

        var calls = world.CallMark;
        var drained = await world.CompleteAsync(worker, ledger, Token);
        var none = await world.CompleteAsync(world.Worker(), empty, Token);

        Assert.Equal((new Slot(2), new Slot(3), true), (drained.From, drained.Below, drained.Complete));
        Assert.Empty(drained.Sections);
        Assert.Equal((Slot.Zero, Slot.Zero, true), (none.From, none.Below, none.Complete));
        Assert.Empty(none.Sections);
        Assert.Equal(0, world.CreatesSince(calls));
        Assert.Equal(new LedgerId(3), (await world.Manifest.ReadAsync(Token)).Value.DrainedBelow);
    }

    /// <summary>
    /// LSM-I1, LSM-I2: two drains of one segment from one watermark take the same whole segment, so the one whose commit
    /// follows the other's finds its goal and answers <c>Applied</c>, its upload unreferenced; its worker's next drain
    /// starts from the segment's end.
    /// </summary>
    [Fact]
    public async Task TwoDrainsOfOneSegmentCommitItOnce()
    {
        await using var world = await ArchiveWorld.StartAsync(Token);
        var ledger = world.Ledgers.OpenContaining([0]).Id;
        _ = await world.Ledgers.ProposeAsync(ledger, [LiveRow.Of("a", RowKind.Append, "a0")], Token);
        var marker = await world.Ledgers.EndSegmentAsync(ledger, Token);
        var tail = await world.Ledgers.ConfirmOpenAsync(ledger, Token);
        var (winner, loser) = (world.Worker(), world.Worker());
        ManifestChange.Drain? won = null;

        var late = Assert.IsType<DrainOutcome.Drained>((await loser.DrainAsync(
            world.Ledgers.Ledger(ledger),
            tail,
            async (change, token) =>
            {
                won = await world.DrainAndCommitAsync(winner, world.Ledgers.Ledger(ledger), tail, token);
                return await world.CommitAsync(change, token);
            },
            Token)).Value);

        _ = Assert.IsType<ManifestCommitOutcome.Applied>(late.Outcome.Value);
        Assert.Equal((won!.From, won.Below), (late.Change.From, late.Change.Below));
        Assert.Equal(won.Sections, (await world.Manifest.ReadAsync(Token)).Value.Sections);
        Assert.DoesNotContain(Assert.Single(late.Change.Sections), won.Sections);
        Assert.Equal(new DrainOutcome(new DrainOutcome.OpenSegment(marker)), await world.DrainOnceAsync(loser, world.Ledgers.Ledger(ledger), tail, Token));
    }

    /// <summary>
    /// A drain answered <c>Superseded</c>, which no drain of a whole segment meets, raises a debug assertion in a Debug build
    /// (LSM-I2); in a Release build, its next start comes from a manifest value at least as new as the one which decided
    /// it, which a refresh of the process's view reads.
    /// </summary>
    [Fact]
    public async Task ASupersededDrainStartsAgainFromTheManifest()
    {
        await using var world = await ArchiveWorld.StartAsync(Token);
        var ledger = world.Ledgers.OpenContaining([0]).Id;
        _ = await world.Ledgers.ProposeAsync(ledger, [LiveRow.Of("a", RowKind.Append, "a0")], Token);
        var marker = await world.Ledgers.EndSegmentAsync(ledger, Token);
        var tail = await world.Ledgers.ConfirmOpenAsync(ledger, Token);
        var stale = new ManifestView(world.Store);
        _ = await stale.ReadAsync(Token);
        var applied = await world.DrainAndCommitAsync(world.Worker(), world.Ledgers.Ledger(ledger), tail, Token);
        var epoch = (await world.Manifest.ReadAsync(Token)).Value.Epoch;
        var worker = world.Worker(stale);

        var (since, test) = (DebugAssertions.RaisedCount, TestContext.Current.Test);
        DrainOutcome superseded;
        try
        {
            superseded = await worker.DrainAsync(
                world.Ledgers.Ledger(ledger),
                tail,
                (_, _) => Task.FromResult<ManifestCommitOutcome>(new ManifestCommitOutcome.Superseded(epoch)),
                Token);
        }
        catch (DebugAssertionException)
        {
            _ = Assert.Single(DebugAssertions.TakeRaisedBy(test, since));
            return;
        }

        Assert.Equal("Release", TestDiagnostics.Configuration);
        Assert.Equal(Slot.Zero, Assert.IsType<DrainOutcome.Drained>(superseded.Value).Change.From);
        var reads = world.ManifestReads;
        Assert.Equal(new DrainOutcome(new DrainOutcome.OpenSegment(applied.Below)), await world.DrainOnceAsync(worker, world.Ledgers.Ledger(ledger), tail, Token));
        Assert.Equal(marker, applied.Below);
        Assert.Equal(reads + 1, world.ManifestReads);
    }

    /// <summary>
    /// A drain output the store refuses because its content changed after the writer verified it reports the defect,
    /// which stops the process (archive §9): the drain fails with the refusal, creating and submitting nothing.
    /// </summary>
    [Fact]
    public async Task ADrainOutputRefusedForChangedContentStopsTheProcess()
    {
        await using var world = await ArchiveWorld.StartAsync(Token);
        var ledger = world.Ledgers.OpenContaining([0]).Id;
        _ = await world.Ledgers.ProposeAsync(ledger, [LiveRow.Of("a", RowKind.Append, "a0")], Token);
        var tail = await world.Ledgers.EndSegmentAsync(ledger, Token) + 1;
        var since = FatalErrors.ReportedCount;
        world.Store.ChangeContent = true;
        var submitted = false;

        _ = await Assert.ThrowsAsync<ObjectContentChangedException>(() => world.Worker().DrainAsync(world.Ledgers.Ledger(ledger), tail, (_, _) =>
        {
            submitted = true;
            return Task.FromResult<ManifestCommitOutcome>(new ManifestCommitOutcome.Applied(1));
        }, Token));

        var report = Assert.Single(FatalErrors.TakeMine(since));
        Assert.Equal(FatalErrorSite.Defect, report.Site);
        _ = Assert.IsType<ObjectContentChangedException>(report.Failure.Exception);
        Assert.False(submitted);
        Assert.Empty(await world.Objects.ListAsync("d/", cancellationToken: Token).ToListAsync(Token));
    }

    /// <summary>
    /// A commit whose outcome is unknown leaves the change pending: the worker's next drain of the ledger submits the same
    /// change again, uploading nothing.
    /// </summary>
    [Fact]
    public async Task AChangeWhoseCommitFailedIsSubmittedAgainWithoutAnUpload()
    {
        await using var world = await ArchiveWorld.StartAsync(Token);
        var ledger = world.Ledgers.OpenContaining([0]).Id;
        _ = await world.Ledgers.ProposeAsync(ledger, [LiveRow.Of("a", RowKind.Append, "a0")], Token);
        var tail = await world.Ledgers.EndSegmentAsync(ledger, Token) + 1;
        var worker = world.Worker();
        ManifestChange? failed = null;

        _ = await Assert.ThrowsAsync<IOException>(() => worker.DrainAsync(world.Ledgers.Ledger(ledger), tail, (change, _) =>
        {
            failed = change;
            throw new IOException("The commit's outcome is unknown.");
        }, Token));

        var calls = world.CallMark;
        var drained = Assert.IsType<DrainOutcome.Drained>((await world.DrainOnceAsync(worker, world.Ledgers.Ledger(ledger), tail, Token)).Value);
        Assert.Equal(failed, drained.Change);
        _ = Assert.IsType<ManifestCommitOutcome.Applied>(drained.Outcome.Value);
        Assert.Equal(0, world.CreatesSince(calls));
    }

    /// <summary>
    /// A commit which applied and then lost its answer: the same worker submits the pending change again, which answers
    /// <c>Applied</c>; a fresh worker over the process's stale manifest view, which recorded nothing, drains the same
    /// segment again from the old watermark, whose commit finds the goal, and then drains on from the applied end.
    /// </summary>
    [Fact]
    public async Task AnAnswerLostAfterTheCommitAppliedIsDecidedByResubmissionOrByTheManifest()
    {
        await using var world = await ArchiveWorld.StartAsync(Token);
        var ledger = world.Ledgers.OpenContaining([0]).Id;
        _ = await world.Ledgers.ProposeAsync(ledger, [LiveRow.Of("a", RowKind.Append, "a0")], Token);
        var first = await world.Ledgers.EndSegmentAsync(ledger, Token);
        _ = await world.Ledgers.ProposeAsync(ledger, [LiveRow.Of("a", RowKind.Append, "a1")], Token);
        var second = await world.Ledgers.EndSegmentAsync(ledger, Token);
        var tail = await world.Ledgers.ConfirmOpenAsync(ledger, Token);
        var stale = new ManifestView(world.Store);
        _ = await stale.ReadAsync(Token);
        var worker = world.Worker(stale);

        _ = await Assert.ThrowsAsync<IOException>(() => worker.DrainAsync(world.Ledgers.Ledger(ledger), tail, async (change, token) =>
        {
            _ = await world.CommitAsync(change, token);
            throw new IOException("The answer was lost after the commit applied.");
        }, Token));

        var calls = world.CallMark;
        var resubmitted = Assert.IsType<DrainOutcome.Drained>((await world.DrainOnceAsync(worker, world.Ledgers.Ledger(ledger), tail, Token)).Value);
        Assert.Equal((Slot.Zero, first), (resubmitted.Change.From, resubmitted.Change.Below));
        _ = Assert.IsType<ManifestCommitOutcome.Applied>(resubmitted.Outcome.Value);
        Assert.Equal(0, world.CreatesSince(calls));

        var restarted = world.Worker(stale);
        var again = Assert.IsType<DrainOutcome.Drained>((await world.DrainOnceAsync(restarted, world.Ledgers.Ledger(ledger), tail, Token)).Value);
        Assert.Equal((Slot.Zero, first), (again.Change.From, again.Change.Below));
        _ = Assert.IsType<ManifestCommitOutcome.Applied>(again.Outcome.Value);
        Assert.DoesNotContain(Assert.Single(again.Change.Sections), (await world.Manifest.ReadAsync(Token)).Value.Sections);
        var progressed = await world.DrainAndCommitAsync(restarted, world.Ledgers.Ledger(ledger), tail, Token);
        Assert.Equal((first, second), (progressed.From, progressed.Below));
    }

    /// <summary>
    /// A bound past a closed ledger's <c>T</c>, or past the contiguous tail the reader knows of an open one, is refused.
    /// </summary>
    [Fact]
    public async Task ABoundPastTheDecidedEndIsRefused()
    {
        await using var world = await ArchiveWorld.StartAsync(Token);
        var ledger = world.Ledgers.OpenContaining([0]).Id;
        _ = await world.Ledgers.ProposeAsync(ledger, [LiveRow.Of("a", RowKind.Append, "a0")], Token);
        var tail = await world.Ledgers.ConfirmOpenAsync(ledger, Token);

        _ = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => world.DrainOnceAsync(world.Worker(), world.Ledgers.Ledger(ledger), tail + 1, Token));
        await world.Ledgers.ReplaceAsync(ledger, Token);
        _ = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => world.DrainOnceAsync(world.Worker(), world.Ledgers.Ledger(ledger), tail + 1, Token));
        _ = await Assert.ThrowsAsync<ArgumentNullException>(() => world.Worker().DrainAsync(world.Ledgers.Ledger(ledger), tail, null!, Token));
    }

    /// <summary>
    /// A segment whose rows would take a drain output past the section size uploads them, sorted, across as many objects
    /// of one section each as hold them, each ending before the row which would pass the size, so a size which two rows'
    /// projected lengths and the object's fixed parts reach exactly holds two rows an object; one change commits them, and
    /// one key's rows, split across them, read back in order.
    /// </summary>
    [Fact]
    public async Task ASegmentPastTheSectionSizeSplitsAcrossObjectsInOneChange()
    {
        await using var world = await ArchiveWorld.StartAsync(Token);
        var ledger = world.Ledgers.OpenContaining([0]).Id;
        for (var index = 0; index < 6; index++)
        {
            _ = await world.Ledgers.ProposeAsync(ledger, [LiveRow.Of("k", RowKind.Append, $"k-{index}")], Token);
        }

        var tail = await world.Ledgers.EndSegmentAsync(ledger, Token) + 1;
        var bound = RunObjectFormat.DrainOutputFixedLength(KeyRange.Root) + 2 * Footprint("k", "k-0");
        var worker = world.Worker(options: new ArchiveOptions { SectionBytes = bound });

        var change = await world.DrainAndCommitAsync(worker, world.Ledgers.Ledger(ledger), tail, Token);

        Assert.Equal((Slot.Zero, new Slot(6), 3), (change.From, change.Below, change.Sections.Length));
        List<(string Key, Tag Tag)> rows = [];
        foreach (var section in change.Sections)
        {
            var listed = (await world.Objects.ListAsync(section.Object.Value, cancellationToken: Token).ToListAsync(Token)).Single();
            Assert.True(listed.Length <= bound, $"{listed.Name} holds {listed.Length} bytes, past the section size of {bound}.");
            var held = await RowsAsync(world, section.Object);
            Assert.Equal(2, held.Count);
            rows.AddRange(held);
        }

        Assert.Equal(Enumerable.Range(0, 6).Select(slot => new Tag(ledger, new Slot(slot), 0)), rows.Select(static row => row.Tag).Order());
        await world.CloseCompleteAndDropAsync(ledger, Token);
        var read = Assert.IsType<ArchiveStoreRead.Read>((await world.ArchiveStore().ReadAsync("k"u8.ToArray(), world.Ledgers.Map.Current, Token)).Value);
        using (read)
        {
            Assert.Equal("k-0k-1k-2k-3k-4k-5", Encoding.UTF8.GetString(read.Prefix.Content.AsReadOnlySequence()));
        }
    }

    /// <summary>
    /// A split drain whose second upload fails leaves its first object as garbage, commits nothing, and leaks no page; the
    /// next drain of the segment commits, and two sweeps delete the garbage.
    /// </summary>
    [Fact]
    public async Task ASplitDrainWhoseSecondUploadFailsLeavesGarbageAndLeaksNothing()
    {
        await using var world = await ArchiveWorld.StartAsync(Token);
        var ledger = world.Ledgers.OpenContaining([0]).Id;
        for (var index = 0; index < 4; index++)
        {
            _ = await world.Ledgers.ProposeAsync(ledger, [LiveRow.Of("k", RowKind.Append, $"k-{index}")], Token);
        }

        var tail = await world.Ledgers.EndSegmentAsync(ledger, Token) + 1;
        var options = new ArchiveOptions { SectionBytes = RunObjectFormat.DrainOutputFixedLength(KeyRange.Root) + 2 * Footprint("k", "k-0") };
        var calls = world.CallMark;
        world.Store.BeforeCreate = static earlier => _ = earlier == 1 ? throw new IOException("The second upload failed.") : 0;

        _ = await Assert.ThrowsAsync<IOException>(() => world.DrainOnceAsync(world.Worker(options: options), world.Ledgers.Ledger(ledger), tail, Token));

        Assert.Equal(0, world.CommitsSince(calls));
        world.Store.BeforeCreate = null;
        var change = await world.DrainAndCommitAsync(world.Worker(options: options), world.Ledgers.Ledger(ledger), tail, Token);
        var sweeper = new Sweeper(world.Store, world.Manifest, TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<Sweeper>.Instance);
        _ = await sweeper.SweepAsync(Token);
        _ = await sweeper.SweepAsync(Token);
        Assert.Equal(change.Sections.Select(static section => section.Object.Value).Order(), (await world.Objects.ListAsync("d/", cancellationToken: Token).ToListAsync(Token)).Select(static listed => listed.Name.Value).Order());
    }

    /// <summary>
    /// Backpressure which outlasts the scan's retries before the segment's end drains nothing, since a segment drains
    /// whole: the drain answers <c>Backpressure</c>, submitting and uploading nothing, and once the replicas answer, the
    /// next drain takes the whole segment.
    /// </summary>
    [Fact]
    public async Task BackpressurePastTheRetriesBeforeTheSegmentsEndDrainsNothing()
    {
        await using var world = await ArchiveWorld.StartAsync(Token);
        var ledger = world.Ledgers.OpenContaining([0]).Id;
        for (var index = 0; index < 4; index++)
        {
            _ = await world.Ledgers.ProposeAsync(ledger, [LiveRow.Of("a", RowKind.Append, $"a{index}")], Token);
        }

        var marker = await world.Ledgers.EndSegmentAsync(ledger, Token);
        var tail = await world.Ledgers.ConfirmOpenAsync(ledger, Token);
        var worker = world.Worker(backpressureRetries: 2);
        world.Ledgers.Transport.Saturated = (_, slot) => slot >= new Slot(2);
        var submitted = false;

        var calls = world.CallMark;
        Assert.Equal(new DrainOutcome(ReplicaWriteOutcome.Backpressure.Instance), await worker.DrainAsync(world.Ledgers.Ledger(ledger), tail, (change, token) =>
        {
            submitted = true;
            return world.CommitAsync(change, token);
        }, Token));
        Assert.False(submitted);
        Assert.Equal(0, world.CreatesSince(calls));
        Assert.True(world.Ledgers.Transport.Answered >= 3, "The scan did not retry twice.");

        world.Ledgers.Transport.Saturated = static (_, _) => false;
        var whole = await world.DrainAndCommitAsync(worker, world.Ledgers.Ledger(ledger), tail, Token);
        Assert.Equal((Slot.Zero, marker), (whole.From, whole.Below));
    }

    /// <summary>
    /// A scan which backpressure stops resumes from the slot it reached, keeping the rows read before it and its segment
    /// view, so it reads no earlier batch again; backpressure on a middle batch and on the batch which closes the segment
    /// each drain the whole segment.
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public async Task AScanResumesAfterBackpressureFromTheSlotItReached(int pressed)
    {
        await using var world = await ArchiveWorld.StartAsync(Token);
        var ledger = world.Ledgers.OpenContaining([0]).Id;
        for (var index = 0; index < 4; index++)
        {
            _ = await world.Ledgers.ProposeAsync(ledger, [LiveRow.Of("a", RowKind.Append, $"a{index}")], Token);
        }

        var marker = await world.Ledgers.EndSegmentAsync(ledger, Token);
        Assert.Equal(new Slot(4), marker);
        var tail = await world.Ledgers.ConfirmOpenAsync(ledger, Token);
        var reads = new int[(int)tail.Value];
        var once = 1;
        world.Ledgers.Transport.Saturated = (id, slot) =>
        {
            _ = Interlocked.Increment(ref reads[slot.Value]);
            return slot == new Slot(pressed) && Interlocked.Exchange(ref once, 0) == 1;
        };

        var whole = await world.DrainAndCommitAsync(world.Worker(), world.Ledgers.Ledger(ledger), tail, Token);

        Assert.Equal((Slot.Zero, marker, 1), (whole.From, whole.Below, world.Ledgers.Transport.Answered));
        Assert.All(reads[..pressed], static count => Assert.Equal(1, count));
        Assert.Equal(4, (await RowsAsync(world, Assert.Single(whole.Sections).Object)).Count);
    }
    /// <summary>
    /// The drain memory bounds the drains of a process together: a drain whose rows would pass it beside another's, held
    /// until that one's commit is answered, releases them and waits to scan again alone, holding the whole budget; both
    /// then commit, and the budget is whole again.
    /// </summary>
    [Fact]
    public async Task ADrainPastTheMemoryBesideAnotherScansAgainAlone()
    {
        await using var world = await ArchiveWorld.StartAsync(Token);
        await world.Ledgers.SplitAsync(world.Ledgers.OpenContaining([0]).Id, Token);
        var (lower, upper) = (world.Ledgers.OpenContaining([0x00]).Id, world.Ledgers.OpenContaining([0x80]).Id);
        foreach (var (ledger, key) in new[] { (lower, "\u0001"), (upper, "\u00ff") })
        {
            for (var index = 0; index < 3; index++)
            {
                _ = await world.Ledgers.ProposeAsync(ledger, [LiveRow.Of(key, RowKind.Append, new string('x', 300))], Token);
            }
        }

        var (lowerTail, upperTail) = (await world.Ledgers.EndSegmentAsync(lower, Token) + 1, await world.Ledgers.EndSegmentAsync(upper, Token) + 1);
        var worker = world.Worker(options: new ArchiveOptions { SectionBytes = 4096, DrainMemoryBytes = 4096 });
        var (reached, held) = (new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        var firstHeld = worker.DrainAsync(world.Ledgers.Ledger(lower), lowerTail, async (change, token) =>
        {
            reached.SetResult();
            await held.Task;
            return await world.CommitAsync(change, token);
        }, Token);
        await reached.Task;

        var second = worker.DrainAsync(world.Ledgers.Ledger(upper), upperTail, world.CommitAsync, Token);
        await Task.Delay(TimeSpan.FromMilliseconds(100), TimeProvider.System, Token);
        Assert.False(second.IsCompleted, "A drain past the memory beside another did not wait to scan alone.");

        held.SetResult();
        _ = Assert.IsType<DrainOutcome.Drained>((await firstHeld).Value);
        var alone = Assert.IsType<DrainOutcome.Drained>((await second).Value);
        _ = Assert.IsType<ManifestCommitOutcome.Applied>(alone.Outcome.Value);
        Assert.Equal(4096, worker.AvailableMemoryBytes);
    }

    /// <summary>
    /// A segment whose rows pass the drain memory alone fails its drain with <see cref="DrainMemoryExceededException"/>,
    /// which names the segment, uploads nothing, and leaves the budget whole.
    /// </summary>
    [Fact]
    public async Task ASegmentPastTheDrainMemoryAloneFailsNamingIt()
    {
        await using var world = await ArchiveWorld.StartAsync(Token);
        var ledger = world.Ledgers.OpenContaining([0]).Id;
        for (var index = 0; index < 8; index++)
        {
            _ = await world.Ledgers.ProposeAsync(ledger, [LiveRow.Of("a", RowKind.Append, new string('x', 600))], Token);
        }

        var tail = await world.Ledgers.EndSegmentAsync(ledger, Token) + 1;
        var worker = world.Worker(options: new ArchiveOptions { SectionBytes = 4096, DrainMemoryBytes = 4096 });
        var calls = world.CallMark;

        var exceeded = await Assert.ThrowsAsync<DrainMemoryExceededException>(() => world.DrainOnceAsync(worker, world.Ledgers.Ledger(ledger), tail, Token));

        Assert.Equal((new LedgerSegmentId(ledger, Slot.Zero), 4096L), (exceeded.Segment, exceeded.Budget));
        Assert.True(exceeded.Charged > 4096);
        Assert.Equal(0, world.CreatesSince(calls));
        Assert.Equal(4096, worker.AvailableMemoryBytes);
    }
    /// <summary>
    /// A drain of a ledger which has left the map, whose replica its node has dropped, answers the scan's
    /// <c>Dropped</c> and uploads nothing.
    /// </summary>
    [Fact]
    public async Task ADrainOfADroppedLedgerAnswersDropped()
    {
        await using var world = await ArchiveWorld.StartAsync(Token);
        var ledger = world.Ledgers.OpenContaining([0]).Id;
        _ = await world.Ledgers.ProposeAsync(ledger, [LiveRow.Of("a", RowKind.Append, "a0")], Token);
        _ = await world.Ledgers.EndSegmentAsync(ledger, Token);
        _ = await world.Ledgers.ProposeAsync(ledger, [LiveRow.Of("a", RowKind.Append, "a1")], Token);
        var straggler = world.Worker();
        _ = await world.DrainAndCommitAsync(straggler, world.Ledgers.Ledger(ledger), await world.Ledgers.ConfirmOpenAsync(ledger, Token), Token);
        await world.Ledgers.ReplaceAsync(ledger, Token);
        var closed = world.Ledgers.Ledger(ledger);
        _ = await world.CompleteAsync(world.Worker(), ledger, Token);
        _ = Assert.IsType<Orleans.Durable.Storage.Engine.Ledgers.Control.LedgerMapCommitOutcome.Applied>(
            (await world.Ledgers.CommitAsync(new Orleans.Durable.Storage.Engine.Ledgers.Control.LedgerMapChange.Drop(ledger), Token)).Value);
        await world.Ledgers.DropReplicaAsync(ledger, Token);

        var calls = world.CallMark;
        Assert.Equal(new DrainOutcome(ReplicaWriteOutcome.Dropped.Instance), await world.DrainOnceAsync(straggler, closed, Assert.IsType<LedgerStatus.Closed>(closed.Status.Value).T, Token));
        Assert.Equal(0, world.CreatesSince(calls));
    }

    /// <summary>
    /// A drain whose map entry lists open a ledger a manifest value shows complete, the map read before the ledger closed,
    /// is covered; a manifest which records a closed ledger's watermark past its <c>T</c> is corrupt.
    /// </summary>
    [Fact]
    public async Task ADrainCompletedSinceItsMapIsCoveredAndAWatermarkPastTIsCorrupt()
    {
        await using var world = await ArchiveWorld.StartAsync(Token);
        var ledger = world.Ledgers.OpenContaining([0]).Id;
        _ = await world.Ledgers.ProposeAsync(ledger, [LiveRow.Of("a", RowKind.Append, "a0")], Token);
        var open = world.Ledgers.Ledger(ledger);
        await world.Ledgers.ReplaceAsync(ledger, Token);
        _ = await world.CompleteAsync(world.Worker(), ledger, Token);

        Assert.Equal(new DrainOutcome(DrainOutcome.Covered.Instance), await world.DrainOnceAsync(world.Worker(), open, Slot.Zero, Token));

        await using var other = await ArchiveWorld.StartAsync(Token);
        var next = other.Ledgers.OpenContaining([0]).Id;
        _ = await other.Ledgers.ProposeAsync(next, [LiveRow.Of("a", RowKind.Append, "a0")], Token);
        await other.Ledgers.ReplaceAsync(next, Token);
        var past = new ManifestValue(1, new LedgerId(1), [new(next, new ManifestWatermark(new Slot(5), false))], []);
        _ = await ManifestChanges.WriteAsync(other.Store, past, null, Token);
        _ = await Assert.ThrowsAsync<ArchiveCorruptException>(() => other.DrainOnceAsync(other.Worker(), other.Ledgers.Ledger(next), new Slot(1), Token));
    }

    /// <summary>
    /// LSM-I2: a manifest which records a watermark at a batch which does not start a segment is corrupt, and the drain
    /// uploads nothing; the <c>lsm-mid-segment-watermark</c> mutant, which drains to the contiguous tail inside a segment,
    /// records such a watermark.
    /// </summary>
    [Fact]
    public async Task AWatermarkInsideASegmentIsCorruptAndTheMidSegmentMutantRecordsOne()
    {
        await using var world = await ArchiveWorld.StartAsync(Token);
        var ledger = world.Ledgers.OpenContaining([0]).Id;
        for (var index = 0; index < 3; index++)
        {
            _ = await world.Ledgers.ProposeAsync(ledger, [LiveRow.Of("a", RowKind.Append, $"a{index}")], Token);
        }

        var tail = await world.Ledgers.ConfirmOpenAsync(ledger, Token);
        var mutant = world.Worker(insideSegments: true);

        var inside = await world.DrainAndCommitAsync(mutant, world.Ledgers.Ledger(ledger), new Slot(2), Token);

        Assert.Equal((Slot.Zero, new Slot(2)), (inside.From, inside.Below));
        Assert.DoesNotContain(inside.Below, (await world.Ledgers.DecidedStartsAsync(world.Ledgers.Ledger(ledger), tail, Token))!.Value);
        var calls = world.CallMark;
        var corrupt = await Assert.ThrowsAsync<ArchiveCorruptException>(() => world.DrainOnceAsync(world.Worker(), world.Ledgers.Ledger(ledger), tail, Token));
        Assert.Contains("LSM-I2", corrupt.Message, StringComparison.Ordinal);
        Assert.Equal(0, world.CreatesSince(calls));
    }
    /// <summary>
    /// HI-I4: a defining row which stores a token other than the empty one, the LSM's plan of every write, is a writer's
    /// programming error, which raises a debug assertion in a Debug build, failing the drain before it uploads anything.
    /// </summary>
    [Fact]
    public async Task ADefiningRowWhichStoresATokenRaisesADebugAssertion()
    {
        await using var world = await ArchiveWorld.StartAsync(Token);
        var ledger = world.Ledgers.OpenContaining([0]).Id;
        await world.Ledgers.ProposeWithTokenAsync(ledger, LiveRow.Of("a", RowKind.Append, "a0"), new RowToken([7]), Token);
        var tail = await world.Ledgers.EndSegmentAsync(ledger, Token) + 1;
        var (since, test, calls) = (DebugAssertions.RaisedCount, TestContext.Current.Test, world.CallMark);

        try
        {
            _ = await world.DrainOnceAsync(world.Worker(), world.Ledgers.Ledger(ledger), tail, Token);
            Assert.Equal("Release", TestDiagnostics.Configuration);
        }
        catch (DebugAssertionException assertion)
        {
            Assert.Contains("HI-I4", assertion.Message, StringComparison.Ordinal);
            _ = Assert.Single(DebugAssertions.TakeRaisedBy(test, since));
            Assert.Equal(0, world.CreatesSince(calls));
        }
    }

    /// <summary>
    /// A drain lands at the deepest grid depth containing its ledger's range (archive §8.8): with a grid step of one bit,
    /// a split ledger's drain lands at its own depth, and with four bits, at the root.
    /// </summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(4, 0)]
    public async Task ADrainLandsAtTheDeepestGridDepthContainingItsLedger(int gridBits, int depth)
    {
        await using var world = await ArchiveWorld.StartAsync(Token);
        await world.Ledgers.SplitAsync(world.Ledgers.OpenContaining([0]).Id, Token);
        var child = world.Ledgers.OpenContaining([0x80]).Id;
        _ = await world.Ledgers.ProposeAsync(child, [new LiveRow([0x80], RowKind.Append, [0x78])], Token);
        var options = new ArchiveOptions { GridBits = gridBits, MaxSectionsPerPartition = 1 << gridBits };

        var change = await world.DrainOpenAsync(world.Worker(options: options), child, Token);

        Assert.Equal(KeyRange.Containing([0x80], depth), Assert.Single(change.Sections).Partition);
    }

    [Theory]
    [InlineData(0, 4, 0)]
    [InlineData(3, 4, 0)]
    [InlineData(4, 4, 4)]
    [InlineData(9, 4, 8)]
    [InlineData(9, 1, 9)]
    public void ADrainsPartitionIsTheDeepestGridDepthContainingItsRange(int depth, int gridBits, int expected)
    {
        var range = KeyRange.Containing([0b1011_0110, 0b1100_0000], depth);

        Assert.Equal(KeyRange.Containing([0b1011_0110, 0b1100_0000], expected), DrainWorker.GridPartition(range, gridBits));
    }

    /// <summary>
    /// A single multi-row batch whose sorted rows split across multiple drain output objects, mixing multi-page bodies and
    /// empty bodies, keeps its batch buffer alive until its last row is written, encodes every row byte-for-byte, restores
    /// the drain memory budget, and releases every buffer page cleanly.
    /// </summary>
    [Fact]
    public async Task ASingleBatchWhoseRowsSplitAcrossOutputObjectsRetainsAndReleasesCleanly()
    {
        await using var world = await ArchiveWorld.StartAsync(Token);
        var ledger = world.Ledgers.OpenContaining([0]).Id;
        var multiPage = new byte[ArcBufferWriter.MinimumPageSize * 2 + 13];
        var secondBody = new byte[1200];
        var thirdBody = new byte[1200];
        new Random(73).NextBytes(multiPage);
        new Random(74).NextBytes(secondBody);
        new Random(75).NextBytes(thirdBody);

        // Interleave keys out of sort order in one batch so sorting splits this single batch across multiple output objects.
        _ = await world.Ledgers.ProposeAsync(
            ledger,
            [
                new LiveRow("z"u8.ToArray(), RowKind.Append, thirdBody),
                new LiveRow("m"u8.ToArray(), RowKind.Delete, []),
                new LiveRow("a"u8.ToArray(), RowKind.Replace, multiPage),
                new LiveRow("b"u8.ToArray(), RowKind.Append, secondBody),
            ],
            Token);

        var options = new ArchiveOptions { SectionBytes = 2048, DrainMemoryBytes = 1L << 20 };
        var worker = world.Worker(options: options);
        var change = await world.DrainOpenAsync(worker, ledger, Token);

        Assert.True(change.Sections.Length >= 2, $"Expected single batch to split across >=2 drain objects, got {change.Sections.Length}.");
        Assert.Equal(options.DrainMemoryBytes, worker.AvailableMemoryBytes);

        List<(string Key, RowKind Kind, byte[] Body)> drained = [];
        foreach (var section in change.Sections)
        {
            var length = (await world.Objects.ListAsync(section.Object.Value, cancellationToken: Token).ToListAsync(Token))
                .Single(listed => listed.Name == section.Object).Length;
            using var content = (await world.Objects.ReadAsync(section.Object, 0, (int)length, Token))!.Value;
            using var decoded = Assert.IsType<RunObject>(RunObjectFormat.Decode(content).Value);
            foreach (var decodedSection in decoded.Sections)
            {
                foreach (var row in decodedSection.Rows.Rows)
                {
                    drained.Add((Encoding.UTF8.GetString(row.Row.Key.Span), row.Row.Kind, row.Row.Body.ToArray()));
                }
            }
        }

        drained.Sort(static (left, right) => string.CompareOrdinal(left.Key, right.Key));
        Assert.Equal(4, drained.Count);
        Assert.Equal(("a", RowKind.Replace), (drained[0].Key, drained[0].Kind));
        Assert.Equal(multiPage, drained[0].Body);
        Assert.Equal(("b", RowKind.Append), (drained[1].Key, drained[1].Kind));
        Assert.Equal(secondBody, drained[1].Body);
        Assert.Equal(("m", RowKind.Delete), (drained[2].Key, drained[2].Kind));
        Assert.Empty(drained[2].Body);
        Assert.Equal(("z", RowKind.Append), (drained[3].Key, drained[3].Kind));
        Assert.Equal(thirdBody, drained[3].Body);
    }

    /// <summary>Returns the most bytes a row of <paramref name="key"/> and <paramref name="payload"/> adds to a drain output.</summary>
    private static long Footprint(string key, string payload)
    {
        using var body = ArcBuffer.Wrap(Encoding.UTF8.GetBytes(payload));
        using var row = new Row(Encoding.UTF8.GetBytes(key), RowKind.Append, body);
        return RunObjectFormat.MaxRowFootprint(row, new ArchiveOptions().BloomBitsPerKey);
    }
    /// <summary>
    /// Requires <paramref name="write"/> to be a call starting <paramref name="call"/> which carried its content's CRC-32C.
    /// </summary>
    private static void AssertCarriesItsCrc32C(ObjectWrite write, string call)
    {
        Assert.StartsWith(call, write.Call, StringComparison.Ordinal);
        Assert.True(write.CarriesItsContentsCrc32C, write.Call + " carries its writer's CRC-32C.");
    }

    /// <summary>
    /// Returns each row of the drain output <paramref name="name"/>, in the object's order, as its key and tag.
    /// </summary>
    private static async Task<List<(string Key, Tag Tag)>> RowsAsync(ArchiveWorld world, ObjectName name)
    {
        var length = (await world.Objects.ListAsync(name.Value, cancellationToken: Token).ToListAsync(Token)).Single(listed => listed.Name == name).Length;
        using var content = (await world.Objects.ReadAsync(name, 0, (int)length, Token))!.Value;
        using var decoded = Assert.IsType<RunObject>(RunObjectFormat.Decode(content).Value);
        List<(string, Tag)> rows = [];
        foreach (var section in decoded.Sections)
        {
            foreach (var row in section.Rows.Rows)
            {
                rows.Add((Encoding.UTF8.GetString(row.Row.Key.Span), row.Tag));
            }
        }

        return rows;
    }
}
