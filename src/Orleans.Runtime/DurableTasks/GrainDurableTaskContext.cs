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

internal sealed class GrainDurableTaskContext(TaskId taskId, IDurableTaskProxy runtime, IDurableTaskState state) : DurableTaskContext(taskId)
{
    // The sequence number for named children.
    private Dictionary<string, int>? _nextChildIds;

    // The sequence number for unnamed children.
    private int _nextSequenceNumber = 0;

    internal IDurableTaskProxy Runtime { get; } = runtime;

    internal IDurableTaskState State { get; } = state;

    protected override async ValueTask<DurableTaskResponse> RunChildTaskAsync(TaskId taskId, DurableTask taskDefinition, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(taskId, default);
        if (!Id.IsParentOf(taskId))
        {
            throw new InvalidOperationException($"The provided task ID '{taskId}' is not a child of this task '{Id}'.");
        }

        var response = await Runtime.ScheduleAsync(taskId, taskDefinition, cancellationToken);
        if (response.IsCompleted)
        {
            return response;
        }

        var handle = Runtime.GetScheduledTaskHandle(taskId);
        return await handle.WaitAsync(cancellationToken);
    }

    protected override TaskId CreateChildTaskId(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            var sequenceNumber = _nextSequenceNumber++;
            return Id.Child(sequenceNumber.ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            ref var nextSequenceNumber = ref CollectionsMarshal.GetValueRefOrAddDefault(_nextChildIds ??= [], name, out _);
            var sequenceNumber = nextSequenceNumber++;
            if (sequenceNumber > 0)
            {
                return Id.Child($"{name}.{sequenceNumber.ToString(CultureInfo.InvariantCulture)}");
            }

            return Id.Child(name);
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
