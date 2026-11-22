using Amazon.S3.Model;
using Google.Cloud.Storage.V1;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Durable.Storage.S3.Archive;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Cloud.Tests;

/// <summary>
/// The listing start each service honours, on which <see cref="IObjectStore.ListAsync"/>'s start name rests beside the
/// providers' own filter (object-store.md §9): S3's <c>StartAfter</c> excludes the start's name, Cloud Storage's
/// <c>startOffset</c> includes it, as Azure's <c>startFrom</c> does (<see cref="AzureAppendableObjectFidelityTests"/>);
/// empty objects on every service; and an S3 directory bucket (<c>--x-s3</c>) listing in ordinal order from a start name
/// over an unordered directory-bucket service.
/// </summary>
public sealed class ListingStartFidelityTests(CloudEmulators emulators) : IDisposable
{
    private static readonly string[] Names = ["s/1", "s/2", "s/3", "s/4"];

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _leaks.Dispose();

    [Fact]
    public async Task S3HonoursStartAfterExcludingTheStart()
    {
        await using var store = new CloudStore(Emulator.LocalStack, emulators.Require(Emulator.LocalStack));
        await CreateAsync(store.Store);
        using var transport = new TransportDouble();
        using var client = CloudClients.S3Client(emulators.Require(Emulator.LocalStack), transport);

        var served = await client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = CloudEmulators.ContainerName, Prefix = store.Prefix + "s/", StartAfter = store.Prefix + "s/2" }, Token);

        Assert.Equal([store.Prefix + "s/3", store.Prefix + "s/4"], served.S3Objects.Select(static entry => entry.Key));
        Assert.Equal(["s/3", "s/4"], (await store.Store.ListAsync("s/", "s/2", Token).ToListAsync(Token)).Select(static entry => entry.Name.Value));
    }

    [Theory]
    [InlineData(Emulator.FakeGcsServer)]
    [InlineData(Emulator.GcsTestbench)]
    public async Task CloudStorageHonoursStartOffsetIncludingTheStart(Emulator emulator)
    {
        await using var store = new CloudStore(emulator, emulators.Require(emulator));
        await CreateAsync(store.Store);
        using var transport = new TransportDouble();
        using var client = CloudClients.StorageClient(emulators.Require(emulator), transport);

        var served = await client
            .ListObjectsAsync(CloudEmulators.ContainerName, store.Prefix + "s/", new ListObjectsOptions { StartOffset = store.Prefix + "s/2" })
            .ToListAsync(Token);

        Assert.Equal([store.Prefix + "s/2", store.Prefix + "s/3", store.Prefix + "s/4"], served.Select(static entry => entry.Name));
        Assert.Equal(["s/3", "s/4"], (await store.Store.ListAsync("s/", "s/2", Token).ToListAsync(Token)).Select(static entry => entry.Name.Value));
    }

    /// <summary>
    /// An empty object is listed with a length of 0 and read back by its properties on every service, Google's testbench
    /// among them, whose answers in the proto3 JSON mapping omit a size of 0.
    /// </summary>
    [Theory]
    [InlineData(Emulator.Azurite)]
    [InlineData(Emulator.LocalStack)]
    [InlineData(Emulator.FakeGcsServer)]
    [InlineData(Emulator.GcsTestbench)]
    public async Task AnEmptyObjectIsListedAndReadByItsProperties(Emulator emulator)
    {
        await using var store = new CloudStore(emulator, emulators.Require(emulator));
        var name = new ObjectName("s/empty");

        Assert.Equal<ObjectCreateOutcome>(ObjectCreateOutcome.Created.Instance, await store.Store.CreateAsync(name, default, null, Token));

        Assert.Equal(new ObjectProperties(0, null), await store.Store.GetPropertiesAsync(name, Token));
        Assert.Equal([new ListedObject(name, 0)], await store.Store.ListAsync("s/", cancellationToken: Token).ToListAsync(Token));
        var versioned = Assert.IsType<VersionedObject>(await store.Store.ReadVersionedAsync(name, Token));
        using var read = versioned.Content;
        Assert.Equal(0, read.Length);
    }

    [Fact]
    public async Task AnS3DirectoryBucketListsInOrdinalOrderFromAStartOverAnUnorderedService()
    {
        using var fake = new FakeS3DirectoryBucketService(listingOrder: FakeS3ListingOrder.Reversed);
        using var client = CloudClients.S3Client(StubResponses.Unserved, fake, disableS3ExpressSessionAuth: true);
        var store = new S3ObjectStore(client, FakeS3DirectoryBucketService.DefaultBucketName);
        await CreateAsync(store);

        Assert.Equal(["s/3", "s/4"], (await store.ListAsync("s/", "s/2", Token).ToListAsync(Token)).Select(static entry => entry.Name.Value));
    }

    private static async Task CreateAsync(IObjectStore store)
    {
        foreach (var name in Names)
        {
            using var content = ArcBuffer.Wrap(new byte[] { 1 });
            _ = await store.CreateAsync(new ObjectName(name), content, null, Token);
        }
    }
}
