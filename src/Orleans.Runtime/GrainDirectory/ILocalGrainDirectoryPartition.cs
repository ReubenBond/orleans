using System;
using System.Collections.Generic;
using Orleans.GrainDirectory;

#nullable enable
namespace Orleans.Runtime.GrainDirectory
{
    /// <summary>
    /// Interface for local grain directory partition storage.
    /// </summary>
    /// <remarks>
    /// This interface abstracts the storage layer for <see cref="LocalGrainDirectory"/>, allowing
    /// different implementations:
    /// <list type="bullet">
    /// <item><see cref="LocalGrainDirectoryPartition"/> - The default in-memory implementation</item>
    /// <item><see cref="DelegatingGrainDirectoryPartition"/> - Delegates to <see cref="DistributedGrainDirectory"/> for migration scenarios</item>
    /// </list>
    /// </remarks>
    internal interface ILocalGrainDirectoryPartition
    {
        /// <summary>
        /// Gets the number of grain registrations in this partition.
        /// </summary>
        int Count { get; }

        /// <summary>
        /// Adds a new activation to the directory partition.
        /// </summary>
        /// <param name="address">The address of the activation to register.</param>
        /// <param name="previousAddress">The previous address, if this is a migration/replacement.</param>
        /// <returns>The registered address and version tag associated with this directory mapping.</returns>
        AddressAndTag AddSingleActivation(GrainAddress address, GrainAddress? previousAddress);

        /// <summary>
        /// Removes an activation of the given grain from the partition.
        /// </summary>
        /// <param name="grain">The identity of the grain.</param>
        /// <param name="activation">The id of the activation.</param>
        /// <param name="cause">The reason for removing the activation.</param>
        void RemoveActivation(GrainId grain, ActivationId activation, UnregistrationCause cause = UnregistrationCause.Force);

        /// <summary>
        /// Removes the grain (and, effectively, all its activations) from the directory.
        /// </summary>
        /// <param name="grain">The identity of the grain to remove.</param>
        void RemoveGrain(GrainId grain);

        /// <summary>
        /// Looks up the activation for a given grain.
        /// </summary>
        /// <param name="grain">The identity of the grain.</param>
        /// <returns>The address and version tag of the activation, or default if not found.</returns>
        AddressAndTag LookUpActivation(GrainId grain);

        /// <summary>
        /// Returns the version number (ETag) of the list of activations for the grain.
        /// </summary>
        /// <param name="grain">The identity of the grain.</param>
        /// <returns>The version tag, or <see cref="GrainInfo.NO_ETAG"/> (-1) if the grain is not found.</returns>
        int GetGrainETag(GrainId grain);

        /// <summary>
        /// Returns all entries stored in the partition.
        /// </summary>
        /// <returns>A list of all grain registrations in the partition.</returns>
        List<KeyValuePair<GrainId, GrainInfo>> GetItems();

        /// <summary>
        /// Clears all entries from the partition.
        /// </summary>
        void Clear();

        /// <summary>
        /// Runs through all entries in the partition and returns entries satisfying the given predicate.
        /// This method is used by the handoff manager to split partitions when the cluster membership changes.
        /// </summary>
        /// <param name="predicate">Filter predicate (usually checks if the given grain is owned by a particular silo).</param>
        /// <returns>Entries satisfying the given predicate.</returns>
        List<GrainAddress> Split(Predicate<GrainId> predicate);
    }
}
