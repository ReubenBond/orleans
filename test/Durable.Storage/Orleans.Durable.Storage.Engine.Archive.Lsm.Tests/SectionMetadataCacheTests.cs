using Microsoft.Extensions.Time.Testing;
using Orleans.Durable.Storage.Engine.Archive.Lsm.Control;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Archive.Lsm.Tests;

/// <summary>
/// The section metadata cache (archive-lsm.md §2.5, §8.3, §8.7): <see cref="LsmArchiveStore.PreloadAsync"/> loads its
/// ranges' sections at once; a view advance loads the new sections in them before the view changes, so an absence answer
/// in a preloaded range fetches nothing; a failed load lets the view advance and loads on demand; a corrupt one answers
/// corruption; and an entry leaves once its object is unlisted, or once it lies outside the ranges and goes unread. Each
/// rule has a mutant its test catches by its message.
/// </summary>
/// <remarks>
/// The archive's grid step is one key bit, so the drains of the root's two halves write sections of distinct partitions,
/// one in each half; the halves' keys are <c>a</c> (0x61) and <c>é</c> (0xC3 0xA9). The absent keys <c>d</c> and
/// <c>ê</c> are those the sections' Bloom filters exclude, so their absence answers read no block.
/// </remarks>
public sealed class SectionMetadataCacheTests : IDisposable
{
    private static readonly ArchiveOptions Options = new() { GridBits = 1 };

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static KeyRange Lower => KeyRange.Root.Children.Lower;

    private static KeyRange Upper => KeyRange.Root.Children.Upper;

    public void Dispose() => _leaks.Dispose();

    /// <summary>
    /// A preload loads at once the metadata of its ranges' sections only; reads then match a store which caches nothing, an
    /// absent key in the range makes no object-store request, and a present one reads only its block.
    /// </summary>
    [Fact]
    public async Task APreloadLoadsItsRangesSectionsAtOnceAndAnAbsentKeyThenFetchesNothing()
    {
        await using var world = await ArchiveWorld.StartAsync(Token);
        var (lower, upper) = await DrainHalvesAsync(world);
        var store = world.ArchiveStore(world.Manifest, Options);
        var (lowerReads, upperReads) = (world.RangedReads(lower.Object), world.RangedReads(upper.Object));

        await store.PreloadAsync([Lower]);

        Assert.Equal(lowerReads + 1, world.RangedReads(lower.Object));
        Assert.Equal(upperReads, world.RangedReads(upper.Object));
        Assert.Equal([lower.Object], store.Tails.Cached);
        var mark = world.CallMark;
        Assert.IsType<ArchiveStoreRead.Absent>((await store.ReadAsync("d"u8.ToArray(), world.Ledgers.Map.Current, Token)).Value);
        Assert.Equal(mark, world.CallMark);

        var uncached = world.ArchiveStore(options: Options with { TailCacheBytes = 0 });
        foreach (var key in new[] { "a", "d", "é", "ê" })
        {
            Assert.Equal(Answer(await uncached.ReadAsync(Bytes(key), world.Ledgers.Map.Current, Token)), Answer(await store.ReadAsync(Bytes(key), world.Ledgers.Map.Current, Token)));
        }
    }

    /// <summary>
    /// A view advance loads the new sections in the preloaded ranges before the view changes, whichever key the read
    /// which advances it asks for: when the load a read of the other half brings arrives, held, the view is still the one
    /// before the advance; once the load is released the view advances, and an absent key in the preloaded range then
    /// fetches nothing.
    /// </summary>
    [Fact]
    public Task AViewAdvanceLoadsTheNewSectionsInItsRangesBeforeTheViewChanges() => AdvanceAsync(MetadataCacheMutant.None);

    /// <summary>The view-advance test catches each mutant of the advance by its message.</summary>
    [Theory]
    [InlineData(MetadataCacheMutant.InstallsBeforeLoads, "before its new sections loaded")]
    [InlineData(MetadataCacheMutant.LoadsNoNewSections, "loaded no new section")]
    internal async Task TheViewAdvanceTestCatchesEachMutant(MetadataCacheMutant mutant, string message) =>
        Assert.Contains(message, (await Assert.ThrowsAnyAsync<Xunit.Sdk.XunitException>(() => AdvanceAsync(mutant))).Message, StringComparison.Ordinal);

    /// <summary>
    /// A preload whose load fails lets the view advance, and the failed section loads on a read's first need of it; one
    /// which finds the tail corrupt answers <see cref="ArchiveCorruptException"/> to the reads which need the section,
    /// with no further fetch.
    /// </summary>
    [Fact]
    public async Task AFailedLoadLetsTheViewAdvanceAndACorruptOneAnswersCorruption()
    {
        await using var world = await ArchiveWorld.StartAsync(Token);
        var (lower, _) = await DrainHalvesAsync(world);
        var time = new FakeTimeProvider();
        var store = world.ArchiveStore(world.Manifest, Options, time);
        await store.PreloadAsync([Lower]);
        var added = await DrainAgainAsync(world, "a");

        world.Store.FailRangedReads(1);
        time.Advance(Options.ViewAdoptionInterval);
        Assert.IsType<ArchiveStoreRead.Absent>((await store.ReadAsync(Bytes("ê"), world.Ledgers.Map.Current, Token)).Value);
        Assert.Equal(world.Manifest.Current!.Value.Epoch, store.ViewEpoch);
        Assert.DoesNotContain(added.Object, store.Tails.Cached);

        var before = world.RangedReads(added.Object);
        await ReadAnyAsync(store, world, "d");
        Assert.Equal(before + 1, world.RangedReads(added.Object));

        await CorruptAsync(world, lower.Object);
        var corrupt = world.ArchiveStore(world.Manifest, Options);
        await corrupt.PreloadAsync([Lower]);
        before = world.RangedReads(lower.Object);
        _ = await Assert.ThrowsAsync<ArchiveCorruptException>(async () => await corrupt.ReadAsync(Bytes("d"), world.Ledgers.Map.Current, Token));
        Assert.Equal(before, world.RangedReads(lower.Object));
    }

    /// <summary>
    /// An entry in a preloaded range stays however long it goes unread; one a read loaded on demand outside the ranges
    /// leaves once no read used it between two passes; and one whose range a later preload drops becomes on demand.
    /// </summary>
    [Fact]
    public Task AnUnreadOnDemandEntryLeavesAndAPreloadedOneStays() => EvictionAsync(MetadataCacheMutant.None);

    /// <summary>The eviction test catches each mutant of the eviction by its message.</summary>
    [Theory]
    [InlineData(MetadataCacheMutant.EvictsPreloaded, "preloaded entry left")]
    [InlineData(MetadataCacheMutant.KeepsUnread, "unread on-demand entry stayed")]
    internal async Task TheEvictionTestCatchesEachMutant(MetadataCacheMutant mutant, string message) =>
        Assert.Contains(message, (await Assert.ThrowsAnyAsync<Xunit.Sdk.XunitException>(() => EvictionAsync(mutant))).Message, StringComparison.Ordinal);

    /// <summary>An entry leaves once the view no longer lists its object, though its range is preloaded.</summary>
    [Fact]
    public Task AnUnlistedObjectsEntryLeaves() => UnlistedAsync(MetadataCacheMutant.None);

    /// <summary>The unlisted test catches a cache which keeps an unlisted object's entry.</summary>
    [Fact]
    public async Task TheUnlistedTestCatchesACacheWhichKeepsIt() =>
        Assert.Contains("unlisted object's entry stayed", (await Assert.ThrowsAnyAsync<Xunit.Sdk.XunitException>(() => UnlistedAsync(MetadataCacheMutant.KeepsUnlisted))).Message, StringComparison.Ordinal);

    /// <summary>
    /// Once a range's section metadata is preloaded, <see cref="ArchiveReader.ReadStoreAsync"/> answers an absent key
    /// in that range with zero object-store requests (archive-lsm.md §8.3, journals.md §8.2).
    /// </summary>
    [Fact]
    public async Task AnAbsenceReadThroughArchiveReaderReadStoreMakesNoObjectStoreRequests()
    {
        await using var world = await ArchiveWorld.StartAsync(Token);
        _ = await DrainHalvesAsync(world);
        var store = world.ArchiveStore(world.Manifest, Options);
        await store.PreloadAsync([Lower]);
        var reader = new ArchiveReader(world.Ledgers.Reader, store, TimeProvider.System);

        var mark = world.CallMark;
        using var answer = await reader.ReadStoreAsync(Bytes("d"), Token);
        Assert.Null(answer.Prefix);
        Assert.Equal(mark, world.CallMark);
    }

    /// <summary>
    /// <see cref="ArchiveReader.ReadStoreAsync"/> refreshes the LSM's view when the ledger map drops a ledger that the
    /// store's cached manifest does not yet show complete, so a store read never loses a dropped ledger's rows
    /// (archive-lsm.md §8.3, <c>uncovered-drop</c>).
    /// </summary>
    [Fact]
    public Task ReadStoreRefreshesAfterADropItsViewDoesNotCover() => UncoveredDropAsync(uncoveredDropMutant: false);

    /// <summary>
    /// The <c>uncovered-drop</c> mutant lets the LSM pair a map value which dropped a ledger with a cached manifest that
    /// does not show that ledger complete, losing the dropped ledger's rows.
    /// </summary>
    [Fact]
    public async Task TheUncoveredDropTestCatchesTheUncoveredDropMutant() =>
        Assert.Contains("uncovered drop lost", (await Assert.ThrowsAnyAsync<Xunit.Sdk.XunitException>(() => UncoveredDropAsync(uncoveredDropMutant: true))).Message, StringComparison.Ordinal);

    private static async Task UncoveredDropAsync(bool uncoveredDropMutant)
    {
        await using var world = await ArchiveWorld.StartAsync(Token);
        var initialLedger = world.Ledgers.OpenContaining(Bytes("a")).Id;
        _ = await world.Ledgers.ProposeAsync(initialLedger, [LiveRow.Of("a", RowKind.Append, "dropped-value")], Token);

        // Prime a separate reader's LsmArchiveStore at the initial manifest (before the ledger drains or drops).
        var staleManifest = new ManifestView(world.Store);
        var store = world.ArchiveStore(staleManifest, Options);
        store.UncoveredDropMutant = uncoveredDropMutant;
        Assert.IsType<ArchiveStoreRead.Absent>((await store.ReadAsync(Bytes("d"), world.Ledgers.Map.Current, Token)).Value);

        // Close, drain to completion, and drop the ledger so the map no longer lists it while the manifest marks it complete.
        await world.CloseCompleteAndDropAsync(initialLedger, Token);

        var reader = new ArchiveReader(world.Ledgers.Reader, store, TimeProvider.System);
        using var answer = await reader.ReadStoreAsync(Bytes("a"), Token);
        Assert.True(answer.Prefix is not null, "An uncovered drop lost the dropped ledger's prefix.");
        Assert.Equal("dropped-value", System.Text.Encoding.UTF8.GetString(answer.Prefix.Content.ToArray()));
    }

    private static async Task AdvanceAsync(MetadataCacheMutant mutant)
    {
        await using var world = await ArchiveWorld.StartAsync(Token);
        var (_, upper) = await DrainHalvesAsync(world);
        var time = new FakeTimeProvider();
        var store = world.ArchiveStore(world.Manifest, Options, time);
        store.Tails.Mutant = mutant;
        await store.PreloadAsync([Lower]);
        await ReadAnyAsync(store, world, "ê");
        Assert.Contains(upper.Object, store.Tails.Cached);
        var added = await DrainAgainAsync(world, "a");

        var load = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        world.Store.BeforeRangedRead = async (name, offset) =>
        {
            if (name == added.Object)
            {
                _ = load.TrySetResult();
                await release.Task;
            }
        };

        var before = store.ViewEpoch;
        time.Advance(Options.ViewAdoptionInterval);
        var read = store.ReadAsync(Bytes("ê"), world.Ledgers.Map.Current, Token).AsTask();

        // The load either arrives and is held, or the read ends without it; the wait bounds only a defect's hang.
        _ = await Task.WhenAny(load.Task, read).WaitAsync(TimeSpan.FromSeconds(30), Token);
        Assert.True(load.Task.IsCompleted, "The view advance loaded no new section in the preloaded range.");
        Assert.True(store.ViewEpoch == before, "The view advanced before its new sections loaded.");
        release.SetResult();
        Assert.IsType<ArchiveStoreRead.Absent>((await read).Value);
        Assert.Equal(world.Manifest.Current!.Value.Epoch, store.ViewEpoch);

        world.Store.BeforeRangedRead = null;
        var mark = world.CallMark;
        Assert.IsType<ArchiveStoreRead.Absent>((await store.ReadAsync(Bytes("d"), world.Ledgers.Map.Current, Token)).Value);
        Assert.Equal(mark, world.CallMark);
    }

    private static async Task EvictionAsync(MetadataCacheMutant mutant)
    {
        await using var world = await ArchiveWorld.StartAsync(Token);
        var (lower, upper) = await DrainHalvesAsync(world);
        var store = world.ArchiveStore(world.Manifest, Options);
        store.Tails.Mutant = mutant;
        var lowerReads = world.RangedReads(lower.Object);
        await store.PreloadAsync([Lower]);
        await ReadAnyAsync(store, world, "ê");

        // The on-demand entry served a read since the last pass, so the first pass keeps it, and the second removes it;
        // the preloaded entry, which no read uses, stays, so its tail was fetched once.
        await store.PreloadAsync([Lower]);
        Assert.Contains(upper.Object, store.Tails.Cached);
        await store.PreloadAsync([Lower]);
        Assert.True(!store.Tails.Cached.Contains(upper.Object), "An unread on-demand entry stayed in the cache.");
        Assert.True(store.Tails.Cached.Contains(lower.Object) && world.RangedReads(lower.Object) == lowerReads + 1, "An unread preloaded entry left the cache.");

        // Once the preload names the other half, the lower entry is on demand, and leaves at the next pass, unread.
        await store.PreloadAsync([Upper]);
        Assert.Equal([upper.Object], store.Tails.Cached);
    }

    private static async Task UnlistedAsync(MetadataCacheMutant mutant)
    {
        await using var world = await ArchiveWorld.StartAsync(Token);
        var (lower, _) = await DrainHalvesAsync(world);
        var time = new FakeTimeProvider();
        var store = world.ArchiveStore(world.Manifest, Options, time);
        store.Tails.Mutant = mutant;
        await store.PreloadAsync([Lower]);
        Assert.Contains(lower.Object, store.Tails.Cached);

        _ = Assert.IsType<ManifestCommitOutcome.Applied>((await world.Committer.CommitAsync(new ManifestChange.Compaction([lower], []), Token)).Value);
        time.Advance(Options.ViewAdoptionInterval);
        await ReadAnyAsync(store, world, "ê");

        Assert.Equal(world.Manifest.Current!.Value.Epoch, store.ViewEpoch);
        Assert.True(!store.Tails.Cached.Contains(lower.Object), "An unlisted object's entry stayed in the cache.");
    }

    /// <summary>
    /// Splits the root and drains one row into each half's ledger, so each half holds one section of its own partition,
    /// and returns the sections.
    /// </summary>
    private static async Task<(SectionRef Lower, SectionRef Upper)> DrainHalvesAsync(ArchiveWorld world)
    {
        await world.Ledgers.SplitAsync(world.Ledgers.OpenContaining([0]).Id, Token);
        var lower = await DrainAgainAsync(world, "a");
        var upper = await DrainAgainAsync(world, "é");
        Assert.Equal(Lower, lower.Partition);
        Assert.Equal(Upper, upper.Partition);
        return (lower, upper);
    }

    /// <summary>Proposes a row of <paramref name="key"/> to its open ledger and drains it into a new section, which it returns.</summary>
    private static async Task<SectionRef> DrainAgainAsync(ArchiveWorld world, string key)
    {
        var ledger = world.Ledgers.OpenContaining(Bytes(key)).Id;
        _ = await world.Ledgers.ProposeAsync(ledger, [LiveRow.Of(key, RowKind.Append, key + "0")], Token);
        return (await world.DrainOpenAsync(world.Worker(world.Manifest, Options), ledger, Token)).Sections.Single();
    }

    private static async Task CorruptAsync(ArchiveWorld world, ObjectName name)
    {
        var length = (await world.Objects.ListAsync(name.Value, cancellationToken: Token).ToListAsync(Token)).Single().Length;
        byte[] bytes;
        using (var content = (await world.Objects.ReadAsync(name, 0, (int)length, Token))!.Value)
        {
            bytes = content.ToArray();
        }

        bytes[^5] ^= 0xff;
        await world.Objects.DeleteAsync(name, Token);
        using var corrupted = ArcBuffer.Wrap(bytes);
        _ = await world.Objects.CreateAsync(name, corrupted, null, Token);
    }

    private static async Task ReadAnyAsync(LsmArchiveStore store, ArchiveWorld world, string key) =>
        _ = Answer(await store.ReadAsync(Bytes(key), world.Ledgers.Map.Current, Token));

    /// <summary>Describes an answer by its case and, for a prefix, its content, disposing the prefix.</summary>
    private static string Answer(ArchiveStoreRead answer)
    {
        switch (answer.Value)
        {
            case ArchiveStoreRead.Read read:
                using (read)
                {
                    return "Read " + Convert.ToHexString(read.Prefix.Content.ToArray());
                }

            case ArchiveStoreRead.Absent:
                return "Absent";
            case ArchiveStoreRead.Stale:
                return "Stale";
            case null:
            default:
                throw new InvalidOperationException("An archive store's read answers a case.");
        }
    }

    private static byte[] Bytes(string key) => System.Text.Encoding.UTF8.GetBytes(key);
}
