using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Metadata;
using Orleans.Runtime;

namespace Orleans.MetadataStore
{
    public interface IRemoteMetadataStore
    {
        /// <summary>
        /// Prepares an operation.
        /// </summary>
        /// <param name="key"></param>
        /// <param name="proposerParentBallot">
        /// The ballot number for the configuration which the proposer is using, taken from <see cref="ClusterMembers.Stamp"/>.
        /// </param>
        /// <param name="ballot"></param>
        /// <returns></returns>
        ValueTask<PrepareResponse<TValue>> Prepare<TValue>(string key, Ballot proposerParentBallot, Ballot ballot);

        /// <summary>
        /// </summary>
        /// <param name="key"></param>
        /// <param name="proposerParentBallot">
        /// The ballot number for the configuration which the proposer is using, taken from <see cref="ClusterMembers.Stamp"/>.
        /// </param>
        /// <param name="ballot"></param>
        /// <param name="value"></param>
        /// <returns></returns>
        ValueTask<AcceptResponse> Accept<TValue>(string key, Ballot proposerParentBallot, Ballot ballot, TValue value);

        /// <summary>
        /// Returns the list of keys which are present on this instance.
        /// </summary>
        /// <returns>The list of keys which are present on this instance.</returns>
        ValueTask<List<string>> GetKeys();
    }

    [GrainInterfaceType("ckv")]
    [DefaultGrainType(RemoteMetadataStoreGrain.GrainTypeString)]
    public interface IRemoteMetadataStoreGrain : IGrain, IRemoteMetadataStore
    {
    }
}