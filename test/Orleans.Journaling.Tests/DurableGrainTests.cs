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
        await grain.WriteStateAsync();
        
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
        grain.AddDictionaryEntry("key1", 1);
        grain.AddDictionaryEntry("key2", 2);
        
        grain.AddListItem("item1");
        grain.AddListItem("item2");
        
        grain.AddQueueItem(100);
        grain.AddQueueItem(200);
        
        grain.AddSetItem("set1");
        grain.AddSetItem("set2");
        
        await grain.WriteStateAsync();
        
        // Assert
        Assert.Equal(2, grain.GetDictionaryCount());
        Assert.Equal(2, grain.GetListCount());
        Assert.Equal(2, grain.GetQueueCount());
        Assert.Equal(2, grain.GetSetCount());
        
        // Act - Create a new grain with the same storage
        var storage = grain.GetStorage();
        var grain2 = new TestDurableGrainWithCollections(LoggerFactory, storage);
        await grain2.OnActivateAsync(CancellationToken.None);
        
        // Assert - All collections should be recovered
        Assert.Equal(2, grain2.GetDictionaryCount());
        Assert.Equal(1, grain2.GetDictionaryValue("key1"));
        Assert.Equal(2, grain2.GetDictionaryValue("key2"));
        
        Assert.Equal(2, grain2.GetListCount());
        Assert.Equal("item1", grain2.GetListItem(0));
        Assert.Equal("item2", grain2.GetListItem(1));
        
        Assert.Equal(2, grain2.GetQueueCount());
        Assert.Equal(100, grain2.PeekQueueItem());
        
        Assert.Equal(2, grain2.GetSetCount());
        Assert.True(grain2.ContainsSetItem("set1"));
        Assert.True(grain2.ContainsSetItem("set2"));
    }
    
    [Fact]
    public async Task DurableGrain_State_Modifications_Test()
    {
        // Arrange
        var grain = new TestDurableGrainWithCollections(LoggerFactory);
        
        // Act - Populate initial state and persist
        grain.AddDictionaryEntry("key1", 1);
        grain.AddListItem("item1");
        grain.AddQueueItem(100);
        grain.AddSetItem("set1");
        await grain.WriteStateAsync();
        
        // Modify state and persist again
        grain.AddDictionaryEntry("key2", 2);
        grain.UpdateDictionaryEntry("key1", 10);
        grain.AddListItem("item2");
        grain.AddQueueItem(200);
        grain.AddSetItem("set2");
        await grain.WriteStateAsync();
        
        // Assert
        Assert.Equal(2, grain.GetDictionaryCount());
        Assert.Equal(10, grain.GetDictionaryValue("key1"));
        Assert.Equal(2, grain.GetListCount());
        Assert.Equal(2, grain.GetQueueCount());
        Assert.Equal(2, grain.GetSetCount());
        
        // Act - Create a new grain with the same storage
        var storage = grain.GetStorage();
        var grain2 = new TestDurableGrainWithCollections(LoggerFactory, storage);
        await grain2.OnActivateAsync(CancellationToken.None);
        
        // Assert - Modified state should be recovered
        Assert.Equal(2, grain2.GetDictionaryCount());
        Assert.Equal(10, grain2.GetDictionaryValue("key1"));
        Assert.Equal(2, grain2.GetDictionaryValue("key2"));
        
        Assert.Equal(2, grain2.GetListCount());
        Assert.Equal("item1", grain2.GetListItem(0));
        Assert.Equal("item2", grain2.GetListItem(1));
        
        // Further modify the state
        grain2.RemoveDictionaryEntry("key1");
        grain2.RemoveListItemAt(0);
        grain2.DequeueItem();
        grain2.RemoveSetItem("set1");
        await grain2.WriteStateAsync();
        
        // Assert the modifications
        Assert.Equal(1, grain2.GetDictionaryCount());
        Assert.Equal(1, grain2.GetListCount());
        Assert.Equal(1, grain2.GetQueueCount());
        Assert.Equal(1, grain2.GetSetCount());
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
        return (VolatileStateMachineStorage)StateStorage;
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
        return (VolatileStateMachineStorage)StateStorage;
    }
}

internal class TestDurableGrainWithCollections : DurableGrain
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
    
    public void AddDictionaryEntry(string key, int value) => _dictionary[key] = value;
    public void UpdateDictionaryEntry(string key, int value) => _dictionary[key] = value;
    public void RemoveDictionaryEntry(string key) => _dictionary.Remove(key);
    public int GetDictionaryValue(string key) => _dictionary[key];
    public int GetDictionaryCount() => _dictionary.Count;
    
    public void AddListItem(string item) => _list.Add(item);
    public void RemoveListItemAt(int index) => _list.RemoveAt(index);
    public string GetListItem(int index) => _list[index];
    public int GetListCount() => _list.Count;
    
    public void AddQueueItem(int item) => _queue.Enqueue(item);
    public int DequeueItem() => _queue.Dequeue();
    public int PeekQueueItem() => _queue.Peek();
    public int GetQueueCount() => _queue.Count;
    
    public void AddSetItem(string item) => _set.Add(item);
    public void RemoveSetItem(string item) => _set.Remove(item);
    public bool ContainsSetItem(string item) => _set.Contains(item);
    public int GetSetCount() => _set.Count;
    
    public VolatileStateMachineStorage GetStorage()
    {
        return (VolatileStateMachineStorage)StateStorage;
    }
}

#endregion