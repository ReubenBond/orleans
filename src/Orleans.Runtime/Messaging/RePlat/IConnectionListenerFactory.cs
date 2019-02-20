using Microsoft.AspNetCore.Connections;

namespace Orleans.Runtime.Messaging.RePlat
{
    public interface IConnectionListenerFactory
    {
        IConnectionListener Create(string endPoint, ConnectionDelegate connectionDelegate);
    }
}
