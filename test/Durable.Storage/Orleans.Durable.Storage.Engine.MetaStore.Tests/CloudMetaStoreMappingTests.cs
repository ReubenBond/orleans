using System.Buffers;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using Azure;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Google.Cloud.Firestore;
using Grpc.Core;
using Orleans.Durable.Storage.AzureStorage;
using Orleans.Durable.Storage.AzureStorage.MetaStore;
using Orleans.Durable.Storage.GoogleCloud.MetaStore;
using Orleans.Durable.Storage.S3.MetaStore;
using Orleans.Durable.Storage.Testing;
using ProtobufTimestamp = Google.Protobuf.WellKnownTypes.Timestamp;

namespace Orleans.Durable.Storage.Engine.MetaStore.Tests;

public sealed class CloudMetaStoreMappingTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    [Fact]
    public void AzureTableEntityMapsKeysAndCopiesValues()
    {
        byte[] source = [1, 2, 3];

        var entity = AzureTableConditionalRegister.CreateEntity("partition", "row", new ReadOnlySequence<byte>(source));
        var keys = entity.Keys.Order(StringComparer.Ordinal).ToArray();
        source[0] = 9;
        entity.ETag = new ETag("\"etag\"");
        using var entry = AzureTableConditionalRegister.MapEntity(entity);

        Assert.Equal("partition", entity.PartitionKey);
        Assert.Equal("row", entity.RowKey);
        Assert.Equal(new byte[] { 1, 2, 3 }, Assert.IsType<byte[]>(entity["Value"]));
        Assert.Equal(["PartitionKey", "RowKey", "Value"], keys);
        Assert.Equal("\"etag\"", entry.Version.Value);
        Assert.Equal(new byte[] { 1, 2, 3 }, entry.Value.ToArray());
    }

    /// <summary>
    /// An applied write's version is the ETag the service answers with, in the form an entity read carries, for the
    /// service's weak ETags and for strong ones; an answer without one leaves the write's outcome unknown.
    /// </summary>
    [Fact]
    public void AzureTableAppliedVersionIsTheAnswersEtag()
    {
        const string Weak = "W/\"datetime'2026-11-12T08%3A00%3A00.1234567Z'\"";
        using var weak = new FakeResponse(204, Weak);
        Assert.Equal(Weak, AzureTableConditionalRegister.MapAppliedVersion(weak).Value);
        using var applied = new FakeResponse(204, "\"applied\"");
        Assert.Equal("\"applied\"", AzureTableConditionalRegister.MapAppliedVersion(applied).Value);
        _ = Assert.Throws<IOException>(() => AzureTableConditionalRegister.MapAppliedVersion(FakeResponse.NoContent));

        var entity = AzureTableConditionalRegister.CreateEntity("partition", "row", new ReadOnlySequence<byte>([1]));
        entity.ETag = new ETag(Weak);
        using var read = AzureTableConditionalRegister.MapEntity(entity);
        Assert.Equal(Weak, read.Version.Value);
    }

    /// <summary>
    /// An applied upload's version is its ETag, formatted as a download's is; an answer without one leaves the write's
    /// outcome unknown.
    /// </summary>
    [Fact]
    public void AzureBlobAppliedVersionIsTheUploadsEtag()
    {
        var etag = new ETag("\"0x8DCB1\"");
        using var read = AzureBlobConditionalRegister.MapEntry(BinaryData.FromBytes([7]), etag);
        Assert.Equal(read.Version.Value, AzureBlobRequests.AppliedETag(etag));
        _ = Assert.Throws<IOException>(() => AzureBlobRequests.AppliedETag(default));
    }

    [Fact]
    public async Task AzureBlobStoreMapsBlobAndConditions()
    {
        var container = new BlobContainerClient(new Uri("https://account.example/container"));
        var store = new AzureBlobMetaStore(container, "metadata/");

        var register = Assert.IsType<AzureBlobConditionalRegister>(
            await store.GetRegisterAsync(
                new Guid("0123456789abcdef0123456789abcdef"),
                "register",
                TestContext.Current.CancellationToken));
        var blob = container.GetBlobClient(register.BlobName);
        var create = AzureBlobRequests.ConditionsFor(null);
        var update = AzureBlobRequests.ConditionsFor("\"etag\"");
        using var entry = AzureBlobConditionalRegister.MapEntry(BinaryData.FromBytes([7]), new ETag("\"read\""));

        Assert.Equal("/container/metadata/0123456789abcdef0123456789abcdef/register", blob.Uri.AbsolutePath);
        Assert.Equal(ETag.All, create.IfNoneMatch);
        Assert.Null(create.IfMatch);
        Assert.Equal(new ETag("\"etag\""), update.IfMatch);
        Assert.Null(update.IfNoneMatch);
        Assert.Equal("\"read\"", entry.Version.Value);
        Assert.Equal(new byte[] { 7 }, entry.Value.ToArray());
    }

    [Fact]
    public void DynamoDbOptionsMapConsistentReadKeyAndCreateCondition()
    {
        var read = DynamoDbConditionalRegister.CreateGetItemRequest("table", "register");
        var write = DynamoDbConditionalRegister.CreatePutItemRequest(
            "table",
            "register",
            null,
            "next",
            new ReadOnlySequence<byte>([1, 2, 3]));

        Assert.Equal("table", read.TableName);
        Assert.True(read.ConsistentRead);
        Assert.Equal("register", read.Key["Id"].S);
        Assert.Equal("attribute_not_exists(#key)", write.ConditionExpression);
        Assert.Equal("Id", write.ExpressionAttributeNames["#key"]);
        Assert.Equal("register", write.Item["Id"].S);
        Assert.Equal("next", write.Item["Version"].S);
        Assert.Equal(["Id", "Value", "Version"], write.Item.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(new byte[] { 1, 2, 3 }, write.Item["Value"].B.ToArray());
    }

    [Fact]
    public void DynamoDbUpdateMapsVersionConditionAndItem()
    {
        var request = DynamoDbConditionalRegister.CreatePutItemRequest(
            "table",
            "register",
            new MetaStoreVersion("expected"),
            "next",
            new ReadOnlySequence<byte>([4, 5]));

        Assert.Equal("#version = :expectedVersion", request.ConditionExpression);
        Assert.Equal("Version", request.ExpressionAttributeNames["#version"]);
        Assert.Equal("expected", request.ExpressionAttributeValues[":expectedVersion"].S);
        Assert.Equal("next", request.Item["Version"].S);
        Assert.Equal(new byte[] { 4, 5 }, request.Item["Value"].B.ToArray());
    }

    [Fact]
    public void DynamoDbItemMapsVersionAndCopiesValue()
    {
        using var source = new MemoryStream([6, 7], writable: false);
        var item = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
        {
            ["Version"] = new AttributeValue { S = "version" },
            ["Value"] = new AttributeValue { B = source },
        };

        using var entry = DynamoDbConditionalRegister.MapItem(item);

        Assert.Equal("version", entry.Version.Value);
        Assert.Equal(new byte[] { 6, 7 }, entry.Value.ToArray());
    }

    /// <summary>
    /// A read's entry wraps the response stream's buffer when the stream exposes it, within the stream's length.
    /// </summary>
    [Fact]
    public void DynamoDbItemWrapsAnExposedStreamBuffer()
    {
        using var source = new MemoryStream();
        source.Write([6, 7, 8]);
        var item = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
        {
            ["Version"] = new AttributeValue { S = "version" },
            ["Value"] = new AttributeValue { B = source },
        };

        using var entry = DynamoDbConditionalRegister.MapItem(item);

        Assert.True(source.TryGetBuffer(out var buffer));
        Assert.Same(buffer.Array, entry.Value.First.ReadableArraySegment.Array);
        Assert.Equal(new byte[] { 6, 7, 8 }, entry.Value.ToArray());
    }

    /// <summary>
    /// A write's document holds a copy of the value and a fresh write identifier, so a write of the same bytes still
    /// changes the document's update time, the register's version (MS-I5).
    /// </summary>
    [Fact]
    public void FirestoreDocumentDataCopiesValueAndChangesTheDocument()
    {
        byte[] source = [1, 2, 3];

        var data = FirestoreConditionalRegister.CreateDocumentData(new ReadOnlySequence<byte>(source));
        var updates = FirestoreConditionalRegister.CreateDocumentUpdates(new ReadOnlySequence<byte>(source));
        var again = FirestoreConditionalRegister.CreateDocumentUpdates(new ReadOnlySequence<byte>(source));
        source[0] = 9;

        Assert.Equal(["value", "write"], data.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(new byte[] { 1, 2, 3 }, Assert.IsType<byte[]>(data["value"]));
        Assert.Equal(new byte[] { 1, 2, 3 }, Assert.IsType<byte[]>(updates[new FieldPath("value")]));
        var writes = new[] { data["write"], updates[new FieldPath("write")], again[new FieldPath("write")] };
        Assert.All(writes, static write => Assert.True(Guid.TryParseExact(Assert.IsType<string>(write), "N", out _)));
        Assert.Equal(3, writes.Distinct().Count());
    }

    [Fact]
    public void FirestoreVersionMapsTimestampWithoutPrecisionLoss()
    {
        var timestamp = Timestamp.FromProto(
            new ProtobufTimestamp
            {
                Seconds = 1_234_567_890,
                Nanos = 123_456_789,
            });

        var version = FirestoreConditionalRegister.EncodeVersion(timestamp);
        var precondition = FirestoreConditionalRegister.TryCreatePrecondition(version);

        Assert.NotNull(precondition);
        Assert.Equal(timestamp, precondition.LastUpdateTime);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a/b")]
    public void FirestoreStoreRejectsInvalidCollections(string collection)
    {
        Assert.Throws<ArgumentException>(
            () => new FirestoreMetaStore(static _ => Task.FromException<FirestoreDb>(new InvalidOperationException()), collection));
    }

    [Fact]
    public void RegistersRejectMissingResourceIdentities()
    {
        var table = new TableClient(new Uri("https://account.example/table"));
        Assert.Throws<ArgumentException>(() => new AzureTableConditionalRegister(table, "partition", ""));
        Assert.Throws<ArgumentException>(() => new AzureTableMetaStore(table, ""));
        using var client = new AmazonDynamoDBStub();
        Assert.Throws<ArgumentException>(() => new DynamoDbMetaStore(client, ""));
    }

    [Fact]
    public async Task FirestoreCancelledRpcMapsOnlyLocallyRequestedCancellation()
    {
        var rpcException = new RpcException(new Status(StatusCode.Cancelled, "cancelled"));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        Assert.True(FirestoreConditionalRegister.IsLocallyRequestedCancellation(
            rpcException,
            cancellation.Token));
        Assert.False(FirestoreConditionalRegister.IsLocallyRequestedCancellation(
            rpcException,
            CancellationToken.None));

        var exception = FirestoreConditionalRegister.CreateCancellationException(
            rpcException,
            cancellation.Token);
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Same(rpcException, exception.InnerException);
    }

    [Theory]
    [InlineData(409, "EntityAlreadyExists", true, true)]
    [InlineData(412, "UpdateConditionNotSatisfied", false, true)]
    [InlineData(404, "ResourceNotFound", false, true)]
    [InlineData(404, "EntityNotFound", false, true)]
    [InlineData(409, "EntityAlreadyExists", false, false)]
    [InlineData(412, "Other", false, false)]
    [InlineData(500, "EntityAlreadyExists", true, false)]
    public void AzureTableVersionConflictRecognizesOnlyNativeCasStatuses(
        int status,
        string errorCode,
        bool isCreate,
        bool expected)
    {
        var exception = new RequestFailedException(status, "failure", errorCode, null);

        Assert.Equal(
            expected,
            AzureTableConditionalRegister.IsVersionConflict(exception, isCreate));
    }

    [Theory]
    [InlineData(412, "ConditionNotMet", true, true)]
    [InlineData(412, "ConditionNotMet", false, true)]
    [InlineData(409, "BlobAlreadyExists", true, true)]
    [InlineData(404, "BlobNotFound", false, true)]
    [InlineData(409, "BlobAlreadyExists", false, false)]
    [InlineData(404, "BlobNotFound", true, false)]
    [InlineData(412, "Other", false, false)]
    [InlineData(500, "ConditionNotMet", false, false)]
    public void AzureBlobVersionConflictRecognizesOnlyNativeCasStatuses(
        int status,
        string errorCode,
        bool isCreate,
        bool expected)
    {
        var exception = new RequestFailedException(status, "failure", errorCode, null);

        Assert.Equal(
            expected,
            AzureBlobRequests.IsConditionFailure(exception, isCreate));
    }

    [Theory]
    [InlineData(StatusCode.AlreadyExists, true, true)]
    [InlineData(StatusCode.FailedPrecondition, false, true)]
    [InlineData(StatusCode.NotFound, false, true)]
    [InlineData(StatusCode.AlreadyExists, false, false)]
    [InlineData(StatusCode.FailedPrecondition, true, false)]
    [InlineData(StatusCode.Internal, false, false)]
    public void FirestoreVersionConflictRecognizesOnlyNativeCasStatuses(
        StatusCode statusCode,
        bool isCreate,
        bool expected)
    {
        Assert.Equal(
            expected,
            FirestoreConditionalRegister.IsVersionConflict(statusCode, isCreate));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AzureTableMapEntityRejectsMissingOrNonBinaryValue(bool includeWrongType)
    {
        var entity = new TableEntity("partition", "row");
        if (includeWrongType)
        {
            entity["Value"] = "not-binary";
        }

        var exception = Assert.Throws<InvalidDataException>(
            () => AzureTableConditionalRegister.MapEntity(entity));

        Assert.Contains("Value", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void DynamoDbMapItemRejectsMissingRequiredMappingData(
        bool includeVersion,
        bool includeValue)
    {
        var item = new Dictionary<string, AttributeValue>(StringComparer.Ordinal);
        if (includeVersion)
        {
            item["Version"] = new AttributeValue { S = includeValue ? "version" : " " };
        }

        if (includeValue)
        {
            item["Value"] = new AttributeValue();
        }

        Assert.Throws<InvalidDataException>(
            () => DynamoDbConditionalRegister.MapItem(item));
    }

    [Theory]
    [InlineData("not-base64")]
    [InlineData("AA==")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAA==")]
    [InlineData(" AAAAAAAAAAAAAAAA")]
    [InlineData("\"etag\"")]
    public void FirestorePreconditionRejectsAForeignOrMalformedVersion(string backendVersion)
    {
        Assert.Null(FirestoreConditionalRegister.TryCreatePrecondition(new MetaStoreVersion(backendVersion)));
    }

    [Theory]
    [InlineData("not-base64")]
    [InlineData("AA==")]
    public async Task FirestoreWriteWithAMalformedVersionIsAMismatchWithoutIo(string backendVersion)
    {
        var builds = 0;
        var store = new FirestoreMetaStore(
            _ =>
            {
                builds++;
                return Task.FromException<FirestoreDb>(new InvalidOperationException("no database"));
            });
        var register = await store.GetRegisterAsync(
            new Guid("0123456789abcdef0123456789abcdef"),
            "l/orders",
            TestContext.Current.CancellationToken);

        Assert.False(
            (await register.WriteAsync(
                new MetaStoreVersion(backendVersion),
                [1],
                TestContext.Current.CancellationToken)).IsApplied());
        Assert.Equal(0, builds);
    }

    [Fact]
    public async Task FirestoreBuildsTheDatabaseOnFirstUseAndDoesNotCacheFailures()
    {
        var attempts = 0;
        var store = new FirestoreMetaStore(
            _ =>
            {
                attempts++;
                return Task.FromException<FirestoreDb>(new InvalidOperationException("no credential"));
            });

        var register = await store.GetRegisterAsync(
            new Guid("0123456789abcdef0123456789abcdef"),
            "l/orders",
            TestContext.Current.CancellationToken);
        Assert.Equal(0, attempts);
        Assert.False(store.IsBuilt);

        _ = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await register.ReadAsync(TestContext.Current.CancellationToken));
        _ = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await register.ReadAsync(TestContext.Current.CancellationToken));

        Assert.Equal(2, attempts);
        Assert.False(store.IsBuilt);
    }

    public void Dispose() => _leaks.Dispose();

    private sealed class AmazonDynamoDBStub()
        : AmazonDynamoDBClient(
            new AnonymousAWSCredentials(),
            new AmazonDynamoDBConfig { ServiceURL = "https://localhost.invalid" });
}
