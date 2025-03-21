using Orleans.Core.Internal;
using Xunit;

namespace Orleans.Journaling.Tests;

public class DurableGrainTests : IntegrationTestBase
{
    [Fact]
    public async Task DurableGrain_State_Persistence_Test()
    {
        // Arrange
        var grain = Client.GetGrain<ITestDurableGrain>(Guid.NewGuid());

        // Act - Set state properties and persist
        await grain.SetTestValues("Test Name", 42);

        // Assert
        Assert.Equal("Test Name", await grain.GetName());
        Assert.Equal(42, await grain.GetCounter());

        // Force deactivation and get a new reference
        await grain.Cast<IGrainManagementExtension>().DeactivateOnIdle();

        // Assert - State should be recovered
        Assert.Equal("Test Name", await grain.GetName());
        Assert.Equal(42, await grain.GetCounter());
    }

    [Fact]
    public async Task DurableGrain_Update_State_Test()
    {
        // Arrange
        var grain = Client.GetGrain<ITestDurableGrain>(Guid.NewGuid());

        // Act - Set state and persist
        await grain.SetTestValues("Initial Name", 10);

        // Update state and persist again
        await grain.SetTestValues("Updated Name", 20);

        // Assert
        Assert.Equal("Updated Name", await grain.GetName());
        Assert.Equal(20, await grain.GetCounter());

        // Force deactivation and get a new reference
        await grain.Cast<IGrainManagementExtension>().DeactivateOnIdle();

        // Assert - Updated state should be recovered
        Assert.Equal("Updated Name", await grain.GetName());
        Assert.Equal(20, await grain.GetCounter());
    }

    [Fact]
    public async Task DurableGrain_Complex_Types_Test()
    {
        // Arrange
        var grain = Client.GetGrain<ITestDurableGrainWithComplexState>(Guid.NewGuid());

        // Act - Set complex state and persist
        var person = new TestPerson { Id = 1, Name = "John Doe", Age = 30 };
        var items = new List<string> { "Item1", "Item2", "Item3" };
        await grain.SetTestValues(person, items);

        // Assert
        var retrievedPerson = await grain.GetPerson();
        var retrievedItems = await grain.GetItems();

        Assert.Equal("John Doe", retrievedPerson.Name);
        Assert.Equal(3, retrievedItems.Count);

        // Force deactivation and get a new reference
        await grain.Cast<IGrainManagementExtension>().DeactivateOnIdle();

        // Assert - Complex state should be recovered
        retrievedPerson = await grain.GetPerson();
        retrievedItems = await grain.GetItems();

        Assert.NotNull(retrievedPerson);
        Assert.Equal(1, retrievedPerson.Id);
        Assert.Equal("John Doe", retrievedPerson.Name);
        Assert.Equal(30, retrievedPerson.Age);

        Assert.Equal(3, retrievedItems.Count);
        Assert.Equal("Item1", retrievedItems[0]);
        Assert.Equal("Item2", retrievedItems[1]);
        Assert.Equal("Item3", retrievedItems[2]);
    }

    [Fact]
    public async Task DurableGrain_Multiple_Collections_Test()
    {
        // Arrange
        var grain = Client.GetGrain<ITestMultiCollectionGrainInterface>(Guid.NewGuid());

        // Act - Populate collections and persist
        await grain.AddToDictionary("key1", 1);
        await grain.AddToDictionary("key2", 2);
        await grain.AddToList("item1");
        await grain.AddToList("item2");
        await grain.AddToQueue(100);
        await grain.AddToQueue(200);
        await grain.AddToSet("set1");
        await grain.AddToSet("set2");

        // Assert
        Assert.Equal(2, await grain.GetDictionaryCount());
        Assert.Equal(2, await grain.GetListCount());
        Assert.Equal(2, await grain.GetQueueCount());
        Assert.Equal(2, await grain.GetSetCount());

        // Force deactivation and get a new reference
        await grain.Cast<IGrainManagementExtension>().DeactivateOnIdle();

        // Assert - All collections should be recovered
        Assert.Equal(2, await grain.GetDictionaryCount());
        Assert.Equal(1, await grain.GetDictionaryValue("key1"));
        Assert.Equal(2, await grain.GetDictionaryValue("key2"));

        Assert.Equal(2, await grain.GetListCount());
        Assert.Equal("item1", await grain.GetListItem(0));
        Assert.Equal("item2", await grain.GetListItem(1));

        Assert.Equal(2, await grain.GetQueueCount());
        Assert.Equal(100, await grain.PeekQueueItem());

        Assert.Equal(2, await grain.GetSetCount());
        Assert.True(await grain.ContainsSetItem("set1"));
        Assert.True(await grain.ContainsSetItem("set2"));
    }

    [Fact]
    public async Task DurableGrain_State_Modifications_Test()
    {
        // Arrange
        var grain = Client.GetGrain<ITestMultiCollectionGrainInterface>(Guid.NewGuid());

        // Act - Populate initial state and persist
        await grain.AddToDictionary("key1", 1);
        await grain.AddToList("item1");
        await grain.AddToQueue(100);
        await grain.AddToSet("set1");

        // Modify state and persist again
        await grain.AddToDictionary("key2", 2);
        await grain.AddToDictionary("key1", 10); // Update via interface method
        await grain.AddToList("item2");
        await grain.AddToQueue(200);
        await grain.AddToSet("set2");

        // Assert
        Assert.Equal(2, await grain.GetDictionaryCount());
        Assert.Equal(10, await grain.GetDictionaryValue("key1"));
        Assert.Equal(2, await grain.GetListCount());
        Assert.Equal(2, await grain.GetQueueCount());
        Assert.Equal(2, await grain.GetSetCount());

        // Force deactivation and get a new reference
        await grain.Cast<IGrainManagementExtension>().DeactivateOnIdle();

        // Assert - Modified state should be recovered
        Assert.Equal(2, await grain.GetDictionaryCount());
        Assert.Equal(10, await grain.GetDictionaryValue("key1"));
        Assert.Equal(2, await grain.GetDictionaryValue("key2"));

        Assert.Equal(2, await grain.GetListCount());
        Assert.Equal("item1", await grain.GetListItem(0));
        Assert.Equal("item2", await grain.GetListItem(1));

        // Further modify the state
        await grain.RemoveFromDictionary("key1");
        await grain.RemoveListItemAt(0);
        await grain.DequeueItem();
        await grain.RemoveFromSet("set1");

        // Assert the modifications
        Assert.Equal(1, await grain.GetDictionaryCount());
        Assert.Equal(1, await grain.GetListCount());
        Assert.Equal(1, await grain.GetQueueCount());
        Assert.Equal(1, await grain.GetSetCount());
    }
}
