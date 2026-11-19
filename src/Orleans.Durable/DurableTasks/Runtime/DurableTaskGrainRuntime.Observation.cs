using System;
using Orleans.Durable.Diagnostics;
using Orleans.DurableTasks.Protocol;
using Orleans.Messaging;

namespace Orleans.DurableTasks.Runtime;

internal sealed partial class DurableTaskGrainRuntime
{
    private CommittedObservationBuffer? _observations;

    private void ObserveTask(
        TaskId taskId,
        string transition,
        bool committed,
        string? detail = null,
        TaskId related = default,
        IDurableTaskState? state = null,
        MessageId messageId = default,
        GrainDurableTaskContext? attempt = null,
        GrainId cause = default)
    {
        if (_shared.Observations is not { IsEnabled: true } source) return;
        var buffer = _observations ??= new(source);
        if (state is null) _storage.TryGetTask(taskId, out state);
        if (attempt is null) _executionContexts.TryGetValue(taskId, out attempt);
        var incarnation = state?.Incarnation ?? Guid.Empty;
        for (var parentId = taskId.Parent(); incarnation == Guid.Empty && !parentId.IsDefault; parentId = parentId.Parent())
        {
            if (_storage.TryGetTask(parentId, out var parentState)) incarnation = parentState.Incarnation;
        }

        var wireId = state is { CallerId.IsDefault: false }
            ? CallerScope.GetCallerTaskId(state.CallerId, taskId)
            : incarnation == Guid.Empty ? default : CallerScope.GetWireId(incarnation, taskId);
        var observation = new DurableObservation
        {
            Component = "task",
            Owner = GrainId.ToString(),
            Transition = transition,
            Timestamp = UtcNow,
            TaskId = taskId.ToString(),
            WireTaskId = wireId.IsDefault ? null : wireId.ToString(),
            RelatedTaskId = (related.IsDefault ? taskId.Parent() : related) is { IsDefault: false } parent ? parent.ToString() : null,
            Incarnation = incarnation,
            MessageId = messageId.IsDefault ? null : messageId.ToString(),
            RelatedGrainId = !cause.IsDefault ? cause.ToString()
                : state is { CallerId.IsDefault: false } ? state.CallerId.ToString()
                : state is { RemoteTarget.IsDefault: false } ? state.RemoteTarget.ToString() : null,
            AttemptId = attempt?.ObservationAttemptId.ToString(),
            DueTime = state?.DueTime,
            Detail = detail ?? state?.Kind.ToString()
        };
        if (committed) buffer.Stage(observation);
        else buffer.Activity(observation);
    }

    void IDurableTaskGrainRuntime.ObserveWait(TaskId taskId, TaskId awaited, bool started) =>
        ObserveTask(taskId, started ? "WaitStarted" : "WaitEnded", committed: false, related: awaited);

    internal void ObserveMessage(TaskId taskId, MessageId messageId, bool wireId = false, GrainId sender = default)
    {
        if (_shared.Observations is not { IsEnabled: true }) return;
        if (wireId && !TryGetNode(taskId, out taskId, out _)) return;
        ObserveTask(taskId, "MessageApplied", committed: true, messageId: messageId, cause: sender);
    }
}
