using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions.DeadlockDetection
{
    [GenerateSerializer]
    public readonly struct LockInfo
    {
        [Id(0)]
        public readonly ParticipantId Resource;

        [Id(1)]
        public readonly Guid TxId;

        [Id(2)]
        public readonly bool IsWait;

        private LockInfo(ParticipantId resource, Guid txId, bool isWait)
        {
            this.Resource = resource;
            this.TxId = txId;
            this.IsWait = isWait;
        }

        public static LockInfo ForWait(ParticipantId waitingFor, Guid waiter) =>
            new LockInfo (waitingFor, waiter, true);

        public static LockInfo  ForLock(ParticipantId locked, Guid lockedBy) =>
            new LockInfo (locked, lockedBy, false);

        public static readonly IEqualityComparer<LockInfo> EqualityComparer = new LockKeyEqualityComparer();

        public override string ToString() => this.IsWait ? $"{this.TxId} -W-> {this.Resource}" : $"{this.Resource} -L-> {this.TxId}";

        private class LockKeyEqualityComparer : IEqualityComparer<LockInfo>
        {
            public bool Equals(LockInfo x, LockInfo y) =>
                x.Resource.Equals(y.Resource) && x.TxId.Equals(y.TxId) && x.IsWait == y.IsWait;

            public int GetHashCode(LockInfo  obj)
            {
                unchecked
                {
                    int hashCode = obj.Resource.GetHashCode();
                    hashCode = (hashCode * 397) ^ obj.TxId.GetHashCode();
                    hashCode = (hashCode * 397) ^ obj.IsWait.GetHashCode();
                    return hashCode;
                }
            }
        }

    }

    public static class LockInfoExtensions
    {
        public static Task BreakLocks(this IEnumerable<LockInfo> locks)
        {
            var tasks = locks.Select(l =>
                l.Resource.Reference.AsReference<IDeadlockResourceExtension>()
                    .BreakLocks(l.Resource.Name));
            return Task.WhenAll(tasks);
        }
    }
}