using System.Buffers;
using System.Runtime.CompilerServices;
using Orleans.Serialization.Buffers;
using Xunit;

namespace Orleans.Journaling.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT")]
public sealed class VolatileJournalStorageOwnershipTests(ITestOutputHelper output)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetainedWrite_HasIndependentPinAndReleasesExactlyOnce(bool replace)
    {
        var storage = new VolatileJournalStorage();
        using var writer = new ArcBufferWriter();
        writer.Write(new byte[] { 10, 20, 30 });
        using var source = writer.PeekSlice(writer.Length);
        var page = Assert.IsType<ArcBufferPage>(source.First);
        var baseline = page.ReferenceCount;
        var capability = storage;
        if (replace) await capability.ReplaceAsync(source, Token);
        else await capability.AppendAsync(source, Token);
        Assert.Equal(baseline + 1, page.ReferenceCount);
        Assert.Same(page, Assert.Single(storage.Storage.Segments).First);
        Assert.Equal(new byte[] { 10, 20, 30 }, await Read(storage));
        Assert.Equal(baseline + 1, page.ReferenceCount);

        await storage.ReplaceAsync(default, Token);
        Assert.Equal(baseline, page.ReferenceCount);
        await storage.DeleteAsync(Token);
        await storage.DeleteAsync(Token);
        Assert.Equal(baseline, page.ReferenceCount);
        Assert.Equal(0, storage.GetMemoryStatistics().RetainedCapacity);
    }

    [Fact]
    public async Task RetainedWrite_SurvivesSourceWriterResetAndDisposal()
    {
        var storage = new VolatileJournalStorage();
        var writer = new ArcBufferWriter();
        writer.Write(new byte[] { 7, 8, 9 });
        var source = writer.PeekSlice(writer.Length);
        await storage.AppendAsync(source, Token);
        source.Dispose();
        writer.Reset();
        writer.Write(new byte[] { 90, 91, 92 });
        writer.Dispose();
        Assert.Equal(new byte[] { 7, 8, 9 }, await Read(storage));
        await storage.DeleteAsync(Token);
        Assert.Equal(0, storage.GetMemoryStatistics().RetainedPages);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetainedWrite_MultiPageOwnersSurviveSourceDisposalAndRetirement(bool replace)
    {
        var storage = new VolatileJournalStorage();
        using var writer = new ArcBufferWriter();
        var bytes = Enumerable.Range(0, 3 * ArcBufferWriter.MinimumPageSize + 17)
            .Select(static i => (byte)(i % 251)).ToArray();
        writer.Write(bytes);
        var source = writer.PeekSlice(writer.Length);
        using var observation = source.Slice(0);
        var pages = source.Pages.ToArray();
        Assert.Equal(4, pages.Length);
        using (source)
        {
            var retained = storage;
            if (replace)
            {
                await retained.ReplaceAsync(source, Token);
            }
            else
            {
                await retained.AppendAsync(source, Token);
            }

            Assert.All(pages, static page => Assert.Equal(4, page.ReferenceCount));
        }

        writer.Reset();
        writer.Write(new byte[] { 99 });
        writer.Dispose();
        Assert.All(pages, static page => Assert.Equal(2, page.ReferenceCount));
        Assert.Equal(bytes, await Read(storage));
        Assert.All(pages, static page => Assert.Equal(2, page.ReferenceCount));
        Assert.Equal(4, storage.GetMemoryStatistics().RetainedPages);

        await storage.ReplaceAsync(default, Token);
        Assert.All(pages, static page => Assert.Equal(1, page.ReferenceCount));
        Assert.Equal(bytes, observation.ToArray());
        await storage.DeleteAsync(Token);
        Assert.All(pages, static page => Assert.Equal(1, page.ReferenceCount));
        Assert.Equal(0, storage.GetMemoryStatistics().RetainedPages);
    }

    [Fact]
    public async Task ConcurrentReaders_PinStableBytesAndMetadataAcrossSharedHandleReplaceDeleteRecreate()
    {
        var provider = new VolatileJournalStorageProvider();
        var first = Assert.IsType<VolatileJournalStorage>(provider.CreateStorage(new("shared")));
        var second = Assert.IsType<VolatileJournalStorage>(provider.CreateStorage(new("shared")));
        await first.CreateIfNotExistsAsync(new Dictionary<string, string> { ["owner"] = "old" }, Token);
        using var writer = new ArcBufferWriter();
        writer.Write(new byte[] { 1, 2, 3 });
        using var source = writer.PeekSlice(writer.Length);
        await first.AppendAsync(source, Token);
        await second.AppendBytesAsync(new ReadOnlySequence<byte>(new byte[] { 4, 5 }), Token);
        var originalMetadata = await first.GetMetadataAsync(Token);
        var baseline = Assert.IsType<ArcBufferPage>(source.First).ReferenceCount;
        using var entered = new CountdownEvent(2);
        using var release = new ManualResetEventSlim();
        var readers = Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
        {
            var consumer = new CapturingConsumer(() =>
            {
                entered.Signal();
                Assert.True(release.Wait(TimeSpan.FromSeconds(30), Token), "Reader release barrier was not signaled.");
            });
            await first.ReadAsync(consumer, Token);
            Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, consumer.Bytes.ToArray());
            Assert.Equal(originalMetadata!.ETag, consumer.Metadata!.ETag);
            Assert.Equal("old", consumer.Metadata.Properties["owner"]);
            Assert.Equal(1, consumer.CompletionCount);
        }, Token)).ToArray();
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(30), Token), "Both stable readers must enter before replacing storage.");
            Assert.Equal(baseline + 2, Assert.IsType<ArcBufferPage>(source.First).ReferenceCount);
            await second.ReplaceBytesAsync(new ReadOnlySequence<byte>(new byte[] { 8, 9 }), Token);
            Assert.Equal(baseline + 1, Assert.IsType<ArcBufferPage>(source.First).ReferenceCount); // writer + source + two readers, no store pin
            await second.DeleteAsync(Token);
            Assert.True(await first.CreateIfNotExistsAsync(new Dictionary<string, string> { ["owner"] = "new" }, Token));
            await second.AppendBytesAsync(new ReadOnlySequence<byte>(new byte[] { 10, 11 }), Token);
            Assert.Equal(new byte[] { 10, 11 }, await Read(second));
            Assert.Equal("new", (await first.GetMetadataAsync(Token))!.Properties["owner"]);
        }
        finally
        {
            release.Set();
            await Task.WhenAll(readers);
        }

        Assert.Equal(baseline - 1, Assert.IsType<ArcBufferPage>(source.First).ReferenceCount);
        await second.DeleteAsync(Token);
        Assert.Equal(0, first.GetMemoryStatistics().RetainedCapacity);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReaderFailureOrCancellation_ReleasesSnapshotWithoutRetiringStoredPin(bool cancel)
    {
        var storage = new VolatileJournalStorage();
        using var writer = new ArcBufferWriter();
        writer.Write(new byte[] { 1, 2, 3 });
        using var source = writer.PeekSlice(writer.Length);
        await storage.AppendAsync(source, Token);
        await storage.AppendBytesAsync(new ReadOnlySequence<byte>(new byte[] { 4 }), Token);
        var baseline = Assert.IsType<ArcBufferPage>(source.First).ReferenceCount;
        using var cancellation = new CancellationTokenSource();
        var expected = new IOException("Consumer failed after capture.");
        var consumer = new CapturingConsumer(() =>
        {
            Assert.Equal(baseline + 1, Assert.IsType<ArcBufferPage>(source.First).ReferenceCount);
            if (cancel) cancellation.Cancel();
            else throw expected;
        });
        if (cancel)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => storage.ReadAsync(consumer, cancellation.Token).AsTask());
        else
            Assert.Same(expected, await Record.ExceptionAsync(() => storage.ReadAsync(consumer, cancellation.Token).AsTask()));
        Assert.Equal(baseline, Assert.IsType<ArcBufferPage>(source.First).ReferenceCount);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await Read(storage));
        await storage.DeleteAsync(Token);
        Assert.Equal(baseline - 1, Assert.IsType<ArcBufferPage>(source.First).ReferenceCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanceledWrites_DoNotAcquirePinsChangeMetadataOrResetCompaction(bool replace)
    {
        var storage = new VolatileJournalStorage(journalFormatKey: null, new VolatileJournalStorageOptions { MaxAppendsBeforeSnapshot = 11 });
        await SeedCompactionRequest(storage, [42]);
        var metadata = await storage.GetMetadataAsync(Token);
        using var writer = new ArcBufferWriter();
        writer.Write(new byte[] { 10 });
        using var source = writer.PeekSlice(writer.Length);
        var baseline = Assert.IsType<ArcBufferPage>(source.First).ReferenceCount;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var retained = storage;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => replace
            ? retained.ReplaceAsync(source, cancellation.Token).AsTask()
            : retained.AppendAsync(source, cancellation.Token).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => replace
            ? storage.ReplaceBytesAsync(new ReadOnlySequence<byte>(new byte[] { 99 }), cancellation.Token).AsTask()
            : storage.AppendBytesAsync(new ReadOnlySequence<byte>(new byte[] { 99 }), cancellation.Token).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => storage.DeleteAsync(cancellation.Token).AsTask());
        Assert.Equal(baseline, Assert.IsType<ArcBufferPage>(source.First).ReferenceCount);
        Assert.Equal(metadata!.ETag, (await storage.GetMetadataAsync(Token))!.ETag);
        Assert.True(storage.IsCompactionRequested);
        Assert.Equal(new byte[] { 42 }, await Read(storage));
        await storage.DeleteAsync(Token);
    }

    [Fact]
    public async Task ManySmallJournals_RetainOneMinimumPageEachUntilDeletion()
    {
        const int journalCount = 64;
        var provider = new VolatileJournalStorageProvider();
        var stores = Enumerable.Range(0, journalCount)
            .Select(i => Assert.IsType<VolatileJournalStorage>(provider.CreateStorage(new($"capacity/{i}"))))
            .ToArray();
        var pages = new List<ArcBufferPage>();
        var observations = new List<ArcBuffer>();
        try
        {
            foreach (var storage in stores)
            {
                using var writer = new ArcBufferWriter();
                writer.Write(new byte[] { 42 });
                using var source = writer.PeekSlice(1);
                await storage.AppendAsync(source, Token);
                observations.Add(source.Slice(0));
                pages.Add(Assert.IsType<ArcBufferPage>(source.First));
            }

            Assert.Equal(journalCount, pages.Distinct().Count());
            Assert.All(pages, static page => Assert.Equal(2, page.ReferenceCount));
            Assert.Equal((long)journalCount * ArcBufferWriter.MinimumPageSize,
                stores.Sum(static storage => storage.GetMemoryStatistics().RetainedCapacity));
            var retainedHandles = stores.Select((_, i) => provider.CreateStorage(new($"capacity/{i}"))).ToArray();
            foreach (var handle in retainedHandles)
            {
                Assert.Equal(new byte[] { 42 }, await Read(handle));
                await handle.DeleteAsync(Token);
            }

            Assert.All(pages, static page => Assert.Equal(1, page.ReferenceCount));
            Assert.All(stores, static storage => Assert.Equal((0, 0L, 0), storage.GetMemoryStatistics()));
        }
        finally
        {
            foreach (var observation in observations)
            {
                observation.Dispose();
            }

            foreach (var storage in stores)
            {
                await storage.DeleteAsync(Token);
            }
        }
    }

    [Fact]
    public async Task EmptyWrites_PreserveSegmentsMetadataAndAppendLimit()
    {
        var storage = new VolatileJournalStorage("empty-format", new VolatileJournalStorageOptions { MaxAppendsBeforeSnapshot = 10 });
        using var writer = new ArcBufferWriter();
        using var source = writer.PeekSlice(0);
        await storage.ReplaceAsync(source, Token);
        var metadata = await storage.GetMetadataAsync(Token);
        Assert.Equal("empty-format", metadata!.FormatKey);
        Assert.Equal("1", metadata.ETag);
        for (var i = 0; i < 9; i++) await storage.AppendAsync(default, Token);
        Assert.False(storage.IsCompactionRequested);
        await storage.AppendAsync(source, Token);
        Assert.True(storage.IsCompactionRequested);
        Assert.Equal(11, storage.GetMemoryStatistics().Segments);
        Assert.Equal(0, storage.GetMemoryStatistics().RetainedCapacity);
        Assert.Empty(await Read(storage));
        await storage.ReplaceAsync(default, Token);
        Assert.False(storage.IsCompactionRequested);
        Assert.Equal("12", (await storage.GetMetadataAsync(Token))!.ETag);
        Assert.Single(storage.Segments);
        await storage.DeleteAsync(Token);
    }

    [Fact]
    public void ArcPagePool_ManyReturnsHaveExactBoundAndOversizedPagesAreNotCached()
    {
        var pool = new ArcBufferPagePool();
        var pages = Enumerable.Range(0, 300).Select(_ => pool.Rent()).ToArray();
        foreach (var page in pages) pool.Return(page);
        Assert.Equal(4 * 1024 * 1024, pool.RetainedBytes);
        Assert.Equal(256, pool.RetainedPages);
        Assert.Equal(44, pages.Count(static page => page.Array.Length == 0));
        var oversized = pool.Rent(2 * 1024 * 1024);
        pool.Return(oversized);
        Assert.Empty(oversized.Array);
        Assert.Equal(4 * 1024 * 1024, pool.RetainedBytes);
        for (var i = 0; i < 256; i++) pool.Rent();
        Assert.Equal(0, pool.RetainedBytes);
        Assert.Equal(0, pool.RetainedPages);
    }

    [Fact]
    public async Task RepeatedPayload_SharedOwnersRetainFourPagesVersusFourHundredCopiedInputPages()
    {
        var borrowed = new VolatileJournalStorage();
        var retained = new VolatileJournalStorage();
        var bytes = Enumerable.Range(0, 64 * 1024).Select(static i => (byte)(i % 251)).ToArray();
        using var writer = new ArcBufferWriter();
        writer.Write(bytes);
        using var source = writer.PeekSlice(writer.Length);
        var sequence = source.AsReadOnlySequence();
        var allocationStart = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++) await retained.AppendAsync(source, Token);
        var retainedAllocations = GC.GetAllocatedBytesForCurrentThread() - allocationStart;
        allocationStart = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++) await borrowed.AppendBytesAsync(sequence, Token);
        var borrowedAllocations = GC.GetAllocatedBytesForCurrentThread() - allocationStart;
        var sharedStats = retained.GetMemoryStatistics();
        var copyStats = borrowed.GetMemoryStatistics();
        Assert.Equal(4, sharedStats.RetainedPages);
        Assert.Equal(65_536, sharedStats.RetainedCapacity);
        Assert.Equal(400, copyStats.RetainedPages);
        Assert.Equal(6_553_600, copyStats.RetainedCapacity);
        Assert.All(retained.Storage.Segments, segment => Assert.Same(source.First, segment.First));
        Assert.All(borrowed.Storage.Segments, segment => Assert.NotSame(source.First, segment.First));
        foreach (var segment in retained.Segments) Assert.Equal(bytes, segment);
        foreach (var segment in borrowed.Segments) Assert.Equal(bytes, segment);
        output.WriteLine($"OWNERSHIP_EVIDENCE retainedAllocatedBytes={retainedAllocations} borrowedAllocatedBytes={borrowedAllocations} sharedRetainedBytes={sharedStats.RetainedCapacity} copiedRetainedBytes={copyStats.RetainedCapacity} eliminatedCopiedBytes={(long)bytes.Length * 100}");
        await retained.DeleteAsync(Token);
        await borrowed.DeleteAsync(Token);
        Assert.Equal(0, retained.GetMemoryStatistics().RetainedCapacity);
        Assert.Equal(0, borrowed.GetMemoryStatistics().RetainedCapacity);
        Assert.Equal(2, Assert.IsType<ArcBufferPage>(source.First).ReferenceCount); // source and its writer, no leaked storage/reader references
    }

    [Fact]
    public async Task ArcPagePool_ConcurrentReturnsRespectExactReservationBound()
    {
        var pool = new ArcBufferPagePool(2 * ArcBufferWriter.MinimumPageSize);
        var pages = Enumerable.Range(0, 200).Select(_ => pool.Rent()).ToArray();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var remaining = pages.Length;
        var returns = pages.Select(page => Task.Run(async () =>
        {
            if (Interlocked.Decrement(ref remaining) == 0) ready.SetResult();
            await release.Task;
            pool.Return(page);
        }, Token)).ToArray();
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(30), Token);
        release.SetResult();
        await Task.WhenAll(returns);
        Assert.Equal(2 * ArcBufferWriter.MinimumPageSize, pool.RetainedBytes);
        Assert.Equal(2, pool.RetainedPages);
        Assert.Equal(198, pages.Count(static page => page.Array.Length == 0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetainedWrite_InvalidOwnershipTokenDoesNotPublishOrResetCounters(bool replace)
    {
        var storage = new VolatileJournalStorage(journalFormatKey: null, new VolatileJournalStorageOptions { MaxAppendsBeforeSnapshot = 11 });
        await SeedCompactionRequest(storage, [42]);
        var before = storage.GetMemoryStatistics();
        var metadata = await storage.GetMetadataAsync(Token);
        using var writer = new ArcBufferWriter();
        writer.Write(new byte[] { 10 });
        var source = writer.PeekSlice(writer.Length);
        source.Dispose();
        Assert.Equal(1, Assert.IsType<ArcBufferPage>(source.First).ReferenceCount); // writer still pins the page, source no longer owns a token
        var capability = storage;
        await Assert.ThrowsAsync<InvalidOperationException>(() => replace
            ? capability.ReplaceAsync(source, Token).AsTask()
            : capability.AppendAsync(source, Token).AsTask());
        Assert.Equal(1, Assert.IsType<ArcBufferPage>(source.First).ReferenceCount);
        Assert.Equal(before, storage.GetMemoryStatistics());
        Assert.Equal(metadata!.ETag, (await storage.GetMetadataAsync(Token))!.ETag);
        Assert.True(storage.IsCompactionRequested);
        Assert.Equal(new byte[] { 42 }, await Read(storage));
        await storage.DeleteAsync(Token);
    }

    [Fact]
    public async Task UnreachableSharedStore_ReleasesItsLastProviderPinExactlyOnce()
    {
        using var writer = new ArcBufferWriter();
        writer.Write(new byte[] { 1, 2, 3 });
        using var source = writer.PeekSlice(writer.Length);
        var baseline = Assert.IsType<ArcBufferPage>(source.First).ReferenceCount;
        var abandoned = await CreateAbandonedStore(source);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(abandoned.IsAlive);
        Assert.Equal(baseline, Assert.IsType<ArcBufferPage>(source.First).ReferenceCount);
        Assert.Equal(new byte[] { 1, 2, 3 }, source.ToArray());
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async ValueTask<WeakReference> CreateAbandonedStore(ArcBuffer source)
    {
        var store = new VolatileJournalStorage.Store("abandoned");
        var storage = new VolatileJournalStorage(store, journalFormatKey: null);
        var baseline = Assert.IsType<ArcBufferPage>(source.First).ReferenceCount;
        await storage.AppendAsync(source, Token);
        Assert.Equal(baseline + 1, Assert.IsType<ArcBufferPage>(source.First).ReferenceCount);
        var result = new WeakReference(store);
        GC.KeepAlive(store);
        return result;
    }

    private static async Task SeedCompactionRequest(VolatileJournalStorage storage, byte[] bytes)
    {
        await storage.AppendBytesAsync(new ReadOnlySequence<byte>(bytes), Token);
        for (var i = 0; i < 10; i++)
        {
            await storage.AppendAsync(default, Token);
        }

        Assert.True(storage.IsCompactionRequested);
    }

    private static async Task<byte[]> Read(IJournalStorage storage)
    {
        var consumer = new CapturingConsumer();
        await storage.ReadAsync(consumer, Token);
        Assert.Equal(1, consumer.CompletionCount);
        return consumer.Bytes.ToArray();
    }

    private sealed class CapturingConsumer(Action? firstRead = null) : IJournalStorageConsumer
    {
        private bool _entered;
        public List<byte> Bytes { get; } = [];
        public IJournalMetadata? Metadata { get; private set; }
        public int CompletionCount { get; private set; }
        public void Read(JournalBufferReader buffer, IJournalMetadata? metadata)
        {
            if (!_entered)
            {
                _entered = true;
                firstRead?.Invoke();
            }
            Metadata = metadata;
            Bytes.AddRange(buffer.ToArray());
            buffer.Skip(buffer.Length);
            if (buffer.IsCompleted) CompletionCount++;
        }
    }

    private sealed class SequenceSegment : ReadOnlySequenceSegment<byte>
    {
        public SequenceSegment(ReadOnlyMemory<byte> memory) => Memory = memory;
        public SequenceSegment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new SequenceSegment(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = next;
            return next;
        }
    }

}
