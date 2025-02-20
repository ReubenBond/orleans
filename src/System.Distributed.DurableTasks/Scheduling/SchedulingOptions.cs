namespace System.Distributed.DurableTasks.Scheduling;

// TODO: do we need this?
public sealed class SchedulingOptions
{
    public DateTimeOffset? DueTime { get; init; }

    public string? PolicyId { get; init; }
}
