namespace Orleans.Durable.Storage.Testing;

/// <summary>
/// One seeded run: its seed, a random source derived from it, and the history the run records.
/// </summary>
internal sealed class SimulationRun(int seed)
{
    private readonly Lock _lock = new();
    private readonly List<string> _events = [];
    private Func<IEnumerable<string>>? _history;

    /// <summary>
    /// Gets the seed which determines the run.
    /// </summary>
    public int Seed { get; } = seed;

    /// <summary>
    /// Gets the run's random source, seeded with <see cref="Seed"/>.
    /// </summary>
    public Random Random { get; } = new(seed);

    /// <summary>
    /// Gets the run's history: the entries it recorded, or the simulation's own trace.
    /// </summary>
    public IReadOnlyList<string> History
    {
        get
        {
            lock (_lock)
            {
                return _history is { } history ? [.. history()] : [.. _events];
            }
        }
    }

    /// <summary>
    /// Records one event in the run's history.
    /// </summary>
    public void Record(string entry)
    {
        lock (_lock)
        {
            _events.Add(entry);
        }
    }

    /// <summary>
    /// Uses a simulation's own trace as the run's history.
    /// </summary>
    public void UseHistory(Func<IEnumerable<string>> history)
    {
        lock (_lock)
        {
            _history = history;
        }
    }
}
