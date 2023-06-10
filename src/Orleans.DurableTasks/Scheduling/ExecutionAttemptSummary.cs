namespace Orleans.DurableTasks;

// Something like this?
public class ExecutionAttemptSummary
{
    public DateTime? ScheduledStart { get; set; }
    public int AttemptCount { get; set; }
    public DateTime? LastAttemptStart { get; set; }
    public DateTime? Deadline { get; set; }
}
