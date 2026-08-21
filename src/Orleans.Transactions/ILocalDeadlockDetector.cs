using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Orleans.Transactions
{
    [GenerateSerializer]
    public sealed class CollectLocksRequest
    {
        [Id(0)]
        public List<Guid> TransactionIds { get; set; } = [];

        [Id(1)]
        public long? MaxVersion { get; set; }

        [Id(2)]
        public Guid BatchId { get; set; }
    }

    public interface ILocalDeadlockDetector : ISystemTarget
    {
        Task CollectLocks(CollectLocksRequest request);
    }
}