using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Journaling;

namespace Orleans.Journaling.Tests;

public class TestMultiCollectionGrain : DurableGrain<MultiCollectionState>, ITestMultiCollectionGrainInterface
{
    public class MultiCollectionState : DurableState
    {
        public virtual DurableDictionary<string, int>? Dictionary { get; set; }
        public virtual DurableList<string>? List { get; set; }
        public virtual DurableQueue<int>? Queue { get; set; }
        public virtual DurableSet<string>? Set { get; set; }
    }

    private DurableDictionary<string, int> _dictionary = null!;
    private DurableList<string> _list = null!;
    private DurableQueue<int> _queue = null!;
    private DurableSet<string> _set = null!;

    public TestMultiCollectionGrain(ILogger<TestMultiCollectionGrain> logger)
    {
        _dictionary = this.State.CreateDurableDictionary<string, int>("dictionary");
        _list = this.State.CreateDurableList<string>("list");
        _queue = this.State.CreateDurableQueue<int>("queue");
        _set = this.State.CreateDurableSet<string>("set");
    }

    // Dictionary operations
    public async Task AddToDictionary(string key, int value)
    {
        this.State.Dictionary[key] = value;
        await this.State.WriteStateAsync();
    }

    public async Task RemoveFromDictionary(string key)
    {
        this.State.Dictionary.Remove(key);
        await this.State.WriteStateAsync();
    }

    public async Task<int> GetDictionaryValue(string key)
    {
        return await Task.FromResult(this.State.Dictionary[key]);
    }

    public async Task<int> GetDictionaryCount()
    {
        return await Task.FromResult(this.State.Dictionary.Count);
    }

    // List operations
    public async Task AddToList(string item)
    {
        this.State.List.Add(item);
        await this.State.WriteStateAsync();
    }

    public async Task RemoveListItemAt(int index)
    {
        this.State.List.RemoveAt(index);
        await this.State.WriteStateAsync();
    }

    public async Task<string> GetListItem(int index)
    {
        return await Task.FromResult(this.State.List[index]);
    }

    public async Task<int> GetListCount()
    {
        return await Task.FromResult(this.State.List.Count);
    }

    // Queue operations
    public async Task AddToQueue(int item)
    {
        this.State.Queue.Enqueue(item);
        await this.State.WriteStateAsync();
    }

    public async Task<int> DequeueItem()
    {
        var item = this.State.Queue.Dequeue();
        return await Task.FromResult(item);
    }

    public async Task<int> PeekQueueItem()
    {
        return await Task.FromResult(this.State.Queue.Peek());
    }

    public async Task<int> GetQueueCount()
    {
        return await Task.FromResult(this.State.Queue.Count);
    }

    // Set operations
    public async Task AddToSet(string item)
    {
        this.State.Set.Add(item);
        await this.State.WriteStateAsync();
    }

    public async Task RemoveFromSet(string item)
    {
        this.State.Set.Remove(item);
        await this.State.WriteStateAsync();
    }

    public async Task<bool> ContainsSetItem(string item)
    {
        return await Task.FromResult(this.State.Set.Contains(item));
    }

    public async Task<int> GetSetCount()
    {
        return await Task.FromResult(this.State.Set.Count);
    }
}
