using Orleans.Durable.Storage.Engine.Archive.Logs;

namespace Orleans.Durable.Storage;

/// <summary>
/// The options of a cluster whose archive store is archive logs (orleans.md §5.13, §6), the <c>Node:ArchiveLogs</c> section
/// of the silo's options: the node's drains' bounds. The logs and durable storage's objects come from the <c>IArchiveLogContainer</c>
/// the silo's services supply, as <c>Orleans.Durable.Storage.AzureStorage</c>'s <c>AddAzureArchiveLogs</c> registers it. Unused on the LSM.
/// </summary>
public sealed class ArchiveLogStoreOptions
{
    /// <summary>Gets or sets the most key writes the node's drains make at once, over every ledger they drain; 64 by default (archive-logs.md §8.6).</summary>
    public int DrainConcurrency { get; set; } = 64;

    /// <summary>
    /// Gets or sets the bytes of segments the node's drains hold at once, a hard bound a segment alone may not pass; 512 MiB
    /// by default (archive-logs.md §8.1, §8.6).
    /// </summary>
    public long DrainMemoryBytes { get; set; } = 512L << 20;

    /// <summary>Throws unless the options are in range, naming the option.</summary>
    /// <exception cref="ArgumentException">An option is out of range.</exception>
    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(DrainConcurrency, nameof(DrainConcurrency));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(DrainMemoryBytes, nameof(DrainMemoryBytes));
    }

    /// <summary>Returns the drains' options. A method, not a property, so a configuration binder sees no key for it.</summary>
    internal ArchiveLogOptions CreateDrainOptions() => new() { DrainConcurrency = DrainConcurrency, DrainMemoryBytes = DrainMemoryBytes };
}
