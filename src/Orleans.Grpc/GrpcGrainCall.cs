using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Invocation;

namespace Orleans.Serialization.gRPC;
[GenerateSerializer, Alias("gRPC.GrainCall"), Immutable]
internal sealed class GrpcGrainCall : IInvokable
{
    [Id(0)]
    public string MethodName;

    [Id(1)]
    public string ServiceName;

    [Id(2)]
    public PooledBuffer Argument;

    [NonSerialized]
    private object _target;

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
    public string GetInterfaceName() => ServiceName;
    public Type GetInterfaceType() => null;
    public MethodInfo GetMethod() => null;
    public string GetMethodName() => MethodName;
    public object GetTarget() => _target;

    public ValueTask<Response> Invoke()
    {
        throw new NotImplementedException();
    }

    public void SetArgument(int index, object value) => throw new NotImplementedException();
    public void SetTarget(ITargetHolder holder) => _target = holder.GetTarget<object>();
}
