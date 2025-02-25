#nullable enable
using System;
using System.Collections.Generic;
using System.Distributed.DurableTasks;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Orleans.DurableTasks;

namespace Orleans.Runtime.DurableTasks;

internal sealed class GrainDurableExecutionContext(TaskId taskId, IDurableTaskGrainRuntime runtime, IDurableTaskState state) : DurableExecutionContext(taskId)
{
    // The sequence number for named children.
    private Dictionary<string, int>? _nextChildIds;

    // The sequence number for unnamed children.
    private int _nextSequenceNumber = 0;

    internal IDurableTaskGrainRuntime Runtime { get; } = runtime;

    internal IDurableTaskState State { get; } = state;
    public DurableTask? Task { get; internal set; }

    protected override ValueTask<IScheduledTaskHandle> ScheduleChildTaskAsync(TaskId taskId, DurableTask taskDefinition, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(taskId, default);
        if (!TaskId.IsParentOf(taskId))
        {
            throw new InvalidOperationException($"The provided task ID '{taskId}' is not a child of this task '{TaskId}'.");
        }

        return Runtime.ScheduleAsync(taskId, taskDefinition, cancellationToken);
    }

    protected override IScheduledTaskHandle GetChildTaskHandle(TaskId taskId) => Runtime.GetScheduledTaskHandle(taskId);

    protected override TaskId CreateChildTaskId(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            var sequenceNumber = _nextSequenceNumber++;
            return TaskId.Child(sequenceNumber.ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            ref var nextSequenceNumber = ref CollectionsMarshal.GetValueRefOrAddDefault(_nextChildIds ??= [], name, out _);
            var sequenceNumber = nextSequenceNumber++;
            if (sequenceNumber > 0)
            {
                return TaskId.Child($"{name}.{sequenceNumber.ToString(CultureInfo.InvariantCulture)}");
            }

            return TaskId.Child(name);
        }
    }

    protected override async Task CancelAsyncCore(CancellationToken cancellationToken)
    {
        if (Task is ISchedulableTask schedulableTask)
        {
            await schedulableTask.GetHandle(TaskId).CancelAsync(cancellationToken);
        }
    }

    //public bool TryGetTaskResponse(TaskId taskId, [NotNullWhen(true)] out DurableTaskResponse? response) => Runtime.GetResponseOrCreateChildTask(taskId, out response);
    //public void SetTaskResponse(TaskId taskId, DurableTaskResponse response) => Runtime.SetChildTaskResponse(taskId, response);

    /*
    protected override ValueTask SignalCancellationAsyncCore(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // If already cancelling or terminated, return.
        // Update state to signal cancellation if not already.
        // Persist state.

        cancellationToken.ThrowIfCancellationRequested();

        // Cancel the CTS passed to child tasks.
        // Wait for children to terminate.

        cancellationToken.ThrowIfCancellationRequested();

        // Set response to canceled (TaskCanceledException).
        // (optional) write state.
        // Return
        return default;
    }
    */
}
