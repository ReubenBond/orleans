using Orleans.Durable.Storage.Engine.Buffers;

namespace Orleans.Durable.Storage.Engine.ObjectStore;

/// <summary>
/// An object's whole content with its version, as <see cref="IObjectStore.ReadVersionedAsync"/> returns it.
/// </summary>
/// <remarks>
/// The caller owns <see cref="Content"/> and disposes it.
/// </remarks>
public readonly struct VersionedObject
{
    /// <summary>
    /// Initializes a new instance of the <see cref="VersionedObject"/> struct.
    /// </summary>
    /// <param name="content">The object's content, which the caller of the read owns.</param>
    /// <param name="version">The version of the content.</param>
    public VersionedObject(ArcBuffer content, ObjectVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        Content = content;
        Version = version;
    }

    /// <summary>
    /// Gets the object's content, which the caller of the read owns and disposes.
    /// </summary>
    public ArcBuffer Content { get; }

    /// <summary>
    /// Gets the version of the content.
    /// </summary>
    public ObjectVersion Version { get; }
}
