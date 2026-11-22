namespace Orleans.Testing.Simulation.Runtime.Decisions;

/// <summary>
/// Records deterministic decisions in a monotonically ordered, append-only log. Scheduling and
/// resource components call <see cref="Record"/> at reproducible choice points; replay consumes
/// <see cref="Records"/> through <see cref="SimulationDecisionReplayValidator"/>.
/// </summary>
public interface ISimulationDecisionLog
{
    /// <summary>
    /// Gets every decision recorded so far, in the exact order <see cref="Record"/> was called.
    /// </summary>
    IReadOnlyList<SimulationDecisionRecord> Records { get; }

    /// <summary>
    /// Records one decision, assigning it the next monotonic <see cref="SimulationDecisionId"/>.
    /// </summary>
    /// <param name="request">The decision to record.</param>
    /// <returns>The recorded decision, including its assigned <see cref="SimulationDecisionRecord.Id"/>.</returns>
    SimulationDecisionRecord Record(SimulationDecisionRequest request);
}

/// <summary>
/// The default in-memory <see cref="ISimulationDecisionLog"/> implementation: a simple,
/// thread-safe, append-only list. Suitable for the current single-simulation-thread execution
/// model; a future scheduler could add a persistent/streaming implementation behind the same
/// interface without changing any caller.
/// </summary>
public sealed class SimulationDecisionLog : ISimulationDecisionLog
{
    private readonly Lock _lock = new();
    private readonly List<SimulationDecisionRecord> _records = [];
    private long _nextSequence;

    /// <inheritdoc/>
    public IReadOnlyList<SimulationDecisionRecord> Records
    {
        get
        {
            lock (_lock)
            {
                return [.. _records];
            }
        }
    }

    /// <inheritdoc/>
    public SimulationDecisionRecord Record(SimulationDecisionRequest request)
    {
        lock (_lock)
        {
            var id = new SimulationDecisionId(_nextSequence++);
            var record = new SimulationDecisionRecord(
                id,
                request.Domain,
                request.Kind,
                request.SourceId,
                request.InputMetadata,
                request.SelectedResult,
                request.RuntimeId,
                request.NodeId,
                request.LogicalExecutionId);

            _records.Add(record);
            return record;
        }
    }
}
