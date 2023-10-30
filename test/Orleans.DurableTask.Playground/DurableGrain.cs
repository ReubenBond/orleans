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
    protected DurableDictionary<K, V> GetOrCreateDictionary<K, V>(string name) where K : notnull => GetOrCreateStateMachine<DurableDictionary<K, V>>(name);
    protected DurableList<T> GetOrCreateList<T>(string name) => GetOrCreateStateMachine<DurableList<T>>(name);
    protected DurableSet<T> GetOrCreateSet<T>(string name) => GetOrCreateStateMachine<DurableSet<T>>(name);
    protected DurableQueue<T> GetOrCreateQueue<T>(string name) => GetOrCreateStateMachine<DurableQueue<T>>(name);
    protected DurableValue<T> GetOrCreateValue<T>(string name) => GetOrCreateStateMachine<DurableValue<T>>(name);
    protected DurableTaskCompletionSource<T> GetOrCreateTaskCompletionSource<T>(string name) => GetOrCreateStateMachine<DurableTaskCompletionSource<T>>(name);

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

public class ShoppingCartGrain : DurableGrain, IShoppingCartGrain
{
    private readonly DurableDictionary<string, int> _cart;
    private readonly DurableValue<long> _version;

    public ShoppingCartGrain()
    {
        _cart = GetOrCreateDictionary<string, int>("shopping-cart");
        _version = GetOrCreateValue<long>("version");
    }

    public async ValueTask<(bool Success, long Version)> UpdateItem(string itemId, int quantity, long version)
    {
        if (_version.Value != version)
        {
            // Conflict
            return (false, _version.Value);
        }

        if (quantity == 0)
        {
            _cart.Remove(itemId);
        }
        else
        {
            _cart[itemId] = quantity;
        }

        _version.Value++;
        await WriteStateAsync();
        return (true, _version.Value);
    }

    public ValueTask<(Dictionary<string, int> Contents, long Version)> GetCart() => new((_cart.ToDictionary(), _version.Value));
    public ValueTask<long> GetVersion() => new(_version.Value);

    public async ValueTask<(bool Success, long Version)> Clear(long version)
    {
        if (_version.Value != version)
        {
            // Conflict
            return (false, _version.Value);
        }

        _cart.Clear();
        _version.Value++;
        await WriteStateAsync();
        return (true, _version.Value);
    }
}

public interface IDictionaryGrain<K, V> : IGrainWithStringKey where K : notnull
{
    ValueTask<(bool Success, V? Value, long Version)> TryGetValueAsync(K key);
    ValueTask<(bool Success, long Version)> TryAddAsync(K key, V value, long version);
    ValueTask<(bool Success, long Version)> SetAsync(K key, V value, long version);
    ValueTask<(bool Success, long Version)> RemoveAsync(K key, long version);
    ValueTask<(bool Success, long Version)> ClearAsync(long version);
    IAsyncEnumerable<(K Key, V Value, long Version)> GetValuesAsync();
}

public class DictionaryGrain<K, V> : DurableGrain, IDictionaryGrain<K, V> where K : notnull
{
    private readonly DurableDictionary<K, V> _dictionary;
    private readonly DurableValue<long> _version;
    public DictionaryGrain()
    {
        _dictionary = GetOrCreateDictionary<K, V>("shopping-cart");
        _version = GetOrCreateValue<long>("version");
    }

    public ValueTask<(bool Success, V? Value, long Version)> TryGetValueAsync(K key)
    {
        var success = _dictionary.TryGetValue(key, out var value);
        return new((success, value, _version.Value));
    }

    public async ValueTask<(bool Success, long Version)> TryAddAsync(K key, V value, long version)
    {
        if (_version.Value != version)
        {
            // Conflict
            return (false, _version.Value);
        }

        if (_dictionary.TryAdd(key, value))
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

        if (_dictionary.Remove(key))
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

        _dictionary.Clear();
        _version.Value++;
        await WriteStateAsync();
        return (true, _version.Value);
    }

    public async IAsyncEnumerable<(K Key, V Value, long Version)> GetValuesAsync()
    {
        await Task.CompletedTask; // Make C# happy.
        foreach (var kvp in _dictionary)
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

        _dictionary[key] = value;
        _version.Value++;
        await WriteStateAsync();
        return (true, _version.Value);
    }
}
