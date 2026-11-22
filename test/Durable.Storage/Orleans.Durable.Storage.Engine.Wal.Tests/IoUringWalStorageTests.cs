using System.Runtime.Versioning;
using Orleans.Durable.Storage.Diagnostics;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Wal.FileSystem;
using Orleans.Durable.Storage.Engine.Wal.IoUring;
using Orleans.Durable.Storage.Testing;
using static Orleans.Durable.Storage.Engine.Wal.Tests.WalTestHelpers;

namespace Orleans.Durable.Storage.Engine.Wal.Tests;

/// <summary>
/// Runs the WAL over Linux io_uring, and checks that each of its storage operations reports its failure to the
/// fatal-error handler with its own site (conventions.md §14); the tests skip on other platforms.
/// </summary>
public sealed class IoUringWalStorageTests : IDisposable
{
    // EIO, the error a device which failed a transfer returns.
    private const int InputOutputError = 5;

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _leaks.Dispose();

    [Theory]
    [InlineData(2048)]
    [InlineData(4096)]
    [InlineData(6144)]
    public async Task AShortTransferResumesAtTheRemainingVectorBytes(int transferred)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "io_uring requires Linux.");
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var path = TemporaryPath();
        try
        {
            using var file = File.Create(path);
            using var writer = ArcBufferWriter.CreateAligned();
            writer.Write(Bytes(8192, 1));
            using var payload = writer.PeekSlice(writer.Length);
            var operation = IoUringOperation.Write(file.SafeFileHandle, 4096, new[] { payload });
            Assert.Equal(IoUringCompletion.Resubmit, operation.Complete(transferred));
            Assert.Equal(IoUringCompletion.Resubmit, operation.Complete(-11)); // EAGAIN must not advance the vector cursor.
            AssertRemaining(operation, payload, transferred);

            Assert.Equal(IoUringCompletion.AwaitSync, operation.Complete(8192 - transferred));
            var sync = IoUringOperation.Sync(file.SafeFileHandle, [operation]);
            AssertDataSync(sync);
            Assert.Equal(IoUringCompletion.Done, sync.Complete(0));
            Assert.Equal(8192, await operation.Completion);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AWriteCompletesOnlyAfterItsDataSyncAndReportsItsFailure()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "io_uring requires Linux.");
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var path = TemporaryPath();
        try
        {
            using var file = File.Create(path);
            using var writer = ArcBufferWriter.CreateAligned();
            writer.Write(Bytes(4096, 1));
            using var payload = writer.PeekSlice(writer.Length);
            var operation = IoUringOperation.Write(file.SafeFileHandle, 0, new[] { payload });
            Assert.Equal(IoUringCompletion.AwaitSync, operation.Complete(4096));
            var completion = operation.Completion;
            Assert.False(completion.IsCompleted);
            var sync = IoUringOperation.Sync(file.SafeFileHandle, [operation]);
            AssertDataSync(sync);
            Assert.Equal(IoUringCompletion.Done, sync.Complete(-InputOutputError));
            _ = await Assert.ThrowsAsync<IOException>(() => completion);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AWriteOfMoreThanTheVectorLimitIsSubmittedInParts()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "io_uring requires Linux.");
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var path = TemporaryPath();
        try
        {
            using var file = File.Create(path);
            using var writer = ArcBufferWriter.CreateAligned();
            const int Pages = IoUringOperation.MaxVectors + 300;
            writer.Write(new byte[Pages * 4096]);
            using var payload = writer.PeekSlice(writer.Length);
            var operation = IoUringOperation.Write(file.SafeFileHandle, 0, new[] { payload });
            Assert.Equal((IoUringOperation.MaxVectors, 0UL), Submission(operation));
            Assert.Equal(IoUringCompletion.Resubmit, operation.Complete(IoUringOperation.MaxVectors * 4096));
            Assert.Equal((300, (ulong)IoUringOperation.MaxVectors * 4096), Submission(operation));
            Assert.Equal(IoUringCompletion.AwaitSync, operation.Complete(300 * 4096));
            Assert.Equal(IoUringCompletion.Done, IoUringOperation.Sync(file.SafeFileHandle, [operation]).Complete(0));
            Assert.Equal(Pages * 4096L, await operation.Completion);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A data sync covers only the writes whose transfers completed before it was submitted: a write which completes
    /// while its file's sync is in flight waits for the next, which every write waiting by then shares.
    /// </summary>
    [Fact]
    public async Task ADataSyncCoversOnlyWritesCompletedBeforeItsSubmission()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "io_uring requires Linux.");
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var path = TemporaryPath();
        try
        {
            using var file = File.Create(path);
            using var writer = ArcBufferWriter.CreateAligned();
            writer.Write(Bytes(3 * 4096, 1));
            using var payload = writer.PeekSlice(writer.Length);
            var writes = new IoUringOperation[3];
            var pages = new ArcBuffer[3];
            for (var index = 0; index < writes.Length; index++)
            {
                pages[index] = payload.Slice(index * 4096, 4096);
                writes[index] = IoUringOperation.Write(file.SafeFileHandle, index * 4096, new[] { pages[index] });
            }

            var handle = file.SafeFileHandle;
            var syncs = new IoUringSyncs();
            Assert.Equal(IoUringCompletion.AwaitSync, writes[0].Complete(4096));
            syncs.Add(writes[0]);
            Assert.True(syncs.TryTake(out _, out var first));
            Assert.Equal([writes[0]], first);

            // Writes completing while the first sync is in flight wait for the next.
            foreach (var write in writes[1..])
            {
                Assert.Equal(IoUringCompletion.AwaitSync, write.Complete(4096));
                syncs.Add(write);
            }

            Assert.False(syncs.TryTake(out _, out _));
            Assert.Equal(IoUringCompletion.Done, IoUringOperation.Sync(handle, first).Complete(0));
            syncs.Completed(handle);
            Assert.Equal(4096, await writes[0].Completion);
            Assert.False(writes[1].Completion.IsCompleted);

            Assert.True(syncs.TryTake(out _, out var second));
            Assert.Equal(writes[1..], second);
            Assert.Equal(IoUringCompletion.Done, IoUringOperation.Sync(handle, second).Complete(0));
            syncs.Completed(handle);
            Assert.True(syncs.IsEmpty);
            var synced = await Task.WhenAll(writes[1].Completion, writes[2].Completion);
            Assert.Equal([4096L, 4096L], synced);
            foreach (var page in pages)
            {
                page.Dispose();
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AZeroByteWriteFailsInsteadOfRetryingForever()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "io_uring requires Linux.");
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var path = TemporaryPath();
        try
        {
            using var file = File.Create(path);
            using var writer = ArcBufferWriter.CreateAligned();
            writer.Write(Bytes(4096, 1));
            using var payload = writer.PeekSlice(writer.Length);
            var operation = IoUringOperation.Write(file.SafeFileHandle, 0, new[] { payload });
            Assert.Equal(IoUringCompletion.Done, operation.Complete(0));
            var completion = operation.Completion;
            _ = await Assert.ThrowsAsync<IOException>(() => completion);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ConcurrentAppendsAreDurableAndRecoverAcrossReopen()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "io_uring requires Linux.");
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "clog-wal-uring-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var storage = new IoUringWalStorage();
            var options = Options(fileSize: 16 * 4096, groups: 4) with { PreallocateFiles = true };
            var payloads = Enumerable.Range(0, 300).Select(static index => Bytes(index % 23 * 997, index)).ToArray();
            WalPosition[] positions;
            await using (var wal = await WriteAheadLog.CreateAsync(storage, directory, options, TestContext.Current.CancellationToken))
            {
                positions = await Task.WhenAll(payloads.Select(payload => Task.Run(() => AppendAsync(wal, payload).AsTask(), Token)));
                for (var index = 0; index < payloads.Length; index += 29)
                {
                    Assert.Equal(payloads[index], (await wal.ReadAsync(positions[index], TestContext.Current.CancellationToken)).ToBytes());
                }
            }

            await using var reopened = await WriteAheadLog.OpenAsync(storage, directory, options, TestContext.Current.CancellationToken);
            var entries = await ReadAllAsync(reopened);
            Assert.All(entries, static entry => Assert.True(entry.IsRecord));
            var byPosition = positions.Zip(payloads).ToDictionary(static pair => pair.First, static pair => pair.Second);
            Assert.Equal(positions.Order(), entries.Select(static entry => entry.Position));
            Assert.All(entries, entry => Assert.Equal(byPosition[entry.Position], entry.Payload));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ALargeRecordIsWrittenAndReadInWaves()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "io_uring requires Linux.");
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "clog-wal-uring-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var storage = new IoUringWalStorage();
            var payload = Bytes(3_000_000, 1);
            await using var wal = await WriteAheadLog.CreateAsync(storage, directory, Options(window: 8), TestContext.Current.CancellationToken);
            var position = await AppendAsync(wal, payload);
            Assert.Equal(payload, (await wal.ReadAsync(position, TestContext.Current.CancellationToken)).ToBytes());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// An open which fails, here because the file's directory is a regular file (<c>ENOTDIR</c>), is reported once as
    /// <see cref="FatalErrorSite.IoUringOpen"/> with its path.
    /// </summary>
    [Fact]
    public async Task AFailedOpenIsReported()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "io_uring requires Linux.");
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var file = TemporaryPath();
        await File.WriteAllBytesAsync(file, [1], Token);
        try
        {
            using var storage = new IoUringWalStorage();
            var path = Path.Combine(file, "below-a-file");
            var since = FatalErrors.ReportedCount;
            IOException? failure = null;
            try
            {
                await storage.OpenFile(path, flush: false).DisposeAsync();
            }
            catch (IOException exception)
            {
                failure = exception;
            }

            Assert.NotNull(failure);
            Assert.Same(failure, AssertReported(since, FatalErrorSite.IoUringOpen, path).Failure.Exception);
        }
        finally
        {
            File.Delete(file);
        }
    }

    /// <summary>
    /// A write which the device fails is reported once as <see cref="FatalErrorSite.IoUringWrite"/> with its file's path,
    /// and stops the log, which reports nothing more and answers nothing more.
    /// </summary>
    [Fact]
    public async Task AFailedWriteIsReportedOnceAndStopsTheLog()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "io_uring requires Linux.");
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var directory = TemporaryPath();
        try
        {
            using var storage = new IoUringWalStorage();
            var wal = await WriteAheadLog.CreateAsync(storage, directory, Options() with { PreallocateFiles = false }, Token);
            storage.Engine.FailNextCompletion(InputOutputError);
            var since = FatalErrors.ReportedCount;
            var failure = await Assert.ThrowsAsync<IOException>(async () => await AppendAsync(wal, [1]));
            Assert.Same(AssertReported(since, FatalErrorSite.IoUringWrite, FilePath(directory, 1)).Failure.Exception, failure.InnerException);
            await AssertAnswersNothingAsync(wal);
            await wal.DisposeAsync();
            Assert.Empty(FatalErrors.TakeMine(since));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// A read which the device fails is reported once as <see cref="FatalErrorSite.IoUringRead"/> with its file's path.
    /// </summary>
    [Fact]
    public async Task AFailedReadIsReported()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "io_uring requires Linux.");
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var directory = TemporaryPath();
        try
        {
            using var storage = new IoUringWalStorage();
            await using var wal = await WriteAheadLog.CreateAsync(storage, directory, Options() with { PreallocateFiles = false }, Token);
            var position = await AppendAsync(wal, [1]);
            storage.Engine.FailNextCompletion(InputOutputError);
            var since = FatalErrors.ReportedCount;
            var failure = await Assert.ThrowsAsync<IOException>(async () => (await wal.ReadAsync(position, Token)).ToBytes());
            Assert.Same(failure, AssertReported(since, FatalErrorSite.IoUringRead, FilePath(directory, 1)).Failure.Exception);
            await AssertAnswersNothingAsync(wal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// A truncation which fails, here the replay's truncation of the last open's preallocated file, is reported once as
    /// <see cref="FatalErrorSite.IoUringTruncate"/> with its file's path.
    /// </summary>
    [Fact]
    public async Task AFailedTruncationIsReported()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "io_uring requires Linux.");
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var directory = TemporaryPath();
        try
        {
            using var storage = new IoUringWalStorage();
            var options = Options() with { PreallocateFiles = true };
            await using (var wal = await WriteAheadLog.CreateAsync(storage, directory, options, Token))
            {
                _ = await AppendAsync(wal, [1]);
            }

            var failure = new IOException("The device failed the truncation.");
            storage.TruncateFault = _ => failure;
            var since = FatalErrors.ReportedCount;
            Assert.Same(failure, await Assert.ThrowsAsync<IOException>(async () => await WriteAheadLog.OpenAsync(storage, directory, options, Token)));
            Assert.Same(failure, AssertReported(since, FatalErrorSite.IoUringTruncate, FilePath(directory, 1)).Failure.Exception);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// A failed <c>io_uring_enter</c> fails the ring every operation needs: it is reported once as
    /// <see cref="FatalErrorSite.IoUringRing"/>, the operation in flight fails with it rather than waiting for ever, and so
    /// does every later operation, unreported.
    /// </summary>
    [Fact]
    public async Task AFailedRingIsReportedOnceAndFailsEveryOperation()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "io_uring requires Linux.");
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var directory = TemporaryPath();
        var other = TemporaryPath();
        try
        {
            using var storage = new IoUringWalStorage();
            var options = Options() with { PreallocateFiles = false };
            var wal = await WriteAheadLog.CreateAsync(storage, directory, options, Token);
            storage.Engine.FailNextEnter(InputOutputError);
            var since = FatalErrors.ReportedCount;
            var failure = await Assert.ThrowsAsync<IOException>(async () => await AppendAsync(wal, [1]));
            var report = Assert.Single(FatalErrors.TakeMine(since));
            Assert.Equal(FatalErrorSite.IoUringRing, report.Site);
            Assert.StartsWith("/proc/self/fd/", report.Path, StringComparison.Ordinal);
            Assert.Same(report.Failure.Exception, failure.InnerException);
            await wal.DisposeAsync();

            var later = await WriteAheadLog.CreateAsync(storage, other, options, Token);
            var refused = await Assert.ThrowsAsync<IOException>(async () => await AppendAsync(later, [2]));
            Assert.Same(report.Failure.Exception, refused.InnerException);
            await later.DisposeAsync();
            Assert.Empty(FatalErrors.TakeMine(since));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
            Directory.Delete(other, recursive: true);
        }
    }

    private static string TemporaryPath() => Path.Combine(Path.GetTempPath(), "clog-wal-uring-" + Guid.NewGuid().ToString("N"));

    private static string FilePath(string directory, ulong file) => Path.Combine(WalFileSystem.Normalize(directory), WalFormat.GetFileName(file));

    [SupportedOSPlatform("linux")]
    private static unsafe void AssertRemaining(IoUringOperation operation, ArcBuffer payload, int transferred)
    {
        var sqe = new IoUringInterop.Sqe();
        operation.Prepare(&sqe);
        Assert.Equal((ulong)(4096 + transferred), sqe.Offset);
        var vectors = (IoUringInterop.IoVector*)sqe.Address;
        nuint remaining = 0;
        for (var index = 0; index < sqe.Length; index++)
        {
            remaining += vectors[index].Length;
        }

        Assert.Equal((nuint)(8192 - transferred), remaining);
        Assert.Equal((nuint)(4096 - transferred % 4096), vectors[0].Length);
        var expected = payload.ToArray();
        Assert.Equal(expected.AsSpan(transferred, (int)vectors[0].Length).ToArray(),
            new ReadOnlySpan<byte>(vectors[0].Base, (int)vectors[0].Length).ToArray());
    }

    [SupportedOSPlatform("linux")]
    private static unsafe (int Vectors, ulong Offset) Submission(IoUringOperation operation)
    {
        var sqe = new IoUringInterop.Sqe();
        operation.Prepare(&sqe);
        Assert.Equal(IoUringInterop.OpWritev, sqe.Opcode);
        return ((int)sqe.Length, sqe.Offset);
    }

    [SupportedOSPlatform("linux")]
    private static unsafe void AssertDataSync(IoUringOperation operation)
    {
        var sqe = new IoUringInterop.Sqe();
        operation.Prepare(&sqe);
        Assert.Equal(IoUringInterop.OpFsync, sqe.Opcode);
        Assert.Equal(IoUringInterop.FsyncDataSync, sqe.OperationFlags);
    }

    /// <summary>
    /// #267: an operation on a handle which has closed, as work which outlasts its store's stop meets, fails with
    /// <see cref="ObjectDisposedException"/> before it pins a buffer: a read's segments hold no pin, and a write's payload
    /// pages return to the pool once it is released, which the leak check confirms.
    /// </summary>
    [Fact]
    public void AnOperationOnAClosedHandlePinsNothing()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "io_uring requires Linux.");
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var path = TemporaryPath();
        try
        {
            AssertPinsNothing(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [SupportedOSPlatform("linux")]
    private static void AssertPinsNothing(string path)
    {
        var handle = File.OpenHandle(path, FileMode.Create, FileAccess.ReadWrite);
        handle.Dispose();
        using var segments = new CountingMemory(4096);

        _ = Assert.Throws<ObjectDisposedException>(() => IoUringOperation.Read(handle, 0, [segments.Memory, segments.Memory]));
        Assert.Equal(0, segments.Outstanding);

        using var writer = ArcBufferWriter.CreateAligned();
        writer.Write(Bytes(8192, 1));
        using var payload = writer.PeekSlice(writer.Length);
        _ = Assert.Throws<ObjectDisposedException>(() => IoUringOperation.Write(handle, 0, new[] { payload }));
        _ = Assert.Throws<ObjectDisposedException>(() => IoUringOperation.Sync(handle, []));
    }

    /// <summary>Memory whose pins are counted, as an ordinary array's segment takes a GC handle for each.</summary>
    private sealed class CountingMemory(int length) : System.Buffers.MemoryManager<byte>
    {
        private readonly byte[] _array = new byte[length];
        private int _outstanding;

        public int Outstanding => Volatile.Read(ref _outstanding);

        public override Span<byte> GetSpan() => _array;

        public override System.Buffers.MemoryHandle Pin(int elementIndex = 0)
        {
            _ = Interlocked.Increment(ref _outstanding);
            var pinned = System.Runtime.InteropServices.GCHandle.Alloc(_array, System.Runtime.InteropServices.GCHandleType.Pinned);
            return new((byte*)pinned.AddrOfPinnedObject() + elementIndex, pinned, this);
        }

        public override void Unpin() => _ = Interlocked.Decrement(ref _outstanding);

        protected override void Dispose(bool disposing)
        {
        }
    }
}
