using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Specialized;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Serialization;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.Session;
using Xunit;

namespace Orleans.Journaling.Tests;

public class LogSegmentTests
{
    private readonly ServiceProvider _serviceProvider;

    public LogSegmentTests()
    {
        var services = new ServiceCollection();
        services.AddSerializer();
        services.AddLogging();
        _serviceProvider = services.BuildServiceProvider();
    }

    /*
    [Fact]
    public void AppendedSegmentsAreEnumerable()
    {
        var sessionPool = _serviceProvider.GetRequiredService<SerializerSessionPool>();
        using var segment = new LogSegment();
        var expectedBuffers = new List<(uint Id, byte[] Data)>();
        for (var i = 0; i < 100; i++)
        {
            var buf = new byte[i * 100];
            Array.Fill(buf, (byte)i);

            for (var id = 0U; id < 5; id++)
            {
                var streamId = new StateMachineId(id);
                expectedBuffers.Add((id, buf));
                segment.AppendEntry(0, streamId, buf);
            }
        }

        var entries = new List<LogSegment.Entry>();
        foreach (var entry in segment.GetEntryEnumerator(0))
        {
            entries.Add(entry);
        }

        Assert.Equal(expectedBuffers.Count, entries.Count);
        for (var i = 0; i < expectedBuffers.Count; i++)
        {
            var (expectedId, expectedData) = expectedBuffers[i];
            var (actualId, actualData) = entries[i];
            Assert.Equal(expectedId, actualId.Value);
            Assert.Equal(expectedData, actualData.ToArray());
        }
    }
    */

    [Fact]
    public async Task DurableListTest()
    {
        var blobServiceClient = new BlobServiceClient("DefaultEndpointsProtocol=https;AccountName=rbnstor;AccountKey=YLMI6gPvI3XTKFcSvJ7GvEfzwUYMqEHlM0MTdhOcLtc9X6Mzp9J4kTCnWTp9bEh0BnpB363grEVw+AStc6WG5w==;EndpointSuffix=core.windows.net");
        var container = blobServiceClient.GetBlobContainerClient("graindb");
        await container.CreateIfNotExistsAsync();
        var blobClient = container.GetAppendBlobClient("my-grain");
        var storage = new AzureAppendBlobLogStorage(blobClient, null!);

        var sessionPool = _serviceProvider.GetRequiredService<SerializerSessionPool>();
        var codecProvider = _serviceProvider.GetRequiredService<ICodecProvider>();
        //var storage = new InMemoryStateMachineStorage();
        var manager = new StateMachineManager(storage, _serviceProvider.GetRequiredService<ILogger<StateMachineManager>>(), sessionPool);
        var list = new DurableList<string>("fooey", manager, codecProvider.GetCodec<string>(), sessionPool);
        await manager.InitializeAsync(CancellationToken.None);

        // NOTE TO SELF: when we register the state machine, we need a signal which indicates when the state machine is ready to be used.
        // In practice, we are waiting for OnActivateAsync
        for (var i = 0; i < 10; ++i)
        {
            list.Add(i.ToString());
        }
        //Assert.Equal(10, list.Count);

        await manager.WriteStateAsync(CancellationToken.None);

        // TODO: make sure that OnRecoveryCompleted is not called before recovery has completed!

        // TODO: throw in state machine if trying to mutate before recovery has completed

        // TODO: throw in state machine MANAGER if trying to append log entries before recovery has completed

        var newManager = new StateMachineManager(storage, _serviceProvider.GetRequiredService<ILogger<StateMachineManager>>(), sessionPool);
        var newList = new DurableList<string>("fooey", newManager, codecProvider.GetCodec<string>(), sessionPool);
        await newManager.InitializeAsync(CancellationToken.None);

        var originalList = list.ToList();
        var recreatedList = newList.ToList();
        Assert.Equal(originalList, recreatedList);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task DurableList_Snapshot_Test()
    {
        var blobServiceClient = new BlobServiceClient("DefaultEndpointsProtocol=https;AccountName=rbnstor;AccountKey=YLMI6gPvI3XTKFcSvJ7GvEfzwUYMqEHlM0MTdhOcLtc9X6Mzp9J4kTCnWTp9bEh0BnpB363grEVw+AStc6WG5w==;EndpointSuffix=core.windows.net");
        var container = blobServiceClient.GetBlobContainerClient("graindb");
        await container.CreateIfNotExistsAsync();
        var blobClient = container.GetAppendBlobClient("my-grain");
        var storage = new AzureAppendBlobLogStorage(blobClient, null!);

        var sessionPool = _serviceProvider.GetRequiredService<SerializerSessionPool>();
        var codecProvider = _serviceProvider.GetRequiredService<ICodecProvider>();
        //var storage = new InMemoryStateMachineStorage();
        var manager = new StateMachineManager(storage, _serviceProvider.GetRequiredService<ILogger<StateMachineManager>>(), sessionPool);
        var list = new DurableList<string>("fooey", manager, codecProvider.GetCodec<string>(), sessionPool);
        await manager.InitializeAsync(CancellationToken.None);

        // NOTE TO SELF: when we register the state machine, we need a signal which indicates when the state machine is ready to be used.
        // In practice, we are waiting for OnActivateAsync
        list.Clear();
        for (var c = 0; c < 15; ++c)
        {
            for (var i = 0; i < 10; ++i)
            {
                list.Add(i.ToString());
            }
            //Assert.Equal(10, list.Count);

            await manager.WriteStateAsync(CancellationToken.None);
        }

        // TODO: make sure that OnRecoveryCompleted is not called before recovery has completed!

        // TODO: throw in state machine if trying to mutate before recovery has completed

        // TODO: throw in state machine MANAGER if trying to append log entries before recovery has completed

        var newManager = new StateMachineManager(storage, _serviceProvider.GetRequiredService<ILogger<StateMachineManager>>(), sessionPool);
        var newList = new DurableList<string>("fooey", newManager, codecProvider.GetCodec<string>(), sessionPool);
        await newManager.InitializeAsync(CancellationToken.None);

        var originalList = list.ToList();
        var recreatedList = newList.ToList();
        Assert.Equal(originalList, recreatedList);
        await Task.CompletedTask;
    }
}
