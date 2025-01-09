namespace Orleans.DurableTasks.Scheduling;

[GenerateSerializer]
[Alias("SchedulingOptions")]
public class SchedulingOptions
{
    [Id(0)]
    public DateTimeOffset? DueTime { get; init; }

    [Id(1)]
    public string? PolicyId { get; init; }
}
