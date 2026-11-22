using System.Globalization;

namespace Orleans.Durable.Storage.Simulation;

/// <summary>
/// The ordered record of a simulation run, each entry stamped with the virtual time since the run began.
/// </summary>
/// <remarks>
/// Two runs from one seed record the same trace, so a trace both explains a failure and shows where two runs diverge.
/// </remarks>
public sealed class SimulationTrace
{
    private readonly List<string> _entries = [];
    private readonly TimeProvider _timeProvider;
    private readonly DateTimeOffset _start;

    /// <summary>
    /// Initializes a new instance of the <see cref="SimulationTrace"/> class.
    /// </summary>
    /// <param name="timeProvider">The run's virtual clock.</param>
    public SimulationTrace(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
        _start = timeProvider.GetUtcNow();
    }

    /// <summary>
    /// Gets the entries recorded so far.
    /// </summary>
    public IReadOnlyList<string> Entries => _entries;

    /// <summary>
    /// Gets the virtual time since the run began.
    /// </summary>
    public TimeSpan Elapsed => _timeProvider.GetUtcNow() - _start;

    /// <summary>
    /// Records an entry at the current virtual time.
    /// </summary>
    /// <param name="entry">The entry.</param>
    public void Record(string entry) =>
        _entries.Add(string.Create(CultureInfo.InvariantCulture, $"{Elapsed.TotalMilliseconds:0.###} {entry}"));
}
