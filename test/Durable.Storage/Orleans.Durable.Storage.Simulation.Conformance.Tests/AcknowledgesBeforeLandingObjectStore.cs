using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.ObjectStore;

namespace Orleans.Durable.Storage.Simulation.Conformance.Tests;

/// <summary>
/// A race mutant of an object store: it answers a non-empty create or extent append at once and lands it a step later,
/// so a read between them finds the slot absent although its writer completed it (OL-I2, OL-I10, LL-I6). Empty creates,
/// the recovery's SEALs and markers, and every other call pass through. Its disposal waits for every landing, so a
/// case's buffers are released before the simulation's leak check.
/// </summary>
internal sealed class AcknowledgesBeforeLandingObjectStore(IObjectStore inner) : IObjectStore, IAppendableObjectStore, IAsyncDisposable
{
    private readonly List<Task> _landings = [];

    public ValueTask<ObjectCreateOutcome> CreateAsync(ObjectName name, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default)
    {
        if (content.Length == 0)
        {
            return inner.CreateAsync(name, content, crc32c, cancellationToken);
        }

        _landings.Add(LandCreateAsync(name, content.Retain(), crc32c));
        return ValueTask.FromResult<ObjectCreateOutcome>(ObjectCreateOutcome.Created.Instance);
    }

    public ValueTask<ObjectCreateOutcome> CreateAppendableAsync(ObjectName name, CancellationToken cancellationToken = default) =>
        ((IAppendableObjectStore)inner).CreateAppendableAsync(name, cancellationToken);

    public ValueTask<ObjectAppendOutcome> AppendAsync(ObjectName name, long expectedLength, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default)
    {
        if (content.Length == 0)
        {
            return ((IAppendableObjectStore)inner).AppendAsync(name, expectedLength, content, crc32c, cancellationToken);
        }

        _landings.Add(LandAppendAsync(name, expectedLength, content.Retain(), crc32c));
        return ValueTask.FromResult<ObjectAppendOutcome>(new ObjectAppendOutcome.Appended(expectedLength + content.Length));
    }

    public ValueTask<ObjectSealOutcome> SealAsync(ObjectName name, CancellationToken cancellationToken = default) =>
        ((IAppendableObjectStore)inner).SealAsync(name, cancellationToken);

    public ValueTask ReplaceWithTombstoneAsync(ObjectName name, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default) =>
        ((IAppendableObjectStore)inner).ReplaceWithTombstoneAsync(name, content, crc32c, cancellationToken);

    public ValueTask<ArcBuffer?> ReadAsync(ObjectName name, long offset, int length, CancellationToken cancellationToken = default) =>
        inner.ReadAsync(name, offset, length, cancellationToken);

    public ValueTask<ObjectProperties?> GetPropertiesAsync(ObjectName name, CancellationToken cancellationToken = default) =>
        inner.GetPropertiesAsync(name, cancellationToken);

    public IAsyncEnumerable<ListedObject> ListAsync(string prefix, string? startAfter = null, CancellationToken cancellationToken = default) =>
        inner.ListAsync(prefix, startAfter, cancellationToken);

    public ValueTask DeleteAsync(ObjectName name, CancellationToken cancellationToken = default) =>
        inner.DeleteAsync(name, cancellationToken);

    public ValueTask<VersionedObject?> ReadVersionedAsync(ObjectName name, CancellationToken cancellationToken = default) =>
        inner.ReadVersionedAsync(name, cancellationToken);

    public ValueTask<ObjectReplaceOutcome> ReplaceAsync(ObjectName name, ObjectVersion? expectedVersion, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default) =>
        inner.ReplaceAsync(name, expectedVersion, content, crc32c, cancellationToken);

    /// <summary>Waits for every landing the store acknowledged.</summary>
    public async ValueTask DisposeAsync()
    {
        foreach (var landing in _landings)
        {
            await landing;
        }
    }

    private async Task LandCreateAsync(ObjectName name, ArcBuffer content, uint? crc32c)
    {
        using (content)
        {
            await SimulationTasks.Yield();
            _ = await inner.CreateAsync(name, content, crc32c);
        }
    }

    private async Task LandAppendAsync(ObjectName name, long expectedLength, ArcBuffer content, uint? crc32c)
    {
        using (content)
        {
            await SimulationTasks.Yield();
            _ = await ((IAppendableObjectStore)inner).AppendAsync(name, expectedLength, content, crc32c);
        }
    }
}
