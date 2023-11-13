
using System;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans.TestingHost
{
    /// <summary>
    /// Grain that tracks storage exceptions to be injected.
    /// </summary>
    [Alias("Orleans.TestingHost.IStorageFaultGrain")]
    public interface IStorageFaultGrain : IGrainWithStringKey
    {
        /// <summary>
        /// Adds a storage exception to be thrown when the referenced grain reads state from a storage provider
        /// </summary>
        /// <returns>Task.</returns>
        [Alias("AddFaultOnRead")]
        Task AddFaultOnRead(GrainId grainId, Exception exception);

        /// <summary>
        /// Adds a storage exception to be thrown when the referenced grain writes state to a storage provider
        /// </summary>
        /// <returns>Task.</returns>
        [Alias("AddFaultOnWrite")]
        Task AddFaultOnWrite(GrainId grainId, Exception exception);

        /// <summary>
        /// Adds a storage exception to be thrown when the referenced grain clears state in a storage provider
        /// </summary>
        /// <returns>Task.</returns>
        [Alias("AddFaultOnClear")]
        Task AddFaultOnClear(GrainId grainId, Exception exception);

        /// <summary>
        /// Throws a storage exception if one has been added for the grain reference for reading.
        /// </summary>
        [Alias("OnRead")]
        Task OnRead(GrainId grainId);

        /// <summary>
        /// Throws a storage exception if one has been added for the grain reference for writing.
        /// </summary>
        [Alias("OnWrite")]
        Task OnWrite(GrainId grainId);

        /// <summary>
        /// Throws a storage exception if one has been added for the grain reference for clearing state.
        /// </summary>
        [Alias("OnClear")]
        Task OnClear(GrainId grainId);
    }
}
