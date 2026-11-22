using System.Buffers;
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using Azure;
using Azure.Core;
using Azure.Data.Tables;
using Azure.Data.Tables.Models;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Orleans.Durable.Storage.AzureStorage.MetaStore;
using Orleans.Durable.Storage.S3.MetaStore;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.MetaStore.Tests;

public sealed class AzureTableMetaStoreTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    [Fact]
    public Task EmptyStoreReturnsNull() =>
        ConditionalRegisterContract.EmptyStoreReturnsNull(CreateRegister());

    [Fact]
    public Task NullVersionCreatesOnlyWhenAbsent() =>
        ConditionalRegisterContract.NullVersionCreatesOnlyWhenAbsent(CreateRegister());

    [Fact]
    public Task EmptyValueReadsAsPresent() =>
        ConditionalRegisterContract.EmptyValueReadsAsPresent(CreateRegister());

    [Fact]
    public Task NonNullVersionDoesNotCreateMissingRegister() =>
        ConditionalRegisterContract.NonNullVersionDoesNotCreateMissingRegister(CreateRegister());

    [Fact]
    public Task MatchingVersionUpdatesAndChangesVersion() =>
        ConditionalRegisterContract.MatchingVersionUpdatesAndChangesVersion(CreateRegister());

    [Fact]
    public Task StaleVersionDoesNotOverwriteValue() =>
        ConditionalRegisterContract.StaleVersionDoesNotOverwriteValue(CreateRegister());

    [Fact]
    public Task AnAppliedWriteReturnsTheRegistersNewVersion() =>
        ConditionalRegisterContract.AnAppliedWriteReturnsTheRegistersNewVersion(CreateRegister());

    [Fact]
    public Task ValuesAreCopiedOnWriteAndEntriesKeepTheirValues() =>
        ConditionalRegisterContract.ValuesAreCopiedOnWriteAndEntriesKeepTheirValues(CreateRegister());

    [Fact]
    public Task ConcurrentCreatesHaveOneWinner() =>
        ConditionalRegisterContract.ConcurrentCreatesHaveOneWinner(CreateRegister());

    [Fact]
    public Task ConcurrentWritesWithOneVersionHaveOneWinner() =>
        ConditionalRegisterContract.ConcurrentWritesWithOneVersionHaveOneWinner(CreateRegister());

    [Fact]
    public Task CancellationIsObserved() =>
        ConditionalRegisterContract.CancellationIsObserved(CreateRegister());

    [Fact]
    public Task AConflictAnsweredAfterCancellationThrows()
    {
        var (store, client) = CreateStoreWithClient();
        return ConditionalRegisterContract.AConflictAnsweredAfterCancellationThrows(store, client.ConflictNextWriteAfterCancellation);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public Task AWriteCancelledMidCallLandsItsOwnCopy(int pages)
    {
        var (store, client) = CreateStoreWithClient();
        return ConditionalRegisterContract.AWriteCancelledMidCallLandsItsOwnCopy(store, client.HoldNextWrite, pages, lands: true);
    }

    [Fact]
    public async Task VersionIsTheNativeEtag()
    {
        var (store, client) = CreateStoreWithClient();
        client.SeedRegister(store, "\"backend\"", [1]);

        using var entry = Assert.IsType<MetaStoreEntry>(
            await store.ReadAsync(TestContext.Current.CancellationToken));

        Assert.Equal("\"backend\"", entry.Version.Value);
        var applied = (await store.WriteAsync(entry.Version, [2], TestContext.Current.CancellationToken)).AppliedVersion();
        Assert.Equal("\"1\"", applied.Value);
    }

    [Fact]
    public async Task StoreAddressesTheRowOfTheRegisterKey()
    {
        var client = new FakeTableClient();
        var store = new AzureTableMetaStore(client, "partition", "prefix-");
        var cluster = new Guid("0123456789abcdef0123456789abcdef");

        var register = Assert.IsType<AzureTableConditionalRegister>(
            await store.GetRegisterAsync(cluster, "l/orders", TestContext.Current.CancellationToken));
        Assert.True((await register.WriteAsync(null, [1], TestContext.Current.CancellationToken)).IsApplied());

        Assert.Equal("partition", register.PartitionKey);
        Assert.Equal("prefix-0123456789abcdef0123456789abcdef:l:orders", register.RowKey);
        Assert.True(client.Contains("partition", "prefix-0123456789abcdef0123456789abcdef:l:orders"));
    }

    public void Dispose() => _leaks.Dispose();

    private static AzureTableConditionalRegister CreateRegister() =>
        CreateStoreWithClient().Store;

    private static (AzureTableConditionalRegister Store, FakeTableClient Client) CreateStoreWithClient()
    {
        var client = new FakeTableClient();
        return (new AzureTableConditionalRegister(client, "partition", "register"), client);
    }

    private sealed class FakeTableClient : TableClient
    {
        private readonly Dictionary<(string PartitionKey, string RowKey), TableEntity> _entities = [];
        private WriteHold? _hold;
        private WriteHold? _conflict;
        private long _version;

        public override async Task<Response> AddEntityAsync<T>(
            T entity,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tableEntity = Assert.IsType<TableEntity>(entity);
            await HoldAsync(() => Add(tableEntity), cancellationToken);
            return Add(tableEntity);
        }

        public override Task<NullableResponse<T>> GetEntityIfExistsAsync<T>(
            string partitionKey,
            string rowKey,
            IEnumerable<string>? select = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_entities)
            {
                if (!_entities.TryGetValue((partitionKey, rowKey), out var entity))
                {
                    return Task.FromResult<NullableResponse<T>>(new MissingResponse<T>());
                }

                return Task.FromResult<NullableResponse<T>>(
                    Response.FromValue((T)(object)Clone(entity), FakeResponse.Ok));
            }
        }

        public override async Task<Response> UpdateEntityAsync<T>(
            T entity,
            ETag ifMatch,
            TableUpdateMode mode = TableUpdateMode.Merge,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(TableUpdateMode.Replace, mode);
            var tableEntity = Assert.IsType<TableEntity>(entity);
            if (Interlocked.Exchange(ref _conflict, null) is { } conflict)
            {
                await conflict.ConflictAfterCancellationAsync(CreateException(412, TableErrorCode.UpdateConditionNotSatisfied), cancellationToken);
            }

            await HoldAsync(() => Update(tableEntity, ifMatch), cancellationToken);
            return Update(tableEntity, ifMatch);
        }

        /// <summary>
        /// Holds the next write in flight: its caller waits until its token is cancelled, and the request lands, with
        /// the entity the register passed, once the test releases the hold.
        /// </summary>
        internal void HoldNextWrite(WriteHold hold) => _hold = hold;

        /// <summary>
        /// Holds the next replace until its caller's token is cancelled, and then answers it with the service's conflict.
        /// </summary>
        internal void ConflictNextWriteAfterCancellation(WriteHold hold) => _conflict = hold;

        internal void SeedRegister(
            AzureTableConditionalRegister register,
            string etag,
            byte[] value)
        {
            var entity = AzureTableConditionalRegister.CreateEntity(register.PartitionKey, register.RowKey, new ReadOnlySequence<byte>(value));
            entity.ETag = new ETag(etag);
            _entities[(register.PartitionKey, register.RowKey)] = entity;
        }

        internal bool Contains(string partitionKey, string rowKey)
        {
            lock (_entities)
            {
                return _entities.ContainsKey((partitionKey, rowKey));
            }
        }

        private static RequestFailedException CreateException(int status, TableErrorCode errorCode) =>
            new(status, "simulated conflict", errorCode.ToString(), null);

        private async Task HoldAsync(Func<Response> land, CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _hold, null) is not { } hold)
            {
                return;
            }

            _ = hold.LandAsync(() => _ = land());
            await Task.Delay(Timeout.InfiniteTimeSpan, TimeProvider.System, cancellationToken);
        }

        private Response Add(TableEntity entity)
        {
            lock (_entities)
            {
                var key = (entity.PartitionKey, entity.RowKey);
                if (_entities.ContainsKey(key))
                {
                    throw CreateException(409, TableErrorCode.EntityAlreadyExists);
                }

                var stored = Clone(entity);
                stored.ETag = CreateEtag();
                _entities.Add(key, stored);
                return new FakeResponse(204, stored.ETag.ToString());
            }
        }

        private Response Update(TableEntity entity, ETag ifMatch)
        {
            lock (_entities)
            {
                var key = (entity.PartitionKey, entity.RowKey);
                if (!_entities.TryGetValue(key, out var current))
                {
                    throw CreateException(404, TableErrorCode.ResourceNotFound);
                }

                if (current.ETag != ifMatch)
                {
                    throw CreateException(412, TableErrorCode.UpdateConditionNotSatisfied);
                }

                var stored = Clone(entity);
                stored.ETag = CreateEtag();
                _entities[key] = stored;
                return new FakeResponse(204, stored.ETag.ToString());
            }
        }

        private static TableEntity Clone(TableEntity source)
        {
            var clone = new TableEntity(source.PartitionKey, source.RowKey)
            {
                ETag = source.ETag,
            };
            foreach (var pair in source)
            {
                clone[pair.Key] = pair.Value is byte[] bytes ? bytes.ToArray() : pair.Value;
            }

            return clone;
        }

        private ETag CreateEtag() =>
            new($"\"{(++_version).ToString(CultureInfo.InvariantCulture)}\"");
    }
}

public sealed class AzureBlobMetaStoreTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    [Fact]
    public Task EmptyStoreReturnsNull() =>
        ConditionalRegisterContract.EmptyStoreReturnsNull(CreateRegister());

    [Fact]
    public Task NullVersionCreatesOnlyWhenAbsent() =>
        ConditionalRegisterContract.NullVersionCreatesOnlyWhenAbsent(CreateRegister());

    [Fact]
    public Task EmptyValueReadsAsPresent() =>
        ConditionalRegisterContract.EmptyValueReadsAsPresent(CreateRegister());

    [Fact]
    public Task NonNullVersionDoesNotCreateMissingRegister() =>
        ConditionalRegisterContract.NonNullVersionDoesNotCreateMissingRegister(CreateRegister());

    [Fact]
    public Task MatchingVersionUpdatesAndChangesVersion() =>
        ConditionalRegisterContract.MatchingVersionUpdatesAndChangesVersion(CreateRegister());

    [Fact]
    public Task StaleVersionDoesNotOverwriteValue() =>
        ConditionalRegisterContract.StaleVersionDoesNotOverwriteValue(CreateRegister());

    [Fact]
    public Task AnAppliedWriteReturnsTheRegistersNewVersion() =>
        ConditionalRegisterContract.AnAppliedWriteReturnsTheRegistersNewVersion(CreateRegister());

    [Fact]
    public Task ValuesAreCopiedOnWriteAndEntriesKeepTheirValues() =>
        ConditionalRegisterContract.ValuesAreCopiedOnWriteAndEntriesKeepTheirValues(CreateRegister());

    [Fact]
    public Task ConcurrentCreatesHaveOneWinner() =>
        ConditionalRegisterContract.ConcurrentCreatesHaveOneWinner(CreateRegister());

    [Fact]
    public Task ConcurrentWritesWithOneVersionHaveOneWinner() =>
        ConditionalRegisterContract.ConcurrentWritesWithOneVersionHaveOneWinner(CreateRegister());

    [Fact]
    public Task CancellationIsObserved() =>
        ConditionalRegisterContract.CancellationIsObserved(CreateRegister());

    [Fact]
    public Task AConflictAnsweredAfterCancellationThrows()
    {
        var (store, container) = CreateStoreWithContainer();
        return ConditionalRegisterContract.AConflictAnsweredAfterCancellationThrows(
            store,
            hold => container.ConflictNextWriteAfterCancellation(store.BlobName, hold));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public Task AWriteCancelledMidCallLandsItsOwnCopy(int pages)
    {
        var (store, container) = CreateStoreWithContainer();
        return ConditionalRegisterContract.AWriteCancelledMidCallLandsItsOwnCopy(
            store,
            hold => container.HoldNextWrite(store.BlobName, hold),
            pages,
            lands: true);
    }

    [Fact]
    public async Task VersionIsTheNativeEtag()
    {
        var (store, container) = CreateStoreWithContainer();
        container.SeedRegister(store.BlobName, new ETag("\"backend\""), [1]);

        using var entry = Assert.IsType<MetaStoreEntry>(
            await store.ReadAsync(TestContext.Current.CancellationToken));

        Assert.Equal("\"backend\"", entry.Version.Value);
        var applied = (await store.WriteAsync(entry.Version, [2], TestContext.Current.CancellationToken)).AppliedVersion();
        Assert.Equal("\"1\"", applied.Value);
    }

    [Fact]
    public async Task StoreAddressesTheBlobOfTheRegisterKey()
    {
        var container = new FakeBlobContainerClient();
        var store = new AzureBlobMetaStore(container, "registers/");
        var cluster = new Guid("0123456789abcdef0123456789abcdef");

        var register = Assert.IsType<AzureBlobConditionalRegister>(
            await store.GetRegisterAsync(cluster, "l/orders", TestContext.Current.CancellationToken));
        Assert.True((await register.WriteAsync(null, [1], TestContext.Current.CancellationToken)).IsApplied());

        Assert.Equal(["registers/0123456789abcdef0123456789abcdef/l/orders"], container.WrittenBlobNames);
    }

    [Fact]
    public void StoreRejectsAPrefixWhichDoesNotEndInASlash() =>
        Assert.Throws<ArgumentException>(() => new AzureBlobMetaStore(new FakeBlobContainerClient(), "registers"));

    public void Dispose() => _leaks.Dispose();

    private static AzureBlobConditionalRegister CreateRegister() =>
        CreateStoreWithContainer().Store;

    private static (AzureBlobConditionalRegister Store, FakeBlobContainerClient Container) CreateStoreWithContainer()
    {
        var container = new FakeBlobContainerClient();
        return (new AzureBlobConditionalRegister(container.GetBlobClient("register")), container);
    }

    private sealed class FakeBlobContainerClient : BlobContainerClient
    {
        private readonly Dictionary<string, FakeBlobClient> _blobs = [];

        public override BlobClient GetBlobClient(string blobName)
        {
            lock (_blobs)
            {
                if (!_blobs.TryGetValue(blobName, out var client))
                {
                    client = new FakeBlobClient(blobName);
                    _blobs.Add(blobName, client);
                }

                return client;
            }
        }

        internal string[] WrittenBlobNames
        {
            get
            {
                lock (_blobs)
                {
                    return [.. _blobs.Where(static pair => pair.Value.HasContent).Select(static pair => pair.Key)];
                }
            }
        }

        internal void SeedRegister(
            string blobName,
            ETag etag,
            byte[] value) =>
            GetFakeBlob(blobName).Seed(etag, value);

        /// <summary>
        /// Holds the blob's next upload in flight: its caller waits until its token is cancelled, and the upload lands,
        /// with the content the register passed, once the test releases the hold.
        /// </summary>
        internal void HoldNextWrite(string blobName, WriteHold hold) => GetFakeBlob(blobName).HoldNextWrite(hold);

        /// <summary>
        /// Holds the blob's next upload until its caller's token is cancelled, and then answers it with the service's
        /// conflict.
        /// </summary>
        internal void ConflictNextWriteAfterCancellation(string blobName, WriteHold hold) =>
            GetFakeBlob(blobName).ConflictNextWriteAfterCancellation(hold);

        private FakeBlobClient GetFakeBlob(string blobName) =>
            (FakeBlobClient)GetBlobClient(blobName);
    }

    private sealed class FakeBlobClient(string name) : BlobClient
    {
        private readonly Lock _lock = new();
        private byte[]? _content;
        private ETag _etag;
        private Dictionary<string, string> _metadata = [];
        private WriteHold? _hold;
        private WriteHold? _conflict;
        private long _version;

        public override Task<Response<BlobDownloadResult>> DownloadContentAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_lock)
            {
                if (_content is null)
                {
                    throw CreateException(404, BlobErrorCode.BlobNotFound);
                }

                var details = BlobsModelFactory.BlobDownloadDetails(
                    blobType: BlobType.Block,
                    contentLength: _content.Length,
                    lastModified: DateTimeOffset.UnixEpoch,
                    metadata: new Dictionary<string, string>(_metadata, StringComparer.Ordinal),
                    eTag: _etag);
                var result = BlobsModelFactory.BlobDownloadResult(
                    BinaryData.FromBytes([.. _content]),
                    details);
                return Task.FromResult(Response.FromValue(result, FakeResponse.Ok));
            }
        }

        public override async Task<Response<BlobContentInfo>> UploadAsync(
            BinaryData content,
            BlobUploadOptions options,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Exchange(ref _conflict, null) is { } conflict)
            {
                await conflict.ConflictAfterCancellationAsync(CreateException(412, BlobErrorCode.ConditionNotMet), cancellationToken);
            }

            if (Interlocked.Exchange(ref _hold, null) is { } hold)
            {
                _ = hold.LandAsync(() => _ = UploadContent(content, options));
                await Task.Delay(Timeout.InfiniteTimeSpan, TimeProvider.System, cancellationToken);
            }

            return UploadContent(content, options);
        }

        public override string Name => name;

        internal bool HasContent
        {
            get
            {
                lock (_lock)
                {
                    return _content is not null;
                }
            }
        }

        internal void HoldNextWrite(WriteHold hold) => _hold = hold;

        internal void ConflictNextWriteAfterCancellation(WriteHold hold) => _conflict = hold;

        internal void Seed(ETag etag, byte[] content)
        {
            _etag = etag;
            _content = [.. content];
            _metadata = [];
        }

        private static RequestFailedException CreateException(int status, BlobErrorCode errorCode) =>
            new(status, "simulated conflict", errorCode.ToString(), null);

        private Response<BlobContentInfo> UploadContent(BinaryData content, BlobUploadOptions options)
        {
            lock (_lock)
            {
                if (options.Conditions?.IfNoneMatch == ETag.All && _content is not null)
                {
                    throw CreateException(409, BlobErrorCode.BlobAlreadyExists);
                }

                if (options.Conditions?.IfMatch is { } ifMatch)
                {
                    if (_content is null)
                    {
                        throw CreateException(404, BlobErrorCode.BlobNotFound);
                    }

                    if (_etag != ifMatch)
                    {
                        throw CreateException(412, BlobErrorCode.ConditionNotMet);
                    }
                }

                Store(content.ToArray(), options.Metadata);
                var result = BlobsModelFactory.BlobContentInfo(
                    _etag,
                    DateTimeOffset.UnixEpoch,
                    contentHash: null,
                    encryptionKeySha256: null,
                    blobSequenceNumber: 0);
                return Response.FromValue(result, FakeResponse.Ok);
            }
        }

        private void Store(byte[] content, IDictionary<string, string>? metadata)
        {
            _content = [.. content];
            _metadata = metadata is null
                ? []
                : metadata.ToDictionary(
                    static pair => pair.Key,
                    static pair => pair.Value,
                    StringComparer.Ordinal);
            _etag = new ETag($"\"{(++_version).ToString(CultureInfo.InvariantCulture)}\"");
        }
    }
}

public sealed class DynamoDbMetaStoreTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    [Fact]
    public Task EmptyStoreReturnsNull() =>
        ConditionalRegisterContract.EmptyStoreReturnsNull(CreateRegister());

    [Fact]
    public Task NullVersionCreatesOnlyWhenAbsent() =>
        ConditionalRegisterContract.NullVersionCreatesOnlyWhenAbsent(CreateRegister());

    [Fact]
    public Task EmptyValueReadsAsPresent() =>
        ConditionalRegisterContract.EmptyValueReadsAsPresent(CreateRegister());

    [Fact]
    public Task NonNullVersionDoesNotCreateMissingRegister() =>
        ConditionalRegisterContract.NonNullVersionDoesNotCreateMissingRegister(CreateRegister());

    [Fact]
    public Task MatchingVersionUpdatesAndChangesVersion() =>
        ConditionalRegisterContract.MatchingVersionUpdatesAndChangesVersion(CreateRegister());

    [Fact]
    public Task StaleVersionDoesNotOverwriteValue() =>
        ConditionalRegisterContract.StaleVersionDoesNotOverwriteValue(CreateRegister());

    [Fact]
    public Task AnAppliedWriteReturnsTheRegistersNewVersion() =>
        ConditionalRegisterContract.AnAppliedWriteReturnsTheRegistersNewVersion(CreateRegister());

    [Fact]
    public Task ValuesAreCopiedOnWriteAndEntriesKeepTheirValues() =>
        ConditionalRegisterContract.ValuesAreCopiedOnWriteAndEntriesKeepTheirValues(CreateRegister());

    [Fact]
    public Task ConcurrentCreatesHaveOneWinner() =>
        ConditionalRegisterContract.ConcurrentCreatesHaveOneWinner(CreateRegister());

    [Fact]
    public Task ConcurrentWritesWithOneVersionHaveOneWinner() =>
        ConditionalRegisterContract.ConcurrentWritesWithOneVersionHaveOneWinner(CreateRegister());

    [Fact]
    public Task CancellationIsObserved() =>
        ConditionalRegisterContract.CancellationIsObserved(CreateRegister());

    [Fact]
    public Task AConflictAnsweredAfterCancellationThrows()
    {
        var (store, client) = CreateStoreWithClient();
        return ConditionalRegisterContract.AConflictAnsweredAfterCancellationThrows(store, client.ConflictNextWriteAfterCancellation);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public Task AWriteCancelledMidCallLandsItsOwnCopy(int pages)
    {
        var (store, client) = CreateStoreWithClient();
        return ConditionalRegisterContract.AWriteCancelledMidCallLandsItsOwnCopy(store, client.HoldNextWrite, pages, lands: true);
    }

    [Fact]
    public async Task VersionIsTheNativeVersionAttribute()
    {
        var (store, client) = CreateStoreWithClient();
        client.SeedRegister(store.PartitionKey, "backend", [1]);

        using var entry = Assert.IsType<MetaStoreEntry>(
            await store.ReadAsync(TestContext.Current.CancellationToken));

        Assert.Equal("backend", entry.Version.Value);
        var applied = (await store.WriteAsync(entry.Version, [2], TestContext.Current.CancellationToken)).AppliedVersion();
        using var written = Assert.IsType<MetaStoreEntry>(await store.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(written.Version, applied);
        Assert.True(Guid.TryParseExact(applied.Value, "N", out _));
    }

    [Fact]
    public async Task ConditionalFailureAfterAnAppliedAttemptIsReportedAsAConflict()
    {
        var (store, client) = CreateStoreWithClient();
        client.FailAfterNextRegisterWrite = true;

        // The register does not disambiguate: its caller re-reads the register and tests its goal (MS-I3).
        Assert.False(
            (await store.WriteAsync(
                null,
                [1],
                TestContext.Current.CancellationToken)).IsApplied());

        using var entry = Assert.IsType<MetaStoreEntry>(
            await store.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(new byte[] { 1 }, entry.Value.ToArray());
    }

    [Fact]
    public async Task StoreAddressesTheItemOfTheRegisterKey()
    {
        using var client = new FakeDynamoDbClient();
        var store = new DynamoDbMetaStore(client, "table", "prefix#");
        var cluster = new Guid("0123456789abcdef0123456789abcdef");

        var register = Assert.IsType<DynamoDbConditionalRegister>(
            await store.GetRegisterAsync(cluster, "l/orders", TestContext.Current.CancellationToken));
        Assert.True((await register.WriteAsync(null, [1], TestContext.Current.CancellationToken)).IsApplied());

        Assert.Equal("table", register.TableName);
        Assert.Equal(["prefix#0123456789abcdef0123456789abcdef/l/orders"], client.Keys);
    }

    public void Dispose() => _leaks.Dispose();

    private static DynamoDbConditionalRegister CreateRegister() =>
        CreateStoreWithClient().Store;

    private static (DynamoDbConditionalRegister Store, FakeDynamoDbClient Client) CreateStoreWithClient()
    {
        var client = new FakeDynamoDbClient();
        return (new DynamoDbConditionalRegister(client, "table", "register"), client);
    }

    private sealed class FakeDynamoDbClient()
        : AmazonDynamoDBClient(
            new AnonymousAWSCredentials(),
            new AmazonDynamoDBConfig { ServiceURL = "https://localhost.invalid" })
    {
        private readonly Dictionary<string, Dictionary<string, AttributeValue>> _items = [];
        private WriteHold? _hold;
        private WriteHold? _conflict;

        internal bool FailAfterNextRegisterWrite { get; set; }

        /// <summary>
        /// Holds the next put in flight: its caller waits until its token is cancelled, and the put lands, with the item
        /// the register passed, once the test releases the hold.
        /// </summary>
        internal void HoldNextWrite(WriteHold hold) => _hold = hold;

        /// <summary>
        /// Holds the next put until its caller's token is cancelled, and then answers it with the service's conflict.
        /// </summary>
        internal void ConflictNextWriteAfterCancellation(WriteHold hold) => _conflict = hold;

        public override Task<GetItemResponse> GetItemAsync(
            GetItemRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = request.Key["Id"].S;
            lock (_items)
            {
                return Task.FromResult(
                    _items.TryGetValue(key, out var item)
                        ? new GetItemResponse { Item = Clone(item) }
                        : new GetItemResponse());
            }
        }

        public override async Task<PutItemResponse> PutItemAsync(
            PutItemRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Exchange(ref _conflict, null) is { } conflict)
            {
                await conflict.ConflictAfterCancellationAsync(new ConditionalCheckFailedException("simulated conflict"), cancellationToken);
            }

            if (Interlocked.Exchange(ref _hold, null) is { } hold)
            {
                _ = hold.LandAsync(() => _ = Put(request));
                await Task.Delay(Timeout.InfiniteTimeSpan, TimeProvider.System, cancellationToken);
            }

            return Put(request);
        }

        private PutItemResponse Put(PutItemRequest request)
        {
            var key = request.Item["Id"].S;
            lock (_items)
            {
                if (string.Equals(
                    request.ConditionExpression,
                    "attribute_not_exists(#key)",
                    StringComparison.Ordinal))
                {
                    if (_items.ContainsKey(key))
                    {
                        throw new ConditionalCheckFailedException("simulated conflict");
                    }
                }
                else
                {
                    var expectedVersion = request.ExpressionAttributeValues[":expectedVersion"].S;
                    if (!_items.TryGetValue(key, out var current)
                        || !string.Equals(
                            current["Version"].S,
                            expectedVersion,
                            StringComparison.Ordinal))
                    {
                        throw new ConditionalCheckFailedException("simulated conflict");
                    }
                }

                _items[key] = Clone(request.Item);
                if (FailAfterNextRegisterWrite)
                {
                    FailAfterNextRegisterWrite = false;
                    throw new ConditionalCheckFailedException("simulated ambiguous result");
                }

                return new PutItemResponse();
            }
        }

        internal string[] Keys
        {
            get
            {
                lock (_items)
                {
                    return [.. _items.Keys];
                }
            }
        }

        internal void SeedRegister(
            string partitionKey,
            string version,
            byte[] value)
        {
            _items[partitionKey] = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
            {
                ["Id"] = new AttributeValue { S = partitionKey },
                ["Version"] = new AttributeValue { S = version },
                ["Value"] = new AttributeValue
                {
                    B = new MemoryStream([.. value], writable: false),
                },
            };
        }

        private static Dictionary<string, AttributeValue> Clone(
            IReadOnlyDictionary<string, AttributeValue> source) =>
            source.ToDictionary(
                static pair => pair.Key,
                static pair => new AttributeValue
                {
                    S = pair.Value.S,
                    B = pair.Value.B is null
                        ? null
                        : new MemoryStream(pair.Value.B.ToArray(), writable: false),
                },
                StringComparer.Ordinal);
    }
}

internal sealed class MissingResponse<T> : NullableResponse<T>
{
    public override bool HasValue => false;

    public override T Value => throw new InvalidOperationException("The response has no value.");

    public override Response GetRawResponse() => FakeResponse.NotFound;
}

internal sealed class FakeResponse(int status, string? etag = null) : Response
{
    internal static FakeResponse NoContent { get; } = new(204);

    internal static FakeResponse NotFound { get; } = new(404);

    internal static FakeResponse Ok { get; } = new(200);

    public override int Status { get; } = status;

    public override string ReasonPhrase => string.Empty;

    public override Stream? ContentStream { get; set; }

    public override string ClientRequestId { get; set; } = string.Empty;

    public override void Dispose()
    {
    }

    protected override bool ContainsHeader(string name) => TryGetHeader(name, out _);

    protected override IEnumerable<HttpHeader> EnumerateHeaders() =>
        etag is null ? [] : [new HttpHeader(HttpHeader.Names.ETag, etag)];

    protected override bool TryGetHeader(string name, out string value)
    {
        value = etag ?? string.Empty;
        return etag is not null && string.Equals(name, HttpHeader.Names.ETag, StringComparison.OrdinalIgnoreCase);
    }

    protected override bool TryGetHeaderValues(string name, out IEnumerable<string> values)
    {
        var found = TryGetHeader(name, out var value);
        values = found ? [value] : [];
        return found;
    }
}
