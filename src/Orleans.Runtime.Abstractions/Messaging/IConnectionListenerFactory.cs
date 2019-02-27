using Microsoft.AspNetCore.Connections;

namespace Orleans.Runtime.Messaging
{
    public interface IConnectionListenerFactory
    {
        IConnectionListener Create(string endPoint, ConnectionDelegate connectionDelegate);
    }
}
