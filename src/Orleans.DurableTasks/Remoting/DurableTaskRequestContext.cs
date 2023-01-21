using Orleans.Runtime;

namespace Orleans.DurableTasks.Remoting;

[GenerateSerializer]
public class DurableTaskRequestContext
{
    [Id(0)]
    public TaskId TaskId { get; set; }

    [Id(1)]
    public IAddressable? Caller { get; set; }

    [Id(2)]
    public GrainId TargetId { get; set; }

    [Id(3)]
    public SchedulingOptions? SchedulingOptions { get; set; }

    [Id(4)]
    public Dictionary<string, byte[]>? Values { get; set; }
}
