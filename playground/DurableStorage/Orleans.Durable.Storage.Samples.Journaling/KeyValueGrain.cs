using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Journaling;
using Orleans.Runtime;

namespace Orleans.Durable.Storage.Samples.Journaling;

/// <summary>A dictionary of strings whose state Orleans Journaling keeps in Clog.</summary>
public interface IKeyValueGrain : IGrainWithStringKey
{
    /// <summary>Sets <paramref name="key"/> to <paramref name="value"/> and writes the grain's state.</summary>
    Task SetAsync(string key, string value);

    /// <summary>Removes <paramref name="key"/> and writes the grain's state; answers whether the grain held it.</summary>
    Task<bool> RemoveAsync(string key);

    /// <summary>Returns the grain's entries and the silo its activation runs on.</summary>
    Task<KeyValueSnapshot> ReadAsync();
}

/// <summary>A key-value grain's entries, as one activation on one silo holds them.</summary>
/// <param name="Silo">The name of the silo the activation runs on.</param>
/// <param name="Entries">The entries, ordered by key.</param>
[GenerateSerializer]
[Immutable]
public sealed record KeyValueSnapshot([property: Id(0)] string Silo, [property: Id(1)] IReadOnlyList<KeyValuePair<string, string>> Entries);

/// <summary>
/// The key-value grain: its dictionary is a durable dictionary, which Orleans Journaling writes to the grain's journal in
/// Clog, and a new activation, on any silo, recovers from that journal. <see cref="JournalPlacementAttribute"/> places
/// each activation on the silo whose Clog node is its journal's primary, so its writes need no forwarding.
/// </summary>
[JournalPlacement]
internal sealed class KeyValueGrain(
    [FromKeyedServices("entries")] IDurableDictionary<string, string> entries,
    IDurableStateManager state,
    ILocalSiloDetails silo) : Grain, IKeyValueGrain
{
    /// <inheritdoc/>
    public async Task SetAsync(string key, string value)
    {
        entries[key] = value;
        await state.WriteStateAsync(CancellationToken.None);
    }

    /// <inheritdoc/>
    public async Task<bool> RemoveAsync(string key)
    {
        if (!entries.Remove(key))
        {
            return false;
        }

        await state.WriteStateAsync(CancellationToken.None);
        return true;
    }

    /// <inheritdoc/>
    public Task<KeyValueSnapshot> ReadAsync() =>
        Task.FromResult(new KeyValueSnapshot(silo.Name, [.. entries.OrderBy(static entry => entry.Key, StringComparer.Ordinal)]));
}

/// <summary>The JSON journal format's metadata for the grain's state and its state manager's own.</summary>
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(uint))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(ulong))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(DateTime))]
[JsonSerializable(typeof(DateTimeOffset))]
[JsonSerializable(typeof(TimeSpan))]
[JsonSerializable(typeof(Guid))]
internal sealed partial class KeyValueJsonContext : JsonSerializerContext;
