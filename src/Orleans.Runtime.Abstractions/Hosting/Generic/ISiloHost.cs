using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace Orleans.Hosting
{
    /// <summary>
    /// Represents a silo instance.
    /// </summary>
    public interface ISiloHost : IHost
    {
        /// <summary>
        /// Gets a <see cref="Task"/> which completes when this silo stops.
        /// </summary>
        Task Stopped { get; }
    }
}