using Orleans.Durable.Storage.Engine.Ledgers.Store;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests;

/// <summary>
/// Asserts that an operation answered <c>Dropped</c>: its node dropped the ledger (LR-I3, LP-I5).
/// </summary>
internal static class DroppedAnswers
{
    internal static void AssertDropped(ReplicaWriteOutcome answer) => Assert.IsType<ReplicaWriteOutcome.Dropped>(answer.Value);

    internal static void AssertDropped(ReplicaRead answer) => Assert.Equal(ReplicaReadCase.Dropped, answer.Case);

    internal static void AssertDropped(ReplicaTailRead answer) => Assert.IsType<ReplicaWriteOutcome.Dropped>(answer.Value);

    internal static void AssertDropped(RecoveryOutcome answer) => Assert.IsType<ReplicaWriteOutcome.Dropped>(answer.Value);

    internal static void AssertDropped(LedgerRecoveryOutcome answer) => Assert.IsType<ReplicaWriteOutcome.Dropped>(answer.Value);

    /// <summary>
    /// Asserts that a creation answered dropped: the store created no replica (LS-I2).
    /// </summary>
    internal static void AssertDropped(LiveReplica? created) => Assert.Null(created);
}
