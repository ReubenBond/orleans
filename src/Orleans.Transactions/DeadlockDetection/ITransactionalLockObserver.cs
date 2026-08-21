using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Providers;
using Orleans.Services;

namespace Orleans.Transactions.DeadlockDetection
{
    internal interface ITransactionalLockObserver
    {
        TimeSpan DetectionTimeout { get; }

        void OnResourceRequested(Guid transactionId, ParticipantId resourceId);

        void OnResourceLocked(Guid transactionId, ParticipantId resourceId);

        void OnResourceUnlocked(Guid transactionId, ParticipantId resourceId);

        Task StartDeadlockDetection(ParticipantId lockedResource, IEnumerable<Guid> lockedByTransactions);
    }
}