using System;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans.Core.Internal
{
    /// <summary>
    /// Provides functionality for entering and exiting regions of code within a grain during which requests bearing the same <see cref="RequestContext.ReentrancyId"/> are allowed to re-enter the grain.
    /// </summary>
    public interface ICallChainReentrantGrainContext
    {
        /// <summary>
        /// Marks the beginning of a region of code within a grain during which requests bearing the same <see cref="RequestContext.ReentrancyId"/> are allowed to re-enter the grain.
        /// </summary>
        void OnEnterReentrantRegion(Guid reentrancyId);

        /// <summary>
        /// Marks the end of a region of code within a grain during which requests bearing the same <see cref="RequestContext.ReentrancyId"/> are allowed to re-enter the grain.
        /// </summary>
        void OnExitReentrantRegion(Guid reentrancyId);
    }

    public interface ICriticalRegionGrainContext
    {
        ValueTask OnEnterCriticalRegionAsync(Guid id);

        void OnExitCriticalRegion(Guid id);
    }
}
