namespace Orleans.DurableTasks;

public interface IDurableTaskState<T>
{
    public T Value { get; set; }
    public ValueTask WriteStateAsync();
}
