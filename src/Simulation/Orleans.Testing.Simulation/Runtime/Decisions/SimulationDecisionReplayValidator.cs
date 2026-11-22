namespace Orleans.Testing.Simulation.Runtime.Decisions;

/// <summary>
/// Thrown by <see cref="SimulationDecisionReplayValidator"/> at the first decision whose content
/// diverges from what was recorded during a prior run (or, symmetrically, when a decision was
/// recorded but replay produced none, or vice versa).
/// </summary>
public sealed class SimulationDecisionReplayMismatchException : InvalidOperationException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SimulationDecisionReplayMismatchException"/> class.
    /// </summary>
    /// <param name="message">The diagnostic message.</param>
    /// <param name="expected">The expected (previously recorded) decision, if any was available.</param>
    /// <param name="actual">
    /// The decision actually observed during replay, or <see langword="null"/> when validation found an
    /// unconsumed recorded suffix at the successful end of the run.
    /// </param>
    public SimulationDecisionReplayMismatchException(string message, SimulationDecisionRecord? expected, SimulationDecisionRecord? actual)
        : base(message)
    {
        Expected = expected;
        Actual = actual;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SimulationDecisionReplayMismatchException"/> class.
    /// </summary>
    public SimulationDecisionReplayMismatchException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SimulationDecisionReplayMismatchException"/> class.
    /// </summary>
    /// <param name="message">The diagnostic message.</param>
    public SimulationDecisionReplayMismatchException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SimulationDecisionReplayMismatchException"/> class.
    /// </summary>
    /// <param name="message">The diagnostic message.</param>
    /// <param name="innerException">The inner exception.</param>
    public SimulationDecisionReplayMismatchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Gets the expected (previously recorded) decision, or <see langword="null"/> if the replay
    /// source was exhausted before this decision (i.e. a new decision was made during replay that
    /// was never recorded).
    /// </summary>
    public SimulationDecisionRecord? Expected { get; }

    /// <summary>
    /// Gets the decision actually observed during replay.
    /// </summary>
    public SimulationDecisionRecord? Actual { get; }
}

/// <summary>
/// <para>
/// Validates a live sequence of decisions against a previously-recorded sequence read from an
/// <see cref="ISimulationDecisionReplayReader"/>, throwing
/// <see cref="SimulationDecisionReplayMismatchException"/> at the <em>first</em> divergent
/// decision rather than collecting every mismatch - once one decision diverges, every subsequent
/// decision is potentially invalidated (the replay has already gone off-script), so continuing to
/// compare would produce noise, not signal.
/// </para>
/// <para>
/// This validates decision <em>content</em> - <see cref="SimulationDecisionRecord.Domain"/>,
/// <see cref="SimulationDecisionRecord.Kind"/>, <see cref="SimulationDecisionRecord.SourceId"/>,
/// <see cref="SimulationDecisionRecord.InputMetadata"/>, and
/// <see cref="SimulationDecisionRecord.SelectedResult"/> - but deliberately not
/// <see cref="SimulationDecisionRecord.Id"/>, <see cref="SimulationDecisionRecord.RuntimeId"/>,
/// <see cref="SimulationDecisionRecord.NodeId"/>, or <see cref="SimulationDecisionRecord.LogicalExecutionId"/>:
/// those identify <em>which run</em> produced the decision, which is expected to differ between
/// the original recording and a later replay (a replay is, by definition, a different process
/// invocation), while the decision's content is exactly what must stay identical for the replay to
/// be valid.
/// </para>
/// <para>
/// This is a standalone validation contract, not a scheduler. Replay components call
/// <see cref="Validate"/> once per decision as they re-execute.
/// </para>
/// </summary>
/// <param name="reader">The source of previously-recorded decisions to validate against.</param>
public sealed class SimulationDecisionReplayValidator(ISimulationDecisionReplayReader reader)
{
    private readonly ISimulationDecisionReplayReader _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    private bool _mismatchAlreadyThrown;

    /// <summary>
    /// Compares <paramref name="actual"/> against the next expected decision from the replay
    /// reader supplied at construction.
    /// </summary>
    /// <param name="actual">The decision just made during the live/replay run.</param>
    /// <exception cref="SimulationDecisionReplayMismatchException">
    /// Thrown if this is the first divergence between <paramref name="actual"/> and the recorded
    /// sequence (including replay exhaustion - a decision made live with no corresponding recorded
    /// decision left to compare against).
    /// </exception>
    public void Validate(SimulationDecisionRecord actual)
    {
        ArgumentNullException.ThrowIfNull(actual);

        if (_mismatchAlreadyThrown)
        {
            // Already diverged once; every decision after that point is potentially invalid, so
            // stop comparing instead of throwing again for what is likely cascading noise.
            return;
        }

        if (!_reader.TryGetNext(out var expected) || expected is null)
        {
            _mismatchAlreadyThrown = true;
            throw new SimulationDecisionReplayMismatchException(
                $"Replay diverged at decision {actual.Id}: the recorded log is exhausted, but a new " +
                $"decision ({actual.Kind} in domain {actual.Domain}, source='{actual.SourceId}') was made.",
                expected: null,
                actual);
        }

        if (!ContentMatches(expected, actual))
        {
            _mismatchAlreadyThrown = true;
            throw new SimulationDecisionReplayMismatchException(
                $"Replay diverged at decision {actual.Id}: expected {Describe(expected)} but observed {Describe(actual)}.",
                expected,
                actual);
        }
    }

    /// <summary>
    /// Verifies that a successful replay consumed the entire recorded decision stream.
    /// </summary>
    /// <remarks>
    /// Call this only at a successful end-of-run boundary. Partial or aborted runs intentionally leave
    /// records unread and should not be validated as complete.
    /// </remarks>
    /// <exception cref="SimulationDecisionReplayMismatchException">
    /// Thrown when the recorded stream contains a suffix that the replay never produced.
    /// </exception>
    public void ValidateComplete()
    {
        if (_mismatchAlreadyThrown)
        {
            return;
        }

        if (_reader.TryGetNext(out var expected) && expected is not null)
        {
            _mismatchAlreadyThrown = true;
            throw new SimulationDecisionReplayMismatchException(
                $"Replay diverged at end of run: recorded decision {expected.Id} ({Describe(expected)}) " +
                "and possibly additional records remain unconsumed.",
                expected,
                actual: null);
        }
    }

    private static bool ContentMatches(SimulationDecisionRecord expected, SimulationDecisionRecord actual) =>
        expected.Domain == actual.Domain &&
        expected.Kind == actual.Kind &&
        string.Equals(expected.SourceId, actual.SourceId, StringComparison.Ordinal) &&
        string.Equals(expected.InputMetadata, actual.InputMetadata, StringComparison.Ordinal) &&
        string.Equals(expected.SelectedResult, actual.SelectedResult, StringComparison.Ordinal);

    private static string Describe(SimulationDecisionRecord record) =>
        $"[{record.Domain}/{record.Kind}] source='{record.SourceId}' input='{record.InputMetadata}' result='{record.SelectedResult}'";
}
