#nullable enable
using System;
using System.Collections.Generic;
using System.Distributed.DurableTasks;
using System.Distributed.DurableTasks.Scheduling;
using Orleans.Runtime;
using Orleans.Serialization.Cloning;

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
    public SchedulingOptions? SchedulingOptions { get; set; }

    // TODO: Use a specialized collection type which allows for late materialization when deserialized.
    [Id(4)]
    public Dictionary<string, byte[]>? Values { get; set; }
}

[RegisterConverter, RegisterCopier]
internal sealed class DurableTaskPopulator : IConverter<DurableTask, DurableTaskSurrogate>, IPopulator<DurableTask, DurableTaskSurrogate>, IBaseCopier<DurableTask>
{
    public void DeepCopy(DurableTask input, DurableTask output, CopyContext context)
    {
        // No-op
    }

    public void Populate(in DurableTaskSurrogate surrogate, DurableTask value)
    {
        // No-op
    }

    DurableTask IConverter<DurableTask, DurableTaskSurrogate>.ConvertFromSurrogate(in DurableTaskSurrogate surrogate)
    {
        // Populator will be used instead.
        throw new NotImplementedException();
    }

    DurableTaskSurrogate IConverter<DurableTask, DurableTaskSurrogate>.ConvertToSurrogate(in DurableTask value)
    {
        return default;
    }
}

[RegisterConverter, RegisterCopier]
internal sealed class DurableTaskPopulator<T> : IConverter<DurableTask<T>, DurableTaskSurrogate>, IPopulator<DurableTask<T>, DurableTaskSurrogate>, IBaseCopier<DurableTask<T>>
{
    public void DeepCopy(DurableTask<T> input, DurableTask<T> output, CopyContext context)
    {
        // No-op
    }

    public void Populate(in DurableTaskSurrogate surrogate, DurableTask<T> value)
    {
        // No-op
    }

    DurableTask<T> IConverter<DurableTask<T>, DurableTaskSurrogate>.ConvertFromSurrogate(in DurableTaskSurrogate surrogate)
    {
        // Populator will be used instead.
        throw new NotImplementedException();
    }

    DurableTaskSurrogate IConverter<DurableTask<T>, DurableTaskSurrogate>.ConvertToSurrogate(in DurableTask<T> value)
    {
        return default;
    }
}

[GenerateSerializer, Immutable]
internal readonly struct DurableTaskSurrogate
{
}

[RegisterConverter]
internal sealed class TaskIdConverter : IConverter<TaskId, TaskIdSurrogate>
{
    public TaskId ConvertFromSurrogate(in TaskIdSurrogate surrogate) => TaskId.Parse(surrogate.Value, provider: null);

    public TaskIdSurrogate ConvertToSurrogate(in TaskId value) => new(value.ToString());
}

[GenerateSerializer, Immutable]
internal readonly struct TaskIdSurrogate(string value)
{
    [Id(0)]
    public string Value { get; } = value;
}

[RegisterConverter]
internal sealed class SchedulingOptionsConverter : IConverter<SchedulingOptions, SchedulingOptionsSurrogate>
{
    public SchedulingOptions ConvertFromSurrogate(in SchedulingOptionsSurrogate surrogate)
    {
        return new SchedulingOptions
        {
            DueTime = surrogate.DueTime,
            PolicyId = surrogate.PolicyId
        };
    }

    public SchedulingOptionsSurrogate ConvertToSurrogate(in SchedulingOptions value) => new()
    {
        DueTime = value.DueTime,
        PolicyId = value.PolicyId
    };
}

[GenerateSerializer, Immutable]
internal readonly struct SchedulingOptionsSurrogate(SchedulingOptions value)
{
    [Id(0)]
    public DateTimeOffset? DueTime { get; init; } = value.DueTime;

    [Id(1)]
    public string? PolicyId { get; init; } = value.PolicyId;
}
