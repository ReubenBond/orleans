namespace Microsoft.Orleans.ProtocolBuffers;
public sealed partial class GrainId
{
    public static implicit operator global::Orleans.Runtime.GrainId(GrainId pb) => global::Orleans.Runtime.GrainId.Create(pb.Type, pb.Key);
    public static implicit operator GrainId(global::Orleans.Runtime.GrainId value) => new() { Type = value.Type.ToString(), Key = value.Key.ToString() };
}
