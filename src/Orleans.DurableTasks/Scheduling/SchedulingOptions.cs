namespace Orleans.DurableTasks;

[GenerateSerializer]
public class SchedulingOptions
{
    [Id(0)]
    public DateTimeOffset? DueTime { get; init; }

    [Id(1)]
    public string? PolicyId { get; init; }
}
