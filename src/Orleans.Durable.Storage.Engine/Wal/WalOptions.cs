using System.Numerics;

namespace Orleans.Durable.Storage.Engine.Wal;

/// <summary>
/// Configures one open of a write-ahead log.
/// </summary>
/// <remarks>
/// Each open records its page size and write window in the header of every file it creates, so a later open
/// may choose different values; readers decode each file with its own.
/// </remarks>
public sealed record WalOptions
{
    /// <summary>
    /// The smallest supported page size.
    /// </summary>
    public const int MinimumPageSize = 512;

    /// <summary>
    /// The largest supported page size.
    /// </summary>
    public const int MaximumPageSize = 64 * 1024;

    /// <summary>
    /// Gets the page size P: a power of two from 512 bytes to 64 KiB, and at least the storage's
    /// <see cref="WalDirectory.MinimumPageSize"/>.
    /// </summary>
    public int PageSize { get; init; } = 4096;

    /// <summary>
    /// Gets the size of a file, including its header page. A file holding a larger record grows to fit
    /// it.
    /// </summary>
    public long FileSize { get; init; } = 256L * 1024 * 1024;

    /// <summary>
    /// Gets a value indicating whether each new file is created at its full size with every byte written as zero,
    /// so later writes change no file metadata and need no metadata sync. Preallocated files start at 4 MiB and
    /// double up to <see cref="FileSize"/>. The next one is prepared once the current one is a quarter full, with
    /// its zero-fill paced to finish when the current one is three quarters full at its observed fill rate. While
    /// durable groups average at least 3 MiB, rolls create files allocated without the zero-fill instead, until groups
    /// average below 1 MiB.
    /// </summary>
    public bool PreallocateFiles { get; init; } = true;

    /// <summary>
    /// Gets the clock which paces the preparation of preallocated files.
    /// </summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>
    /// Gets the fewest write groups the log keeps in flight together while records wait. The log uses this many
    /// while the device runs durable writes one at a time, so records accepted during a write share the next.
    /// </summary>
    public int MinWriteGroupsInFlight { get; init; } = 1;

    /// <summary>
    /// Gets the most write groups the log keeps in flight together. The log uses this many while groups submitted
    /// alongside others become durable about as soon as lone groups, as on a device which runs durable writes in
    /// parallel. Setting it equal to <see cref="MinWriteGroupsInFlight"/> fixes the concurrency.
    /// </summary>
    public int MaxWriteGroupsInFlight { get; init; } = 4;

    /// <summary>
    /// Gets the write window W: a data page is submitted only when every page at least W pages before it in its
    /// file is durable.
    /// </summary>
    public int WriteWindowPages { get; init; } = 4096;

    /// <summary>
    /// Gets the number of record bytes which may be accepted and not yet durable before
    /// <see cref="WriteAheadLog.WriteAsync"/> waits for capacity.
    /// </summary>
    public long PageBufferBudgetBytes { get; init; } = 16 * 1024 * 1024;

    /// <summary>
    /// Validates the options.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">An option is outside its supported range.</exception>
    public void Validate()
    {
        if (PageSize is < MinimumPageSize or > MaximumPageSize || !BitOperations.IsPow2(PageSize))
        {
            throw Invalid(nameof(PageSize), PageSize, "The page size must be a power of two from 512 bytes to 64 KiB.");
        }

        if (FileSize < 2L * PageSize || FileSize % PageSize != 0)
        {
            throw Invalid(nameof(FileSize), FileSize, "The file size must be a multiple of the page size holding at least two pages.");
        }

        // A WalPage indexes data pages with 32 bits. Records of at most int.MaxValue bytes never grow a file past
        // this bound.
        if (FileSize / PageSize - 1 > uint.MaxValue)
        {
            throw Invalid(nameof(FileSize), FileSize, "A file holds at most 2^32 data pages.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MinWriteGroupsInFlight);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxWriteGroupsInFlight, MinWriteGroupsInFlight);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(WriteWindowPages);
        ArgumentOutOfRangeException.ThrowIfLessThan(PageBufferBudgetBytes, PageSize);
        ArgumentNullException.ThrowIfNull(TimeProvider);
    }

    private static ArgumentOutOfRangeException Invalid(string name, object value, string message) => new(name, value, message);
}
