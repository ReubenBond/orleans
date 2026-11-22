using System.Buffers;
using System.Globalization;
using Orleans.Durable.Storage.Engine.Buffers;

namespace Orleans.Durable.Storage.Engine.MetaStore;

/// <summary>
/// One conditional register in process memory, whose version is the decimal count of applied writes.
/// </summary>
/// <remarks>
/// A write copies its value into an array of its own, and an applied write replaces the register's array rather than
/// changing it, so a read wraps the array it finds without a copy.
/// </remarks>
public sealed class InMemoryConditionalRegister : IConditionalRegister
{
    private readonly Lock _lock = new();
    private byte[]? _value;
    private long _version;

    /// <inheritdoc/>
    public ValueTask<MetaStoreEntry?> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
#pragma warning disable CA2000 // Ownership of the entry transfers to the caller.
        return ValueTask.FromResult(Read());
#pragma warning restore CA2000
    }

    /// <inheritdoc/>
    public ValueTask<RegisterWriteOutcome> WriteAsync(
        MetaStoreVersion? expectedVersion,
        ReadOnlySequence<byte> value,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            // "The MetaStore component has a simple API: it is a single versioned register supporting a
            // conditional write. Reading the MetaStore returns a value with an attached version. Writing to it
            // requires supplying a new value and an expected existing version."
            // See research/papers/delos/paper.md, section 3.2.
            if (expectedVersion is null
                ? _value is not null
                : _value is null || expectedVersion != CreateVersion(_version))
            {
                return ValueTask.FromResult<RegisterWriteOutcome>(RegisterWriteOutcome.Mismatch.Instance);
            }

            _value = value.ToArray();
            _version++;
            return ValueTask.FromResult<RegisterWriteOutcome>(new RegisterWriteOutcome.Applied(CreateVersion(_version)));
        }
    }

    // MS-I5 (metastore.md §5): the count of applied writes only grows, so a version is never reissued.
    private static MetaStoreVersion CreateVersion(long version) =>
        new(version.ToString(CultureInfo.InvariantCulture));

    private MetaStoreEntry? Read()
    {
        lock (_lock)
        {
            return _value is null ? null : new MetaStoreEntry(CreateVersion(_version), ArcBuffer.Wrap(_value));
        }
    }
}
