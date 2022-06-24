using Orleans.Runtime;

namespace Orleans
{
    /// <summary>
    /// This is a markup interface for system services.
    /// System services are internal runtime objects that share some behavior with grains, but also impose certain restrictions. In particular:
    /// System services are asynchronously addressable actors.
    /// Proxy class is being generated for ISystemService, just like for IGrain.
    /// System services are scheduled on the .NET thread pool and do not follow turn-based concurrency.
    /// </summary> 
    public interface ISystemService : IAddressable
    {
    }
}
