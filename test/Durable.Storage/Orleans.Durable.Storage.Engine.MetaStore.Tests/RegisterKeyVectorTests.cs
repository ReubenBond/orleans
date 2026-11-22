using System.Globalization;
using System.Text.Json;
using Google.Cloud.Firestore;
using Orleans.Durable.Storage.AzureStorage.MetaStore;
using Orleans.Durable.Storage.GoogleCloud.MetaStore;
using Orleans.Durable.Storage.S3.MetaStore;
using Orleans.Durable.Storage.Testing;
using ProtobufTimestamp = Google.Protobuf.WellKnownTypes.Timestamp;

namespace Orleans.Durable.Storage.Engine.MetaStore.Tests;

/// <summary>
/// Checks the shared register-key vectors <c>tests/shared-vectors/metastore/register-keys.json</c> (metastore.md §7;
/// MS-I4).
/// </summary>
[NoPageLeakCheck("Keys and versions are strings; the tests rent no pages.")]
public sealed class RegisterKeyVectorTests
{
    [Fact]
    public async Task RegistersMapToTheirRegisterKeyAndEveryBackendKey()
    {
        using var document = await LoadVectorAsync();
        var backends = document.RootElement.GetProperty("backends");
        var blobPrefix = backends.GetProperty("azure-blob").GetProperty("prefix").GetString()!;
        var rowKeyPrefix = backends.GetProperty("azure-table").GetProperty("rowKeyPrefix").GetString()!;
        var partitionKeyPrefix = backends.GetProperty("aws-dynamodb").GetProperty("partitionKeyPrefix").GetString()!;
        var collection = backends.GetProperty("google-firestore").GetProperty("collection").GetString()!;
        var registers = document.RootElement.GetProperty("registers").EnumerateArray().ToArray();

        Assert.NotEmpty(registers);
        foreach (var register in registers)
        {
            var cluster = Guid.ParseExact(register.GetProperty("cluster").GetString()!, "N");
            var name = register.GetProperty("name").GetString()!;
            var keys = register.GetProperty("backendKeys");

            Assert.True(MetaStoreRegisterName.IsValid(name), name);
            Assert.Equal(register.GetProperty("registerKey").GetString(), MetaStoreRegisterName.ToKey(cluster, name));
            Assert.Equal(keys.GetProperty("azure-blob").GetString(), AzureBlobMetaStore.GetBlobName(blobPrefix, cluster, name));
            Assert.Equal(keys.GetProperty("azure-table").GetString(), AzureTableMetaStore.GetRowKey(rowKeyPrefix, cluster, name));
            Assert.Equal(
                keys.GetProperty("aws-dynamodb").GetString(),
                DynamoDbMetaStore.GetPartitionKey(partitionKeyPrefix, cluster, name));
            Assert.Equal(
                keys.GetProperty("google-firestore").GetString(),
                FirestoreMetaStore.GetDocumentPath(collection, cluster, name));
        }
    }

    [Fact]
    public async Task BackendKeysOfDistinctRegistersAreDistinct()
    {
        using var document = await LoadVectorAsync();
        var registers = document.RootElement.GetProperty("registers").EnumerateArray().ToArray();

        foreach (var backend in registers[0].GetProperty("backendKeys").EnumerateObject().Select(static property => property.Name))
        {
            var keys = registers.Select(register => register.GetProperty("backendKeys").GetProperty(backend).GetString()).ToArray();
            Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
        }
    }

    [Fact]
    public async Task InvalidNamesAndTheEmptyClusterAreRejectedByEveryStore()
    {
        using var document = await LoadVectorAsync();
        var cluster = Guid.ParseExact(
            document.RootElement.GetProperty("registers")[0].GetProperty("cluster").GetString()!,
            "N");
        var emptyCluster = Guid.ParseExact(document.RootElement.GetProperty("emptyCluster").GetString()!, "N");
        var names = document.RootElement.GetProperty("invalidNames").EnumerateArray().Select(static name => name.GetString()!).ToArray();
        IMetaStore[] stores =
        [
            new InMemoryMetaStore(),
            new FirestoreMetaStore(static _ => Task.FromException<FirestoreDb>(new InvalidOperationException())),
        ];

        Assert.NotEmpty(names);
        foreach (var name in names)
        {
            Assert.False(MetaStoreRegisterName.IsValid(name), name);
            Assert.Throws<ArgumentException>(() => MetaStoreRegisterName.ToKey(cluster, name));
            foreach (var store in stores)
            {
                await Assert.ThrowsAsync<ArgumentException>(
                    async () => _ = await store.GetRegisterAsync(cluster, name, TestContext.Current.CancellationToken));
            }
        }

        Assert.Throws<ArgumentException>(() => MetaStoreRegisterName.ToKey(emptyCluster, "l/orders"));
        foreach (var store in stores)
        {
            await Assert.ThrowsAsync<ArgumentException>(
                async () => _ = await store.GetRegisterAsync(emptyCluster, "l/orders", TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task FirestoreVersionsEncodeTheUpdateTimeWithoutPrecisionLoss()
    {
        using var document = await LoadVectorAsync();
        var versions = document.RootElement.GetProperty("firestoreVersions").EnumerateArray().ToArray();

        Assert.NotEmpty(versions);
        foreach (var vector in versions)
        {
            var timestamp = Timestamp.FromProto(
                new ProtobufTimestamp
                {
                    Seconds = long.Parse(vector.GetProperty("seconds").GetString()!, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture),
                    Nanos = vector.GetProperty("nanos").GetInt32(),
                });
            var version = FirestoreConditionalRegister.EncodeVersion(timestamp);

            Assert.Equal(vector.GetProperty("version").GetString(), version.Value);
            Assert.Equal(timestamp, FirestoreConditionalRegister.TryCreatePrecondition(version)?.LastUpdateTime);
        }

        foreach (var malformed in document.RootElement.GetProperty("malformedFirestoreVersions").EnumerateArray())
        {
            Assert.Null(FirestoreConditionalRegister.TryCreatePrecondition(new MetaStoreVersion(malformed.GetString()!)));
        }
    }

    private static async Task<JsonDocument> LoadVectorAsync() =>
        JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(FindRepositoryRoot(), "tests", "shared-vectors", "metastore", "register-keys.json"),
            TestContext.Current.CancellationToken));

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
            directory is not null;
            directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Clog.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not find the Clog repository root.");
    }
}
