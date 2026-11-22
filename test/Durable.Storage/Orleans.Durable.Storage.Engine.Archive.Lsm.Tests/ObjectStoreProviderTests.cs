using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Orleans.Durable.Storage.Diagnostics;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Durable.Storage.Engine.ObjectStore.Memory;
using Orleans.Durable.Storage.Engine.Wal.FileSystem;
using Orleans.Durable.Storage.FileSystem.Archive;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Archive.Lsm.Tests;

/// <summary>
/// Runs <see cref="ObjectStoreContract"/>'s examples against every provider in this repository: the memory store, and
/// the file-system store on the operating system's file system and on the recorded one through both of its publish
/// paths (archive §9).
/// </summary>
public sealed class ObjectStoreProviderTests : IDisposable
{
    private static readonly string RecordedRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "clog-recorded-objects"));
    private static readonly ObjectName Recreated = new("c/1/recreated");
    private static readonly FileSystemObjectStoreOptions Reclamation = new() { TemporaryFileAge = TimeSpan.FromMinutes(30) };
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public static TheoryData<string> Examples => ObjectStoreContract.Examples;

    public void Dispose() => _leaks.Dispose();

    [Theory]
    [MemberData(nameof(Examples))]
    public Task TheMemoryStoreKeepsTheContract(string example) => ObjectStoreContract.RunAsync(example, new InMemoryObjectStore());

    /// <summary>
    /// orleans.md §5.13: a store placed under a cluster's prefix keeps the contract, and every name it gives the store it
    /// lies in, written, read, listed, or deleted, lies under the prefix.
    /// </summary>
    [Theory]
    [MemberData(nameof(Examples))]
    public async Task APrefixedStoreKeepsTheContractUnderItsPrefix(string example)
    {
        var inner = new NameRecordingObjectStore(new InMemoryObjectStore());
        await ObjectStoreContract.RunAsync(example, new PrefixedObjectStore(inner, "p/"));
        Assert.NotEmpty(inner.Names);
        Assert.All(inner.Names, static name => Assert.StartsWith("p/", name, StringComparison.Ordinal));
    }

    /// <summary>
    /// Each example leaves the operating system's directory with no temporary file and no file open.
    /// </summary>
    [Theory]
    [MemberData(nameof(Examples))]
    public async Task TheFileSystemStoreKeepsTheContract(string example)
    {
        using var directory = new TemporaryDirectory();
        var store = new FileSystemObjectStore(directory.Path, TimeProvider.System);
        await ObjectStoreContract.RunAsync(example, store);
        AssertClean(directory.Path);
    }

    /// <summary>
    /// An existing create, a replace at a stale version, a replace expecting no object over an existing one, and a replace
    /// whose wait for the store's lock is cancelled leave no temporary file and no file open.
    /// </summary>
    [Fact]
    public async Task RefusedAndCancelledWritesLeaveNoTemporaryFileOrHandle()
    {
        using var directory = new TemporaryDirectory();
        var store = new FileSystemObjectStore(directory.Path, TimeProvider.System);
        var name = new ObjectName("d/1/0/a");
        _ = await ObjectStoreContract.CreateAsync(store, name, ObjectStoreContract.Bytes(10, 1));
        Assert.Equal<ObjectCreateOutcome>(ObjectCreateOutcome.Exists.Instance, await ObjectStoreContract.CreateAsync(store, name, ObjectStoreContract.Bytes(10, 2)));
        var applied = Assert.IsType<ObjectReplaceOutcome.Applied>((await ObjectStoreContract.ReplaceAsync(store, ObjectName.Manifest, null, ObjectStoreContract.Bytes(10, 3))).Value);
        _ = await ObjectStoreContract.ReplaceAsync(store, ObjectName.Manifest, applied.Version, ObjectStoreContract.Bytes(10, 4));
        Assert.Equal<ObjectReplaceOutcome>(ObjectReplaceOutcome.Mismatch.Instance, await ObjectStoreContract.ReplaceAsync(store, ObjectName.Manifest, applied.Version, ObjectStoreContract.Bytes(10, 5)));
        Assert.Equal<ObjectReplaceOutcome>(ObjectReplaceOutcome.Mismatch.Instance, await ObjectStoreContract.ReplaceAsync(store, ObjectName.Manifest, null, ObjectStoreContract.Bytes(10, 6)));

        using (File.Open(Path.Combine(directory.Path, "store.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            using var content = ArcBuffer.Wrap(ObjectStoreContract.Bytes(10, 7));
            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await store.ReplaceAsync(ObjectName.Manifest, null, content, null, cancellation.Token));
        }

        AssertClean(directory.Path);
    }

    /// <summary>
    /// On Windows a file whose deletion is pending keeps its name, and an open of it answers access denied: a listing
    /// names its object with its length, and leaves it out once the deletion completes, as a name a concurrent delete
    /// changes (archive §3).
    /// </summary>
    [Fact]
    public async Task AListingNamesAnObjectWhoseDeletionIsPending()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows keeps a file's name while its deletion is pending.");
        var token = TestContext.Current.CancellationToken;
        using var directory = new TemporaryDirectory();
        var store = new FileSystemObjectStore(directory.Path, TimeProvider.System);
        var name = new ObjectName("d/1/0/a");
        _ = await ObjectStoreContract.CreateAsync(store, name, ObjectStoreContract.Bytes(10, 1));
        var path = Path.Combine(directory.Path, "+d+1+0+a");

        // The delete-on-close handle's close leaves the deletion pending while the first handle keeps the file open.
        using (File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.DeleteOnClose).Dispose();
            _ = Assert.Throws<UnauthorizedAccessException>(() => File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete).Dispose());

            Assert.Equal([new ListedObject(name, 10)], await store.ListAsync(string.Empty, cancellationToken: token).ToListAsync(token));
        }

        Assert.Empty(await store.ListAsync(string.Empty, cancellationToken: token).ToListAsync(token));
    }

    /// <summary>
    /// On Windows a read and a versioned read which meet a file whose deletion is pending wait it out as contention, and
    /// answer the object's absence once the deletion completes, as reads concurrent with a delete may.
    /// </summary>
    [Fact]
    public async Task AReadOfAnObjectWhoseDeletionIsPendingAnswersItsAbsence()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows keeps a file's name while its deletion is pending.");
        var token = TestContext.Current.CancellationToken;
        using var directory = new TemporaryDirectory();
        var store = new FileSystemObjectStore(directory.Path, TimeProvider.System);
        var name = new ObjectName("d/1/0/a");
        _ = await ObjectStoreContract.CreateAsync(store, name, ObjectStoreContract.Bytes(10, 1));
        var path = Path.Combine(directory.Path, "+d+1+0+a");
        Task<ArcBuffer?> read;
        Task<VersionedObject?> versioned;

        // The delete-on-close handle's close leaves the deletion pending while the first handle keeps the file open.
        using (File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.DeleteOnClose).Dispose();
            read = Task.Run(() => store.ReadAsync(name, 0, 10, token).AsTask(), token);
            versioned = Task.Run(() => store.ReadVersionedAsync(name, token).AsTask(), token);
            await Task.Delay(TimeSpan.FromMilliseconds(100), TimeProvider.System, token);
            Assert.False(read.IsCompleted);
            Assert.False(versioned.IsCompleted);
        }

        Assert.Null(await read);
        Assert.Null(await versioned);
    }

    /// <summary>
    /// Operations which observe while a directory flush runs share the next flush, which starts after their observations:
    /// four reads of the manifest, three of them made while the first one's flush runs, flush twice, and every read
    /// answers.
    /// </summary>
    [Fact]
    public async Task OperationsWhichObserveDuringAFlushShareTheNext()
    {
        var view = new ScriptedFileSystem(new RecordingFileSystem(RecordedRoot));
        var store = new FileSystemObjectStore(RecordedRoot, TimeProvider.System, view);
        var name = ObjectName.Manifest;
        _ = await ObjectStoreContract.ReplaceAsync(store, name, null, ObjectStoreContract.Bytes(10, 1));
        List<Task<byte[]>> during = [];
        view.BeforeFlush = () =>
        {
            if (during.Count == 0)
            {
                during.AddRange(Enumerable.Range(0, 3).Select(_ => ObjectStoreContract.ReadAllAsync(store, name)));
                Assert.All(during, static read => Assert.False(read.IsCompleted));
            }
        };
        var flushes = view.Flushes;

        var first = await ObjectStoreContract.ReadAllAsync(store, name);

        Assert.All(await Task.WhenAll(during), read => Assert.Equal(first, read));
        Assert.Equal(flushes + 2, view.Flushes);
    }

    /// <summary>
    /// A process reads an object other than the manifest which it has seen durable without flushing the directory again,
    /// since the object keeps its content until it is deleted (LSM-I7): its own create's flush covers its reads, another
    /// process flushes once, on its first read, and every read of the manifest, which a replace changes, flushes.
    /// </summary>
    [Fact]
    public async Task ReadsOfAnObjectThisProcessSawDurableFlushNoMore()
    {
        var recording = new RecordingFileSystem(RecordedRoot);
        var (view, other) = (new ScriptedFileSystem(recording), new ScriptedFileSystem(recording.StartProcess()));
        var store = new FileSystemObjectStore(RecordedRoot, TimeProvider.System, view);
        var otherStore = new FileSystemObjectStore(RecordedRoot, TimeProvider.System, other);
        var name = new ObjectName("d/1/0/a");
        _ = await ObjectStoreContract.CreateAsync(store, name, ObjectStoreContract.Bytes(10, 1));
        _ = await ObjectStoreContract.ReplaceAsync(store, ObjectName.Manifest, null, ObjectStoreContract.Bytes(10, 2));
        var flushes = view.Flushes;

        for (var read = 0; read < 3; read++)
        {
            using var bytes = Assert.IsType<ArcBuffer>(await store.ReadAsync(name, 0, 10, TestContext.Current.CancellationToken));
            using var otherBytes = Assert.IsType<ArcBuffer>(await otherStore.ReadAsync(name, 0, 10, TestContext.Current.CancellationToken));
            _ = await ObjectStoreContract.ReadAllAsync(store, ObjectName.Manifest);
        }

        Assert.Equal((flushes + 3, 1), (view.Flushes, other.Flushes));
    }

    /// <summary>
    /// A versioned read, which reads its object whole, shares the object's file with the handle a ranged read or a
    /// replace's version check holds while it reads, so on Windows the two overlap rather than refuse each other.
    /// </summary>
    [Fact]
    public async Task AVersionedReadOverlapsTheHandleOfARangedRead()
    {
        using var directory = new TemporaryDirectory();
        var store = new FileSystemObjectStore(directory.Path, TimeProvider.System);
        _ = await ObjectStoreContract.ReplaceAsync(store, ObjectName.Manifest, null, ObjectStoreContract.Bytes(10, 1));
        var file = Assert.Single(Directory.GetFiles(directory.Path), static path => !path.EndsWith("store.lock", StringComparison.Ordinal));

        using (WalFileSystem.Os.OpenFile(file, direct: false))
        {
            Assert.Equal(ObjectStoreContract.Bytes(10, 1), await ObjectStoreContract.ReadAllAsync(store, ObjectName.Manifest));
        }
    }

    /// <summary>
    /// The file-system store's version is a digest of the content, so an object which holds earlier content again holds
    /// its earlier version again, and a replace naming that version applies: the reason a replace never writes content
    /// the object held before (archive §3).
    /// </summary>
    [Fact]
    public async Task TheFileSystemStoresVersionNamesContent()
    {
        using var directory = new TemporaryDirectory();
        var store = new FileSystemObjectStore(directory.Path, TimeProvider.System);
        var (first, again) = await RewriteAsync(store);

        Assert.Equal(first.Version, again.Version);
        _ = Assert.IsType<ObjectReplaceOutcome.Applied>((await ObjectStoreContract.ReplaceAsync(store, ObjectName.Manifest, first.Version, ObjectStoreContract.Bytes(20, 3))).Value);
        var recreated = await RecreateAsync(store);
        Assert.Equal(recreated.Before, recreated.After);
        _ = Assert.IsType<ObjectReplaceOutcome.Applied>((await ObjectStoreContract.ReplaceAsync(store, Recreated, recreated.Before, ObjectStoreContract.Bytes(12, 10))).Value);
        AssertClean(directory.Path);
    }

    /// <summary>
    /// The memory store gives every applied write a version the object never had, so a stale version stays stale.
    /// </summary>
    [Fact]
    public async Task TheMemoryStoresVersionNamesAWrite()
    {
        var store = new InMemoryObjectStore();
        var (first, again) = await RewriteAsync(store);

        Assert.NotEqual(first.Version, again.Version);
        Assert.Equal<ObjectReplaceOutcome>(
            ObjectReplaceOutcome.Mismatch.Instance, await ObjectStoreContract.ReplaceAsync(store, ObjectName.Manifest, first.Version, ObjectStoreContract.Bytes(20, 3)));
        var recreated = await RecreateAsync(store);
        Assert.NotEqual(recreated.Before, recreated.After);
        Assert.Equal<ObjectReplaceOutcome>(
            ObjectReplaceOutcome.Mismatch.Instance, await ObjectStoreContract.ReplaceAsync(store, Recreated, recreated.Before, ObjectStoreContract.Bytes(12, 10)));
    }

    /// <summary>
    /// Replaces the manifest with content A, then B, then A again, and returns the first and the last write.
    /// </summary>
    private static async Task<(ObjectReplaceOutcome.Applied First, ObjectReplaceOutcome.Applied Again)> RewriteAsync(IObjectStore store)
    {
        var first = Assert.IsType<ObjectReplaceOutcome.Applied>((await ObjectStoreContract.ReplaceAsync(store, ObjectName.Manifest, null, ObjectStoreContract.Bytes(20, 1))).Value);
        var second = Assert.IsType<ObjectReplaceOutcome.Applied>((await ObjectStoreContract.ReplaceAsync(store, ObjectName.Manifest, first.Version, ObjectStoreContract.Bytes(20, 2))).Value);
        var again = Assert.IsType<ObjectReplaceOutcome.Applied>((await ObjectStoreContract.ReplaceAsync(store, ObjectName.Manifest, second.Version, ObjectStoreContract.Bytes(20, 1))).Value);
        return (first, again);
    }

    /// <summary>
    /// Creates an object, deletes it, and creates it again with the same content, returning its versions before and after.
    /// </summary>
    private static async Task<(ObjectVersion Before, ObjectVersion After)> RecreateAsync(IObjectStore store)
    {
        _ = await ObjectStoreContract.CreateAsync(store, Recreated, ObjectStoreContract.Bytes(12, 9));
        var before = (await ObjectStoreContract.ReadVersionedAsync(store, Recreated)).Version;
        await store.DeleteAsync(Recreated, TestContext.Current.CancellationToken);
        _ = await ObjectStoreContract.CreateAsync(store, Recreated, ObjectStoreContract.Bytes(12, 9));
        return (before, (await ObjectStoreContract.ReadVersionedAsync(store, Recreated)).Version);
    }

    /// <summary>
    /// Requires the store's directory to hold no temporary file, and no file which another handle holds open.
    /// </summary>
    private static void AssertClean(string directory)
    {
        Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
        FileHandleLeakCheck.AssertClosed(directory);
    }

    /// <summary>
    /// Writes a temporary file of the object whose file is <paramref name="file"/>, as a write which a crash stopped
    /// leaves it, last written at <paramref name="lastWrite"/>, and returns its path.
    /// </summary>
    private static string Temporary(string directory, string file, DateTimeOffset lastWrite) =>
        Write(Path.Combine(directory, file + "." + Guid.NewGuid().ToString("N") + ".tmp"), lastWrite);

    private static string Write(string path, DateTimeOffset lastWrite)
    {
        File.WriteAllBytes(path, [1, 2, 3]);
        File.SetLastWriteTimeUtc(path, lastWrite.UtcDateTime);
        return path;
    }

    /// <summary>
    /// The store reclaims its own temporary files (archive §9): as it opens, it deletes those whose last write is older
    /// than the configured age, and on each later call those which have aged past it since; it keeps a file exactly at
    /// the age, younger ones, objects, and files which are not its temporary files, however old.
    /// </summary>
    [Fact]
    public void TheStoreReclaimsTemporaryFilesPastTheirAgeAsItOpensAndWhenCalled()
    {
        using var directory = new TemporaryDirectory();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var old = Temporary(directory.Path, "+d+1+0+a", clock.GetUtcNow() - Reclamation.TemporaryFileAge - TimeSpan.FromSeconds(1));
        var manifest = Temporary(directory.Path, "+manifest", clock.GetUtcNow() - TimeSpan.FromHours(2));
        var young = Temporary(directory.Path, "+d+1+0+a", clock.GetUtcNow() - TimeSpan.FromMinutes(1));
        var age = Reclamation.TemporaryFileAge;
        var keep = new[]
            {
                "+d+1+0+a", "+d+1+0+a.notaguid.tmp", "+d+1+0+a." + new string('z', 32) + ".tmp", "store.lock", "notes.tmp",
                "notes." + Guid.NewGuid().ToString("N") + ".tmp", "+D+1." + Guid.NewGuid().ToString("N") + ".tmp",
            }
            .Select(name => Write(Path.Combine(directory.Path, name), clock.GetUtcNow() - TimeSpan.FromDays(1)))
            .ToArray();

        var store = new FileSystemObjectStore(directory.Path, clock, Reclamation, NullLogger<FileSystemObjectStore>.Instance);

        Assert.False(File.Exists(old));
        Assert.False(File.Exists(manifest));
        Assert.True(File.Exists(young));
        // The young file reaches the age exactly, which keeps it, and passes it a second later.
        clock.Advance(age - TimeSpan.FromMinutes(1));
        Assert.Equal(new TemporaryFileReclamation(0, 0), store.ReclaimTemporaryFiles());
        Assert.True(File.Exists(young));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(new TemporaryFileReclamation(1, 0), store.ReclaimTemporaryFiles());
        Assert.False(File.Exists(young));
        Assert.All(keep, static path => Assert.True(File.Exists(path)));
        FileHandleLeakCheck.AssertClosed(directory.Path);
    }

    /// <summary>
    /// A temporary file's age is at least <see cref="FileSystemObjectStoreOptions.MinTemporaryFileAge"/>, far above any
    /// write's duration.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(9)]
    public void AnAgeBelowTheFloorIsRefused(int minutes)
    {
        using var directory = new TemporaryDirectory();
        var options = new FileSystemObjectStoreOptions { TemporaryFileAge = TimeSpan.FromMinutes(minutes) };

        var refused = Assert.Throws<ArgumentOutOfRangeException>(
            () => new FileSystemObjectStore(directory.Path, TimeProvider.System, options, NullLogger<FileSystemObjectStore>.Instance));
        Assert.Equal(nameof(FileSystemObjectStoreOptions.TemporaryFileAge), refused.ParamName);
    }

    /// <summary>
    /// On Windows another handle, as a writer's, refuses a reclamation's deletion of its temporary file, which leaves
    /// the file for a later pass and counts it rather than failing; the pass after the handle closes deletes it.
    /// </summary>
    [Fact]
    public void AReclamationLeavesATemporaryFileAnotherHandleHoldsOpenForALaterPass()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows refuses to delete a file another handle holds open.");
        using var directory = new TemporaryDirectory();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var store = new FileSystemObjectStore(directory.Path, clock, Reclamation, NullLogger<FileSystemObjectStore>.Instance);
        var held = Temporary(directory.Path, "+d+1+0+a", clock.GetUtcNow() - TimeSpan.FromHours(2));

        using (File.Open(held, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            Assert.Equal(new TemporaryFileReclamation(0, 1), store.ReclaimTemporaryFiles());
            Assert.True(File.Exists(held));
        }

        Assert.Equal(new TemporaryFileReclamation(1, 0), store.ReclaimTemporaryFiles());
        Assert.False(File.Exists(held));
    }

    /// <summary>
    /// A write whose temporary file a reclamation deletes before its publication, through a create, a replace expecting
    /// no object, or a replace over an existing object, throws <see cref="FileNotFoundException"/> with its outcome
    /// unknown, publishes nothing, and stops no process: the age is a liveness bound only (archive §9).
    /// </summary>
    [Theory]
    [InlineData("create")]
    [InlineData("replace-none")]
    [InlineData("replace-existing")]
    public async Task AWriteWhoseTemporaryFileAReclamationDeletesPublishesNothing(string write)
    {
        using var directory = new TemporaryDirectory();
        var clock = new FakeTimeProvider(TimeProvider.System.GetUtcNow());
        var view = new PublishingFileSystem(WalFileSystem.Os);
        var store = new FileSystemObjectStore(directory.Path, clock, view, Reclamation, NullLogger<FileSystemObjectStore>.Instance);
        var name = write == "create" ? new ObjectName("d/1/0/a") : ObjectName.Manifest;
        ObjectVersion? expected = null;
        if (write == "replace-existing")
        {
            expected = Assert.IsType<ObjectReplaceOutcome.Applied>((await ObjectStoreContract.ReplaceAsync(store, name, null, ObjectStoreContract.Bytes(10, 1))).Value).Version;
        }

        TemporaryFileReclamation? reclaimed = null;
        view.BeforePublish = temporary =>
        {
            File.SetLastWriteTimeUtc(temporary, (clock.GetUtcNow() - TimeSpan.FromHours(2)).UtcDateTime);
            reclaimed = store.ReclaimTemporaryFiles();
        };
        var since = FatalErrors.ReportedCount;

        _ = await Assert.ThrowsAsync<FileNotFoundException>(async () =>
        {
            if (write == "create")
            {
                _ = await ObjectStoreContract.CreateAsync(store, name, ObjectStoreContract.Bytes(10, 2));
            }
            else
            {
                _ = await ObjectStoreContract.ReplaceAsync(store, name, expected, ObjectStoreContract.Bytes(10, 2));
            }
        });

        Assert.Equal(new TemporaryFileReclamation(1, 0), reclaimed);
        Assert.Empty(FatalErrors.TakeMine(since));
        view.BeforePublish = null;
        if (expected is { } version)
        {
            Assert.Equal(version, (await ObjectStoreContract.ReadVersionedAsync(store, name)).Version);
        }
        else
        {
            Assert.Null(await store.ReadVersionedAsync(name, TestContext.Current.CancellationToken));
        }

        AssertClean(directory.Path);
    }

    /// <summary>
    /// On Windows a reclamation holds its temporary file against every other handle from the check of its last write to
    /// its deletion, so a publication's rename which lands between them meets a sharing violation rather than giving the
    /// file the name the deletion then removes (archive §9).
    /// </summary>
    [Fact]
    public void AReclamationHoldsItsFileAgainstAConcurrentPublication()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows deletes a file by the handle a reclamation holds.");
        using var directory = new TemporaryDirectory();
        var clock = new FakeTimeProvider(TimeProvider.System.GetUtcNow());
        var published = Path.Combine(directory.Path, "+d+1+0+a");
        Exception? rename = null;
        var fileSystem = WalFileSystem.CreateOs(clock, temporary => rename = Record.Exception(() => File.Move(temporary, published)));
        var temporary = Temporary(directory.Path, "+d+1+0+a", clock.GetUtcNow() - TimeSpan.FromHours(2));

        _ = new FileSystemObjectStore(directory.Path, clock, fileSystem, Reclamation, NullLogger<FileSystemObjectStore>.Instance);

        _ = Assert.IsType<IOException>(rename, exactMatch: false);
        Assert.False(File.Exists(temporary));
        Assert.False(File.Exists(published));
    }

    /// <summary>
    /// A store whose opening reclamation fails reports the failure, which stops the process, and its constructor throws:
    /// a directory which has gone is such a failure (conventions.md §14).
    /// </summary>
    [Fact]
    public void AStoreWhoseOpeningReclamationFailsReportsIt()
    {
        using var directory = new TemporaryDirectory();
        var missing = Path.Combine(directory.Path, "missing");
        var since = FatalErrors.ReportedCount;

        _ = Assert.Throws<DirectoryNotFoundException>(() => new FileSystemObjectStore(missing, TimeProvider.System));

        var reported = Assert.Single(FatalErrors.TakeMine(since));
        Assert.Equal((FatalErrorSite.ListFiles, missing), (reported.Site, reported.Path));
    }

    [Theory]
    [MemberData(nameof(Examples))]
    public async Task TheFileSystemStoreKeepsTheContractWhenItPublishesByLinking(string example)
    {
        var store = new FileSystemObjectStore(RecordedRoot, TimeProvider.System, new RecordingFileSystem(RecordedRoot));
        await ObjectStoreContract.RunAsync(example, store);
    }

    [Theory]
    [MemberData(nameof(Examples))]
    public async Task TheFileSystemStoreKeepsTheContractWhenItPublishesByRenaming(string example)
    {
        var store = new FileSystemObjectStore(RecordedRoot, TimeProvider.System, new RecordingFileSystem(RecordedRoot) { LinkByRenaming = true });
        await ObjectStoreContract.RunAsync(example, store);
    }
}

/// <summary>An object store which records every name and listing prefix it is given, then passes the call on.</summary>
file sealed class NameRecordingObjectStore(IObjectStore inner) : IObjectStore
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _names = new();

    public IReadOnlyCollection<string> Names => _names;

    public ValueTask<ObjectCreateOutcome> CreateAsync(ObjectName name, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default) =>
        inner.CreateAsync(Record(name), content, crc32c, cancellationToken);

    public ValueTask<ArcBuffer?> ReadAsync(ObjectName name, long offset, int length, CancellationToken cancellationToken = default) =>
        inner.ReadAsync(Record(name), offset, length, cancellationToken);

    public ValueTask<ObjectProperties?> GetPropertiesAsync(ObjectName name, CancellationToken cancellationToken = default) =>
        inner.GetPropertiesAsync(Record(name), cancellationToken);

    public IAsyncEnumerable<ListedObject> ListAsync(string prefix, string? startAfter = null, CancellationToken cancellationToken = default)
    {
        _names.Enqueue(prefix);
        return inner.ListAsync(prefix, startAfter, cancellationToken);
    }

    public ValueTask DeleteAsync(ObjectName name, CancellationToken cancellationToken = default) => inner.DeleteAsync(Record(name), cancellationToken);

    public ValueTask<VersionedObject?> ReadVersionedAsync(ObjectName name, CancellationToken cancellationToken = default) =>
        inner.ReadVersionedAsync(Record(name), cancellationToken);

    public ValueTask<ObjectReplaceOutcome> ReplaceAsync(ObjectName name, ObjectVersion? expectedVersion, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default) =>
        inner.ReplaceAsync(Record(name), expectedVersion, content, crc32c, cancellationToken);

    private ObjectName Record(ObjectName name)
    {
        _names.Enqueue(name.Value);
        return name;
    }
}

/// <summary>
/// The operating system's file system, which runs <see cref="BeforePublish"/> with a write's temporary file just before
/// the store publishes it, by a link or a rename.
/// </summary>
file sealed class PublishingFileSystem(WalFileSystem inner) : WalFileSystem
{
    public Action<string>? BeforePublish { get; set; }

    internal override bool DirectoryExistsCore(string path) => inner.DirectoryExistsCore(path);

    internal override void CreateDirectoryCore(string path) => inner.CreateDirectoryCore(path);

    internal override void DeleteDirectoryCore(string path) => inner.DeleteDirectoryCore(path);

    internal override bool TryDeleteEmptyDirectoryCore(string path) => inner.TryDeleteEmptyDirectoryCore(path);

    internal override bool TryDeleteLockFileCore(string path) => inner.TryDeleteLockFileCore(path);

    internal override IEnumerable<string> EnumerateFilesCore(string path, string searchPattern) => inner.EnumerateFilesCore(path, searchPattern);

    internal override IEnumerable<WalListedFile> EnumerateFilesWithLengthsCore(string path, WalFileNameFilter includeName) =>
        inner.EnumerateFilesWithLengthsCore(path, includeName);

    internal override IEnumerable<WalListedFileWriteTime> EnumerateFilesWithLastWriteTimesCore(string path, WalFileNameFilter includeName) =>
        inner.EnumerateFilesWithLastWriteTimesCore(path, includeName);

    internal override bool TryDeleteFileWrittenBeforeCore(string path, DateTimeOffset cutoff) => inner.TryDeleteFileWrittenBeforeCore(path, cutoff);

    internal override IEnumerable<string> EnumerateDirectoriesCore(string path) => inner.EnumerateDirectoriesCore(path);

    internal override bool FileExistsCore(string path) => inner.FileExistsCore(path);

    internal override IDisposable OpenLockCore(string path) => inner.OpenLockCore(path);

    internal override IDisposable OpenLockForDeletionCore(string path) => inner.OpenLockForDeletionCore(path);

    internal override IWalFileSystemFile CreateFileCore(string path, bool replace, bool writeThrough, long preallocationSize) =>
        inner.CreateFileCore(path, replace, writeThrough, preallocationSize);

    internal override IWalFileSystemFile OpenFileCore(string path, bool direct) => inner.OpenFileCore(path, direct);

    internal override void MoveFileCore(string source, string destination, bool replace)
    {
        BeforePublish?.Invoke(source);
        inner.MoveFileCore(source, destination, replace);
    }

    internal override bool TryMoveFileCore(string source, string destination, bool replace, long contentionStarted)
    {
        BeforePublish?.Invoke(source);
        return inner.TryMoveFileCore(source, destination, replace, contentionStarted);
    }

    internal override void LinkFileCore(string existing, string link)
    {
        BeforePublish?.Invoke(existing);
        inner.LinkFileCore(existing, link);
    }

    internal override ValueTask<byte[]?> ReadFileCoreAsync(string path, CancellationToken cancellationToken) => inner.ReadFileCoreAsync(path, cancellationToken);

    internal override void DeleteFileCore(string path) => inner.DeleteFileCore(path);

    internal override bool TryDeleteFileCore(string path, long contentionStarted) => inner.TryDeleteFileCore(path, contentionStarted);

    internal override void FlushDirectoryCore(string path, DirectoryEntries entries) => inner.FlushDirectoryCore(path, entries);
}
