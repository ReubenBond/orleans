using Orleans.Durable.Storage.Engine.Wal.FileSystem;
using Orleans.Durable.Storage.FileSystem.MetaStore;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.MetaStore.Tests;

public sealed class FileSystemConditionalRegisterTests : IDisposable
{
    private const string Name = "l/orders";
    private static readonly Guid Cluster = new("0123456789abcdef0123456789abcdef");
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("clog-fs-metastore-");
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    [Fact]
    public async Task EmptyStoreReturnsNull() =>
        await ConditionalRegisterContract.EmptyStoreReturnsNull(await CreateRegisterAsync());

    [Fact]
    public async Task NullVersionCreatesOnlyWhenAbsent() =>
        await ConditionalRegisterContract.NullVersionCreatesOnlyWhenAbsent(await CreateRegisterAsync());

    [Fact]
    public async Task EmptyValueReadsAsPresent() =>
        await ConditionalRegisterContract.EmptyValueReadsAsPresent(await CreateRegisterAsync());

    [Fact]
    public async Task NonNullVersionDoesNotCreateMissingRegister() =>
        await ConditionalRegisterContract.NonNullVersionDoesNotCreateMissingRegister(await CreateRegisterAsync());

    [Fact]
    public async Task MatchingVersionUpdatesAndChangesVersion() =>
        await ConditionalRegisterContract.MatchingVersionUpdatesAndChangesVersion(await CreateRegisterAsync());

    [Fact]
    public async Task AnAppliedWriteReturnsTheRegistersNewVersion() =>
        await ConditionalRegisterContract.AnAppliedWriteReturnsTheRegistersNewVersion(await CreateRegisterAsync());

    [Fact]
    public async Task StaleVersionDoesNotOverwriteValue() =>
        await ConditionalRegisterContract.StaleVersionDoesNotOverwriteValue(await CreateRegisterAsync());

    [Fact]
    public async Task ValuesAreCopiedOnWriteAndEntriesKeepTheirValues() =>
        await ConditionalRegisterContract.ValuesAreCopiedOnWriteAndEntriesKeepTheirValues(await CreateRegisterAsync());

    [Fact]
    public async Task ConcurrentCreatesHaveOneWinner() =>
        await ConditionalRegisterContract.ConcurrentCreatesHaveOneWinner(await CreateRegisterAsync());

    [Fact]
    public async Task ConcurrentWritesWithOneVersionHaveOneWinner() =>
        await ConditionalRegisterContract.ConcurrentWritesWithOneVersionHaveOneWinner(await CreateRegisterAsync());

    [Fact]
    public async Task CancellationIsObserved() =>
        await ConditionalRegisterContract.CancellationIsObserved(await CreateRegisterAsync());

    /// <summary>
    /// Another writer holds the register's lock, so the write waits for it, and its caller cancels the wait. The write
    /// ends before it reads its value: once the lock is released and the writer's wait for its next retry has ended, the
    /// test takes the lock back, and the register holds exactly its previous value.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task AWriteCancelledMidCallLandsItsOwnCopy(int pages)
    {
        var timeProvider = new LockWaitTimeProvider();
        var register = await new FileSystemMetaStore(_root.FullName, timeProvider).GetRegisterAsync(Cluster, Name, TestContext.Current.CancellationToken);
        var lockPath = Path.Combine(_root.FullName, Cluster.ToString("N"), FileSystemMetaStore.Stem(Name) + "~lock");
        IDisposable? retaken = null;
        try
        {
            await ConditionalRegisterContract.AWriteCancelledMidCallLandsItsOwnCopy(
                register,
                hold =>
                {
                    var held = WalFileSystem.Os.OpenLock(lockPath);
                    Task? retry = null;
                    retry = timeProvider.WatchNextTimer(() => _ = hold.LandAsync(async () =>
                    {
                        held.Dispose();
                        await retry!;
                        retaken = WalFileSystem.Os.OpenLock(lockPath);
                    }));
                },
                pages,
                lands: false);
        }
        finally
        {
            retaken?.Dispose();
        }
    }

    [Fact]
    public async Task StoresOverOneRootShareRegisters()
    {
        var first = await CreateRegisterAsync();
        var second = await CreateRegisterAsync();

        Assert.True((await first.WriteAsync(null, [1], TestContext.Current.CancellationToken)).IsApplied());
        using var entry = Assert.IsType<MetaStoreEntry>(await second.ReadAsync(TestContext.Current.CancellationToken));
        Assert.True((await second.WriteAsync(entry.Version, [2], TestContext.Current.CancellationToken)).IsApplied());

        Assert.False((await first.WriteAsync(entry.Version, [3], TestContext.Current.CancellationToken)).IsApplied());
        using var current = Assert.IsType<MetaStoreEntry>(await first.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(new byte[] { 2 }, current.Value.ToArray());
    }

    [Fact]
    public async Task ConcurrentWritersAcrossStoresApplyEveryIncrementOnce()
    {
        const int Writers = 4;
        const int Increments = 25;
        var registers = new IConditionalRegister[Writers];
        for (var index = 0; index < Writers; index++)
        {
            registers[index] = await CreateRegisterAsync();
        }

        Assert.True((await registers[0].WriteAsync(null, BitConverter.GetBytes(0), TestContext.Current.CancellationToken)).IsApplied());
        await Task.WhenAll(registers.Select(register => Task.Run(
            async () =>
            {
                for (var applied = 0; applied < Increments;)
                {
                    using var entry = Assert.IsType<MetaStoreEntry>(await register.ReadAsync(TestContext.Current.CancellationToken));
                    var next = BitConverter.ToInt32(entry.Value.ToArray()) + 1;
                    if ((await register.WriteAsync(entry.Version, BitConverter.GetBytes(next), TestContext.Current.CancellationToken)).IsApplied())
                    {
                        applied++;
                    }
                }
            },
            TestContext.Current.CancellationToken)));

        using var final = Assert.IsType<MetaStoreEntry>(await registers[0].ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(Writers * Increments, BitConverter.ToInt32(final.Value.ToArray()));

        // Every write released the register's lock file and closed the register (test/Durable.Storage/docs/spec/verification.md §11.1).
        FileHandleLeakCheck.AssertClosed(_root.FullName);
    }

    /// <summary>
    /// The register classifies every register file rejection as a malformed entry: a read and a write each fail with the
    /// rejection itself, carrying the vector's reason, since the MetaStore has no corruption exception (metastore.md §7,
    /// conventions.md §15).
    /// </summary>
    [Fact]
    public async Task EveryMalformedRegisterFileFailsTheReadAndTheWriteWithItsRejection()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var register = await CreateRegisterAsync();
        Assert.True((await register.WriteAsync(null, [1], cancellationToken)).IsApplied());
        var file = Assert.Single(Directory.GetFiles(_root.FullName, "*~clfm", SearchOption.AllDirectories));
        foreach (var vector in SharedVectors.Cases("metastore", "register-file-rejections.json"))
        {
            var reason = SharedVectors.Text(vector, "reason");
            await File.WriteAllBytesAsync(file, SharedVectors.Hex(vector), cancellationToken);
            var read = await Assert.ThrowsAsync<InvalidDataException>(async () => await register.ReadAsync(cancellationToken));
            Assert.Equal(reason, DecodeRejection.ReasonOf(read));

            // A write which fails under the lock releases it.
            var write = await Assert.ThrowsAsync<InvalidDataException>(async () => await register.WriteAsync(null, [2], cancellationToken));
            Assert.Equal(reason, DecodeRejection.ReasonOf(write));
        }

        FileHandleLeakCheck.AssertClosed(_root.FullName);
    }

    /// <summary>
    /// The root is an operator's precondition: a register below a root which does not exist fails to read and to
    /// write, naming the root, whatever version the write names, and creates nothing.
    /// </summary>
    [Fact]
    public async Task AMissingRootIsRefusedAndNotCreated()
    {
        var root = Path.Combine(_root.FullName, "missing");
        var register = await new FileSystemMetaStore(root, TimeProvider.System).GetRegisterAsync(Cluster, "l/orders", TestContext.Current.CancellationToken);

        var read = await Assert.ThrowsAsync<DirectoryNotFoundException>(async () => await register.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Contains(root, read.Message, StringComparison.Ordinal);
        var write = await Assert.ThrowsAsync<DirectoryNotFoundException>(async () => await register.WriteAsync(null, [1], TestContext.Current.CancellationToken));
        Assert.Contains(root, write.Message, StringComparison.Ordinal);
        _ = await Assert.ThrowsAsync<DirectoryNotFoundException>(async () => await register.WriteAsync(new MetaStoreVersion("not-a-version"), [1], TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(root));
    }

    /// <summary>
    /// MS-I4: names whose components would be path syntax, or which differ only where the file system folds names,
    /// address distinct registers whose files lie in their cluster's directory; a register named like another's lock file
    /// is a register of its own.
    /// </summary>
    [Fact]
    public async Task RegistersWhoseNamesLookLikePathsAreDistinctAndStayInTheirClusterDirectory()
    {
        string[] names = ["a", "a/b", "x", "x.lock", "x.", ".", "..", "../x", "a/../x", "a/./b"];
        var store = new FileSystemMetaStore(_root.FullName, TimeProvider.System);
        for (var index = 0; index < names.Length; index++)
        {
            var register = await store.GetRegisterAsync(Cluster, names[index], TestContext.Current.CancellationToken);
            Assert.True((await register.WriteAsync(null, [(byte)index], TestContext.Current.CancellationToken)).IsApplied(), names[index]);
        }

        for (var index = 0; index < names.Length; index++)
        {
            var register = await store.GetRegisterAsync(Cluster, names[index], TestContext.Current.CancellationToken);
            using var entry = Assert.IsType<MetaStoreEntry>(await register.ReadAsync(TestContext.Current.CancellationToken));
            Assert.Equal(new[] { (byte)index }, entry.Value.ToArray());
        }

        var directory = Path.Combine(_root.FullName, Cluster.ToString("N"));
        Assert.Equal(directory, Assert.Single(Directory.GetDirectories(_root.FullName)));
        Assert.Empty(Directory.GetDirectories(directory));
        Assert.Equal(names.Length, Directory.GetFiles(directory, "*~clfm").Length);
    }

    /// <summary>
    /// A name whose register's temporary file name would exceed the 255 characters a file name holds is refused when the
    /// register is obtained; the longest which fits is not.
    /// </summary>
    [Fact]
    public async Task ANameTooLongForAFileNameIsRefused()
    {
        var store = new FileSystemMetaStore(_root.FullName, TimeProvider.System);
        var longest = new string('a', 128) + "/" + new string('b', FileSystemMetaStore.MaximumStemLength - 129);
        _ = await store.GetRegisterAsync(Cluster, longest, TestContext.Current.CancellationToken);
        _ = await Assert.ThrowsAsync<ArgumentException>(async () => await store.GetRegisterAsync(Cluster, longest + "b", TestContext.Current.CancellationToken));
    }

    public void Dispose()
    {
        try
        {
            _leaks.Dispose();
        }
        finally
        {
            _root.Delete(recursive: true);
        }
    }

    private ValueTask<IConditionalRegister> CreateRegisterAsync() =>
        new FileSystemMetaStore(_root.FullName, TimeProvider.System).GetRegisterAsync(Cluster, Name, TestContext.Current.CancellationToken);

    /// <summary>
    /// The system's time, which watches the next timer created: a write waiting for its register's lock creates one for
    /// its next retry, which fires or, once the write's wait is cancelled, is disposed.
    /// </summary>
    private sealed class LockWaitTimeProvider : TimeProvider
    {
        private (Action Created, TaskCompletionSource Ended)? _next;

        /// <summary>
        /// Runs <paramref name="created"/> when the next timer is created, and returns a task which completes once that
        /// timer has fired or been disposed.
        /// </summary>
        public Task WatchNextTimer(Action created)
        {
            var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _next = (created, ended);
            return ended.Task;
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (_next is not { } next)
            {
                return System.CreateTimer(callback, state, dueTime, period);
            }

            _next = null;
            next.Created();
            return new WatchedTimer(
                System.CreateTimer(
                    fired =>
                    {
                        callback(fired);
                        _ = next.Ended.TrySetResult();
                    },
                    state,
                    dueTime,
                    period),
                next.Ended);
        }

        private sealed class WatchedTimer(ITimer timer, TaskCompletionSource ended) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => timer.Change(dueTime, period);

            public void Dispose()
            {
                timer.Dispose();
                _ = ended.TrySetResult();
            }

            public async ValueTask DisposeAsync()
            {
                await timer.DisposeAsync();
                _ = ended.TrySetResult();
            }
        }
    }
}
