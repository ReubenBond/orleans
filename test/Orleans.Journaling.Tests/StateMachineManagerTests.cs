using Microsoft.Extensions.Logging;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.Session;
using Xunit;

namespace Orleans.Journaling.Tests;

public class StateMachineManagerTests : TestBase
{
    [Fact]
    public async Task StateMachineManager_Initialization_Test()
    {
        // Arrange
        var storage = CreateInMemoryStorage();
        var logger = LoggerFactory.CreateLogger<StateMachineManager>();

        // Act
        var manager = new StateMachineManager(storage, logger, SessionPool);
        await manager.InitializeAsync(CancellationToken.None);

        // Assert
        Assert.NotNull(manager);
        Assert.Equal(storage, manager.Storage);
    }

    [Fact]
    public async Task StateMachineManager_RegisterStateMachine_Test()
    {
        // Arrange
        var manager = await CreateManagerAsync();
        var codec = CodecProvider.GetCodec<int>();

        // Act - Register state machines
        var dictionary = new DurableDictionary<string, int>("dict1", manager, CodecProvider.GetCodec<string>(), codec, SessionPool);
        var list = new DurableList<string>("list1", manager, CodecProvider.GetCodec<string>(), SessionPool);
        var queue = new DurableQueue<int>("queue1", manager, codec, SessionPool);

        // Add some data
        dictionary.Add("key1", 1);
        list.Add("item1");
        queue.Enqueue(42);

        // Write state
        await manager.WriteStateAsync(CancellationToken.None);

        // Assert - Data is correctly stored
        Assert.Equal(1, dictionary["key1"]);
        Assert.Equal("item1", list[0]);
        Assert.Equal(42, queue.Peek());
    }

    [Fact]
    public async Task StateMachineManager_StateRecovery_Test()
    {
        // Arrange
        var storage = CreateInMemoryStorage();
        var logger = LoggerFactory.CreateLogger<StateMachineManager>();

        // First manager
        var manager1 = new StateMachineManager(storage, logger, SessionPool);
        await manager1.InitializeAsync(CancellationToken.None);

        // Create and populate state machines
        var dictionary = new DurableDictionary<string, int>("dict1", manager1, CodecProvider.GetCodec<string>(), CodecProvider.GetCodec<int>(), SessionPool);
        var list = new DurableList<string>("list1", manager1, CodecProvider.GetCodec<string>(), SessionPool);

        dictionary.Add("key1", 1);
        dictionary.Add("key2", 2);
        list.Add("item1");
        list.Add("item2");

        await manager1.WriteStateAsync(CancellationToken.None);

        // Act - Create new manager with same storage
        var manager2 = new StateMachineManager(storage, logger, SessionPool);
        await manager2.InitializeAsync(CancellationToken.None);

        var recoveredDict = new DurableDictionary<string, int>("dict1", manager2, CodecProvider.GetCodec<string>(), CodecProvider.GetCodec<int>(), SessionPool);
        var recoveredList = new DurableList<string>("list1", manager2, CodecProvider.GetCodec<string>(), SessionPool);

        // Assert - State should be recovered
        Assert.Equal(2, recoveredDict.Count);
        Assert.Equal(1, recoveredDict["key1"]);
        Assert.Equal(2, recoveredDict["key2"]);

        Assert.Equal(2, recoveredList.Count);
        Assert.Equal("item1", recoveredList[0]);
        Assert.Equal("item2", recoveredList[1]);
    }

    [Fact]
    public async Task StateMachineManager_MultipleWriteStates_Test()
    {
        // Arrange
        var manager = await CreateManagerAsync();
        var dictionary = new DurableDictionary<string, int>("dict1", manager, CodecProvider.GetCodec<string>(), CodecProvider.GetCodec<int>(), SessionPool);

        // Act - Multiple operations with WriteState in between
        dictionary.Add("key1", 1);
        await manager.WriteStateAsync(CancellationToken.None);

        dictionary.Add("key2", 2);
        await manager.WriteStateAsync(CancellationToken.None);

        dictionary["key1"] = 10;
        await manager.WriteStateAsync(CancellationToken.None);

        dictionary.Remove("key2");
        await manager.WriteStateAsync(CancellationToken.None);

        // Assert - Final state is correct
        Assert.Single(dictionary);
        Assert.Equal(10, dictionary["key1"]);
        Assert.False(dictionary.ContainsKey("key2"));

        // Create new manager to verify recovery
        var storage = (VolatileStateMachineStorage)manager.Storage;
        var logger = LoggerFactory.CreateLogger<StateMachineManager>();
        var manager2 = new StateMachineManager(storage, logger, SessionPool);
        await manager2.InitializeAsync(CancellationToken.None);

        var recoveredDict = new DurableDictionary<string, int>("dict1", manager2, CodecProvider.GetCodec<string>(), CodecProvider.GetCodec<int>(), SessionPool);

        // Assert - Recovery should have final state
        Assert.Single(recoveredDict);
        Assert.Equal(10, recoveredDict["key1"]);
        Assert.False(recoveredDict.ContainsKey("key2"));
    }

    [Fact]
    public async Task StateMachineManager_MultipleStateMachines_Test()
    {
        // Arrange
        var manager = await CreateManagerAsync();

        // Create multiple state machines with different types
        var intDict = new DurableDictionary<int, string>("intDict", manager, CodecProvider.GetCodec<int>(), CodecProvider.GetCodec<string>(), SessionPool);
        var stringList = new DurableList<string>("stringList", manager, CodecProvider.GetCodec<string>(), SessionPool);
        var personValue = new DurableValue<TestPerson>("personValue", manager, CodecProvider.GetCodec<TestPerson>(), SessionPool);

        // Act - Populate all state machines
        intDict.Add(1, "one");
        intDict.Add(2, "two");

        stringList.Add("item1");
        stringList.Add("item2");

        personValue.Value = new TestPerson { Id = 100, Name = "Test Person", Age = 30 };

        await manager.WriteStateAsync(CancellationToken.None);

        // Assert - All should have correct values
        Assert.Equal(2, intDict.Count);
        Assert.Equal("one", intDict[1]);

        Assert.Equal(2, stringList.Count);
        Assert.Equal("item1", stringList[0]);

        Assert.NotNull(personValue.Value);
        Assert.Equal(100, personValue.Value.Id);
        Assert.Equal("Test Person", personValue.Value.Name);

        // Create new manager to verify recovery of multiple state machines
        var storage = (VolatileStateMachineStorage)manager.Storage;
        var logger = LoggerFactory.CreateLogger<StateMachineManager>();
        var manager2 = new StateMachineManager(storage, logger, SessionPool);
        await manager2.InitializeAsync(CancellationToken.None);

        var recoveredIntDict = new DurableDictionary<int, string>("intDict", manager2, CodecProvider.GetCodec<int>(), CodecProvider.GetCodec<string>(), SessionPool);
        var recoveredStringList = new DurableList<string>("stringList", manager2, CodecProvider.GetCodec<string>(), SessionPool);
        var recoveredPersonValue = new DurableValue<TestPerson>("personValue", manager2, CodecProvider.GetCodec<TestPerson>(), SessionPool);

        // Assert - All should be recovered with correct values
        Assert.Equal(2, recoveredIntDict.Count);
        Assert.Equal("one", recoveredIntDict[1]);

        Assert.Equal(2, recoveredStringList.Count);
        Assert.Equal("item1", recoveredStringList[0]);

        Assert.NotNull(recoveredPersonValue.Value);
        Assert.Equal(100, recoveredPersonValue.Value.Id);
        Assert.Equal("Test Person", recoveredPersonValue.Value.Name);
    }

    [Fact]
    public async Task StateMachineManager_Concurrency_Test()
    {
        // Arrange
        var manager = await CreateManagerAsync();
        var dict1 = new DurableDictionary<string, int>("dict1", manager, CodecProvider.GetCodec<string>(), CodecProvider.GetCodec<int>(), SessionPool);
        var dict2 = new DurableDictionary<string, int>("dict2", manager, CodecProvider.GetCodec<string>(), CodecProvider.GetCodec<int>(), SessionPool);

        // Act - Simulate concurrent operations on different state machines
        dict1.Add("key1", 1);
        dict2.Add("key1", 100);

        dict1.Add("key2", 2);
        dict2.Add("key2", 200);

        await manager.WriteStateAsync(CancellationToken.None);

        // Assert - Both state machines should have their correct values
        Assert.Equal(2, dict1.Count);
        Assert.Equal(2, dict2.Count);

        Assert.Equal(1, dict1["key1"]);
        Assert.Equal(100, dict2["key1"]);

        Assert.Equal(2, dict1["key2"]);
        Assert.Equal(200, dict2["key2"]);
    }

    [Fact]
    public async Task StateMachineManager_LargeStateRecovery_Test()
    {
        // Arrange
        var storage = CreateInMemoryStorage();
        var logger = LoggerFactory.CreateLogger<StateMachineManager>();

        var manager1 = new StateMachineManager(storage, logger, SessionPool);
        await manager1.InitializeAsync(CancellationToken.None);

        var largeDict = new DurableDictionary<int, string>("largeDict", manager1, CodecProvider.GetCodec<int>(), CodecProvider.GetCodec<string>(), SessionPool);

        // Act - Add many items
        const int itemCount = 1000;
        for (int i = 0; i < itemCount; i++)
        {
            largeDict.Add(i, $"Value {i}");
        }

        await manager1.WriteStateAsync(CancellationToken.None);

        // Create new manager for recovery
        var manager2 = new StateMachineManager(storage, logger, SessionPool);
        await manager2.InitializeAsync(CancellationToken.None);

        var recoveredDict = new DurableDictionary<int, string>("largeDict", manager2, CodecProvider.GetCodec<int>(), CodecProvider.GetCodec<string>(), SessionPool);

        // Assert - All items should be recovered
        Assert.Equal(itemCount, recoveredDict.Count);
        for (int i = 0; i < itemCount; i++)
        {
            Assert.Equal($"Value {i}", recoveredDict[i]);
        }
    }
}