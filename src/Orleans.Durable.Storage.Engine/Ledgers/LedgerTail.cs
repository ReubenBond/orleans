namespace Orleans.Durable.Storage.Engine.Ledgers;

/// <summary>
/// A ledger's known contiguous tail and a seal check's result (ledger-protocol.md §2.1).
/// </summary>
/// <param name="ContiguousTail">The known contiguous tail: every slot below it holds a batch on <c>Q</c> replicas (LP-I11).</param>
/// <param name="IsSealed">Whether a replica answered sealed or dropped (LP-I5, LP-I9).</param>
/// <param name="ServedHere">
/// Whether the process's <see cref="Ledger"/> held the writer which serves the ledger's proposals before the check began,
/// so that <paramref name="ContiguousTail"/> covers every proposal that writer answered durable (ledgers.md §2.5, LG-I8).
/// </param>
public readonly record struct LedgerTail(Slot ContiguousTail, bool IsSealed, bool ServedHere = false);
