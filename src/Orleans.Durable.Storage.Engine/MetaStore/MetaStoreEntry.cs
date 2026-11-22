using Orleans.Durable.Storage.Engine.Buffers;

namespace Orleans.Durable.Storage.Engine.MetaStore;

/// <summary>
/// A value read from a conditional register, with its version. The entry owns its value; its reader disposes it.
/// </summary>
/// <remarks>
/// An empty value is <c>default(ArcBuffer)</c>, which owns nothing. Entries have no content equality: a reader compares
/// versions, which name values (MS-I5), or the bytes it holds.
/// </remarks>
public sealed class MetaStoreEntry : IDisposable
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MetaStoreEntry"/> class, which takes ownership of
    /// <paramref name="value"/>.
    /// </summary>
    /// <param name="version">The register's version.</param>
    /// <param name="value">The register's value, which the entry owns.</param>
    public MetaStoreEntry(MetaStoreVersion version, ArcBuffer value)
    {
        ArgumentNullException.ThrowIfNull(version);
        Version = version;
        Value = value;
    }

    /// <summary>
    /// Gets the register's version.
    /// </summary>
    public MetaStoreVersion Version { get; }

    /// <summary>
    /// Gets the register's value, valid until the entry is disposed.
    /// </summary>
    public ArcBuffer Value { get; }

    /// <summary>
    /// Releases the value.
    /// </summary>
    public void Dispose() => Value.Dispose();
}
