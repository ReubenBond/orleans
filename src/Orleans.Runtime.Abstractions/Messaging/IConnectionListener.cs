using System.Threading.Tasks;

namespace Orleans.Runtime.Messaging
{
    public interface IConnectionListener
    {
        Task Bind();
        Task Unbind();
        Task Stop();
    }
}
