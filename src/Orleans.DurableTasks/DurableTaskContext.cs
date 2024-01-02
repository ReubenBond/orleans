using Orleans.Serialization.Invocation;

namespace Orleans.DurableTasks;

public abstract partial class DurableTaskContext(TaskId id)
{
    private static readonly AsyncLocal<DurableTaskContext?> Current = new();

    public static DurableTaskContext? CurrentContext => Current.Value;
    public static DurableTaskContext GetCurrentContextOrThrow() => Current.Value ?? throw new InvalidOperationException($"An ambient {nameof(DurableTaskContext)} is required but not present.");
    internal static void Reset() => Current.Value = null;
    internal static void SetCurrentContext(DurableTaskContext? context) => Current.Value = context;
    internal static void SetCurrentContext(DurableTaskContext? context, out DurableTaskContext? previous)
    {
        previous = Current.Value;
        Current.Value = context;
    }

    public TaskId Id { get; } = id;
    internal CancellationTokenSource CancellationTokenSource { get; } = new();
    public CancellationToken CancellationToken => CancellationTokenSource.Token;
    protected internal abstract ValueTask<DurableTaskContext> EvaluateAsync(TaskId taskId, DurableTask taskDefinition, CancellationToken cancellationToken);
    protected internal abstract ValueTask<Response> InvokeAsync(TaskId taskId, DurableTask taskDefinition, CancellationToken cancellationToken);
    protected internal abstract TaskId CreateChildTaskId(string? name);
}
