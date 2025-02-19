using System.Collections.Generic;
using System.Distributed.DurableTasks;
using System.Distributed.DurableTasks.Scheduling;
using Orleans.Runtime;

namespace Orleans.DurableTasks;

[GenerateSerializer]
[Alias("DurableTaskRequestContext")]
public class DurableTaskRequestContext
{
    [Id(0)]
    public TaskId TaskId { get; set; }

    [Id(1)]
    public GrainId CallerId { get; set; }

    [Id(2)]
    public GrainId TargetId { get; set; }

    [Id(3)]
    public SchedulingOptions SchedulingOptions { get; set; }

    [Id(4)]
    public Dictionary<string, byte[]> Values { get; set; }
}
