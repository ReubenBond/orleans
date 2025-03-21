using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Serialization.Serializers;
using Xunit;

namespace Orleans.Journaling.Tests;

public class DurableGrainTests : TestBase
{
    [Fact]
    public async Task DurableGrain_State_Persistence_Test()
    {
        // Arrange
        var grain = new TestDurableGrain(LoggerFactory);

        // Act - Set state properties and persist
        grain.SetTestValues("Test Name", 42);

        // Assert
        Assert.Equal("Test Name", grain.Name);
        Assert.Equal(42, grain.Counter);

        // Act - Create a new grain with the same storage
        var storage = grain.GetStorage();
        var grain2 = new TestDurableGrain(LoggerFactory, storage);
        await grain2.OnActivateAsync(CancellationToken.None);

        // Assert - State should be recovered
        Assert.Equal("Test Name", grain2.Name);
        Assert.Equal(42, grain2.Counter);
    }

    [Fact]
    public async Task DurableGrain_Update_State_Test()
    {
        // Arrange
        var grain = new TestDurableGrain(LoggerFactory);

        // Act - Set state and persist
        grain.SetTestValues("Initial Name", 10);
        await grain.WriteStateAsync();

        // Update state and persist again
        grain.SetTestValues("Updated Name", 20);
        await grain.WriteStateAsync();

        // Assert
        Assert.Equal("Updated Name", grain.Name);
        Assert.Equal(20, grain.Counter);

        // Act - Create a new grain with the same storage
        var storage = grain.GetStorage();
        var grain2 = new TestDurableGrain(LoggerFactory, storage);
        await grain2.OnActivateAsync(CancellationToken.None);

        // Assert - Updated state should be recovered
        Assert.Equal("Updated Name", grain2.Name);
        Assert.Equal(20, grain2.Counter);
    }

    [Fact]
    public async Task DurableGrain_Complex_Types_Test()
    {
        // Arrange
        var grain = new TestDurableGrainWithComplexState(LoggerFactory);

        // Act - Set complex state and persist
        var person = new TestPerson { Id = 1, Name = "John Doe", Age = 30 };
        var items = new List<string> { "Item1", "Item2", "Item3" };
        grain.SetTestValues(person, items);
        await grain.WriteStateAsync();

        // Assert
        Assert.Equal("John Doe", grain.Person.Name);
        Assert.Equal(3, grain.Items.Count);

        // Act - Create a new grain with the same storage
        var storage = grain.GetStorage();
        var grain2 = new TestDurableGrainWithComplexState(LoggerFactory, storage);
        await grain2.OnActivateAsync(CancellationToken.None);

        // Assert - Complex state should be recovered
        Assert.NotNull(grain2.Person);
        Assert.Equal(1, grain2.Person.Id);
        Assert.Equal("John Doe", grain2.Person.Name);
        Assert.Equal(30, grain2.Person.Age);

        Assert.Equal(3, grain2.Items.Count);
        Assert.Equal("Item1", grain2.Items[0]);
        Assert.Equal("Item2", grain2.Items[1]);
        Assert.Equal("Item3", grain2.Items[2]);
    }

    [Fact]
    public async Task DurableGrain_Multiple_Collections_Test()
    {
        // Arrange
        var grain = new TestDurableGrainWithCollections(LoggerFactory);

        // Act - Populate collections and persist
        await grain.AddToDictionary("key1", 1);
        await grain.AddToDictionary("key2", 2);

        await grain.AddToList("item1");
        await grain.AddToList("item2");

        await grain.AddToQueue(100);
        await grain.AddToQueue(200);

        await grain.AddToSet("set1");
        await grain.AddToSet("set2");

        await grain.WriteStateAsync();

        // Assert
        Assert.Equal(2, await grain.GetDictionaryCount());
        Assert.Equal(2, await grain.GetListCount());
        Assert.Equal(2, await grain.GetQueueCount());
        Assert.Equal(2, await grain.GetSetCount());

        // Act - Create a new grain with the same storage
        var storage = grain.GetStorage();
        var grain2 = new TestDurableGrainWithCollections(LoggerFactory, storage);
        await grain2.OnActivateAsync(CancellationToken.None);

        // Assert - All collections should be recovered
        Assert.Equal(2, await grain2.GetDictionaryCount());
        Assert.Equal(1, await grain2.GetDictionaryValue("key1"));
        Assert.Equal(2, await grain2.GetDictionaryValue("key2"));

        Assert.Equal(2, await grain2.GetListCount());
        Assert.Equal("item1", await grain2.GetListItem(0));
        Assert.Equal("item2", await grain2.GetListItem(1));

        Assert.Equal(2, await grain2.GetQueueCount());
        Assert.Equal(100, await grain2.PeekQueueItem());

        Assert.Equal(2, await grain2.GetSetCount());
        Assert.True(await grain2.ContainsSetItem("set1"));
        Assert.True(await grain2.ContainsSetItem("set2"));
    }

    [Fact]
    public async Task DurableGrain_State_Modifications_Test()
    {
        // Arrange
        var grain = new TestDurableGrainWithCollections(LoggerFactory);

        // Act - Populate initial state and persist
        await grain.AddToDictionary("key1", 1);
        await grain.AddToList("item1");
        await grain.AddToQueue(100);
        await grain.AddToSet("set1");
        await grain.WriteStateAsync();

        // Modify state and persist again
        await grain.AddToDictionary("key2", 2);
        grain.UpdateDictionaryEntry("key1", 10);
        await grain.AddToList("item2");
        await grain.AddToQueue(200);
        await grain.AddToSet("set2");
        await grain.WriteStateAsync();

        // Assert
        Assert.Equal(2, await grain.GetDictionaryCount());
        Assert.Equal(10, await grain.GetDictionaryValue("key1"));
        Assert.Equal(2, await grain.GetListCount());
        Assert.Equal(2, await grain.GetQueueCount());
        Assert.Equal(2, await grain.GetSetCount());

        // Act - Create a new grain with the same storage
        var storage = grain.GetStorage();
        var grain2 = new TestDurableGrainWithCollections(LoggerFactory, storage);
        await grain2.OnActivateAsync(CancellationToken.None);

        // Assert - Modified state should be recovered
        Assert.Equal(2, await grain2.GetDictionaryCount());
        Assert.Equal(10, await grain2.GetDictionaryValue("key1"));
        Assert.Equal(2, await grain2.GetDictionaryValue("key2"));

        Assert.Equal(2, await grain2.GetListCount());
        Assert.Equal("item1", await grain2.GetListItem(0));
        Assert.Equal("item2", await grain2.GetListItem(1));

        // Further modify the state
        await grain2.RemoveFromDictionary("key1");
        await grain2.RemoveListItemAt(0);
        await grain2.DequeueItem();
        await grain2.RemoveFromSet("set1");
        await grain2.WriteStateAsync();

        // Assert the modifications
        Assert.Equal(1, await grain2.GetDictionaryCount());
        Assert.Equal(1, await grain2.GetListCount());
        Assert.Equal(1, await grain2.GetQueueCount());
        Assert.Equal(1, await grain2.GetSetCount());
    }
}

#region Test Grain Implementations

internal class TestDurableGrain : DurableGrain
{
    private readonly DurableValue<string> _name;
    private readonly DurableValue<int> _counter;

    public string Name => _name.Value;
    public int Counter => _counter.Value;

    public TestDurableGrain(ILoggerFactory loggerFactory)
        : this(loggerFactory, new VolatileStateMachineStorage())
    {
    }

    public TestDurableGrain(ILoggerFactory loggerFactory, VolatileStateMachineStorage storage)
        : base(loggerFactory.CreateLogger<TestDurableGrain>())
    {
        this.SetStateStorage(storage);

        _name = CreateDurableValue<string>("name");
        _counter = CreateDurableValue<int>("counter");
    }

    public void SetTestValues(string name, int counter)
    {
        _name.Value = name;
        _counter.Value = counter;
    }

    public VolatileStateMachineStorage GetStorage()
    {
        return (VolatileStateMachineStorage)((StateMachineManager)StateMachineManager)._storage;
    }
}

internal class TestDurableGrainWithComplexState : DurableGrain
{
    private readonly DurableValue<TestPerson> _person;
    private readonly DurableList<string> _items;

    public TestPerson Person => _person.Value;
    public IReadOnlyList<string> Items => _items;

    public TestDurableGrainWithComplexState(ILoggerFactory loggerFactory)
        : this(loggerFactory, new VolatileStateMachineStorage())
    {
    }

    public TestDurableGrainWithComplexState(ILoggerFactory loggerFactory, VolatileStateMachineStorage storage)
        : base(loggerFactory.CreateLogger<TestDurableGrainWithComplexState>())
    {
        this.SetStateStorage(storage);

        _person = CreateDurableValue<TestPerson>("person");
        _items = CreateDurableList<string>("items");
    }

    public void SetTestValues(TestPerson person, List<string> items)
    {
        _person.Value = person;

        _items.Clear();
        foreach (var item in items)
        {
            _items.Add(item);
        }
    }

    public VolatileStateMachineStorage GetStorage()
    {
        return (VolatileStateMachineStorage)((StateMachineManager)StateMachineManager)._storage;
    }
}

internal class TestDurableGrainWithCollections : DurableGrain, ITestMultiCollectionGrainInterface
{
    private readonly DurableDictionary<string, int> _dictionary;
    private readonly DurableList<string> _list;
    private readonly DurableQueue<int> _queue;
    private readonly DurableSet<string> _set;

    public TestDurableGrainWithCollections(ILoggerFactory loggerFactory)
        : this(loggerFactory, new VolatileStateMachineStorage())
    {
    }

    public TestDurableGrainWithCollections(ILoggerFactory loggerFactory, VolatileStateMachineStorage storage)
        : base(loggerFactory.CreateLogger<TestDurableGrainWithCollections>())
    {
        this.SetStateStorage(storage);

        _dictionary = CreateDurableDictionary<string, int>("dictionary");
        _list = CreateDurableList<string>("list");
        _queue = CreateDurableQueue<int>("queue");
        _set = CreateDurableSet<string>("set");
    }

    // Dictionary operations
    public Task AddToDictionary(string key, int value)
    {
        _dictionary[key] = value;
        return Task.CompletedTask;
    }

    public void UpdateDictionaryEntry(string key, int value) => _dictionary[key] = value;

    public Task RemoveFromDictionary(string key)
    {
        _dictionary.Remove(key);
        return Task.CompletedTask;
    }

    public Task<int> GetDictionaryValue(string key) => Task.FromResult(_dictionary[key]);
    public Task<int> GetDictionaryCount() => Task.FromResult(_dictionary.Count);

    // List operations
    public Task AddToList(string item)
    {
        _list.Add(item);
        return Task.CompletedTask;
    }

    public Task RemoveListItemAt(int index)
    {
        _list.RemoveAt(index);
        return Task.CompletedTask;
    }

    public Task<string> GetListItem(int index) => Task.FromResult(_list[index]);
    public Task<int> GetListCount() => Task.FromResult(_list.Count);

    // Queue operations
    public Task AddToQueue(int item)
    {
        _queue.Enqueue(item);
        return Task.CompletedTask;
    }

    public Task<int> DequeueItem() => Task.FromResult(_queue.Dequeue());
    public Task<int> PeekQueueItem() => Task.FromResult(_queue.Peek());
    public Task<int> GetQueueCount() => Task.FromResult(_queue.Count);

    // Set operations
    public Task AddToSet(string item)
    {
        _set.Add(item);
        return Task.CompletedTask;
    }

    public Task RemoveFromSet(string item)
    {
        _set.Remove(item);
        return Task.CompletedTask;
    }

    public Task<bool> ContainsSetItem(string item) => Task.FromResult(_set.Contains(item));
    public Task<int> GetSetCount() => Task.FromResult(_set.Count);

    // Non-interface methods for test convenience
    public void AddDictionaryEntry(string key, int value) => _dictionary[key] = value;
    public void RemoveDictionaryEntry(string key) => _dictionary.Remove(key);
    public void AddListItem(string item) => _list.Add(item);
    public void AddQueueItem(int item) => _queue.Enqueue(item);
    public void AddSetItem(string item) => _set.Add(item);
    public void RemoveSetItem(string item) => _set.Remove(item);

    public VolatileStateMachineStorage GetStorage()
    {
        return (VolatileStateMachineStorage)StateStorage;
    }
}

#endregion
