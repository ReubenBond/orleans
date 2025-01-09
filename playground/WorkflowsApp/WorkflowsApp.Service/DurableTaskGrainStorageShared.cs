using Orleans.DurableTasks;
using Orleans.DurableTasks.Remoting;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Invocation;
using Orleans.Serialization.Session;

namespace Orleans.DurableTask.Playground;

internal sealed class DurableTaskGrainStorageShared(
    IGrainFactory grainFactory,
    IFieldCodec<TaskId> taskIdCodec,
    IFieldCodec<DurableTaskState> taskStateCodec,
    IFieldCodec<Response> responseCodec,
    IFieldCodec<IDurableTaskObserverGrainExtension> observerCodec,
    IFieldCodec<DateTimeOffset> dateTimeOffsetCodec,
    IFieldCodec<IDurableTaskRequest> requestCodec,
    SerializerSessionPool serializerSessionPool,
    TimeProvider timeProvider)
{
    public readonly IGrainFactory GrainFactory = grainFactory;
    public readonly TimeProvider TimeProvider = timeProvider;
    public readonly IFieldCodec<TaskId> KeyCodec = taskIdCodec;
    public readonly IFieldCodec<DurableTaskState> ValueCodec = taskStateCodec;
    public readonly IFieldCodec<Response> ResponseCodec = responseCodec;
    public readonly IFieldCodec<IDurableTaskObserverGrainExtension> ObserverCodec = observerCodec;
    public readonly IFieldCodec<DateTimeOffset> DateTimeOffsetCodec = dateTimeOffsetCodec;
    public readonly IFieldCodec<IDurableTaskRequest> RequestCodec = requestCodec;
    public readonly SerializerSessionPool SerializerSessionPool = serializerSessionPool;
}
