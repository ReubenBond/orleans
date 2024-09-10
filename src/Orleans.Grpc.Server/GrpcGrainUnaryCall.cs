using System;
using System.Reflection;
using System.Threading.Tasks;
using Google.Protobuf;
using Microsoft.Hosting;
using Orleans.Runtime;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Invocation;

namespace Orleans.Serialization.gRPC;
[GenerateSerializer, Alias("gRPC.GrainCall"), Immutable]
internal sealed class GrpcGrainUnaryCall : IInvokable
{
    [Id(0)]
    public string? MethodName;

    [Id(1)]
    public string? ServiceName;

    [Id(2)]
    public IMessage? Argument;

    [NonSerialized]
    private IGrainContext? _context;
    public void Dispose() => throw new NotImplementedException();
    public string GetActivityName() => throw new NotImplementedException();
    public object GetArgument(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(0, index);
        throw new NotImplementedException();

        //return Argument; // TODO: Deserialize if not deserialized already.
    }

    public int GetArgumentCount() => 1;
    public TimeSpan? GetDefaultResponseTimeout() => null;
    public string GetInterfaceName() => ServiceName!;
    public Type GetInterfaceType() => null!;
    public MethodInfo GetMethod() => null!;
    public string GetMethodName() => MethodName!;
    public object GetTarget() => _context?.GrainInstance!;

    public ValueTask<Response> Invoke()
    {
        var invoker = _context!.GetComponent<GrpcServiceGrainCallInvoker>() ?? throw new InvalidOperationException($"Grain '{_context}' does not support gRPC service '{ServiceName}'.");
        return invoker.Invoke(_context.GrainInstance!, this);
    }

    public void SetArgument(int index, object value) => throw new NotImplementedException();
   
    public void SetTarget(ITargetHolder holder)
    {
        _context = holder.GetComponent<IGrainContext>();
    }
}
