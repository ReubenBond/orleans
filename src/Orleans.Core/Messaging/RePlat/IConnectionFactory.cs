using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;

namespace Orleans.Runtime.Messaging
{
    public interface IConnectionFactory
    {
        Task<ConnectionContext> Connect(string endPoint);
    }
}
