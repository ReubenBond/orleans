using System.Globalization;
using Orleans.Runtime;

namespace Orleans.Durable.Storage;

/// <summary>
/// A journal write's outcome is unknown: the write was sent, and no answer settled it, so it may yet apply, at the version
/// it expected and only there (orleans.md §5.5, OR-I7, conventions §13). It asserts nothing about the journal; Orleans'
/// journaled state manager fences on it and recovers through a new storage instance, which reads first.
/// </summary>
public sealed class JournalWriteOutcomeUnknownException : OrleansException
{
    /// <summary>Initializes a new instance of the <see cref="JournalWriteOutcomeUnknownException"/> class.</summary>
    public JournalWriteOutcomeUnknownException()
        : base("A journal write's outcome is unknown: it may yet apply.")
    {
    }

    /// <summary>Initializes a new instance of the <see cref="JournalWriteOutcomeUnknownException"/> class.</summary>
    public JournalWriteOutcomeUnknownException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="JournalWriteOutcomeUnknownException"/> class.</summary>
    public JournalWriteOutcomeUnknownException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="JournalWriteOutcomeUnknownException"/> class for the
    /// <paramref name="operation"/> of the journal <paramref name="journal"/>.
    /// </summary>
    internal JournalWriteOutcomeUnknownException(string journal, string operation)
        : base(string.Create(CultureInfo.InvariantCulture, $"The outcome of {operation} of the journal '{journal}' is unknown: it may yet apply.")) => Journal = journal;

    /// <summary>Gets the journal the write named, when known.</summary>
    public string? Journal { get; }
}
