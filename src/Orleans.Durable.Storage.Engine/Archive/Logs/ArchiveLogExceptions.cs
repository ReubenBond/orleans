namespace Orleans.Durable.Storage.Engine.Archive.Logs;

/// <summary>
/// A log's objects contradict themselves or their format, or name another key (archive-logs.md §3, §7.3): stored state
/// which is damaged or was written by another writer, never reported as absent or stale. <c>LogArchiveStore</c> and
/// <c>ArchiveLogWriter</c> report it upward as the archive's corruption.
/// </summary>
public sealed class ArchiveLogCorruptException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="ArchiveLogCorruptException"/> class.</summary>
    public ArchiveLogCorruptException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ArchiveLogCorruptException"/> class.</summary>
    public ArchiveLogCorruptException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ArchiveLogCorruptException"/> class.</summary>
    public ArchiveLogCorruptException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The store refused a write which no retry can make (archive-logs.md §6): a name, block count, or blob type the service
/// rejects. Its caller does not retry it; a drain which meets it stops at the key with cause <c>Refused</c>.
/// </summary>
public sealed class ArchiveLogRefusedException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="ArchiveLogRefusedException"/> class.</summary>
    public ArchiveLogRefusedException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ArchiveLogRefusedException"/> class.</summary>
    public ArchiveLogRefusedException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ArchiveLogRefusedException"/> class.</summary>
    public ArchiveLogRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
