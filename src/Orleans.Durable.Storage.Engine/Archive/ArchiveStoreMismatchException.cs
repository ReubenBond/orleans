namespace Orleans.Durable.Storage.Engine.Archive;

/// <summary>
/// An archive store's record names other store fields or caller parameters than the process's (archive.md §2.4, §4): the
/// process is configured with another store, whose store fields would give its pure members another meaning (HI-I3), or
/// with other parameters than the caller's own contract records. <see cref="IArchiveStore.StoreRecordAsync"/> throws it on every store for a store field, and
/// <see cref="ArchiveStoreRecord.CheckParametersMatch"/> for a caller parameter.
/// </summary>
public sealed class ArchiveStoreMismatchException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="ArchiveStoreMismatchException"/> class.</summary>
    public ArchiveStoreMismatchException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ArchiveStoreMismatchException"/> class.</summary>
    public ArchiveStoreMismatchException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ArchiveStoreMismatchException"/> class.</summary>
    public ArchiveStoreMismatchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ArchiveStoreMismatchException"/> class for the field which differs, with
    /// the record's value and the process's.
    /// </summary>
    /// <param name="field">The first field or caller parameter of the record which differs from the process's.</param>
    /// <param name="stored">The record's value of the field, or <see langword="null"/> when the record lacks it.</param>
    /// <param name="caller">
    /// The process's value of the field: the store's own for a store field, the caller's for a caller parameter; or
    /// <see langword="null"/> when the process lacks it.
    /// </param>
    public ArchiveStoreMismatchException(ArchiveStoreRecordField field, string? stored, string? caller)
        : base(field.IsParameter
            ? $"The archive store's record holds the caller parameter '{field.Name}' as {Quoted(stored)}, and this process is configured with {Quoted(caller)}: the process is configured with other parameters."
            : $"The archive store's record holds '{field.Name}' as {Quoted(stored)}, and this process is configured with {Quoted(caller)}: the process is configured with another store.")
    {
        Field = field.Name;
        IsParameter = field.IsParameter;
        StoredValue = stored;
        CallerValue = caller;
    }

    /// <summary>Gets the name of the first store field or caller parameter of the store's record which differs from the process's.</summary>
    public string? Field { get; }

    /// <summary>Gets a value indicating whether <see cref="Field"/> is one of the caller's parameters, not a store field.</summary>
    public bool IsParameter { get; }

    /// <summary>Gets the record's value of <see cref="Field"/>, or <see langword="null"/> when the record lacks it.</summary>
    public string? StoredValue { get; }

    /// <summary>
    /// Gets the process's value of <see cref="Field"/>: the store's own for a store field, the caller's for a caller
    /// parameter; or <see langword="null"/> when the process lacks it.
    /// </summary>
    public string? CallerValue { get; }

    private static string Quoted(string? value) => value is null ? "nothing" : $"'{value}'";
}
