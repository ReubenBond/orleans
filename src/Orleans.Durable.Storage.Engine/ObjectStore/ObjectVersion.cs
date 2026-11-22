namespace Orleans.Durable.Storage.Engine.ObjectStore;

/// <summary>
/// The opaque, provider-native version of an object's content, such as an ETag or a generation.
/// </summary>
/// <remarks>
/// Equality is ordinal. Callers never parse, order, or construct one; it is valid only for the object from which it was
/// read or whose replace returned it. A replace applies exactly when the object holds the version it names (object-store.md
/// §2.1). A version may be a digest of the content, as the file-system provider's is, so an object which holds earlier
/// content again holds its earlier version again; a replace's caller never writes content the object held before its
/// current content.
/// </remarks>
public sealed record ObjectVersion
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ObjectVersion"/> class.
    /// </summary>
    /// <param name="value">The non-blank provider-native token.</param>
    public ObjectVersion(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    /// <summary>
    /// Gets the provider-native token.
    /// </summary>
    public string Value { get; }

    /// <inheritdoc/>
    public override string ToString() => Value;
}
