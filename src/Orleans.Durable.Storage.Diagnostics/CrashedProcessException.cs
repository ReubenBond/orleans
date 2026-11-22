namespace Orleans.Durable.Storage.Diagnostics;

/// <summary>
/// The failure of a call made through a crashed process's view of storage, which the storage test doubles raise: the
/// simulated storage after a crash, and the crash-state recorder's file system. The process stopped, so the call reaches
/// nothing. A crashed process reports nothing, so the test hosts' recorder drops a <see cref="FatalErrorHandler"/> report
/// which carries this failure.
/// </summary>
public sealed class CrashedProcessException : IOException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CrashedProcessException"/> class.
    /// </summary>
    public CrashedProcessException()
        : base("The process crashed.")
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CrashedProcessException"/> class with a message.
    /// </summary>
    /// <param name="message">The message.</param>
    public CrashedProcessException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CrashedProcessException"/> class with a message and the failure
    /// which stopped the process before it crashed.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The failure which stopped the process.</param>
    public CrashedProcessException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
