using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;
using Orleans.DurableTasks.Remoting;
using Orleans.Serialization.Invocation;

namespace Orleans.DurableTasks;

internal sealed class GrainDurableTaskContext(TaskId taskId, IDurableTaskGrainRuntime runtime, IDurableTaskState state) : DurableTaskContext(taskId)
{
    // The sequence number for named children.
    private Dictionary<string, int>? _nextChildIds;

    // The sequence number for unnamed children.
    private int _nextSequenceNumber = 0;

    internal IDurableTaskGrainRuntime Runtime { get; } = runtime;

    internal IDurableTaskState State { get; } = state;

    protected internal override async ValueTask<Response> RunAsync(TaskId taskId, DurableTask taskDefinition, CancellationToken cancellationToken)
    {
        var context = await Runtime.ScheduleAsync(taskId, taskDefinition, cancellationToken);
        return await context.AsValueTask();
    }

    protected internal override TaskId CreateChildTaskId(string? name)
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

    public bool TryGetTaskResponse(TaskId taskId, [NotNullWhen(true)] out Response? response) => Runtime.GetResponseOrCreateChildTask(taskId, out response);
    public void SetTaskResponse(TaskId taskId, Response response) => Runtime.SetChildTaskResponse(taskId, response);
}
