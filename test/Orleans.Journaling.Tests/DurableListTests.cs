using Microsoft.Extensions.Logging;
using Xunit;

namespace Orleans.Journaling.Tests;

public class DurableListTests : TestBase
{
    [Fact]
    public async Task DurableList_BasicOperations_Test()
    {
        // Arrange
        var (manager, _) = await CreateManagerAsync();
        var codec = CodecProvider.GetCodec<string>();
        var list = new DurableList<string>("testList", manager, codec, SessionPool);
        
        // Act - Add items
        list.Add("one");
        list.Add("two");
        list.Add("three");
        await manager.WriteStateAsync(CancellationToken.None);
        
        // Assert
        Assert.Equal(3, list.Count);
        Assert.Equal("one", list[0]);
        Assert.Equal("two", list[1]);
        Assert.Equal("three", list[2]);
        
        // Act - Update item
        list[1] = "updated";
        await manager.WriteStateAsync(CancellationToken.None);
        
        // Assert
        Assert.Equal("updated", list[1]);
        
        // Act - Remove item
        list.RemoveAt(0);
        await manager.WriteStateAsync(CancellationToken.None);
        
        // Assert
        Assert.Equal(2, list.Count);
        Assert.Equal("updated", list[0]);
        Assert.Equal("three", list[1]);
    }
    
    [Fact]
    public async Task DurableList_Persistence_Test()
    {
        // Arrange
        var storage = CreateInMemoryStorage();
        var logger = LoggerFactory.CreateLogger<StateMachineManager>();
        
        // First manager and list
        var manager1 = new StateMachineManager(storage, logger, SessionPool);
        await manager1.InitializeAsync(CancellationToken.None);
        
        var codec = CodecProvider.GetCodec<string>();
        var list1 = new DurableList<string>("testList", manager1, codec, SessionPool);
        
        // Act - Add items and persist
        list1.Add("one");
        list1.Add("two");
        list1.Add("three");
        await manager1.WriteStateAsync(CancellationToken.None);
        
        // Create a new manager with the same storage
        var manager2 = new StateMachineManager(storage, logger, SessionPool);
        await manager2.InitializeAsync(CancellationToken.None);
        
        var list2 = new DurableList<string>("testList", manager2, codec, SessionPool);
        
        // Assert - List should be recovered
        Assert.Equal(3, list2.Count);
        Assert.Equal("one", list2[0]);
        Assert.Equal("two", list2[1]);
        Assert.Equal("three", list2[2]);
    }
    
    [Fact]
    public async Task DurableList_ComplexValues_Test()
    {
        // Arrange
        var (manager, _) = await CreateManagerAsync();
        var codec = CodecProvider.GetCodec<TestPerson>();
        var list = new DurableList<TestPerson>("personList", manager, codec, SessionPool);
        
        // Act
        var person1 = new TestPerson { Id = 1, Name = "John", Age = 30 };
        var person2 = new TestPerson { Id = 2, Name = "Jane", Age = 25 };
        
        list.Add(person1);
        list.Add(person2);
        await manager.WriteStateAsync(CancellationToken.None);
        
        // Assert
        Assert.Equal(2, list.Count);
        Assert.Equal("John", list[0].Name);
        Assert.Equal(25, list[1].Age);
        
        // Act - Update
        list[0].Age = 31;
        await manager.WriteStateAsync(CancellationToken.None);
        
        // Assert
        Assert.Equal(31, list[0].Age);
    }
    
    [Fact]
    public async Task DurableList_Clear_Test()
    {
        // Arrange
        var (manager, _) = await CreateManagerAsync();
        var codec = CodecProvider.GetCodec<string>();
        var list = new DurableList<string>("clearList", manager, codec, SessionPool);
        
        // Add items
        list.Add("one");
        list.Add("two");
        list.Add("three");
        await manager.WriteStateAsync(CancellationToken.None);
        
        // Act - Clear
        list.Clear();
        await manager.WriteStateAsync(CancellationToken.None);
        
        // Assert
        Assert.Empty(list);
        Assert.Empty(list);
    }
    
    [Fact]
    public async Task DurableList_Contains_Test()
    {
        // Arrange
        var (manager, _) = await CreateManagerAsync();
        var codec = CodecProvider.GetCodec<string>();
        var list = new DurableList<string>("containsList", manager, codec, SessionPool);
        
        // Add items
        list.Add("one");
        list.Add("two");
        list.Add("three");
        await manager.WriteStateAsync(CancellationToken.None);
        
        // Act & Assert
        Assert.Contains("two", list);
        Assert.DoesNotContain("four", list);
        
        // Act - Remove
        list.Remove("two");
        await manager.WriteStateAsync(CancellationToken.None);
        
        // Assert
        Assert.DoesNotContain("two", list);
    }
    
    [Fact]
    public async Task DurableList_InsertAndRemove_Test()
    {
        // Arrange
        var (manager, _) = await CreateManagerAsync();
        var codec = CodecProvider.GetCodec<string>();
        var list = new DurableList<string>("insertList", manager, codec, SessionPool);
        
        // Add initial items
        list.Add("one");
        list.Add("three");
        await manager.WriteStateAsync(CancellationToken.None);
        
        // Act - Insert
        list.Insert(1, "two");
        await manager.WriteStateAsync(CancellationToken.None);
        
        // Assert
        Assert.Equal(3, list.Count);
        Assert.Equal("one", list[0]);
        Assert.Equal("two", list[1]);
        Assert.Equal("three", list[2]);
        
        // Act - Remove by value
        bool removed = list.Remove("two");
        await manager.WriteStateAsync(CancellationToken.None);
        
        // Assert
        Assert.True(removed);
        Assert.Equal(2, list.Count);
        Assert.Equal("one", list[0]);
        Assert.Equal("three", list[1]);
    }
    
    [Fact]
    public async Task DurableList_Enumeration_Test()
    {
        // Arrange
        var (manager, _) = await CreateManagerAsync();
        var codec = CodecProvider.GetCodec<string>();
        var list = new DurableList<string>("enumList", manager, codec, SessionPool);
        
        // Add items
        var expectedItems = new List<string> { "one", "two", "three" };
        
        foreach (var item in expectedItems)
        {
            list.Add(item);
        }
        
        await manager.WriteStateAsync(CancellationToken.None);
        
        // Act
        var actualItems = list.ToList();
        
        // Assert
        Assert.Equal(expectedItems, actualItems);
    }
    
    [Fact]
    public async Task DurableList_LargeNumberOfOperations_Test()
    {
        // Arrange
        var (manager, storage) = await CreateManagerAsync();
        var codec = CodecProvider.GetCodec<int>();
        var list = new DurableList<int>("largeList", manager, codec, SessionPool);
        
        // Act - Add many items
        const int itemCount = 1000;
        for (int i = 0; i < itemCount; i++)
        {
            list.Add(i);
        }
        
        await manager.WriteStateAsync(CancellationToken.None);
        
        // Assert
        Assert.Equal(itemCount, list.Count);
        
        // Act - Update many items
        for (int i = 0; i < itemCount; i += 2)
        {
            list[i] = list[i] * 2;
        }
        
        await manager.WriteStateAsync(CancellationToken.None);
        
        // Assert
        for (int i = 0; i < itemCount; i++)
        {
            if (i % 2 == 0)
            {
                Assert.Equal(i * 2, list[i]);
            }
            else
            {
                Assert.Equal(i, list[i]);
            }
        }
        
        // Create a new manager with the same storage to test recovery of large list
        var logger = LoggerFactory.CreateLogger<StateMachineManager>();
        var manager2 = new StateMachineManager(storage, logger, SessionPool);
        await manager2.InitializeAsync(CancellationToken.None);
        
        var list2 = new DurableList<int>("largeList", manager2, codec, SessionPool);
        
        // Assert - Large list is correctly recovered
        Assert.Equal(itemCount, list2.Count);
        for (int i = 0; i < itemCount; i++)
        {
            if (i % 2 == 0)
            {
                Assert.Equal(i * 2, list2[i]);
            }
            else
            {
                Assert.Equal(i, list2[i]);
            }
        }
    }
}