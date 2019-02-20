using System.Threading.Tasks;

namespace Orleans.Runtime.Messaging.RePlat
{
    public interface IConnectionListener
    {
        Task Bind();
        Task Unbind();
        Task Stop();
    }
}
