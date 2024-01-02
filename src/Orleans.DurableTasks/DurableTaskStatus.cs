namespace Orleans.DurableTasks;

public enum DurableTaskStatus
{
    NotStarted,
    InProgress,
    Success,
    Faulted,
    Canceled
}
