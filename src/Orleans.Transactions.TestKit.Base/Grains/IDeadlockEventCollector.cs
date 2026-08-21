using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Concurrency;
using Orleans.Transactions.DeadlockDetection;

namespace Orleans.Transactions.TestKit.Base.Grains
{
    [GenerateSerializer]
    [Immutable]
    public sealed class DeadlockEvent
    {
        [Id(0)]
        public DateTime StartTime;

        [Id(1)]
        public TimeSpan Duration;

        [Id(2)]
        public bool Local;

        [Id(3)]
        public int RequestCount;

        [Id(4)]
        public bool IsDefinite;

        [Id(5)]
        public LockInfo[]? Locks;

        [Id(6)]
        public bool Deadlocked;
    }

    public interface IDeadlockEventCollector : IGrainWithIntegerKey
    {
        [OneWay]
        [Transaction(TransactionOption.Suppress)]
        Task ReportEvent(DeadlockEvent @event);

        [Transaction(TransactionOption.Suppress)]
        Task<IList<DeadlockEvent>> GetEvents();

        [Transaction(TransactionOption.Suppress)]
        Task Clear();
    }
}