namespace Orleans.Testing.Simulation.Runtime.Decisions;

/// <summary>
/// <para>
/// Supplies previously recorded decisions for comparison against a live replay. The in-memory
/// implementation (<see cref="SimulationInMemoryDecisionReplayReader"/>) is used by replay scheduling
/// and contract tests.
/// </para>
/// <para>
/// Readers are expected to be stateful, single-pass, forward-only enumerations - exactly mirroring
/// how decisions were originally recorded (one at a time, in order).
/// </para>
/// </summary>
public interface ISimulationDecisionReplayReader
{
    /// <summary>
    /// Attempts to read the next expected decision from the replay source.
    /// </summary>
    /// <param name="expected">The next expected decision, if one is available.</param>
    /// <returns><see langword="true"/> if a decision was available; <see langword="false"/> if the replay source is exhausted.</returns>
    bool TryGetNext(out SimulationDecisionRecord? expected);
}

/// <summary>
/// A simple <see cref="ISimulationDecisionReplayReader"/> backed by an in-memory list of
/// previously-recorded decisions (typically <see cref="ISimulationDecisionLog.Records"/> from a
/// prior run). Intended for tests and small in-process replay scenarios; file/stream-backed readers
/// can implement the same interface without changing consumers.
/// </summary>
/// <param name="records">The previously-recorded decisions, in their original order.</param>
public sealed class SimulationInMemoryDecisionReplayReader(IReadOnlyList<SimulationDecisionRecord> records) : ISimulationDecisionReplayReader
{
    private readonly IReadOnlyList<SimulationDecisionRecord> _records = records ?? throw new ArgumentNullException(nameof(records));
    private int _index;

    /// <summary>
    /// Gets the number of records this reader has not yet returned via <see cref="TryGetNext"/>.
    /// </summary>
    public int RemainingCount => _records.Count - _index;

    /// <inheritdoc/>
    public bool TryGetNext(out SimulationDecisionRecord? expected)
    {
        if (_index >= _records.Count)
        {
            expected = null;
            return false;
        }

        expected = _records[_index++];
        return true;
    }
}
