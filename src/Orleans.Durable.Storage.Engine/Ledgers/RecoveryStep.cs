namespace Orleans.Durable.Storage.Engine.Ledgers;

/// <summary>
/// The kind of replica answer a recovery handles.
/// </summary>
internal enum RecoveryStepKind
{
    /// <summary>
    /// A seal's answer.
    /// </summary>
    Seal,

    /// <summary>
    /// A read's answer.
    /// </summary>
    Read,

    /// <summary>
    /// A recovery write's answer.
    /// </summary>
    RecoveryWrite,
}

/// <summary>
/// The answer a replica gave an operation of a recovery.
/// </summary>
internal enum RecoveryAnswer
{
    /// <summary>
    /// A seal's answer: the replica's contiguous tail, sealed.
    /// </summary>
    Tail,

    /// <summary>
    /// A read's batch.
    /// </summary>
    Batch,

    /// <summary>
    /// A read's <see cref="ReplicaRead.Missing"/>.
    /// </summary>
    Missing,

    /// <summary>
    /// A recovery write's <see cref="ReplicaWriteOutcome.Written"/>.
    /// </summary>
    Written,

    /// <summary>
    /// The replica's node dropped the ledger (LP-I5).
    /// </summary>
    Dropped,
}

/// <summary>
/// One replica answer a recovery handled. The observer a recovery is given receives each, serially and in the order the
/// recovery handles them (the observer <see cref="LedgerQuorum"/> is built with), so its first
/// <c>N − Q + 1</c> seal steps with a tail are the answers the recovery's <c>c</c> comes from (LP-I6), and every call
/// returns before the recovery does. Deterministic simulation maps the recovery onto the ledger model's steps through it
/// (test/Durable.Storage/docs/spec/verification.md §5.1).
/// </summary>
/// <param name="Kind">The kind of answer.</param>
/// <param name="Replica">The replica's index in the ledger's replicas.</param>
/// <param name="Address">The address of a read or a recovery write.</param>
/// <param name="Tail">An answered seal's tail and seal.</param>
/// <param name="Answer">The replica's answer, unless the operation failed or was stopped.</param>
/// <param name="AfterSeal">Whether a read was sent after the recovery handled its replica's seal answer (LP-I7).</param>
/// <param name="Failure">The replica operation's failure, if it failed.</param>
/// <param name="Stopped">Whether the recovery stopped the operation, once it no longer needed the answer.</param>
internal readonly record struct RecoveryStep(
    RecoveryStepKind Kind,
    int Replica,
    long? Address,
    LedgerReplicaTail? Tail,
    RecoveryAnswer? Answer,
    bool AfterSeal,
    Exception? Failure,
    bool Stopped);
