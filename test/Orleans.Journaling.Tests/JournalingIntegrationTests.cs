using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.TestingHost;
using Orleans.TestingHost.Utils;
using Xunit;

namespace Orleans.Journaling.Tests;

public class JournalingIntegrationTests
{
    private class TestClusterFixture : IDisposable
    {
        public TestCluster Cluster { get; }

        public TestClusterFixture()
        {
            var builder = new TestClusterBuilder();
            builder.AddSiloBuilderConfigurator<TestSiloConfigurator>();
            builder.AddClientBuilderConfigurator<TestClientConfigurator>();
            Cluster = builder.Build();
            Cluster.Deploy();
        }

        public void Dispose()
        {
            Cluster.StopAllSilos();
        }

        private class TestSiloConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder siloBuilder)
            {
                siloBuilder.ConfigureServices(services =>
                {
                    services.AddSerializer();
                });
            }
        }

        private class TestClientConfigurator : IClientBuilderConfigurator
        {
            public void Configure(IClientBuilder clientBuilder)
            {
                clientBuilder.ConfigureServices(services =>
                {
                    services.AddSerializer();
                });
            }
        }
    }

    public class GrainPersistenceTests : IClassFixture<TestClusterFixture>
    {
        private readonly TestClusterFixture _fixture;

        public GrainPersistenceTests(TestClusterFixture fixture)
        {
            _fixture = fixture;
        }

        [Fact]
        public async Task Grain_State_Should_Persist_Between_Activations()
        {
            // Arrange - Get a reference to a grain
            var grain = _fixture.Cluster.GrainFactory.GetGrain<ITestDurableGrainInterface>(Guid.NewGuid());

            // Act - Set the grain state
            await grain.SetValues("Test Name", 42);
            var initialState = await grain.GetValues();

            // Deactivate the grain forcefully
            await _fixture.Cluster.GrainFactory.GetGrain<IManagementGrain>(0)
                .DeactivateGrain(grain.GetGrainId());

            // Wait a moment to ensure deactivation
            await Task.Delay(500);

            // Get the values from the grain (which will be reactivated)
            var newState = await grain.GetValues();

            // Assert
            Assert.Equal(initialState.Name, newState.Name);
            Assert.Equal(initialState.Counter, newState.Counter);
        }

        [Fact]
        public async Task Grain_Should_Handle_Multiple_Collections()
        {
            // Arrange
            var grain = _fixture.Cluster.GrainFactory.GetGrain<ITestMultiCollectionGrainInterface>(Guid.NewGuid());

            // Act - Add items to collections
            await grain.AddToDictionary("key1", 1);
            await grain.AddToDictionary("key2", 2);

            await grain.AddToList("item1");
            await grain.AddToList("item2");

            await grain.AddToQueue(100);
            await grain.AddToQueue(200);

            await grain.AddToSet("set1");
            await grain.AddToSet("set2");
            await grain.AddToSet("set1"); // Duplicate, should be ignored

            // Assert - Check counts
            Assert.Equal(2, await grain.GetDictionaryCount());
            Assert.Equal(2, await grain.GetListCount());
            Assert.Equal(2, await grain.GetQueueCount());
            Assert.Equal(2, await grain.GetSetCount());

            // Deactivate the grain forcefully
            await _fixture.Cluster.GrainFactory.GetGrain<IManagementGrain>(0)
                .DeactivateGrain(grain.GetGrainId());

            // Wait a moment to ensure deactivation
            await Task.Delay(500);

            // Assert - Check values after reactivation
            Assert.Equal(1, await grain.GetDictionaryValue("key1"));
            Assert.Equal(2, await grain.GetDictionaryValue("key2"));
            Assert.Equal("item1", await grain.GetListItem(0));
            Assert.Equal("item2", await grain.GetListItem(1));
            Assert.Equal(100, await grain.PeekQueueItem());
            Assert.True(await grain.ContainsSetItem("set1"));
            Assert.True(await grain.ContainsSetItem("set2"));

            // Act - Modify collections
            await grain.RemoveFromDictionary("key1");
            await grain.RemoveListItemAt(0);
            await grain.DequeueItem();
            await grain.RemoveFromSet("set1");

            // Assert - Check counts after modifications
            Assert.Equal(1, await grain.GetDictionaryCount());
            Assert.Equal(1, await grain.GetListCount());
            Assert.Equal(1, await grain.GetQueueCount());
            Assert.Equal(1, await grain.GetSetCount());

            // Deactivate the grain again
            await _fixture.Cluster.GrainFactory.GetGrain<IManagementGrain>(0)
                .DeactivateGrain(grain.GetGrainId());

            // Wait a moment to ensure deactivation
            await Task.Delay(500);

            // Assert - Check values after second reactivation
            Assert.Equal(1, await grain.GetDictionaryCount());
            Assert.Equal(1, await grain.GetListCount());
            Assert.Equal(1, await grain.GetQueueCount());
            Assert.Equal(1, await grain.GetSetCount());
            Assert.Equal(2, await grain.GetDictionaryValue("key2"));
            Assert.Equal("item2", await grain.GetListItem(0));
            Assert.Equal(200, await grain.PeekQueueItem());
            Assert.True(await grain.ContainsSetItem("set2"));
        }

        [Fact]
        public async Task Grain_Should_Handle_Large_State()
        {
            // Arrange
            var grain = _fixture.Cluster.GrainFactory.GetGrain<ITestMultiCollectionGrainInterface>(Guid.NewGuid());

            // Act - Add many items
            const int itemCount = 1000;
            for (int i = 0; i < itemCount; i++)
            {
                await grain.AddToDictionary($"key{i}", i);
                if (i < 100) // Add fewer items to other collections to keep test runtime reasonable
                {
                    await grain.AddToList($"item{i}");
                    await grain.AddToQueue(i);
                    await grain.AddToSet($"set{i}");
                }
            }

            // Assert - Check counts
            Assert.Equal(itemCount, await grain.GetDictionaryCount());
            Assert.Equal(100, await grain.GetListCount());
            Assert.Equal(100, await grain.GetQueueCount());
            Assert.Equal(100, await grain.GetSetCount());

            // Deactivate the grain forcefully
            await _fixture.Cluster.GrainFactory.GetGrain<IManagementGrain>(0)
                .DeactivateGrain(grain.GetGrainId());

            // Wait a moment to ensure deactivation
            await Task.Delay(500);

            // Assert - Check random values after reactivation
            for (int i = 0; i < 10; i++)
            {
                var randomIndex = new Random().Next(0, itemCount - 1);
                Assert.Equal(randomIndex, await grain.GetDictionaryValue($"key{randomIndex}"));
                
                if (randomIndex < 100)
                {
                    Assert.Equal($"item{randomIndex}", await grain.GetListItem(randomIndex));
                    Assert.True(await grain.ContainsSetItem($"set{randomIndex}"));
                }
            }
        }
    }
}

// Grain interfaces
public interface ITestDurableGrainInterface : IGrainWithGuidKey
{
    Task SetValues(string name, int counter);
    Task<(string Name, int Counter)> GetValues();
}

public interface ITestMultiCollectionGrainInterface : IGrainWithGuidKey
{
    Task AddToDictionary(string key, int value);
    Task RemoveFromDictionary(string key);
    Task<int> GetDictionaryValue(string key);
    Task<int> GetDictionaryCount();

    Task AddToList(string item);
    Task RemoveListItemAt(int index);
    Task<string> GetListItem(int index);
    Task<int> GetListCount();

    Task AddToQueue(int item);
    Task<int> DequeueItem();
    Task<int> PeekQueueItem();
    Task<int> GetQueueCount();

    Task AddToSet(string item);
    Task RemoveFromSet(string item);
    Task<bool> ContainsSetItem(string item);
    Task<int> GetSetCount();
}