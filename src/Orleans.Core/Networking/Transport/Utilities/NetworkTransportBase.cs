#nullable enable

namespace Orleans.Networking.Transport.Utilities;

public abstract class MessageTransportBase : MessageTransport
{
    public override FeatureCollection Features { get; } = new FeatureCollection();
}
