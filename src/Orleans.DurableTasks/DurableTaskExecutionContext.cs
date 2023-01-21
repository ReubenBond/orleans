using System.Diagnostics.CodeAnalysis;
using Orleans.DurableTasks.Remoting;

namespace Orleans.DurableTasks;

public sealed partial class DurableTaskExecutionContext
{
    private static readonly AsyncLocal<DurableTaskExecutionContext?> Current = new();

    public static DurableTaskExecutionContext? CurrentContext => Current.Value;
    public static DurableTaskExecutionContext GetCurrentContextOrThrow() => Current.Value ?? throw new InvalidOperationException($"An ambient {nameof(DurableTaskExecutionContext)} is required but not present.");
    internal static void Reset() => Current.Value = null;
    internal static void SetCurrentContext(DurableTaskExecutionContext? context) => Current.Value = context;
    internal static void SetCurrentContext(DurableTaskExecutionContext? context, out DurableTaskExecutionContext? previous)
    {
        previous = Current.Value;
        Current.Value = context;
    }

    public TaskId TaskId { get; }
    internal IDurableTaskGrainRuntime Runtime { get; }
    internal DurableTaskState State { get; }

    internal DurableTaskExecutionContext(TaskId taskId, IDurableTaskGrainRuntime runtime, DurableTaskState state)
    {
        TaskId = taskId;
        Runtime = runtime;
        State = state;
    }
}
