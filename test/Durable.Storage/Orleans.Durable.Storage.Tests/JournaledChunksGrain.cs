using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Journaling;
using Orleans.Placement;
using Orleans.Runtime;

namespace Orleans.Durable.Storage.Tests;

/// <summary>A journaled grain over Clog's journal storage, whose state is a durable list of chunks.</summary>
public interface IChunksGrain : IGrainWithStringKey
{
    /// <summary>Appends <paramref name="chunk"/> and writes the grain's state; returns the chunks it holds.</summary>
    Task<int> AppendAsync(byte[] chunk);

    /// <summary>
    /// Appends <paramref name="chunk"/> and writes the grain's state, waiting for the write until
    /// <paramref name="cancellationToken"/> is cancelled; returns the chunks it holds.
    /// </summary>
    Task<int> AppendUntilCancelledAsync(byte[] chunk, CancellationToken cancellationToken);

    /// <summary>Returns the chunks' bytes, concatenated.</summary>
    Task<byte[]> ReadAsync();

    /// <summary>Deactivates the activation once idle, so the next call recovers the state from its journal.</summary>
    Task DeactivateAsync();

    /// <summary>Returns the activation's own identity, which a new activation draws anew.</summary>
    Task<Guid> ActivationAsync();

    /// <summary>Returns the silo the activation runs on.</summary>
    Task<SiloAddress> SiloAsync();
}

/// <summary>The chunks grain over the default journal storage provider, Clog's.</summary>
public interface IJournaledChunksGrain : IChunksGrain;

/// <summary>The chunks grain over the journal storage provider named <see cref="NamedJournaledChunksGrain.Provider"/>.</summary>
public interface INamedJournaledChunksGrain : IChunksGrain;

/// <summary>The chunks grain over the default journal storage provider, placed on its journal's primary.</summary>
public interface IPlacedJournaledChunksGrain : IChunksGrain;

/// <summary>
/// The chunks grain's behaviour. Each grain class names its placement: one which activates on the silo which calls it
/// first lets a test choose the silo whose storage reads its journal.
/// </summary>
public abstract class ChunksGrain(IDurableList<byte[]> chunks, IDurableStateManager state, ILocalSiloDetails silo) : Grain, IChunksGrain
{
    private readonly Guid _activation = Guid.NewGuid();

    /// <inheritdoc/>
    public async Task<int> AppendAsync(byte[] chunk)
    {
        chunks.Add(chunk);
        await state.WriteStateAsync(CancellationToken.None);
        return chunks.Count;
    }

    /// <inheritdoc/>
    public async Task<int> AppendUntilCancelledAsync(byte[] chunk, CancellationToken cancellationToken)
    {
        chunks.Add(chunk);
        await state.WriteStateAsync(cancellationToken);
        return chunks.Count;
    }

    /// <inheritdoc/>
    public Task<byte[]> ReadAsync() => Task.FromResult(chunks.SelectMany(static chunk => chunk).ToArray());

    /// <inheritdoc/>
    public Task DeactivateAsync()
    {
        this.DeactivateOnIdle();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<Guid> ActivationAsync() => Task.FromResult(_activation);

    /// <inheritdoc/>
    public Task<SiloAddress> SiloAsync() => Task.FromResult(silo.SiloAddress);
}

/// <inheritdoc cref="IJournaledChunksGrain"/>
[PreferLocalPlacement]
public sealed class JournaledChunksGrain([FromKeyedServices("chunks")] IDurableList<byte[]> chunks, IDurableStateManager state, ILocalSiloDetails silo) : ChunksGrain(chunks, state, silo), IJournaledChunksGrain;

/// <inheritdoc cref="INamedJournaledChunksGrain"/>
[PreferLocalPlacement]
[JournalStorageProvider(Provider)]
public sealed class NamedJournaledChunksGrain([FromKeyedServices("chunks")] IDurableList<byte[]> chunks, IDurableStateManager state, ILocalSiloDetails silo) : ChunksGrain(chunks, state, silo), INamedJournaledChunksGrain
{
    /// <summary>The name of the journal storage provider the grain's state uses.</summary>
    public const string Provider = "clog";
}

/// <inheritdoc cref="IPlacedJournaledChunksGrain"/>
[JournalPlacement]
public sealed class PlacedJournaledChunksGrain([FromKeyedServices("chunks")] IDurableList<byte[]> chunks, IDurableStateManager state, ILocalSiloDetails silo) : ChunksGrain(chunks, state, silo), IPlacedJournaledChunksGrain;

/// <summary>The JSON journal format's metadata for <see cref="JournaledChunksGrain"/>'s state and its manager's own, which the test silos register.</summary>
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(byte[]))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(uint))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(ulong))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(DateTime))]
[JsonSerializable(typeof(DateTimeOffset))]
[JsonSerializable(typeof(TimeSpan))]
[JsonSerializable(typeof(Guid))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(float))]
[JsonSerializable(typeof(decimal))]
[JsonSerializable(typeof(short))]
[JsonSerializable(typeof(ushort))]
[JsonSerializable(typeof(byte))]
[JsonSerializable(typeof(sbyte))]
[JsonSerializable(typeof(char))]
internal sealed partial class JournaledChunksJsonContext : JsonSerializerContext;
