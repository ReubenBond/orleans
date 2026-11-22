using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Simulation.Conformance.Tests;

/// <summary>
/// A race mutant of a replica: it answers a writer write written at once and lands it a step later, so a seal which lands
/// in between refuses the write its answer reported written, and a read between them finds its slot missing (LR-I8,
/// LP-I3, LP-I10). Recovery writes, reads, and seals pass through. Its disposal waits for every landing, so a case's
/// buffers are released before the simulation's leak check.
/// </summary>
internal sealed class AcknowledgesBeforeLandingReplica(ILedgerReplica inner) : ILedgerReplica
{
    private readonly List<Task> _landings = [];

    public ValueTask<ReplicaWriteOutcome> WriteAsync(
        Slot slot, ArcBuffer payload, Slot contiguousTail, bool recovery, CancellationToken cancellationToken = default)
    {
        if (recovery)
        {
            return inner.WriteAsync(slot, payload, contiguousTail, recovery, cancellationToken);
        }

        _landings.Add(LandAsync(slot, payload.Retain(), contiguousTail));
        return ValueTask.FromResult<ReplicaWriteOutcome>(ReplicaWriteOutcome.Written.Instance);
    }

    public ValueTask<ReplicaRead> ReadAsync(Slot slot, Slot contiguousTail, CancellationToken cancellationToken = default) =>
        inner.ReadAsync(slot, contiguousTail, cancellationToken);

    public ValueTask<ReplicaRead> ReadAsync(Slot slot, int offset, int length, Slot contiguousTail, CancellationToken cancellationToken = default) =>
        inner.ReadAsync(slot, offset, length, contiguousTail, cancellationToken);

    public ValueTask<ReplicaTailRead> ReadTailAsync(Slot contiguousTail, bool seal, CancellationToken cancellationToken = default) =>
        inner.ReadTailAsync(contiguousTail, seal, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await LandedAsync();
        await inner.DisposeAsync();
    }

    /// <summary>
    /// Returns a task which completes once every write the replica acknowledged has landed or failed. A harness awaits it
    /// before the owner of the replica disposes it: the simulation sees the landings, but not a rewritten caller's wait on
    /// this unrewritten task.
    /// </summary>
    public async Task LandedAsync()
    {
        foreach (var landing in _landings)
        {
            await landing;
        }
    }

    private async Task LandAsync(Slot slot, ArcBuffer payload, Slot contiguousTail)
    {
        using (payload)
        {
            await SimulationTasks.Yield();
            try
            {
                _ = await inner.WriteAsync(slot, payload, contiguousTail, recovery: false);
            }
#pragma warning disable CA1031 // The mutant already answered; its landing's failure is the race it plants.
            catch (Exception)
#pragma warning restore CA1031
            {
            }
        }
    }
}
