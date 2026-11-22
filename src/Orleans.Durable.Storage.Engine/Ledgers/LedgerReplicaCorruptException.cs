namespace Orleans.Durable.Storage.Engine.Ledgers;

/// <summary>
/// Indicates that a ledger replica address holds a stored value which fails its integrity check, or that a node's store is
/// damaged so it cannot establish its replicas.
/// </summary>
/// <remarks>
/// LR-I2: corruption is never reported as missing or DATA. The ledger protocol treats this exception as the
/// replica's failure, never as a missing observation.
/// </remarks>
public sealed class LedgerReplicaCorruptException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="LedgerReplicaCorruptException"/> class.
    /// </summary>
    public LedgerReplicaCorruptException()
        : base("A ledger replica's slot holds a corrupt value.")
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="LedgerReplicaCorruptException"/> class.
    /// </summary>
    public LedgerReplicaCorruptException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="LedgerReplicaCorruptException"/> class.
    /// </summary>
    public LedgerReplicaCorruptException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="LedgerReplicaCorruptException"/> class for
    /// <paramref name="slot"/>.
    /// </summary>
    public LedgerReplicaCorruptException(Slot slot, string message)
        : base(message) => Slot = slot;

    /// <summary>
    /// Gets the corrupt slot, when known.
    /// </summary>
    public Slot? Slot { get; }
}
