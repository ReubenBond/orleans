using System.Collections.Generic;
using System.Threading.Tasks;


namespace Orleans.Runtime
{
    /// <summary>
    /// Remote interface to grain and activation state
    /// </summary>
    [Alias("Orleans.Runtime.ICatalog")]
    internal interface ICatalog : ISystemTarget
    {
        /// <summary>
        /// Delete activations from this silo
        /// </summary>
        /// <param name="activationAddresses"></param>
        /// <param name="reasonCode"></param>
        /// <param name="reasonText"></param>
        /// <returns></returns>
        [Alias("DeleteActivations")]
        Task DeleteActivations(List<GrainAddress> activationAddresses, DeactivationReasonCode reasonCode, string reasonText);
    }
}
