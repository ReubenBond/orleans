namespace Orleans.Durable.Storage.Engine.Archive.Logs;

/// <summary>
/// A log's name as its store forms it (archive-logs.md §4, §7.1): the composition's injective name for the key, which
/// names nothing under <c>durable-storage/</c>, and the xxHash64 of the key, which every write records and every read checks.
/// </summary>
public sealed record ArchiveLogName
{
    /// <summary>Initializes a new instance of the <see cref="ArchiveLogName"/> class.</summary>
    /// <param name="value">The log's name, non-empty.</param>
    /// <param name="keyHash">The xxHash64 (seed 0) of the log's key.</param>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty.</exception>
    public ArchiveLogName(string value, ulong keyHash)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        Value = value;
        KeyHash = keyHash;
    }

    /// <summary>Gets the log's name: on Azure the <c>{id}</c> of <c>wal/{id}</c> and <c>checkpoints/{id}/{g:D20}</c>.</summary>
    public string Value { get; }

    /// <summary>Gets the xxHash64 (seed 0) of the log's key.</summary>
    public ulong KeyHash { get; }

    /// <inheritdoc/>
    public override string ToString() => Value;
}

/// <summary>A stored generation base of a log, as <see cref="IArchiveLogStore.ListGenerationsAsync"/> lists it.</summary>
/// <param name="Log">The log's name, <see cref="ArchiveLogName.Value"/>.</param>
/// <param name="Generation">The base's generation.</param>
/// <param name="KeyHash">
/// The key hash the base records, with which the sweeper names the log in its properties read, which checks it against
/// the log's own (archive-logs.md §8.5); <see langword="null"/> for a base whose recorded key hash does not parse, which
/// the sweeper counts and keeps.
/// </param>
public readonly record struct ArchiveLogGeneration(string Log, ulong Generation, ulong? KeyHash)
{
    /// <summary>Gets the name of the log the base belongs to, with the key hash it records; none when it records none which parses.</summary>
    public ArchiveLogName? Name => KeyHash is { } keyHash ? new(Log, keyHash) : null;
}

/// <summary>One page of the store's generation bases, as <see cref="IArchiveLogStore.ListGenerationsAsync"/> lists them.</summary>
/// <param name="Generations">The page's bases, in the listing's order.</param>
/// <param name="Continuation">
/// The store's opaque continuation after the page, from which a later listing resumes with the next page;
/// <see langword="null"/> after the listing's last page.
/// </param>
public sealed record ArchiveLogGenerationPage(IReadOnlyList<ArchiveLogGeneration> Generations, string? Continuation);
