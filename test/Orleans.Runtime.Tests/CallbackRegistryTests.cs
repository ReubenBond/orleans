using Orleans.Runtime;
using Xunit;

namespace UnitTests;

[Trait("Category", "BVT")]
public class CallbackRegistryTests
{
    [Fact]
    public void SupportsAddGetRemoveAndSnapshots()
    {
        var registry = new CallbackRegistry<Entry>(static value => value.Key);
        var key = new CorrelationId(42);
        var value = new Entry(key);

        Assert.True(registry.TryAdd(key, value));
        Assert.False(registry.TryAdd(key, new(key)));
        Assert.True(registry.TryGetValue(key, out var found));
        Assert.Same(value, found);
        Assert.Contains(value, registry.GetValues());
        Assert.True(registry.TryRemove(key, out var removed));
        Assert.Same(value, removed);
        Assert.False(registry.TryGetValue(key, out _));
        Assert.Empty(registry.GetValues());
    }

    [Fact]
    public void SupportsCollidingKeys()
    {
        var registry = new CallbackRegistry<Entry>(static value => value.Key);
        var first = new Entry(new(1));
        var second = new Entry(new(1_025));

        Assert.True(registry.TryAdd(first.Key, first));
        Assert.True(registry.TryAdd(second.Key, second));
        Assert.True(registry.TryGetValue(first.Key, out var foundFirst));
        Assert.True(registry.TryGetValue(second.Key, out var foundSecond));
        Assert.Same(first, foundFirst);
        Assert.Same(second, foundSecond);
        Assert.True(registry.TryRemove(second.Key, out var removedSecond));
        Assert.True(registry.TryRemove(first.Key, out var removedFirst));
        Assert.Same(second, removedSecond);
        Assert.Same(first, removedFirst);
    }

    [Fact]
    public async Task SupportsConcurrentAddAndRemove()
    {
        const int workerCount = 16;
        const int operationsPerWorker = 1_000;
        var registry = new CallbackRegistry<Entry>(static value => value.Key);
        var tasks = new Task[workerCount];

        for (var workerId = 0; workerId < workerCount; workerId++)
        {
            var id = workerId;
            tasks[id] = Task.Run(() =>
            {
                for (var operation = 0; operation < operationsPerWorker; operation++)
                {
                    var key = new CorrelationId(((long)id << 32) | (uint)operation);
                    var value = new Entry(key);
                    Assert.True(registry.TryAdd(key, value));
                    Assert.True(registry.TryRemove(key, out var removed));
                    Assert.Same(value, removed);
                }
            });
        }

        await Task.WhenAll(tasks);
        Assert.Empty(registry.GetValues());
    }

    private sealed record Entry(CorrelationId Key);
}
