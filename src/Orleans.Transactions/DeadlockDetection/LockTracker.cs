using System;
using System.Collections.Generic;

namespace Orleans.Transactions.DeadlockDetection
{
    /// <summary>
    /// Supports thread safe tracking for a collection of locks and waits.
    /// </summary>
    internal class LockTracker
    {
        private readonly object gate = new();
        private readonly HashSet<LockInfo> locksAndWaits = new(LockInfo.EqualityComparer);
        private long nextSnapshotVersion;

        public void TrackEnterLock(ParticipantId lockedGrain, Guid lockedByTx)
        {
            lock (this.gate)
            {
                this.locksAndWaits.Remove(LockInfo.ForWait(lockedGrain, lockedByTx));
                this.locksAndWaits.Add(LockInfo.ForLock(lockedGrain, lockedByTx));
            }
        }

        public void TrackExitLock(ParticipantId lockedGrain, Guid lockedByTx)
        {
            lock (this.gate)
            {
                this.locksAndWaits.Remove(LockInfo.ForWait(lockedGrain, lockedByTx));
                this.locksAndWaits.Remove(LockInfo.ForLock(lockedGrain, lockedByTx));
            }
        }

        public void TrackWait(ParticipantId waitingForGrain, Guid waitingTx)
        {
            lock (this.gate)
            {
                this.locksAndWaits.Add(LockInfo.ForWait(waitingForGrain, waitingTx));
            }
        }

        public (long Version, LockInfo[] Locks) CaptureSnapshot()
        {
            lock (this.gate)
            {
                return (this.nextSnapshotVersion++, [.. this.locksAndWaits]);
            }
        }
    }
}