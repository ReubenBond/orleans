namespace System.Distributed.DurableTasks.Scheduling;

// Something like this?
public class ExecutionAttemptSummary
{
    public DateTimeOffset? ScheduledStart { get; set; }
    public int AttemptCount { get; set; }
    public DateTimeOffset? LastAttemptStart { get; set; }
    public DateTimeOffset? Deadline { get; set; }
}
