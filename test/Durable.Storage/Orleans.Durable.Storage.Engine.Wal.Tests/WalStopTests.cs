using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Wal.Simulation;
using Orleans.Durable.Storage.Testing;
using static Orleans.Durable.Storage.Engine.Wal.Tests.WalTestHelpers;

namespace Orleans.Durable.Storage.Engine.Wal.Tests;

/// <summary>
/// Checks how the log stops (conventions.md §14): a failure which no storage seam reported is a defect the log reports
/// once, at its own site, before any request fails with it; a failure its storage reported is not reported again; and
/// from then on the log answers nothing.
/// </summary>
public sealed class WalStopTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _leaks.Dispose();

    /// <summary>
    /// A failure no storage seam reported, in placing records, in a write group, in a roll to the next file, or in
    /// discarding a prepared file, is a defect the log reports once by its name, and the log then answers nothing:
    /// the failed request fails with it, as does every later one.
    /// </summary>
    [Theory]
    [InlineData("WalPlacement")]
    [InlineData("WalWriteGroup")]
    [InlineData("WalRoll")]
    [InlineData("WalDiscard")]
    public async Task AnUnreportedFailureIsReportedOnceAsItsDefectAndTheLogAnswersNothing(string site)
    {
        var storage = new SimulatedWalStorage();
        await storage.CreateDirectoryAsync(LogPath, Token);
        await using var directory = new DefectiveDirectory(await storage.OpenDirectoryAsync(LogPath, Token));
        var defect = new InvalidOperationException("A defect no storage seam reported.");

        // A roll creates file 2; with preallocation, the log prepares file 2 as soon as it starts.
        directory.CreateFault = file => site is "WalRoll" or "WalDiscard" && file >= 2 ? defect : null;
        var options = site == "WalDiscard"
            ? Options(pageSize: 512, fileSize: 4 * 512) with { PreallocateFiles = true }
            : Options(fileSize: 4 * 4096) with { PreallocateFiles = false };
        var wal = await WriteAheadLog.CreateAsync(directory, options, Token);
        var since = FatalErrors.ReportedCount;
        if (site == "WalDiscard")
        {
            // The prepared file is taken only by a roll or by disposal, which discards it.
            _ = await AppendAsync(wal, [1]);
            await wal.DisposeAsync();
            Assert.Same(defect, AssertDefect(since, site).Failure.Exception);
            return;
        }

        if (site == "WalRoll")
        {
            // Three records, each most of a page, fill file 1's three data pages, so the fourth rolls.
            for (var index = 0; index < 3; index++)
            {
                _ = await AppendAsync(wal, Bytes(3_000, index));
            }
        }

        wal.PlacementFault = site == "WalPlacement" ? defect : null;
        directory.WriteDefect = site == "WalWriteGroup" ? defect : null;
        var failure = await Assert.ThrowsAsync<IOException>(async () => await AppendAsync(wal, Bytes(3_000, 3)));
        Assert.Same(defect, failure.InnerException);
        Assert.Same(defect, AssertDefect(since, site).Failure.Exception);
        await AssertAnswersNothingAsync(wal);
        await wal.DisposeAsync();
        Assert.Empty(FatalErrors.TakeMine(since));
    }

    /// <summary>
    /// A failure its storage reported, of a read, of a release's deletion, or of the next file's preparation, stops the
    /// log at once without a second report, though no write failed: the log answers nothing from then on.
    /// </summary>
    [Theory]
    [InlineData("read")]
    [InlineData("release")]
    [InlineData("preparation")]
    public async Task AReportedFailureOfAnyStorageOperationStopsTheLog(string operation)
    {
        var storage = new SimulatedWalStorage();
        await storage.CreateDirectoryAsync(LogPath, Token);
        await using var directory = new DefectiveDirectory(await storage.OpenDirectoryAsync(LogPath, Token));
        var fault = new IOException("An injected failure.");
        var preparation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = operation == "preparation"
            ? Options(pageSize: 512, fileSize: 4 * 512) with { PreallocateFiles = true }
            : Options(fileSize: 4 * 4096) with { PreallocateFiles = false };
        if (operation == "preparation")
        {
            // The log prepares file 2 as soon as file 1 starts taking records. The preparation waits for the append to be
            // durable, and only then does a directory flush, the one which publishes file 2, fail, so the stop follows the
            // append's answer rather than racing it (#167).
            directory.CreateHold = file => file >= 2 ? preparation.Task : null;
            storage.DirectoryFlushFault = _ => preparation.Task.IsCompleted ? fault : null;
        }

        var wal = await WriteAheadLog.CreateAsync(directory, options, Token);
        var since = FatalErrors.ReportedCount;
        var last = default(WalPosition);
        for (var index = 0; index < (operation == "release" ? 4 : 1); index++)
        {
            last = await AppendAsync(wal, Bytes(operation == "preparation" ? 100 : 3_000, index));
        }

        await WaitForFrontierAsync(wal, last);
        switch (operation)
        {
            case "read":
                storage.ReadFault = (_, _, _, _) => fault;
                _ = await Assert.ThrowsAsync<IOException>(async () => (await wal.ReadAsync(last, Token)).ToBytes());
                break;
            case "release":
                storage.DeleteFault = (_, _) => fault;
                _ = await Assert.ThrowsAsync<IOException>(async () => await wal.ReleaseAsync(wal.CurrentFile, Token));
                break;
            default:
                preparation.SetResult();
                await WaitForStopAsync(wal);
                break;
        }

        Assert.Same(fault, Assert.Single(FatalErrors.TakeMine(since)).Failure.Exception);
        await AssertAnswersNothingAsync(wal);
        await wal.DisposeAsync();
        Assert.Empty(FatalErrors.TakeMine(since));
    }

    /// <summary>
    /// A directory whose file creations and writes can fail as a defect would, with an exception no storage seam
    /// reported, and whose file creations can wait on the test.
    /// </summary>
    private sealed class DefectiveDirectory(WalDirectory inner) : WalDirectory
    {
        public Func<ulong, Exception?>? CreateFault { get; set; }

        /// <summary>Gets or sets what returns the task, if any, which a file's creation waits for before it begins.</summary>
        public Func<ulong, Task?>? CreateHold { get; set; }

        public Exception? WriteDefect { get; set; }

        public override string Path => inner.Path;

        public override int MinimumPageSize => inner.MinimumPageSize;

        public override ValueTask<IReadOnlyList<ulong>> ListFilesAsync(CancellationToken cancellationToken) => inner.ListFilesAsync(cancellationToken);

        public override async ValueTask<WalFile> CreateFileAsync(
            ulong file, ArcBuffer header, long preallocatedLength, WalFillPace? pace, CancellationToken cancellationToken)
        {
            if (CreateHold?.Invoke(file) is { } hold)
            {
                await hold;
            }

            if (CreateFault?.Invoke(file) is { } fault)
            {
                throw fault;
            }

            return new DefectiveFile(this, await inner.CreateFileAsync(file, header, preallocatedLength, pace, cancellationToken));
        }

        public override async ValueTask<WalFile> CreateAllocatedFileAsync(ulong file, ArcBuffer header, long length, CancellationToken cancellationToken)
        {
            if (CreateHold?.Invoke(file) is { } hold)
            {
                await hold;
            }

            if (CreateFault?.Invoke(file) is { } fault)
            {
                throw fault;
            }

            return new DefectiveFile(this, await inner.CreateAllocatedFileAsync(file, header, length, cancellationToken));
        }

        public override async ValueTask<WalFile> OpenFileAsync(ulong file, CancellationToken cancellationToken) =>
            new DefectiveFile(this, await inner.OpenFileAsync(file, cancellationToken));

        public override ValueTask DeleteFileAsync(ulong file, CancellationToken cancellationToken) => inner.DeleteFileAsync(file, cancellationToken);

        public override ValueTask<WalFile> CreateNamedFileAsync(string name, ArcBuffer contents, CancellationToken cancellationToken) =>
            inner.CreateNamedFileAsync(name, contents, cancellationToken);

        public override ValueTask<WalFile> OpenNamedFileAsync(string name, CancellationToken cancellationToken) =>
            inner.OpenNamedFileAsync(name, cancellationToken);

        public override ValueTask<IReadOnlyList<string>> ListNamedFilesAsync(string extension, CancellationToken cancellationToken) =>
            inner.ListNamedFilesAsync(extension, cancellationToken);

        public override ValueTask DeleteNamedFileAsync(string name, CancellationToken cancellationToken) =>
            inner.DeleteNamedFileAsync(name, cancellationToken);

        public override ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class DefectiveFile(DefectiveDirectory directory, WalFile inner) : WalFile
    {
        public override long Length => inner.Length;

        public override ValueTask WriteAsync(long offset, ReadOnlyMemory<ArcBuffer> buffers) =>
            directory.WriteDefect is { } defect ? throw defect : inner.WriteAsync(offset, buffers);

        public override ValueTask<ArcBuffer> ReadAsync(long offset, int length, CancellationToken cancellationToken) =>
            inner.ReadAsync(offset, length, cancellationToken);

        public override ValueTask TruncateAsync(long length, CancellationToken cancellationToken) => inner.TruncateAsync(length, cancellationToken);

        public override ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
