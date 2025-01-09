using System.Distributed.DurableTasks;
using System.Globalization;
using System.Runtime.InteropServices;
using Orleans.DurableTasks;
using Orleans.Serialization.Invocation;
namespace PaymentWorkflowApp.Runtime;

internal sealed class JobDurableTaskExecutionContext(TaskId taskId, JobScheduler jobScheduler, JobTaskState state) : DurableTaskContext(taskId)
{
    private readonly JobScheduler _jobScheduler = jobScheduler;

    // The sequence number for named children.
    private Dictionary<string, int>? _nextChildIds;

    // The sequence number for unnamed children.
    private int _nextSequenceNumber = 0;

    public JobTaskState State { get; } = state;

    protected override ValueTask<Response> RunAsync(TaskId taskId, DurableTask taskDefinition, CancellationToken cancellationToken)
        => _jobScheduler.InvokeAsync(taskId, taskDefinition, cancellationToken);

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
}
