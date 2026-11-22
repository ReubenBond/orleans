using System.Globalization;

namespace Orleans.Durable.Storage.Engine.Ledgers;

/// <summary>
/// Reports that a sequencer lifetime is stopped: it grants and completes no slot (LP-I13). The writer answers an append
/// which meets it as <c>Unresolved</c> (ledger-protocol.md §2.4).
/// </summary>
internal sealed class SequencerStoppedException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SequencerStoppedException"/> class.
    /// </summary>
    public SequencerStoppedException()
        : base("The sequencer lifetime was stopped.")
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SequencerStoppedException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    public SequencerStoppedException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SequencerStoppedException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public SequencerStoppedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SequencerStoppedException"/> class.
    /// </summary>
    /// <param name="reason">Why the lifetime was stopped.</param>
    public SequencerStoppedException(SequencerStopReason reason)
        : base(string.Create(CultureInfo.InvariantCulture, $"The sequencer lifetime was stopped ({reason}).")) =>
        Reason = reason;

    /// <summary>
    /// Gets why the lifetime was stopped, or <see cref="SequencerStopReason.None"/> when the exception was created without
    /// one.
    /// </summary>
    public SequencerStopReason Reason { get; }
}
