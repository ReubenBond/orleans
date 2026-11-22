namespace Orleans.Durable.Storage.Engine.Ledgers;

/// <summary>
/// The kind of step a sequencer lifetime takes.
/// </summary>
internal enum SequencerStepKind
{
    /// <summary>
    /// The lifetime granted a slot.
    /// </summary>
    Acquired,

    /// <summary>
    /// The writer called the slot's completion, which it does only once the slot's write was written on <c>Q</c>
    /// replicas (ledger-protocol.md §8.1).
    /// </summary>
    Completing,

    /// <summary>
    /// The slot's completion returned: every lower slot has completed (LP-I3).
    /// </summary>
    Completed,

    /// <summary>
    /// The slot's write met a seal or a drop, so the slot never completes (LP-I8); the writer reports it before it stops
    /// the lifetime.
    /// </summary>
    Sealed,

    /// <summary>
    /// A stop request returned. A stop is idempotent, so a stopped lifetime reports a later request again; its first stop
    /// is the one which ended it.
    /// </summary>
    Stopped,
}

/// <summary>
/// One step of a sequencer lifetime, which its <see cref="Sequencer.Observer"/> receives.
/// </summary>
/// <param name="Kind">The kind of step.</param>
/// <param name="Slot">The slot of an acquisition, a completion, or a write which met a seal.</param>
/// <param name="Reason">The reason of a stop.</param>
internal readonly record struct SequencerStep(SequencerStepKind Kind, Slot? Slot, SequencerStopReason? Reason)
{
    internal static SequencerStep Acquired(Slot slot) => new(SequencerStepKind.Acquired, slot, Reason: null);

    internal static SequencerStep Completing(Slot slot) => new(SequencerStepKind.Completing, slot, Reason: null);

    internal static SequencerStep Completed(Slot slot) => new(SequencerStepKind.Completed, slot, Reason: null);

    internal static SequencerStep Sealed(Slot slot) => new(SequencerStepKind.Sealed, slot, Reason: null);

    internal static SequencerStep Stopped(SequencerStopReason reason) => new(SequencerStepKind.Stopped, Slot: null, reason);
}
