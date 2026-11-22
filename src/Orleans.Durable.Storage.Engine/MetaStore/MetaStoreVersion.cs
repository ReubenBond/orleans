namespace Orleans.Durable.Storage.Engine.MetaStore;

/// <summary>
/// The opaque, backend-native condition token of a register value, such as an ETag or a version attribute.
/// </summary>
/// <remarks>
/// Equality is ordinal. Callers never parse, order, or construct one; it is valid only for the register which issued
/// it, through a read or an applied write, and every applied write gives the register a version it never had (MS-I5,
/// metastore.md §5).
/// </remarks>
public sealed record MetaStoreVersion
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MetaStoreVersion"/> class.
    /// </summary>
    /// <param name="value">The non-blank backend-native token.</param>
    public MetaStoreVersion(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    /// <summary>
    /// Gets the backend-native token.
    /// </summary>
    public string Value { get; }

    /// <inheritdoc/>
    public override string ToString() => Value;
}
