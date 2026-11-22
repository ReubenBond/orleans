namespace Orleans.Durable.Storage.Engine.Ledgers;

/// <summary>
/// A completed recovery of a ledger's storage: the decided prefix it learned on the way, and its candidate end
/// (ledger-protocol.md §2.7, LL-I3).
/// </summary>
/// <param name="ContiguousTail">
/// The recovery's <c>c</c>, at or above the contiguous tail it carried: every slot below it is decided, so it is a known
/// contiguous tail. The quorum's is the greatest among its seal answers and the tail it carried (LP-I11).
/// </param>
/// <param name="End">
/// The candidate end, at or above <c>c</c> and below <c>c + 2K</c> (LL-I3; the quorum's, LP-I7); a candidate until the
/// ledger map records it as the ledger's <c>T</c>.
/// </param>
/// <param name="LeaseRemaining">
/// The maximum remaining replica lease duration across the sealing quorum (LP-I18), or <see cref="TimeSpan.Zero"/>
/// when leases are disabled, expired, or the storage does not use replica leases.
/// </param>
internal readonly record struct LedgerRecovery(Slot ContiguousTail, Slot End, TimeSpan LeaseRemaining = default);

/// <summary>
/// What a recovery of a ledger's storage gives (<see cref="ILedgerStorage.RecoverAsync"/>, conventions.md §15): the
/// completed recovery, or <see cref="ReplicaWriteOutcome.Dropped"/> once the storage answered that the ledger was
/// dropped (LL-I4).
/// </summary>
internal readonly union LedgerRecoveryOutcome(LedgerRecovery, ReplicaWriteOutcome.Dropped);
