namespace Orleans.Durable.Storage.Engine.Ledgers;

/// <summary>
/// Describes a replica's contiguous tail, whether it is sealed, and, on a sealing read, any remaining replica lease
/// duration, as <see cref="ILedgerReplica.ReadTailAsync"/> reports them (ledger-replicas.md §2.1, LR-I10).
/// </summary>
/// <param name="ContiguousTail">
/// A known contiguous tail of the ledger: every address below it holds DATA on <c>Q</c> of the ledger's replicas, and it is
/// never below the operation's argument (LR-I6).
/// </param>
/// <param name="IsSealed">
/// Whether the replica is sealed; a ledger replica's seal is durable (LR-I8).
/// </param>
/// <param name="LeaseRemaining">
/// On a sealing read (<c>seal: true</c>), the remaining duration of any granted or boot lease horizon in the replica's
/// monotonic clock, or <see cref="TimeSpan.Zero"/> when leases are disabled, expired, or <c>seal</c> is
/// <see langword="false"/> (LR-I10).
/// </param>
public readonly record struct LedgerReplicaTail(Slot ContiguousTail, bool IsSealed, TimeSpan LeaseRemaining = default);
