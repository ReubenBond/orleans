using System;
using System.Collections.Generic;
using Orleans.Runtime;

namespace Orleans.Transactions.DeadlockDetection
{
    /// <summary>
    /// Sent from a silo to the deadlock detector grain to either initiate a deadlock detection (when BatchId is null)
    /// or in response to a CollectLocksRequest.
    /// </summary>
    [GenerateSerializer]
    public sealed class CollectLocksResponse
    {
        [Id(0)]
        public Guid? BatchId { get; set; }

        [Id(1)]
        public long? MaxVersion { get; set; }

        [Id(2)]
        public SiloAddress SiloAddress { get; set; } = null!;

        [Id(3)]
        public IList<LockInfo> Locks { get; set; } = [];
    }
}