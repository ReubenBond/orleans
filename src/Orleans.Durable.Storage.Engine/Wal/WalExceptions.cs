namespace Orleans.Durable.Storage.Engine.Wal;

/// <summary>
/// Indicates that <see cref="WriteAheadLog.CreateAsync(WalStorage, string, WalOptions, CancellationToken)"/> found a write-ahead log which already holds files.
/// </summary>
public sealed class WalExistsException : IOException
{
    public WalExistsException()
        : base("The write-ahead log already exists.")
    {
    }

    public WalExistsException(string message)
        : base(message)
    {
    }

    public WalExistsException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Indicates that <see cref="WriteAheadLog.OpenAsync(WalStorage, string, WalOptions, CancellationToken)"/> found no write-ahead log: the directory is missing or holds no
/// files.
/// </summary>
public sealed class WalNotFoundException : IOException
{
    public WalNotFoundException()
        : base("The write-ahead log does not exist.")
    {
    }

    public WalNotFoundException(string message)
        : base(message)
    {
    }

    public WalNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Indicates a file format version this implementation does not read, or a page size the storage cannot
/// write atomically.
/// </summary>
public sealed class WalUnsupportedFormatException : IOException
{
    public WalUnsupportedFormatException()
        : base("The write-ahead log format is not supported.")
    {
    }

    public WalUnsupportedFormatException(string message)
        : base(message)
    {
    }

    public WalUnsupportedFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
