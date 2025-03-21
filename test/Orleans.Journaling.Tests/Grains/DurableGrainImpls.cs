using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Journaling.Tests.Grains;

[GenerateSerializer]
public sealed class TestDurableGrainState
{
    [Id(0)]
    public string Name { get; set; } = string.Empty;
    [Id(1)]
    public int Counter { get; set; }
}

public class TestDurableGrain(
    [FromKeyedServices("state")] IPersistentState<TestDurableGrainState> state) : DurableGrain, ITestDurableGrain
{
    public Task<string> GetName() => Task.FromResult(state.State.Name);
    public Task<int> GetCounter() => Task.FromResult(state.State.Counter);

    public async Task SetTestValues(string name, int counter)
    {
        state.State.Name = name;
        state.State.Counter = counter;
        await WriteStateAsync();
    }
}

public class TestDurableGrainWithComplexState(
    [FromKeyedServices("person")] IDurableValue<TestPerson> person,
    [FromKeyedServices("list")] IDurableList<string> list) : DurableGrain, ITestDurableGrainWithComplexState
{
    private readonly IDurableValue<TestPerson> _person = person;
    private readonly IDurableList<string> _list = list;

    public Task<TestPerson> GetPerson() => Task.FromResult(_person.Value ?? new TestPerson());
    public Task<IReadOnlyList<string>> GetItems() => Task.FromResult<IReadOnlyList<string>>(_list.AsReadOnly());

    public async Task SetTestValues(TestPerson person, List<string> items)
    {
        _person.Value = person;
        _list.Clear();
        _list.AddRange(items);
        await WriteStateAsync();
    }
}

public class TestDurableGrainWithCollections(
    [FromKeyedServices("dictionary")] IDurableDictionary<string, int> dictionary,
    [FromKeyedServices("list")] IDurableList<string> list,
    [FromKeyedServices("queue")] IDurableQueue<int> queue,
    [FromKeyedServices("set")] IDurableSet<string> set,
    ILogger<TestDurableGrainWithCollections> logger) : Grain, ITestMultiCollectionGrainInterface
{
    private readonly ILogger<TestDurableGrainWithCollections> _logger = logger;

    public async Task AddToDictionary(string key, int value)
    {
        dictionary[key] = value;
        await WriteStateAsync();
    }

    public async Task RemoveFromDictionary(string key)
    {
        dictionary.Remove(key);
        await WriteStateAsync();
    }

    public Task<int> GetDictionaryValue(string key) => Task.FromResult(dictionary[key]);
    public Task<int> GetDictionaryCount() => Task.FromResult(dictionary.Count);

    public async Task AddToList(string item)
    {
        list.Add(item);
        await WriteStateAsync();
    }

    public async Task RemoveListItemAt(int index)
    {
        list.RemoveAt(index);
        await WriteStateAsync();
    }

    public Task<string> GetListItem(int index) => Task.FromResult(list[index]);
    public Task<int> GetListCount() => Task.FromResult(list.Count);

    public async Task AddToQueue(int item)
    {
        queue.Enqueue(item);
        await WriteStateAsync();
    }

    public async Task<int> DequeueItem()
    {
        var item = queue.Dequeue();
        await WriteStateAsync();
        return item;
    }

    public Task<int> PeekQueueItem() => Task.FromResult(queue.Peek());
    public Task<int> GetQueueCount() => Task.FromResult(queue.Count);

    public async Task AddToSet(string item)
    {
        set.Add(item);
        await WriteStateAsync();
    }

    public async Task RemoveFromSet(string item)
    {
        set.Remove(item);
        await WriteStateAsync();
    }

    public Task<bool> ContainsSetItem(string item) => Task.FromResult(set.Contains(item));
    public Task<int> GetSetCount() => Task.FromResult(set.Count);

    public Task WriteStateAsync()
    {
        return WriteStateAsync();
    }
}
