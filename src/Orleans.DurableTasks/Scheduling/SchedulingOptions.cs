namespace Orleans.DurableTasks;

[GenerateSerializer]
public class SchedulingOptions
{
    [Id(0)]
    public DateTime? DueTime { get; init; }

    [Id(1)]
    public string? RetryPolicy { get; init; }
}
