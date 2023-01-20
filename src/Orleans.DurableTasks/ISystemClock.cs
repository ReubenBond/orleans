namespace Orleans.DurableTasks;

/// <summary>
/// System clock abstraction
/// </summary>
public interface ISystemClock
{
    /// <summary>
    /// Gets the current time in UTC
    /// </summary>
    DateTime GetUtcNow();
}
