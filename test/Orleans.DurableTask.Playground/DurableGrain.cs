using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableTask.Playground;
using Orleans.Journaling;
using Orleans.Runtime;

namespace Orleans.DurableTasks.Playground;

public abstract class DurableGrain : Grain, IGrainBase
{
    protected DurableGrain()
    {
        StateMachineManager = ServiceProvider.GetRequiredService<IStateMachineManager>();
        if (StateMachineManager is ILifecycleParticipant<IGrainLifecycle> participant)
        {
            participant.Participate(((IGrainBase)this).GrainContext.ObservableLifecycle);
        }
        var taskStorage = ServiceProvider.GetRequiredService<DurableTaskGrainStorage>();
    }
    
    protected IStateMachineManager StateMachineManager { get; }

    protected TStateMachine GetOrCreateStateMachine<TStateMachine>(string name) where TStateMachine : class, IDurableStateMachine
        => GetOrCreateStateMachine(name, static sp => sp.GetRequiredService<TStateMachine>(), ServiceProvider);

    protected TStateMachine GetOrCreateStateMachine<TState, TStateMachine>(string name, Func<TState, TStateMachine> createStateMachine, TState state) where TStateMachine : class, IDurableStateMachine
    {
        if (StateMachineManager.TryGetStateMachine(name, out var stateMachine))
        {
            return stateMachine as TStateMachine
                ?? throw new InvalidOperationException($"A state machine named '{name}' already exists with an incompatible type {stateMachine.GetType()} versus {typeof(TStateMachine)}");
        }

        var result = createStateMachine(state);
        StateMachineManager.RegisterStateMachine(name, result);
        return result;
    }

    protected ValueTask WriteStateAsync(CancellationToken cancellationToken = default) => StateMachineManager.WriteStateAsync(cancellationToken);
}

public interface IShoppingCartGrain
{
    ValueTask<(bool Success, long Version)> UpdateItem(string itemId, int quantity, long version);
    ValueTask<(Dictionary<string, int> Contents, long Version)> GetCart();
    ValueTask<long> GetVersion();
    ValueTask<(bool Success, long Version)> Clear(long version);
}

public class ShoppingCartGrain(
    [FromKeyedServices("shopping-cart")] IDurableDictionary<string, int> cart,
    [FromKeyedServices("version")] IDurableValue<long> version) : DurableGrain, IShoppingCartGrain
{
    private readonly IDurableValue<long> _version = version;

    public async ValueTask<(bool Success, long Version)> UpdateItem(string itemId, int quantity, long version)
    {
        if (_version.Value != version)
        {
            // Conflict
            return (false, _version.Value);
        }

        if (quantity == 0)
        {
            cart.Remove(itemId);
        }
        else
        {
            cart[itemId] = quantity;
        }

        _version.Value++;
        await WriteStateAsync();
        return (true, _version.Value);
    }

    public ValueTask<(Dictionary<string, int> Contents, long Version)> GetCart() => new((cart.ToDictionary(), _version.Value));
    public ValueTask<long> GetVersion() => new(_version.Value);

    public async ValueTask<(bool Success, long Version)> Clear(long version)
    {
        if (_version.Value != version)
        {
            // Conflict
            return (false, _version.Value);
        }

        cart.Clear();
        _version.Value++;
        await WriteStateAsync();
        return (true, _version.Value);
    }
}

[Alias("IDictionaryGrain`2")]
public interface IDictionaryGrain<K, V> : IGrainWithStringKey where K : notnull
{
    [Alias("TryGetValueAsync")]
    ValueTask<(bool Success, V? Value, long Version)> TryGetValueAsync(K key);
    [Alias("TryAddAsync")]
    ValueTask<(bool Success, long Version)> TryAddAsync(K key, V value, long version);
    [Alias("SetAsync")]
    ValueTask<(bool Success, long Version)> SetAsync(K key, V value, long version);
    [Alias("RemoveAsync")]
    ValueTask<(bool Success, long Version)> RemoveAsync(K key, long version);
    [Alias("ClearAsync")]
    ValueTask<(bool Success, long Version)> ClearAsync(long version);
    [Alias("GetValuesAsync")]
    IAsyncEnumerable<(K Key, V Value, long Version)> GetValuesAsync();
}

public class DictionaryGrain<K, V>(
    [FromKeyedServices("values")] IDurableDictionary<K, V> dictionary,
    [FromKeyedServices("version")] IDurableValue<long> version) : DurableGrain, IDictionaryGrain<K, V> where K : notnull
{
    private readonly IDurableValue<long> _version = version;

    public ValueTask<(bool Success, V? Value, long Version)> TryGetValueAsync(K key)
    {
        var success = dictionary.TryGetValue(key, out var value);
        return new((success, value, _version.Value));
    }

    public async ValueTask<(bool Success, long Version)> TryAddAsync(K key, V value, long version)
    {
        if (_version.Value != version)
        {
            // Conflict
            return (false, _version.Value);
        }

        if (dictionary.TryAdd(key, value))
        {
            _version.Value++;
            await WriteStateAsync();
            return (true, _version.Value);
        }

        return (false, _version.Value);
    }

    public async ValueTask<(bool Success, long Version)> RemoveAsync(K key, long version)
    {
        if (_version.Value != version)
        {
            // Conflict
            return (false, _version.Value);
        }

        if (dictionary.Remove(key))
        {
            _version.Value++;
            await WriteStateAsync();
            return (true, _version.Value);
        }

        return (false, _version.Value);
    }

    public async ValueTask<(bool Success, long Version)> ClearAsync(long version)
    {
        if (_version.Value != version)
        {
            // Conflict
            return (false, _version.Value);
        }

        dictionary.Clear();
        _version.Value++;
        await WriteStateAsync();
        return (true, _version.Value);
    }

    public async IAsyncEnumerable<(K Key, V Value, long Version)> GetValuesAsync()
    {
        await Task.CompletedTask; // Make C# happy.
        foreach (var kvp in dictionary)
        {
            yield return (kvp.Key, kvp.Value, _version.Value);
        }
    }

    public async ValueTask<(bool Success, long Version)> SetAsync(K key, V value, long version)
    {
        if (_version.Value != version)
        {
            // Conflict
            return (false, _version.Value);
        }

        dictionary[key] = value;
        _version.Value++;
        await WriteStateAsync();
        return (true, _version.Value);
    }
}
