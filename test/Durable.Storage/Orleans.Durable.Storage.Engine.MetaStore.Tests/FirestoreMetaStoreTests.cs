using Google.Api.Gax;
using Google.Cloud.Firestore;
using Google.Cloud.Firestore.V1;
using Grpc.Core;
using Orleans.Durable.Storage.GoogleCloud.MetaStore;
using Orleans.Durable.Storage.Testing;
using ProtobufTimestamp = Google.Protobuf.WellKnownTypes.Timestamp;
using V1 = Google.Cloud.Firestore.V1;

namespace Orleans.Durable.Storage.Engine.MetaStore.Tests;

/// <summary>
/// The Firestore register over a credential-free <see cref="FirestoreDb"/>, the SDK's own client over
/// <see cref="FakeFirestore"/>'s calls: the contract kit, the version a write returns, and a fresh version for a write of
/// unchanged bytes (MS-I5).
/// </summary>
public sealed class FirestoreMetaStoreTests : IDisposable
{
    private static readonly Guid Cluster = new("0123456789abcdef0123456789abcdef");
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

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task AWriteCancelledMidCallLandsItsOwnCopy(int pages)
    {
        var client = new FakeFirestore();
        await ConditionalRegisterContract.AWriteCancelledMidCallLandsItsOwnCopy(
            await CreateRegisterAsync(client),
            client.HoldNextWrite,
            pages,
            lands: true);
    }

    [Fact]
    public async Task AConflictAnsweredAfterCancellationThrows()
    {
        var client = new FakeFirestore();
        await ConditionalRegisterContract.AConflictAnsweredAfterCancellationThrows(
            await CreateRegisterAsync(client),
            client.ConflictNextWriteAfterCancellation);
    }

    /// <summary>
    /// A write which changes none of a document's fields leaves its update time unchanged, as Firestore does, so the
    /// register's write field is what gives a write of unchanged bytes a version the register never had (MS-I5).
    /// </summary>
    [Fact]
    public async Task AWriteOfUnchangedBytesGetsAFreshVersion()
    {
        var client = new FakeFirestore();
        var register = await CreateRegisterAsync(client);
        var first = (await register.WriteAsync(null, "same"u8.ToArray(), TestContext.Current.CancellationToken)).AppliedVersion();
        var second = (await register.WriteAsync(first, "same"u8.ToArray(), TestContext.Current.CancellationToken)).AppliedVersion();

        Assert.NotEqual(first, second);
        using var entry = Assert.IsType<MetaStoreEntry>(await register.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(second, entry.Version);
        Assert.False((await register.WriteAsync(first, "same"u8.ToArray(), TestContext.Current.CancellationToken)).IsApplied());

        var unchanged = Assert.Single(client.Documents);
        var time = unchanged.UpdateTime;
        Assert.Equal(time, client.Apply(new V1.Write { Update = unchanged.Clone() }).UpdateTime);
    }

    public void Dispose() => _leaks.Dispose();

    private static ValueTask<IConditionalRegister> CreateRegisterAsync() => CreateRegisterAsync(new FakeFirestore());

    private static ValueTask<IConditionalRegister> CreateRegisterAsync(FakeFirestore client) =>
        new FirestoreMetaStore(new FirestoreDbBuilder
        {
            ProjectId = "clog-test",
            CallInvoker = client,
            EmulatorDetection = EmulatorDetection.None,
        }.Build()).GetRegisterAsync(Cluster, "l/orders", TestContext.Current.CancellationToken);

    /// <summary>
    /// The Firestore service's calls in memory: a commit applies each write under its precondition, as the service does,
    /// and leaves a document's update time unchanged when a write changes none of its fields; a batch read streams each
    /// document found or missing. It can hold one commit in flight until the test releases it, and answers a call whose
    /// token is cancelled with the status gRPC gives it.
    /// </summary>
    private sealed class FakeFirestore : CallInvoker
    {
        private readonly Lock _lock = new();
        private readonly Dictionary<string, Document> _documents = [with(StringComparer.Ordinal)];
        private WriteHold? _hold;
        private WriteHold? _conflict;
        private long _ticks;

        internal IReadOnlyList<Document> Documents
        {
            get
            {
                lock (_lock)
                {
                    return [.. _documents.Values.Select(static document => document.Clone())];
                }
            }
        }

        /// <summary>
        /// Holds the next commit in flight: its caller waits until its token is cancelled, and the commit lands, with the
        /// request the register passed, once the test releases the hold.
        /// </summary>
        internal void HoldNextWrite(WriteHold hold) => _hold = hold;

        /// <summary>
        /// Holds the next commit until its caller's token is cancelled, and then answers it with the service's failed
        /// precondition.
        /// </summary>
        internal void ConflictNextWriteAfterCancellation(WriteHold hold) => _conflict = hold;

        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method,
            string? host,
            CallOptions options,
            TRequest request) =>
            new(
                CommitAsync(Assert.IsType<CommitRequest>(request), options.CancellationToken).ContinueWith(
                    static commit => (TResponse)(object)commit.GetAwaiter().GetResult(),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default),
                Task.FromResult(new Metadata()),
                static () => Status.DefaultSuccess,
                static () => [],
                static () => { });

        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method,
            string? host,
            CallOptions options,
            TRequest request)
        {
            options.CancellationToken.ThrowIfCancellationRequested();
            var batch = Assert.IsType<BatchGetDocumentsRequest>(request);
            List<TResponse> responses;
            lock (_lock)
            {
                var readTime = Now();
                responses = [.. batch.Documents.Select(name => (TResponse)(object)(_documents.TryGetValue(name, out var document)
                    ? new BatchGetDocumentsResponse { Found = document.Clone(), ReadTime = readTime }
                    : new BatchGetDocumentsResponse { Missing = name, ReadTime = readTime }))];
            }

            return new(new Reader<TResponse>(responses), Task.FromResult(new Metadata()), static () => Status.DefaultSuccess, static () => [], static () => { });
        }

        public override TResponse BlockingUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
            throw new NotSupportedException(method.FullName);

        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options) =>
            throw new NotSupportedException(method.FullName);

        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options) =>
            throw new NotSupportedException(method.FullName);

        /// <summary>
        /// Applies one write under its precondition, as the service does.
        /// </summary>
        /// <exception cref="RpcException">The precondition fails.</exception>
        internal V1.WriteResult Apply(V1.Write write)
        {
            lock (_lock)
            {
                var name = write.Update.Name;
                var exists = _documents.TryGetValue(name, out var current);
                var precondition = write.CurrentDocument;
                if (precondition?.ConditionTypeCase == V1.Precondition.ConditionTypeOneofCase.Exists && precondition.Exists != exists)
                {
                    throw new RpcException(new Status(exists ? StatusCode.AlreadyExists : StatusCode.NotFound, "The precondition failed."));
                }

                if (precondition?.ConditionTypeCase == V1.Precondition.ConditionTypeOneofCase.UpdateTime)
                {
                    if (current is null)
                    {
                        throw new RpcException(new Status(StatusCode.NotFound, "The document does not exist."));
                    }

                    if (!current.UpdateTime.Equals(precondition.UpdateTime))
                    {
                        throw new RpcException(new Status(StatusCode.FailedPrecondition, "The document's update time differs."));
                    }
                }

                var next = current?.Clone() ?? new Document { Name = name };
                if (write.UpdateMask is { } mask)
                {
                    foreach (var path in mask.FieldPaths)
                    {
                        next.Fields[path] = write.Update.Fields[path];
                    }
                }
                else
                {
                    next.Fields.Clear();
                    next.Fields.Add(write.Update.Fields);
                }

                if (current is not null && current.Fields.Equals(next.Fields))
                {
                    // The service leaves the update time of a document which a write did not change.
                    return new V1.WriteResult { UpdateTime = current.UpdateTime };
                }

                var time = Now();
                next.CreateTime = current?.CreateTime ?? time;
                next.UpdateTime = time;
                _documents[name] = next;
                return new V1.WriteResult { UpdateTime = time };
            }
        }

        private async Task<CommitResponse> CommitAsync(CommitRequest request, CancellationToken cancellationToken)
        {
            var write = Assert.Single(request.Writes);
            if (Interlocked.Exchange(ref _conflict, null) is { } conflict)
            {
                await conflict.ConflictAfterCancellationAsync(
                    new RpcException(new Status(StatusCode.FailedPrecondition, "The document's update time differs.")),
                    cancellationToken);
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Interlocked.Exchange(ref _hold, null) is { } hold)
                {
                    _ = hold.LandAsync(() => _ = Apply(write));
                    await Task.Delay(Timeout.InfiniteTimeSpan, TimeProvider.System, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                throw new RpcException(new Status(StatusCode.Cancelled, "The call was cancelled."));
            }

            var result = Apply(write);
            return new CommitResponse { CommitTime = result.UpdateTime, WriteResults = { result } };
        }

        private ProtobufTimestamp Now()
        {
            var ticks = Interlocked.Increment(ref _ticks);
            return new ProtobufTimestamp { Seconds = 1_700_000_000 + ticks / 1_000_000, Nanos = (int)(ticks % 1_000_000 * 1_000) };
        }

        private sealed class Reader<T>(IReadOnlyList<T> responses) : IAsyncStreamReader<T>
        {
            private int _next = -1;

            public T Current => responses[_next];

            public Task<bool> MoveNext(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(++_next < responses.Count);
            }
        }
    }
}
