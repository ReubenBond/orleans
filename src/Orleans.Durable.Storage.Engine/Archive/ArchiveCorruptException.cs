namespace Orleans.Durable.Storage.Engine.Archive;

/// <summary>
/// The history contradicts itself (archive.md §4): a CRC-32C mismatch, a row the store names and does not hold, a
/// store's record which contradicts the ledger map, or an LSM object or manifest which fails its integrity check
/// (archive §6). Stored bytes which are damaged, never reported as missing or absent (outcomes.md §8).
/// </summary>
public sealed class ArchiveCorruptException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="ArchiveCorruptException"/> class.</summary>
    public ArchiveCorruptException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ArchiveCorruptException"/> class.</summary>
    public ArchiveCorruptException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ArchiveCorruptException"/> class.</summary>
    public ArchiveCorruptException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
