using System.Globalization;
using CsCheck;
using Microsoft.Extensions.Time.Testing;
using Orleans.Durable.Storage.Diagnostics;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers.Store;
using Orleans.Durable.Storage.Engine.Wal;
using Orleans.Durable.Storage.Engine.Wal.Simulation;
using Orleans.Durable.Storage.Testing;
using WalLog = Orleans.Durable.Storage.Engine.Wal.WriteAheadLog;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests;

/// <summary>
/// Pins the LedgerStore's catalog checkpoints (ledgerstore.md §7.2, §8.7, §8.8): each reflects exactly the create and
/// drop records below its cut, tombstones included, and either of the two newest valid ones derives the catalog with the
/// retained WAL (LS-I3); the store releases WAL only while it holds two valid ones, never past the older one's cut, and
/// only after it deletes every other one (LS-I4); and a store whose catalog cannot be derived, or whose catalog
/// checkpoints contradict each other or the WAL, refuses to open and keeps its files (LS-I2, LS-I5).
/// </summary>
public sealed class LedgerStoreCatalogCheckpointTests : IDisposable
{
    private enum ModelOperation
    {
        Write,
        WriteUntilRoll,
        Drop,
        CatalogCheckpoint,
        ProcessCrash,
        OperatingSystemCrash,
    }

    private static readonly string WalPath = SimulatedNodeRoot.WalPath;
    private static readonly LedgerReplicaOptions Window = new() { WindowSize = 64 };

    // 400-byte payloads fill one 512-byte page each, so an 8-page file holds 7 records.
    private static readonly LedgerStoreOptions SmallFiles = new()
    {
        DirectoryPath = SimulatedNodeRoot.Path,
        PageSize = 512,
        FileSize = 8 * 512,
        WriteWindowPages = 4,
        PreallocateFiles = false,
    };

    private static readonly LedgerStoreCheckpointOptions ExplicitOnly = new() { CheckpointInterval = TimeSpan.FromDays(1) };

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _leaks.Dispose();

    /// <summary>
    /// LS-I3, LS-I4: for random creations, drops, writes, catalog checkpoints, and process and operating-system crashes,
    /// the catalog matches a model of the node after every step, every release stays at or below the older kept catalog
    /// checkpoint's cut, and recovery from either kept catalog checkpoint yields the same catalog.
    /// </summary>
    [Fact]
    public void RecoveryFromEitherKeptCatalogCheckpointYieldsTheSameCatalog() =>
        Gen.Select(Gen.Enum<ModelOperation>(), Gen.ULong[1, 6]).Array[1, 30].Sample(

            operations => _leaks.InScope(() => RunAsync(operations).GetAwaiter().GetResult()),
            iter: 25,
            threads: 1);

    /// <summary>
    /// §6.2, LS-I3: a damaged newest catalog checkpoint is never loaded: the open takes the older one and replays the
    /// create and drop records above its cut, so every tombstone survives; the open deletes nothing, and maintenance
    /// deletes the damaged one only once it writes a newer one.
    /// </summary>
    [Fact]
    public async Task ADamagedNewestCatalogCheckpointFallsBackToTheOlderOneWithEveryTombstoneIntact()
    {
        var root = NewRoot();
        var store = await root.OpenAsync(create: true);
        await WriteAsync(store, new(1), 0);
        await WriteAsync(store, new(2), 0);
        await store.DropReplicaAsync(new(3), Token);
        await CheckpointCatalogAsync(store);
        await store.DropReplicaAsync(new(1), Token);
        await CheckpointCatalogAsync(store);
        var expected = store.Catalog;
        await root.CrashAsync(store, operatingSystem: true);

        var damaged = DamageNewestValid(root);
        store = await root.OpenAsync();
        AssertSameCatalog(expected, store.Catalog);
        Assert.Equal(LedgerReplicaTestData.Ledgers(1, 3), store.Catalog.Tombstones);
        Assert.Contains(damaged, CatalogNames(root));

        await WriteAsync(store, new(2), 1);
        await store.WriteNewCatalogCheckpointAsync();
        Assert.DoesNotContain(damaged, CatalogNames(root));
        Assert.Equal(2, CatalogNames(root).Count);
        await store.DisposeAsync();
    }

    /// <summary>
    /// LS-I4, §8.7 step 8: an open which finds fewer than two valid catalog checkpoints deletes none, an invalid one
    /// included, and releases no WAL; it writes its own at its frontier, and maintenance's next catalog checkpoint then
    /// deletes the others and releases the WAL behind the older kept one.
    /// </summary>
    [Fact]
    public async Task AnOpenWithOneValidCatalogCheckpointDeletesAndReleasesNothingUntilMaintenanceWritesAnother()
    {
        var root = NewRoot();
        var store = await root.OpenAsync(create: true);
        await WriteAsync(store, new(1), 0, 10);
        await store.DropReplicaAsync(new(1), Token);
        await CheckpointCatalogAsync(store);
        await root.CrashAsync(store, operatingSystem: true);

        // Of the two catalog checkpoints the store holds, the first and the drop's, only the newest stays valid.
        var names = CatalogNames(root);
        Assert.Equal(2, names.Count);
        Damage(root, names[0]);
        var files = root.Storage.ListFiles(WalPath);
        store = await root.OpenAsync();
        Assert.Equal(names, CatalogNames(root).Take(2));
        Assert.Equal(3, CatalogNames(root).Count);
        Assert.Equal(files, root.Storage.ListFiles(WalPath).Take(files.Count));

        await WriteAsync(store, new(2), 0);
        await store.WriteNewCatalogCheckpointAsync();
        Assert.DoesNotContain(names[0], CatalogNames(root));
        Assert.Equal(2, CatalogNames(root).Count);
        Assert.True(root.Storage.ListFiles(WalPath)[0] > 1, "Maintenance released no WAL file.");
        await store.DisposeAsync();
    }

    /// <summary>
    /// §6.2: a catalog checkpoint which cannot be read is reported, which stops the process (conventions.md §14): the open
    /// fails with the I/O error and deletes nothing, and the restarted process's open reads it again.
    /// </summary>
    [Fact]
    public async Task AnUnreadableCatalogCheckpointFailsTheOpenAndDeletesNothing()
    {
        var root = NewRoot();
        var store = await root.OpenAsync(create: true);
        await WriteAsync(store, new(1), 0);
        await store.DropReplicaAsync(new(2), Token);
        await CheckpointCatalogAsync(store);
        await root.CrashAsync(store, operatingSystem: false);
        var names = CatalogNames(root);
        var files = root.Storage.ListFiles(WalPath);

        var reported = FatalErrors.ReportedCount;
        root.Storage.NamedReadFault = static (_, name, _, _) => name.EndsWith(CatalogCheckpoints.Extension, StringComparison.Ordinal) ? new IOException("injected catalog read failure") : null;
        var failure = await Assert.ThrowsAsync<IOException>(async () => await root.OpenAsync());
        Assert.Equal("injected catalog read failure", failure.Message);
        _ = StorageFailures.TakeOne(reported, FatalErrorSite.HandleReadScatter, WalPath);
        Assert.Equal(names, CatalogNames(root));
        Assert.Subset(root.Storage.ListFiles(WalPath).ToHashSet(), files.ToHashSet());

        root.Crash(operatingSystem: false);
        await using var reopened = await root.OpenAsync();
        Assert.Equal(LedgerReplicaTestData.Ledgers(1), reopened.Replicas);
        Assert.Equal(LedgerReplicaTestData.Ledgers(2), reopened.Catalog.Tombstones);
    }

    /// <summary>
    /// §7.2, §8.8: a catalog checkpoint holds exactly the catalog which the create and drop records below its cut leave,
    /// its tombstones included, and none at or above it.
    /// </summary>
    [Fact]
    public async Task ACatalogCheckpointReflectsExactlyTheRecordsBelowItsCut()
    {
        var root = NewRoot();
        await using var store = await root.OpenAsync(create: true);
        await WriteAsync(store, new(1), 0);
        await store.DropReplicaAsync(new(4), Token);
        await WriteAsync(store, new(2), 0, 3);
        await CheckpointCatalogAsync(store);
        await WriteAsync(store, new(3), 0);
        await store.DropReplicaAsync(new(2), Token);
        await CheckpointCatalogAsync(store);
        await WriteAsync(store, new(5), 0);

        // The live replica of ledger 1 pins file 1, so the WAL holds every record.
        Assert.Equal(1ul, root.Storage.ListFiles(WalPath)[0]);

        foreach (var name in CatalogNames(root))
        {
            _ = CatalogCheckpoints.TryParseFileName(name, out var cut);
            var checkpoint = ReplicaCatalog.Decode(root.Storage.ReadDurableFile(WalPath, name), cut).Checkpoint();

            // A model of the catalog, folded from the create and drop records below the cut.
            var nextHandle = 0u;
            var replicas = new Dictionary<LedgerId, (uint Handle, uint Window, ulong FirstFile)>();
            var tombstones = new SortedSet<LedgerId>();
            await foreach (var entry in store.Wal.ReadAllAsync(default, Token))
            {
                if (!entry.TryGetValue(out WalRecord record) || record.Position >= cut || StoreRecordTestHelpers.Decode(record.Payload, out _) is not { } parsed)
                {
                    continue;
                }

                switch (parsed)
                {
                    case CreateRecord create:
                        foreach (var pair in create.Pairs)
                        {
                            replicas.Add(pair.Ledger, (pair.Handle, pair.Window, record.Position.File));
                            nextHandle = pair.Handle + 1;
                        }

                        break;
                    case DropRecord drop:
                        _ = replicas.Remove(drop.Ledger);
                        _ = tombstones.Add(drop.Ledger);
                        break;
                    default:
                        throw new InvalidOperationException("The test seals no replica, so a store record is a create or a drop.");
                }
            }

            Assert.Equal(nextHandle, checkpoint.Catalog.NextHandle);
            Assert.Equal(
                replicas.OrderBy(static replica => replica.Value.Handle)
                    .Select(static replica => new CatalogReplica(replica.Value.Handle, (int)replica.Value.Window, replica.Value.FirstFile, replica.Key)),
                checkpoint.Catalog.Replicas);
            Assert.Equal(tombstones, checkpoint.Catalog.Tombstones);
        }

        var newest = ReplicaCatalog.Decode(root.Storage.ReadDurableFile(WalPath, CatalogNames(root)[^1]), store.CatalogCut).Checkpoint().Catalog;
        Assert.Equal(LedgerReplicaTestData.Ledgers(2, 4), newest.Tombstones);
        Assert.Equal(LedgerReplicaTestData.Ledgers(1, 3), newest.Replicas.Select(static replica => replica.Ledger));
    }

    /// <summary>
    /// LS-I4, §8.4: a dropped replica's files are released only once two catalog checkpoints cover its drop: the first
    /// catalog checkpoint after the drop keeps the WAL from the older one's cut, and the next releases it up to its own.
    /// </summary>
    [Fact]
    public async Task ADroppedReplicasFilesAreReleasedOnlyOnceTwoCatalogCheckpointsCoverItsDrop()
    {
        var root = NewRoot();
        await using var store = await root.OpenAsync(create: true);
        await WriteAsync(store, new(1), 0, 20);
        Assert.True(store.Wal.CurrentFile >= 3, $"The WAL rolled to file {store.Wal.CurrentFile} only.");
        await store.DropReplicaAsync(new(1), Token);
        await CheckpointCatalogAsync(store);
        var covering = store.ValidCatalogCheckpoints[^1];
        Assert.Equal(1ul, root.Storage.ListFiles(WalPath)[0]);

        await WriteAsync(store, new(2), 0);
        await store.WriteNewCatalogCheckpointAsync();
        Assert.Equal(covering, store.ValidCatalogCheckpoints[0]);
        Assert.Equal(covering.File, root.Storage.ListFiles(WalPath)[0]);
        Assert.Equal(covering.File, store.GetRetention().FirstRetainedFile);
    }

    /// <summary>
    /// LS-I4: whatever catalog checkpoints maintenance writes, it releases no WAL file past the older of the two newest
    /// valid ones' cut, though no live replica pins the WAL.
    /// </summary>
    [Fact]
    public async Task ReleaseNeverPassesTheOlderValidCatalogCheckpointsCut()
    {
        var root = NewRoot();
        await using var store = await root.OpenAsync(create: true);
        for (var round = 1UL; round <= 6; round++)
        {
            await WriteAsync(store, new(round), 0, 8);
            await store.DropReplicaAsync(new(round), Token);
            await CheckpointCatalogAsync(store);
            var valid = store.ValidCatalogCheckpoints;
            Assert.True(valid.Count >= 2, "The store holds fewer than two valid catalog checkpoints after a drop.");
            Assert.Equal(valid[^2].File, root.Storage.ListFiles(WalPath)[0]);
            Assert.Equal(valid[^2].File, store.GetRetention().FirstRetainedFile);
        }
    }

    /// <summary>
    /// LS-I4, §8.8: the store deletes every catalog checkpoint but the two newest valid ones before it releases the WAL
    /// past their cuts, so a deletion which fails, which stops the process (conventions.md §14), releases nothing, and the
    /// restarted process's maintenance deletes them before it releases.
    /// </summary>
    [Fact]
    public async Task CatalogCheckpointsButTheTwoNewestAreDeletedBeforeTheWalPastTheirCutsIsReleased()
    {
        var root = NewRoot();
        var store = await root.OpenAsync(create: true);
        await WriteAsync(store, new(1), 0, 10);
        await store.DropReplicaAsync(new(1), Token);
        await CheckpointCatalogAsync(store);
        var reported = FatalErrors.ReportedCount;
        root.Storage.NamedDeleteFault = static (_, _) => new IOException("injected catalog deletion failure");
        await WriteAsync(store, new(2), 0);
        store.RequestCatalogCheckpoint();
        await EventuallyAsync(() => store.MaintenanceCompletion is { IsCompleted: true }, "maintenance stops at the failed deletion");
        _ = StorageFailures.TakeOne(reported, FatalErrorSite.DeleteFile, WalPath);
        Assert.Equal(1ul, root.Storage.ListFiles(WalPath)[0]);
        Assert.True(CatalogNames(root).Count > 2);

        await root.CrashAsync(store, operatingSystem: false);
        await using var restarted = await root.OpenAsync();
        await WriteAsync(restarted, new(2), 1);
        await restarted.WriteNewCatalogCheckpointAsync();
        Assert.Equal(2, CatalogNames(root).Count);
        Assert.Equal(Math.Min(restarted.ValidCatalogCheckpoints[0].File, restarted.FindLive(new(2))!.Entry.FirstFile), root.Storage.ListFiles(WalPath)[0]);
        Assert.True(root.Storage.ListFiles(WalPath)[0] > 1, "No WAL file was released.");
    }

    /// <summary>
    /// LS-I4, §6.1: a crash after a catalog checkpoint is written and before the older ones are deleted, here the stop of
    /// a deletion which failed (conventions.md §14), leaves at least two valid ones and every WAL file at or above the
    /// older kept one's cut, so the store reopens with the same catalog, and from the older kept one too.
    /// </summary>
    [Fact]
    public async Task ACrashBetweenWritingACatalogCheckpointAndDiscardingAnOldOneKeepsTwoValid()
    {
        var root = NewRoot();
        var store = await root.OpenAsync(create: true);
        await WriteAsync(store, new(1), 0, 10);
        await store.DropReplicaAsync(new(1), Token);
        await CheckpointCatalogAsync(store);
        var reported = FatalErrors.ReportedCount;
        root.Storage.NamedDeleteFault = static (_, _) => new IOException("injected catalog deletion failure");
        await WriteAsync(store, new(2), 0);
        store.RequestCatalogCheckpoint();
        await EventuallyAsync(() => store.MaintenanceCompletion is { IsCompleted: true }, "maintenance stops at the failed deletion");
        _ = StorageFailures.TakeOne(reported, FatalErrorSite.DeleteFile, WalPath);
        var expected = store.Catalog;
        await root.CrashAsync(store, operatingSystem: true);

        Assert.True(CatalogNames(root).Count >= 3);
        store = await root.OpenAsync();
        AssertSameCatalog(expected, store.Catalog);
        await root.CrashAsync(store, operatingSystem: false);

        _ = DamageNewestValid(root);
        await using var older = await root.OpenAsync();
        AssertSameCatalog(expected, older.Catalog);
    }

    /// <summary>
    /// LS-I4, §8.7 step 6: an older kept catalog checkpoint may list a replica whose first file the store has released,
    /// a dropped one whose drop record lies above its cut; recovery from it skips the released files and applies the
    /// drop, so the replica's tombstone survives.
    /// </summary>
    [Fact]
    public async Task RecoveryFromTheOlderCatalogCheckpointSkipsTheReleasedFilesOfReplicasItLists()
    {
        var root = NewRoot();
        var store = await root.OpenAsync(create: true);
        await WriteAsync(store, new(1), 0, 14);
        await WriteAsync(store, new(2), 0);
        await CheckpointCatalogAsync(store);
        await store.DropReplicaAsync(new(1), Token);
        await CheckpointCatalogAsync(store);

        // The older kept catalog checkpoint lists the dropped replica, whose first file was released.
        _ = CatalogCheckpoints.TryParseFileName(CatalogNames(root)[0], out var olderCut);
        var older = ReplicaCatalog.Decode(root.Storage.ReadDurableFile(WalPath, CatalogNames(root)[0]), olderCut).Checkpoint().Catalog;
        Assert.Equal(1ul, older.Find(new(1))!.FirstFile);
        Assert.True(root.Storage.ListFiles(WalPath)[0] > 1, "No WAL file was released.");
        var expected = store.Catalog;
        await root.CrashAsync(store, operatingSystem: true);

        _ = DamageNewestValid(root);
        await using var reopened = await root.OpenAsync();
        AssertSameCatalog(expected, reopened.Catalog);
        Assert.Contains(new LedgerId(1), reopened.Catalog.Tombstones);
        DroppedAnswers.AssertDropped(await reopened.CreateReplicaAsync(new(1), Token));
    }

    /// <summary>
    /// LS-I2, LS-I4: a tombstone outlives the release of the WAL file holding its drop record, in the catalog
    /// checkpoints: the ledger stays dropped across restarts, from either kept catalog checkpoint.
    /// </summary>
    [Fact]
    public async Task ATombstoneOutlivesTheReleaseOfItsDropRecord()
    {
        var root = NewRoot();
        var store = await root.OpenAsync(create: true);
        await store.DropReplicaAsync(new(7), Token);
        var dropFile = store.Wal.CurrentFile;
        await WriteAsync(store, new(8), 0, 10);
        for (var round = 0; round < 2; round++)
        {
            await store.DropReplicaAsync(new((ulong)(20 + round)), Token);
            await CheckpointCatalogAsync(store);
        }

        await WriteAsync(store, new(9), 0, 10);
        await store.DropReplicaAsync(new(8), Token);
        await CheckpointCatalogAsync(store);
        await WriteAsync(store, new(9), 10);
        await store.WriteNewCatalogCheckpointAsync();
        Assert.True(root.Storage.ListFiles(WalPath)[0] > dropFile, "The drop record's file was not released.");
        await root.CrashAsync(store, operatingSystem: true);

        for (var fallback = 0; fallback < 2; fallback++)
        {
            if (fallback == 1)
            {
                _ = DamageNewestValid(root);
            }

            store = await root.OpenAsync();
            Assert.Equal(LedgerReplicaTestData.Ledgers(7, 8, 20, 21), store.Catalog.Tombstones);
            foreach (var dropped in store.Catalog.Tombstones)
            {
                DroppedAnswers.AssertDropped(await LedgerStoreReplicaTests.WriteAsync(store.OpenReplica(dropped, Window), 0, Payload(dropped, 0)));
                DroppedAnswers.AssertDropped(await store.OpenReplica(dropped, Window).ReadAsync(Slot.Zero, Token));
            }

            Assert.Equal([new LedgerId(9)], store.Replicas);
            await root.CrashAsync(store, operatingSystem: false);
        }
    }

    /// <summary>
    /// LS-I5: WAL damage which ends above the catalog checkpoint's cut may hide a create or drop record, so the open
    /// refuses and leaves the WAL in place.
    /// </summary>
    [Fact]
    public async Task WalDamageAtOrAboveTheCatalogCutRefusesToOpen()
    {
        var root = NewRoot();
        var store = await root.OpenAsync(create: true);
        await WriteAsync(store, new(1), 0);
        await store.CheckpointCatalogPastWritesAsync();
        var cut = store.CatalogCut!.Value;
        await WriteAsync(store, new(1), 1, 30);
        await root.CrashAsync(store, operatingSystem: false);

        // Recovery uses the newest catalog checkpoint, whose cut the damage must lie above.
        Assert.Equal(CatalogCheckpoints.FileName(cut), CatalogNames(root)[^1]);

        // Every damaged page lies well before the crash region recovery finalizes, so it is damage, not a torn write.
        var damaged = cut.File + 1;
        Assert.True(damaged < root.Storage.ListFiles(WalPath)[^1]);
        root.Storage.Damage(WalPath, damaged, 2 * SmallFiles.PageSize + 40, [1, 2, 3]);
        _ = await Assert.ThrowsAsync<LedgerReplicaCorruptException>(async () => await root.OpenAsync());
        Assert.Contains(damaged, root.Storage.ListFiles(WalPath));
    }

    /// <summary>
    /// §6.2, §7.3: a create, drop, or seal record above the catalog cut which breaks a rule of its format, or which
    /// disagrees with the catalog it applies to, is a damaged store record: the open refuses and keeps its files.
    /// </summary>
    [Theory]
    [InlineData("drop of a dropped ledger")]
    [InlineData("create of a dropped ledger")]
    [InlineData("create of a skipped handle")]
    [InlineData("seal of an unassigned handle")]
    [InlineData("seal naming another ledger")]
    [InlineData("seal with a truncated body")]
    [InlineData("store record of an unknown kind")]
    public async Task ADamagedStoreRecordRefusesToOpen(string damage)
    {
        var root = NewRoot();
        var store = await root.OpenAsync(create: true);
        await WriteAsync(store, new(1), 0);
        await store.DropReplicaAsync(new(2), Token);
        await store.DisposeAsync();

        byte[] record = damage switch
        {
            "drop of a dropped ledger" => Store(StoreRecordKind.Drop, new DropRecord(new(2)).Body()),
            "create of a dropped ledger" => Store(StoreRecordKind.Create, new CreateRecord([new CatalogPair(new(2), 1, 64)]).Body()),
            "create of a skipped handle" => Store(StoreRecordKind.Create, new CreateRecord([new CatalogPair(new(3), 2, 64)]).Body()),
            "seal of an unassigned handle" => Store(StoreRecordKind.Seal, new SealRecord(new(3), 1).Body()),
            "seal naming another ledger" => Store(StoreRecordKind.Seal, new SealRecord(new(3), 0).Body()),
            "seal with a truncated body" => Store(StoreRecordKind.Seal, new SealRecord(new(1), 0).Body()[..^1]),
            _ => Store((StoreRecordKind)4, new CreateRecord([new CatalogPair(new(1), 0, 64)]).Body()),
        };
        await AppendAsync(root, record);
        var files = root.Storage.ListFiles(WalPath);
        _ = await Assert.ThrowsAsync<LedgerReplicaCorruptException>(async () => await root.OpenAsync());
        Assert.Subset(root.Storage.ListFiles(WalPath).ToHashSet(), files.ToHashSet());

        static byte[] Store(StoreRecordKind kind, byte[] body) => [.. BitConverter.GetBytes(StoreRecord.KeyOf(kind)), .. body];
    }

    /// <summary>
    /// §6.2, LS-I5: a torn range above the catalog cut holds no acknowledged record, a torn create record's included, so
    /// the open skips it and serves every acknowledged write.
    /// </summary>
    [Fact]
    public async Task ATornTailAboveTheCatalogCutOpens()
    {
        var options = SmallFiles with { PageSize = 4096, FileSize = 64 * 4096 };
        for (var seed = 0; seed < 8; seed++)
        {
            var root = new SimulatedNodeRoot(options, ExplicitOnly, new SimulatedWalStorage(seed));
            var store = await root.OpenAsync(create: true);
            await WriteAsync(store, new(1), 0, 3);
            await store.CheckpointCatalogPastWritesAsync();
            root.Storage.HoldWrites = true;
            var replica = store.OpenReplica(new(1), Window);
            var pending = Enumerable.Range(3, 6).Select(slot => LedgerStoreReplicaTests.WriteAsync(replica, slot, Payload(new(1), slot))).Append(
                LedgerStoreReplicaTests.WriteAsync(store.OpenReplica(new(2), Window), 0, Payload(new(2), 0))).ToArray();
            await EventuallyAsync(() => root.Storage.PendingWriteCount != 0, "the writes are in flight");
            await root.CrashAsync(store, operatingSystem: true);
            await SettleAsync(pending);

            // Ledger 2's create record was in flight at the crash, so the crash tore or lost it with its write.
            Assert.False(pending[^1].IsCompletedSuccessfully);
            await using var reopened = await root.OpenAsync();
            Assert.Equal([new LedgerId(1)], reopened.Replicas);
            Assert.False(reopened.Catalog.IsDropped(new(2)));
            for (var slot = 0; slot < 3; slot++)
            {
                Assert.Equal(Payload(new(1), slot), await LedgerReplicaTestData.ReadBytesAsync(reopened.OpenReplica(new(1), Window), slot, Token));
            }
        }
    }

    /// <summary>
    /// LS-I5, §6.2: a store which holds no valid catalog checkpoint, though its WAL holds a record, lost its catalog, a
    /// detected node loss: the open refuses and keeps every file.
    /// </summary>
    [Fact]
    public async Task NoValidCatalogCheckpointRefusesToOpen()
    {
        var root = NewRoot();
        var store = await root.OpenAsync(create: true);
        await WriteAsync(store, new(1), 0);
        await store.DropReplicaAsync(new(2), Token);
        await CheckpointCatalogAsync(store);
        await root.CrashAsync(store, operatingSystem: false);

        var names = CatalogNames(root);
        foreach (var name in names)
        {
            Damage(root, name);
        }

        var files = root.Storage.ListFiles(WalPath);
        _ = await Assert.ThrowsAsync<LedgerReplicaCorruptException>(async () => await root.OpenAsync());
        Assert.Equal(names, CatalogNames(root));
        Assert.Subset(root.Storage.ListFiles(WalPath).ToHashSet(), files.ToHashSet());
    }

    /// <summary>
    /// LS-I2, §8.7 step 8: an open whose replay meets a record past the newest catalog checkpoint's cut writes a catalog
    /// checkpoint at its frontier before it serves, and one whose replay meets none writes none; the replay applies a
    /// create record past the cut, which the checkpoint does not reflect (§7.2).
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task AnOpenCheckpointsTheCatalogOnlyWhenItsReplayReachesTheCut(int records)
    {
        var root = NewRoot();
        var store = await root.OpenAsync(create: true);
        await store.CreateReplicaAsync(new(1), Token);
        await store.WriteNewCatalogCheckpointAsync();
        var cut = store.CatalogCut!.Value;
        Assert.Equal(2, CatalogNames(root).Count);
        if (records > 0)
        {
            await store.CreateReplicaAsync(new(2), Token);
        }

        if (records > 1)
        {
            await WriteAsync(store, new(2), 0);
        }

        await root.CrashAsync(store, operatingSystem: false);
        await using var reopened = await root.OpenAsync();
        Assert.Equal(records == 0 ? LedgerReplicaTestData.Ledgers(1) : LedgerReplicaTestData.Ledgers(1, 2), reopened.Replicas);
        if (records == 0)
        {
            Assert.Equal(cut, reopened.CatalogCut);
        }
        else
        {
            Assert.True(reopened.CatalogCut > cut, $"The open wrote no catalog checkpoint past {cut}.");
        }
    }

    /// <summary>
    /// §8.7 step 8, §8.8: with a one-page write window, a create record which begins in the last bytes of a page holding a
    /// durable write and ends in the next keeps the durable frontier where it begins while its last page is in flight,
    /// so the catalog checkpoint captured then is cut exactly at the create record, which it does not reflect: the record
    /// awaits the next catalog checkpoint, and after a crash the open applies it and checkpoints the catalog past it.
    /// </summary>
    [Fact]
    public async Task ACreateRecordWhichBeginsAtTheCatalogCutIsReplayedAndCheckpointed()
    {
        var root = new SimulatedNodeRoot(SmallFiles with { WriteWindowPages = 1, FileSize = 64 * SmallFiles.PageSize }, ExplicitOnly);
        var store = await root.OpenAsync(create: true);
        await WriteAsync(store, new(1), 0);
        await store.WriteNewCatalogCheckpointAsync();
        var replica = store.OpenReplica(new(1), Window);

        // The helper retired every record, so the WAL counts only this test's.
        Assert.Equal(0, store.Wal.OutstandingRecords);
        root.Storage.HoldWrites = true;
        var held = LedgerStoreReplicaTests.WriteAsync(replica, 1, Payload(new(1), 1));
        await EventuallyAsync(() => root.Storage.PendingWriteCount == 1, "the first write is in flight");

        // A page holds its CRC, then the filler's frame word and key, then 20 bytes: the create record's first frame
        // header and 8 of its bytes.
        var filler = LedgerStoreReplicaTests.WriteAsync(replica, 2, LedgerReplicaTestData.CreatePayload(SmallFiles.PageSize - 36, 2));
        var creation = store.CreateLiveAsync(new(2), Window.WindowSize, Token).AsTask();
        await EventuallyAsync(() => store.Wal.OutstandingRecords == 3, "the WAL admits the filler and the create record");
        Assert.True(root.Storage.CompleteWrite(static (name, _, _) => name is null));
        Assert.Equal(ReplicaWriteCase.Written, (await held).Case);
        await EventuallyAsync(() => root.Storage.PendingWriteCount == 1, "the page the filler shares with the create record is in flight");
        Assert.True(root.Storage.CompleteWrite(static (name, _, _) => name is null));
        Assert.Equal(ReplicaWriteCase.Written, (await filler).Case);

        // The WAL advances its frontier after the outcome callbacks, as it retires the records.
        await EventuallyAsync(() => store.Wal.OutstandingRecords == 1, "the frontier passes the filler");
        var frontier = store.Wal.DurableFrontier;
        store.RequestCatalogCheckpoint();
        await EventuallyAsync(() => store.CatalogCut == frontier, "maintenance captures a catalog checkpoint at the frontier");
        await EventuallyAsync(() => root.Storage.PendingWriteCount == 1, "the create record's last page is in flight");
        Assert.False(creation.IsCompleted);
        root.Storage.HoldWrites = false;
        Assert.True(root.Storage.CompleteWrite(static (name, _, _) => name is null));
        _ = await creation;
        Assert.Equal(frontier, await FindCreateRecordAsync(store, new(2)));
        Assert.False(store.CatalogCheckpointCurrent, "The create record at the catalog cut does not await a catalog checkpoint.");

        await root.CrashAsync(store, operatingSystem: true);
        await using var reopened = await root.OpenAsync();
        Assert.Equal(LedgerReplicaTestData.Ledgers(1, 2), reopened.Replicas);
        Assert.True(reopened.CatalogCut > frontier, $"The open wrote no catalog checkpoint past {frontier}.");
    }

    /// <summary>
    /// §8.8: a create or drop record can begin at the durable frontier, when it shares its first page with a durable
    /// record while its later pages are in flight, so a capture whose frontier is the latest published record's position
    /// cuts just past that record.
    /// </summary>
    [Fact]
    public async Task ACaptureAtTheLatestPublishedRecordsPositionCutsPastIt()
    {
        var storage = new SimulatedWalStorage();
        await storage.CreateDirectoryAsync(WalPath, Token);
        await using var directory = await storage.OpenDirectoryAsync(WalPath, Token);
        var checkpoints = new CatalogCheckpoints(directory, [], firstRetainedFile: 1);
        var position = new WalPosition(1, 4096);
        checkpoints.Published(position);
        Assert.Equal(new WalPosition(1, 4097), checkpoints.Capture(position, final: false));
    }

    /// <summary>
    /// LS-I4, LS-I5, §6.2: a catalog checkpoint whose cut, from its name, lies past the WAL the store recovers reflects a
    /// lost tail, and one below the first retained file needs a released prefix: either refuses the open, which compares
    /// the cut with the retained files before it opens the WAL, so it publishes no WAL file and leaves no handle open.
    /// </summary>
    [Fact]
    public async Task ACatalogCheckpointWhoseCutLiesOutsideTheRecoveredWalRefusesToOpen()
    {
        var root = NewRoot();
        var store = await root.OpenAsync(create: true);
        await WriteAsync(store, new(1), 0, 10);
        await store.DropReplicaAsync(new(1), Token);
        await CheckpointCatalogAsync(store);
        await WriteAsync(store, new(2), 0);
        await store.WriteNewCatalogCheckpointAsync();
        var catalog = store.Catalog;
        await root.CrashAsync(store, operatingSystem: false);
        var files = root.Storage.ListFiles(WalPath);
        Assert.True(files[0] > 1, "No WAL file was released.");

        foreach (var cut in new[] { new WalPosition(files[^1] + 1, 4096), new WalPosition(files[0] - 1, 4096) })
        {
            var name = CatalogCheckpoints.FileName(cut);
            await WriteCatalogAsync(root, catalog, cut);
            _ = await Assert.ThrowsAsync<LedgerReplicaCorruptException>(async () => await root.OpenAsync());
            Assert.Equal(files, root.Storage.ListFiles(WalPath));
            Assert.Equal(0, root.Storage.OpenHandleCount);
            await DeleteCatalogAsync(root, name);
        }

        await using var reopened = await root.OpenAsync();
        AssertSameCatalog(catalog, reopened.Catalog);
    }

    /// <summary>
    /// LS-I2, §8.7 step 8: a valid catalog checkpoint whose derived catalog lacks a tombstone of an older valid one
    /// contradicts it, so the open refuses.
    /// </summary>
    [Fact]
    public async Task ACatalogCheckpointMissingAnOlderOnesTombstoneRefusesToOpen()
    {
        var root = NewRoot();
        var store = await root.OpenAsync(create: true);
        await WriteAsync(store, new(1), 0);
        await store.DropReplicaAsync(new(4), Token);
        await CheckpointCatalogAsync(store);
        var newest = store.CatalogCut!.Value;
        var catalog = store.Catalog;
        await root.CrashAsync(store, operatingSystem: false);

        var forged = new ReplicaCatalog(catalog.NextHandle, [.. catalog.Replicas], catalog.Tombstones.Remove(new(4)));
        await WriteCatalogAsync(root, forged, newest with { Offset = newest.Offset + 1 });
        _ = await Assert.ThrowsAsync<LedgerReplicaCorruptException>(async () => await root.OpenAsync());
    }

    /// <summary>
    /// LS-I5, §6.2: with every catalog checkpoint lost while the WAL holds a record, the store detects the lost catalog
    /// and refuses to open, keeping its files.
    /// </summary>
    [Fact]
    public async Task EveryCatalogCheckpointLostWithARecordInTheWalRefusesToOpen()
    {
        var root = NewRoot();
        var store = await root.OpenAsync(create: true);
        await store.DropReplicaAsync(new(4), Token);
        await root.CrashAsync(store, operatingSystem: false);

        await DeleteCatalogsAsync(root);
        var files = root.Storage.ListFiles(WalPath);
        Assert.Equal(1ul, files[0]);
        _ = await Assert.ThrowsAsync<LedgerReplicaCorruptException>(async () => await root.OpenAsync());
        Assert.Subset(root.Storage.ListFiles(WalPath).ToHashSet(), files.ToHashSet());
        Assert.Empty(CatalogNames(root));
    }

    /// <summary>
    /// LS-I5, §6.2: a WAL which has released its file 1 had a catalog, so with every catalog checkpoint lost the store
    /// refuses to open, though its retained WAL holds no record.
    /// </summary>
    [Fact]
    public async Task EveryCatalogCheckpointLostAfterAReleaseRefusesToOpen()
    {
        var root = NewRoot();
        var store = await root.OpenAsync(create: true);
        await WriteAsync(store, new(1), 0, 3);
        await store.DropReplicaAsync(new(1), Token);
        await CheckpointCatalogAsync(store);
        for (var restart = 0; restart < 3; restart++)
        {
            await root.CrashAsync(store, operatingSystem: false);
            store = await root.OpenAsync();
            await CheckpointCatalogAsync(store);
        }

        Assert.True(root.Storage.ListFiles(WalPath)[0] > 1, "No WAL file was released.");
        await foreach (var entry in store.Wal.ReadAllAsync(default, Token))
        {
            Assert.False(entry.TryGetValue(out WalRecord _), "The retained WAL holds a record.");
        }

        await root.CrashAsync(store, operatingSystem: false);
        await DeleteCatalogsAsync(root);
        _ = await Assert.ThrowsAsync<LedgerReplicaCorruptException>(async () => await root.OpenAsync());
    }

    /// <summary>
    /// §8.7 steps 4 and 5: a store whose WAL still holds its file 1 and no record, as a crash before the first catalog
    /// checkpoint leaves it, opens with the empty catalog and writes its checkpoint.
    /// </summary>
    [Fact]
    public async Task OnlyAWalWithItsFileOneAndNoRecordWritesTheEmptyCatalog()
    {
        var root = NewRoot();
        var store = await root.OpenAsync(create: true);
        await root.CrashAsync(store, operatingSystem: false);
        await DeleteCatalogsAsync(root);

        await using var reopened = await root.OpenAsync();
        AssertSameCatalog(ReplicaCatalog.Empty, reopened.Catalog);
        Assert.Single(CatalogNames(root));
        Assert.Equal(1ul, root.Storage.ListFiles(WalPath)[0]);
    }

    /// <summary>
    /// Regression test, §8.8: a capture whose write failed had already moved the published drop into the catalog it
    /// captured, and its retry cut at the durable frontier, which a lower write group still in flight held below the drop;
    /// the checkpoint reflected a record above its cut, and the open refused when it replayed the drop again. A capture
    /// now takes its catalog and its cut together, the cut just past the latest published record, so a capture taken
    /// while the lower write is in flight cuts past the drop.
    /// </summary>
    [Fact]
    public async Task ACaptureCutsPastAPublishedDropWhileALowerWriteIsInFlight()
    {
        var root = new SimulatedNodeRoot(SmallFiles with { MinWriteGroupsInFlight = 2, MaxWriteGroupsInFlight = 2 }, ExplicitOnly);
        var store = await root.OpenAsync(create: true);
        await WriteAsync(store, new(1), 0);
        root.Storage.HoldWrites = true;
        var write = LedgerStoreReplicaTests.WriteAsync(store.OpenReplica(new(1), Window), 1, Payload(new(1), 1));
        await EventuallyAsync(() => root.Storage.PendingWriteCount == 1, "the slot write is in flight");
        var drop = store.DropReplicaAsync(new(2), Token).AsTask();
        await EventuallyAsync(() => root.Storage.PendingWriteCount == 2, "the drop record is in flight above it");
        long last = -1;
        _ = root.Storage.CompleteWrite((_, _, offset) =>
        {
            last = Math.Max(last, offset);
            return false;
        });
        Assert.True(root.Storage.CompleteWrite((name, _, offset) => name is null && offset == last));
        await drop;
        Assert.False(write.IsCompleted);

        store.RequestCatalogCheckpoint();
        await EventuallyAsync(() => store.CatalogCheckpointCurrent && store.MaintenanceIdle, "maintenance writes the catalog checkpoint");
        Assert.True(store.CatalogCut > store.Wal.DurableFrontier, "The capture cut at the frontier the held write keeps below the drop.");

        await root.CrashAsync(store, operatingSystem: false);
        await SettleAsync([write]);
        await using var reopened = await root.OpenAsync();
        Assert.True(reopened.Catalog.IsDropped(new(2)));
        Assert.Equal([new LedgerId(1)], reopened.Replicas);
    }

    /// <summary>
    /// §8.8: a requested catalog checkpoint which would capture no record past the newest one's cut is not written, so
    /// the request writes nothing.
    /// </summary>
    [Fact]
    public async Task ARequestedCatalogCheckpointWithNothingPastTheNewestCutWritesNothing()
    {
        var root = NewRoot();
        await using var store = await root.OpenAsync(create: true);
        await store.CreateReplicaAsync(new(1), Token);
        await store.WriteNewCatalogCheckpointAsync();
        var names = CatalogNames(root);
        var written = store.CatalogCheckpointsWritten;
        for (var round = 0; round < 3; round++)
        {
            store.RequestCatalogCheckpoint();
            await EventuallyAsync(() => store.CatalogCheckpointCurrent && store.MaintenanceIdle, "maintenance takes the request");
        }

        Assert.Equal(written, store.CatalogCheckpointsWritten);
        Assert.Equal(names, CatalogNames(root));
    }

    /// <summary>
    /// §8.7 step 5: the open reads catalog checkpoints newest first only until two are valid, so it never reads an older
    /// one which maintenance could not yet delete, here because the deletion failed and stopped the process
    /// (conventions.md §14), and a failed read of it, which would stop the process, never happens.
    /// </summary>
    [Fact]
    public async Task AnOpenReadsCatalogCheckpointsOnlyUntilTwoAreValid()
    {
        var root = NewRoot();
        var store = await root.OpenAsync(create: true);
        var oldest = Assert.Single(CatalogNames(root));
        var reported = FatalErrors.ReportedCount;
        root.Storage.NamedDeleteFault = static (_, _) => new IOException("Another process holds the file open.");
        await WriteAsync(store, new(1), 0);
        await CheckpointCatalogAsync(store);
        await WriteAsync(store, new(1), 1);

        // The write is a slot record, which the capture reaches only below the frontier, so the third checkpoint is cut past
        // it only once the WAL retires it.
        await store.WaitForFrontierAsync();
        store.RequestCatalogCheckpoint();
        await EventuallyAsync(() => store.MaintenanceCompletion is { IsCompleted: true }, "maintenance stops at the failed deletion");
        _ = StorageFailures.TakeOne(reported, FatalErrorSite.DeleteFile, WalPath);
        Assert.Equal(3, CatalogNames(root).Count);
        await root.CrashAsync(store, operatingSystem: false);

        root.Storage.NamedReadFault = (_, name, _, _) => name == oldest ? new IOException("The disk failed.") : null;
        await using var reopened = await root.OpenAsync();
        Assert.Equal(LedgerReplicaTestData.Ledgers(1), reopened.Replicas);
    }

    /// <summary>
    /// §8.7 step 4: a store which holds no valid catalog checkpoint, a new one or one which lost its catalog checkpoints
    /// before writing a record, serves only once it has written one, so an open whose write fails, which is reported and
    /// stops the process (conventions.md §14), fails, leaves nothing open, and writes nothing else, a new store's node file
    /// included; the restarted process's open writes it.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnOpenWhichCannotWriteItsFirstCatalogCheckpointFails(bool created)
    {
        var root = NewRoot();
        if (created)
        {
            await root.CrashAsync(await root.OpenAsync(create: true), operatingSystem: false);
            await DeleteCatalogsAsync(root);
        }

        var reported = FatalErrors.ReportedCount;
        root.Storage.NamedWriteFault = static (_, name, _, _) => IsCatalogCheckpoint(name) ? new IOException("The disk is full.") : null;
        _ = await Assert.ThrowsAsync<IOException>(async () => await root.OpenAsync(create: true));
        _ = StorageFailures.TakeOne(reported, FatalErrorSite.CreateFile, WalPath);
        Assert.Empty(CatalogNames(root));
        Assert.Equal(created, root.Storage.ListNamedFiles(WalPath).Contains(NodeFile.Name));
        Assert.Equal(0, root.Storage.OpenHandleCount);

        root.Crash(operatingSystem: false);
        await using var store = await root.OpenAsync(create: true);
        Assert.Single(CatalogNames(root));
        Assert.Empty(store.Replicas);
    }

    /// <summary>
    /// §8.7 step 8, conventions.md §14: an open which holds a valid catalog checkpoint and needs its own fails when that
    /// write fails, which is reported and stops the process, and leaves the catalog checkpoints it found; the restarted
    /// process's open writes its own and serves.
    /// </summary>
    [Fact]
    public async Task AnOpenWhoseOwnCatalogCheckpointFailsFailsAndTheRestartedOpenWritesIt()
    {
        var root = NewRoot();
        var store = await root.OpenAsync(create: true);
        await WriteAsync(store, new(1), 0);
        await root.CrashAsync(store, operatingSystem: false);
        var names = CatalogNames(root);

        var reported = FatalErrors.ReportedCount;
        root.Storage.NamedWriteFault = static (_, name, _, _) => IsCatalogCheckpoint(name) ? new IOException("The disk is full.") : null;
        _ = await Assert.ThrowsAsync<IOException>(async () => await root.OpenAsync());
        _ = StorageFailures.TakeOne(reported, FatalErrorSite.CreateFile, WalPath);
        Assert.Equal(names, CatalogNames(root));
        Assert.Equal(0, root.Storage.OpenHandleCount);

        root.Crash(operatingSystem: false);
        await using var restarted = await root.OpenAsync();
        Assert.Equal(LedgerReplicaTestData.Ledgers(1), restarted.Replicas);
        Assert.Equal(names.Count + 1, CatalogNames(root).Count);
        await AssertWrittenAsync(restarted, new() { [new(1)] = 1 });
    }

    /// <summary>
    /// Regression test, §8.8: a catalog checkpoint whose write failed after its rename published its name was a file the
    /// store did not know of, so no deletion removed it and a release passed its cut, and the next open refused the
    /// checkpoint below its first retained file. The failure now stops the process (conventions.md §14), and the
    /// restarted process's open lists the published checkpoint with the others, so maintenance deletes it with the other
    /// superseded ones before any release passes its cut.
    /// </summary>
    [Fact]
    public async Task ACatalogCheckpointAFailedWritePublishedIsDeletedBeforeAnyRelease()
    {
        var root = NewRoot();
        var store = await root.OpenAsync(create: true);
        await WriteAsync(store, new(1), 0, 10);
        await store.DropReplicaAsync(new(1), Token);
        string? published = null;
        var reported = FatalErrors.ReportedCount;
        root.Storage.NamedPublishedFault = (_, name) =>
        {
            if (!IsCatalogCheckpoint(name))
            {
                return null;
            }

            published = name;
            return new IOException("The directory flush failed after the rename.");
        };
        store.RequestCatalogCheckpoint();
        await EventuallyAsync(() => store.MaintenanceCompletion is { IsCompleted: true }, "maintenance stops at the failed write");
        _ = StorageFailures.TakeOne(reported, FatalErrorSite.FlushDirectory, WalPath);
        Assert.Contains(published, CatalogNames(root));

        await root.CrashAsync(store, operatingSystem: false);
        store = await root.OpenAsync();
        Assert.True(store.Catalog.IsDropped(new(1)));
        for (var round = 0; round < 2; round++)
        {
            await WriteAsync(store, new(2), round * 10, 10);
            await store.WriteNewCatalogCheckpointAsync();
        }

        Assert.DoesNotContain(published, CatalogNames(root));
        Assert.True(root.Storage.ListFiles(WalPath)[0] > 1, "Maintenance released no WAL file.");

        await root.CrashAsync(store, operatingSystem: true);
        await using var reopened = await root.OpenAsync();
        Assert.True(reopened.Catalog.IsDropped(new(1)));
        Assert.Equal([new LedgerId(2)], reopened.Replicas);
    }

    /// <summary>
    /// LS-I5, §6.2: WAL damage which ends exactly at the catalog checkpoint's cut faults only the slots it reaches, and the
    /// open serves the rest; damage which straddles the cut may hide a create or drop record above it, so the open
    /// refuses.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-100)]
    public async Task DamageEndingAtTheCatalogCutOpensAndDamageStraddlingItRefuses(int pastDamage)
    {
        var root = NewRoot();
        var store = await root.OpenAsync(create: true);
        await WriteAsync(store, new(1), 0, 21);
        var catalog = store.Catalog;
        await root.CrashAsync(store, operatingSystem: false);

        // File 2 holds only ledger 1's slot records and lies before the final file, so the damage is not a torn write.
        Assert.True(root.Storage.ListFiles(WalPath)[^1] > 2);
        const int Page = 2;
        var damageEnd = SmallFiles.PageSize * (1 + Page + 1);
        await WriteCatalogAsync(root, catalog, new WalPosition(2, (ulong)(damageEnd + pastDamage)));
        root.Storage.Damage(WalPath, 2, SmallFiles.PageSize * (1 + Page) + 40, [1, 2, 3]);
        if (pastDamage < 0)
        {
            _ = await Assert.ThrowsAsync<LedgerReplicaCorruptException>(async () => await root.OpenAsync());
            return;
        }

        await using var reopened = await root.OpenAsync();
        var replica = reopened.OpenReplica(new(1), Window);
        var corrupt = 0;
        for (var slot = 0; slot < 21; slot++)
        {
            try
            {
                Assert.Equal(Payload(new(1), slot), await LedgerReplicaTestData.ReadBytesAsync(replica, slot, Token));
            }
            catch (LedgerReplicaCorruptException)
            {
                corrupt++;
            }
        }

        Assert.InRange(corrupt, 1, 20);
    }

    /// <summary>
    /// LS-I5, §6.2: a catalog checkpoint whose cut lies past the end recovery found in its file reflects records the WAL
    /// lost from that file's tail, though the file survives, so the open refuses.
    /// </summary>
    [Fact]
    public async Task ACatalogCheckpointWhoseCutLiesPastTheRecoveredEndOfItsFileRefusesToOpen()
    {
        var root = NewRoot();
        var store = await root.OpenAsync(create: true);
        await WriteAsync(store, new(1), 0, 3);
        await store.CheckpointCatalogPastWritesAsync();
        var cut = store.CatalogCut!.Value;
        await root.CrashAsync(store, operatingSystem: false);
        Assert.Equal(cut.File, root.Storage.ListFiles(WalPath)[^1]);

        // The file loses the pages below the cut, and no later file shows the loss.
        root.Storage.TruncateFile(WalPath, cut.File, 2 * SmallFiles.PageSize);
        Assert.True(cut.Offset > 2 * (ulong)SmallFiles.PageSize);
        _ = await Assert.ThrowsAsync<LedgerReplicaCorruptException>(async () => await root.OpenAsync());
    }

    /// <summary>
    /// §8.8: a published create or drop record awaits a catalog checkpoint until the interval since the last has passed,
    /// so drops within an interval share one; and a catalog which has not changed is not written again as intervals pass.
    /// </summary>
    [Fact]
    public async Task DropsWithinAnIntervalShareOneCatalogCheckpointAndAnUnchangedCatalogIsNotRewritten()
    {
        var time = new FakeTimeProvider();
        var interval = TimeSpan.FromSeconds(10);
        var root = new SimulatedNodeRoot(SmallFiles, new LedgerStoreCheckpointOptions { CheckpointInterval = interval });
        await using var store = await root.OpenAsync(create: true, timeProvider: time);
        var written = store.CatalogCheckpointsWritten;
        await store.DropReplicaAsync(new(1), Token);
        await EventuallyAsync(() => store.MaintenanceIdle, "maintenance waits out the interval");
        time.Advance(interval / 2);
        await store.DropReplicaAsync(new(2), Token);
        await EventuallyAsync(() => store.MaintenanceIdle, "maintenance waits out the interval");
        Assert.Equal(written, store.CatalogCheckpointsWritten);
        Assert.False(store.CatalogCheckpointCurrent);

        time.Advance(interval / 2);
        await EventuallyAsync(() => store.CatalogCheckpointCurrent && store.MaintenanceIdle, "maintenance checkpoints both drops");
        Assert.Equal(written + 1, store.CatalogCheckpointsWritten);
        for (var round = 0; round < 3; round++)
        {
            time.Advance(interval);
            await EventuallyAsync(() => store.MaintenanceIdle, "maintenance finds nothing due");
        }

        Assert.Equal(written + 1, store.CatalogCheckpointsWritten);
    }

    /// <summary>
    /// §8.8: once the WAL past the catalog checkpoint's cut reaches the replay bound, a catalog checkpoint is due without
    /// waiting for the interval.
    /// </summary>
    [Fact]
    public async Task TheReplayBoundMakesACatalogCheckpointDueBeforeTheInterval()
    {
        var root = new SimulatedNodeRoot(SmallFiles, ExplicitOnly with { CheckpointReplayBytes = 4 * SmallFiles.PageSize });
        await using var store = await root.OpenAsync(create: true);
        var written = store.CatalogCheckpointsWritten;
        await WriteAsync(store, new(1), 0, 10);
        await EventuallyAsync(() => store.CatalogCheckpointsWritten > written && store.MaintenanceIdle, "maintenance checkpoints the catalog at the replay bound");
    }

    /// <summary>
    /// LS-I2, §8.7 step 8: the derived catalog follows each valid catalog checkpoint the open read, clause by clause: one
    /// which assigned more handles, or which lists a replica under another handle, contradicts it, so the open refuses.
    /// </summary>
    [Theory]
    [InlineData("next handle")]
    [InlineData("replica")]
    public async Task ACatalogCheckpointTheDerivedCatalogDoesNotFollowRefusesToOpen(string clause)
    {
        var root = NewRoot();
        var store = await root.OpenAsync(create: true);
        await WriteAsync(store, new(1), 0);
        await WriteAsync(store, new(2), 0);
        await CheckpointCatalogAsync(store);
        var newest = store.CatalogCut!.Value;
        var catalog = store.Catalog;
        await root.CrashAsync(store, operatingSystem: false);

        var forged = clause == "next handle"
            ? new ReplicaCatalog(catalog.NextHandle + 3, [.. catalog.Replicas], catalog.Tombstones)
            : new ReplicaCatalog(
                catalog.NextHandle,
                [.. catalog.Replicas.Zip(catalog.Replicas.Reverse(), static (slot, replica) => replica with { Handle = slot.Handle })],
                catalog.Tombstones);
        await WriteCatalogAsync(root, forged, newest with { Offset = newest.Offset - 1 });
        _ = await Assert.ThrowsAsync<LedgerReplicaCorruptException>(async () => await root.OpenAsync());
    }

    private static SimulatedNodeRoot NewRoot() => new(SmallFiles, ExplicitOnly);

    private static bool IsCatalogCheckpoint(string name) => name.EndsWith(CatalogCheckpoints.Extension, StringComparison.Ordinal);

    /// <summary>
    /// Waits for writes a crash failed, whatever their outcomes.
    /// </summary>
    private static Task SettleAsync(IEnumerable<Task<ReplicaWriteOutcome>> writes) =>
        ((Task)Task.WhenAll(writes)).ContinueWith(static _ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private static async Task RunAsync((ModelOperation Operation, ulong Ledger)[] operations)
    {
        var root = NewRoot();
        var store = await root.OpenAsync(create: true);
        var live = new Dictionary<LedgerId, uint>();
        var dropped = new SortedSet<LedgerId>();
        var written = new Dictionary<LedgerId, int>();
        var nextHandle = 0u;
        try
        {
            foreach (var (operation, value) in operations)
            {
                var ledger = new LedgerId(value);
                switch (operation)
                {
                    case ModelOperation.Write or ModelOperation.WriteUntilRoll when dropped.Contains(ledger):
                        DroppedAnswers.AssertDropped(await LedgerStoreReplicaTests.WriteAsync(store.OpenReplica(ledger, Window), 0, Payload(ledger, 0)));
                        break;
                    case ModelOperation.Write or ModelOperation.WriteUntilRoll:
                        var file = store.Wal.CurrentFile;
                        do
                        {
                            var slot = written.GetValueOrDefault(ledger);
                            Assert.Equal(ReplicaWriteCase.Written, (await LedgerStoreReplicaTests.WriteAsync(store.OpenReplica(ledger, Window), slot, Payload(ledger, slot))).Case);
                            written[ledger] = slot + 1;
                        }
                        while (operation == ModelOperation.WriteUntilRoll && store.Wal.CurrentFile == file);

                        if (live.TryAdd(ledger, nextHandle))
                        {
                            nextHandle++;
                        }

                        break;
                    case ModelOperation.Drop:
                        await store.DropReplicaAsync(ledger, Token);
                        _ = live.Remove(ledger);
                        _ = written.Remove(ledger);
                        _ = dropped.Add(ledger);
                        break;
                    case ModelOperation.CatalogCheckpoint:
                        await CheckpointCatalogAsync(store);
                        break;
                    case ModelOperation.ProcessCrash or ModelOperation.OperatingSystemCrash:
                        // Maintenance settles first, so the crash follows the workload rather than a race with it.
                        await EventuallyAsync(() => store.MaintenanceIdle, "maintenance settles before the crash");
                        await root.CrashAsync(store, operatingSystem: operation == ModelOperation.OperatingSystemCrash);
                        store = await root.OpenAsync();
                        await AssertWrittenAsync(store, written);
                        break;
                    default:
                        throw new InvalidOperationException("The model has no such operation.");
                }

                Assert.Equal(live.OrderBy(static replica => replica.Value).Select(static replica => (replica.Key, replica.Value)), store.Catalog.Replicas.Select(static replica => (replica.Ledger, replica.Handle)));
                Assert.Equal(dropped, store.Catalog.Tombstones);
                Assert.Equal(nextHandle, store.Catalog.NextHandle);
                var valid = CatalogNames(root).Where(name => IsValid(root, name)).Select(static name => CatalogCheckpoints.TryParseFileName(name, out var cut) ? cut : default).ToList();
                if (valid.Count >= 2)
                {
                    Assert.True(root.Storage.ListFiles(WalPath)[0] <= valid[^2].File, "The store released WAL past the older kept catalog checkpoint's cut.");
                }
            }

            var expected = store.Catalog;
            await EventuallyAsync(() => store.MaintenanceIdle, "maintenance settles before the crash");
            await root.CrashAsync(store, operatingSystem: false);
            if (CatalogNames(root).Count(name => IsValid(root, name)) >= 2)
            {
                _ = DamageNewestValid(root);
            }

            store = await root.OpenAsync();
            AssertSameCatalog(expected, store.Catalog);
            await AssertWrittenAsync(store, written);
        }
        finally
        {
            await store.DisposeAsync();
        }
    }

    /// <summary>
    /// Reads back every slot written to a live replica.
    /// </summary>
    private static async Task AssertWrittenAsync(LedgerStore store, Dictionary<LedgerId, int> written)
    {
        foreach (var (ledger, count) in written)
        {
            var replica = store.OpenReplica(ledger, Window);
            for (var slot = 0; slot < count; slot++)
            {
                Assert.Equal(Payload(ledger, slot), await LedgerReplicaTestData.ReadBytesAsync(replica, slot, Token));
            }
        }
    }

    /// <summary>
    /// Requests a catalog checkpoint and waits until maintenance has written it, or found one current, and settled.
    /// </summary>
    private static Task CheckpointCatalogAsync(LedgerStore store)
    {
        store.RequestCatalogCheckpoint();
        return EventuallyAsync(() => store.CatalogCheckpointCurrent && store.MaintenanceIdle, "maintenance writes the requested catalog checkpoint");
    }

    private static async Task WriteAsync(LedgerStore store, LedgerId ledger, long first, int count = 1)
    {
        var replica = store.OpenReplica(ledger, Window);
        for (var slot = first; slot < first + count; slot++)
        {
            Assert.Equal(ReplicaWriteCase.Written, (await LedgerStoreReplicaTests.WriteAsync(replica, slot, Payload(ledger, slot))).Case);
        }
    }

    private static byte[] Payload(LedgerId ledger, long slot) => LedgerReplicaTestData.CreatePayload(400, (int)ledger.Value * 1_000 + (int)slot);

    private static List<string> CatalogNames(SimulatedNodeRoot root) =>
        [.. root.Storage.ListNamedFiles(WalPath).Where(static name => CatalogCheckpoints.TryParseFileName(name, out _)).Order(StringComparer.Ordinal)];

    private static bool IsValid(SimulatedNodeRoot root, string name)
    {
        _ = CatalogCheckpoints.TryParseFileName(name, out var cut);
        try
        {
            _ = ReplicaCatalog.Decode(root.Storage.ReadDurableFile(WalPath, name), cut).Checkpoint();
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    /// <summary>
    /// Damages a catalog checkpoint's CRC, so it fails validation.
    /// </summary>
    private static void Damage(SimulatedNodeRoot root, string name)
    {
        var bytes = root.Storage.ReadDurableFile(WalPath, name);
        root.Storage.DamageFile(WalPath, name, ReplicaCatalog.BodyCrcOffset, [.. bytes.AsSpan(ReplicaCatalog.BodyCrcOffset, 4).ToArray().Select(static value => (byte)(value ^ 0xFF))]);
    }

    private static string DamageNewestValid(SimulatedNodeRoot root)
    {
        var newest = CatalogNames(root).Last(name => IsValid(root, name));
        Damage(root, newest);
        return newest;
    }

    private static async Task WriteCatalogAsync(SimulatedNodeRoot root, ReplicaCatalog catalog, WalPosition cut)
    {
        await using var directory = await root.Storage.OpenDirectoryAsync(WalPath, Token);
        _ = await new CatalogCheckpoints(directory, [], firstRetainedFile: 1).WriteAsync(catalog, cut, Token);
    }

    private static async Task DeleteCatalogAsync(SimulatedNodeRoot root, string name)
    {
        await using var directory = await root.Storage.OpenDirectoryAsync(WalPath, Token);
        await directory.DeleteNamedFileAsync(name, Token);
    }

    private static async Task DeleteCatalogsAsync(SimulatedNodeRoot root)
    {
        foreach (var name in CatalogNames(root))
        {
            await DeleteCatalogAsync(root, name);
        }
    }

    /// <summary>
    /// Returns the position of the create record which lists <paramref name="ledger"/> in the store's durable WAL, once the
    /// WAL has retired every record it accepted, which it does after answering them.
    /// </summary>
    private static async Task<WalPosition> FindCreateRecordAsync(LedgerStore store, LedgerId ledger)
    {
        await store.WaitForFrontierAsync();
        await foreach (var entry in store.Wal.ReadAllAsync(default, Token))
        {
            if (entry.TryGetValue(out WalRecord record)
                && StoreRecordTestHelpers.Decode(record.Payload, out _) is { } parsed
                && parsed is CreateRecord create
                && create.Pairs.Any(pair => pair.Ledger == ledger))
            {
                return record.Position;
            }
        }

        throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"The WAL holds no create record of ledger {ledger.Value}."));
    }

    /// <summary>
    /// Appends one record to the WAL of a closed store, in a new open above every catalog checkpoint's cut, as a damaged
    /// record whose page CRC holds.
    /// </summary>
    private static async Task AppendAsync(SimulatedNodeRoot root, byte[] record)
    {
        var minimum = CatalogNames(root).Max(static name => CatalogCheckpoints.TryParseFileName(name, out var cut) ? cut.File + 1 : 0);
        await using var directory = await root.Storage.OpenDirectoryAsync(WalPath, Token);
        await using var wal = await WalLog.OpenAsync(directory, SmallFiles.ToWalOptions(new FakeTimeProvider()), minimum, Token);
        using var writer = new ArcBufferWriter();
        writer.Write(record);
        using var payload = writer.ConsumeSlice(record.Length);
        _ = await wal.AppendAsync(payload, Token);
    }

    private static void AssertSameCatalog(ReplicaCatalog expected, ReplicaCatalog actual)
    {
        Assert.Equal(expected.NextHandle, actual.NextHandle);
        Assert.Equal(expected.Replicas, actual.Replicas);
        Assert.Equal(expected.Tombstones, actual.Tombstones);
    }

    private static async Task EventuallyAsync(Func<bool> condition, string what)
    {
        var started = TimeProvider.System.GetTimestamp();
        while (!condition())
        {
            Assert.True(TimeProvider.System.GetElapsedTime(started) < TimeSpan.FromSeconds(20), $"Timed out waiting until {what}.");
            await Task.Delay(TimeSpan.FromMilliseconds(1), TimeProvider.System, Token);
        }
    }
}
