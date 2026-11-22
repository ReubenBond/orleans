namespace Orleans.Durable.Storage.Engine.Ledgers;

/// <summary>
/// Identifies why one sequencer lifetime was permanently stopped (ledger-protocol.md §4).
/// </summary>
internal enum SequencerStopReason
{
    /// <summary>
    /// No stop reason was specified. This value is invalid for a stop request.
    /// </summary>
    None = 0,

    /// <summary>
    /// The writer acquired a slot but can never write its batch: the write failed in a way repeating the identical write
    /// cannot change. Storage outages only delay a write (LP-I12).
    /// </summary>
    PayloadWriteFailed = 1,

    /// <summary>
    /// The writer was disposed, which ends every append it admitted. From row 1.4, the executor of a transition whose map
    /// write did not apply disposes the writer it created, and the primary disposes its writer once its view no longer
    /// lists its ledger.
    /// </summary>
    Requested = 2,

    /// <summary>
    /// The ledger is being sealed: a recovery in the writer's process, an append whose write met a seal or a dropped
    /// replica, or a seal check in the writer's process which found the ledger sealed (LP-I14).
    /// </summary>
    Sealing = 4,

    /// <summary>
    /// A batch is durable, but its slot's completion did not return.
    /// </summary>
    CompletionFailed = 5,

    /// <summary>
    /// The writer's user met a fault of its own and proposes nothing more (<see cref="LedgerWriter.Abandon"/>), so the
    /// ledger is replaced by a takeover.
    /// </summary>
    Abandoned = 6,

    /// <summary>
    /// The primary drained in-flight writes and stopped admitting cooperatively for a planned handoff
    /// (<see cref="LedgerWriter.PrepareReleaseAsync"/>); its successor replaces it with a cooperative predecessor hint.
    /// </summary>
    Released = 7,
}
