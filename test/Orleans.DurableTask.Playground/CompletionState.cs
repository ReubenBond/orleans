namespace Orleans.DurableTasks.Playground;

[GenerateSerializer, Immutable]
[Alias("CompletionState`1")]
public readonly struct CompletionState<T>
{
    [Id(0)]
    public bool Complete { get; init; }

    [Id(1)]
    public T? Result { get; init; }

    [Id(2)]
    public Exception? Exception { get; init; }
}