namespace Orleans.Testing.Simulation;

/// <summary>
/// A simple scheduled item that wraps an Action callback.
/// Used for general-purpose delayed execution.
/// </summary>
internal sealed class ScheduledActionItem(Action callback) : ScheduledItem
{
    internal override string Kind => "action";

    internal override string Description => "Scheduled action";

    internal override void Invoke() => callback();
}
