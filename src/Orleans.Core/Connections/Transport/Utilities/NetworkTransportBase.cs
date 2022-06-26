#nullable enable

namespace Orleans.Connections.Transport.Utilities;

public abstract class MessageTransportBase : MessageTransport
{
    public override FeatureCollection Features { get; } = new FeatureCollection();
}
