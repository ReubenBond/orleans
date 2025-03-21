using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace Orleans.Journaling.Tests;

public class TestMultiCollectionGrain : DurableGrain, ITestMultiCollectionGrainInterface
{
    private readonly DurableDictionary<string, int> _dictionary;
    private readonly DurableList<string> _list;
    private readonly DurableQueue<int> _queue;
    private readonly DurableSet<string> _set;
    
    public TestMultiCollectionGrain(ILogger<TestMultiCollectionGrain> logger) 
        : base(logger)
    {
        _dictionary = CreateDurableDictionary<string, int>("dictionary");
        _list = CreateDurableList<string>("list");
        _queue = CreateDurableQueue<int>("queue");
        _set = CreateDurableSet<string>("set");
    }
    
    // Dictionary operations
    public Task AddToDictionary(string key, int value)
    {
        _dictionary[key] = value;
        return WriteStateAsync();
    }
    
    public Task RemoveFromDictionary(string key)
    {
        _dictionary.Remove(key);
        return WriteStateAsync();
    }
    
    public Task<int> GetDictionaryValue(string key)
    {
        return Task.FromResult(_dictionary[key]);
    }
    
    public Task<int> GetDictionaryCount()
    {
        return Task.FromResult(_dictionary.Count);
    }
    
    // List operations
    public Task AddToList(string item)
    {
        _list.Add(item);
        return WriteStateAsync();
    }
    
    public Task RemoveListItemAt(int index)
    {
        _list.RemoveAt(index);
        return WriteStateAsync();
    }
    
    public Task<string> GetListItem(int index)
    {
        return Task.FromResult(_list[index]);
    }
    
    public Task<int> GetListCount()
    {
        return Task.FromResult(_list.Count);
    }
    
    // Queue operations
    public Task AddToQueue(int item)
    {
        _queue.Enqueue(item);
        return WriteStateAsync();
    }
    
    public Task<int> DequeueItem()
    {
        var item = _queue.Dequeue();
        WriteStateAsync();
        return Task.FromResult(item);
    }
    
    public Task<int> PeekQueueItem()
    {
        return Task.FromResult(_queue.Peek());
    }
    
    public Task<int> GetQueueCount()
    {
        return Task.FromResult(_queue.Count);
    }
    
    // Set operations
    public Task AddToSet(string item)
    {
        _set.Add(item);
        return WriteStateAsync();
    }
    
    public Task RemoveFromSet(string item)
    {
        _set.Remove(item);
        return WriteStateAsync();
    }
    
    public Task<bool> ContainsSetItem(string item)
    {
        return Task.FromResult(_set.Contains(item));
    }
    
    public Task<int> GetSetCount()
    {
        return Task.FromResult(_set.Count);
    }
}