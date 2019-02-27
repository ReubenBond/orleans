using System.Threading.Tasks;

namespace Orleans.Runtime.Messaging
{
    public interface IConnectionListener
    {
        Task BindAsync();
        Task UnbindAsync();
        Task StopAsync();
    }
}
