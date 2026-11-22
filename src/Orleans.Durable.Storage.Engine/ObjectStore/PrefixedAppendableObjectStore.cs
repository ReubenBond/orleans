using Orleans.Durable.Storage.Engine.Buffers;

namespace Orleans.Durable.Storage.Engine.ObjectStore;

/// <summary>
/// An object store of both interfaces under a prefix of one provider instance held through its two interfaces
/// (object-store.md §2.3, orleans.md §5.13, §5.14): every call forwards to the same underlying store under
/// <c>{prefix}{name}</c>, and a listing lists only the objects under the prefix, naming them without it, as
/// <see cref="PrefixedObjectStore"/> does. It makes no request of its own.
/// </summary>
public sealed class PrefixedAppendableObjectStore : IObjectStore, IAppendableObjectStore
{
    private readonly IAppendableObjectStore _appendable;
    private readonly PrefixedObjectStore _objects;

    /// <summary>Initializes a new instance of the <see cref="PrefixedAppendableObjectStore"/> class.</summary>
    /// <param name="objects">The provider instance as <see cref="IObjectStore"/>.</param>
    /// <param name="appendable">The same provider instance as <see cref="IAppendableObjectStore"/>.</param>
    /// <param name="prefix">
    /// The prefix: one or more object-name components (<see cref="ObjectName"/>'s rule), each followed by <c>/</c>.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="objects"/> and <paramref name="appendable"/> are not the same instance, or <paramref name="prefix"/>
    /// is invalid.
    /// </exception>
    public PrefixedAppendableObjectStore(IObjectStore objects, IAppendableObjectStore appendable, string prefix)
    {
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(appendable);
        if (!ReferenceEquals(objects, appendable))
        {
            throw new ArgumentException("The IObjectStore and IAppendableObjectStore must be the same provider instance.", nameof(appendable));
        }

        _objects = new PrefixedObjectStore(objects, prefix);
        _appendable = appendable;
    }

    /// <summary>Creates a prefixed wrapper over one store implementing both object-store interfaces.</summary>
    public static PrefixedAppendableObjectStore Create<TStore>(TStore inner, string prefix)
        where TStore : IObjectStore, IAppendableObjectStore
    {
        ArgumentNullException.ThrowIfNull(inner);
        return new(inner, inner, prefix);
    }

    /// <inheritdoc/>
    public ValueTask<ObjectCreateOutcome> CreateAsync(ObjectName name, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default) =>
        _objects.CreateAsync(name, content, crc32c, cancellationToken);

    /// <inheritdoc/>
    public ValueTask<ArcBuffer?> ReadAsync(ObjectName name, long offset, int length, CancellationToken cancellationToken = default) =>
        _objects.ReadAsync(name, offset, length, cancellationToken);

    /// <inheritdoc/>
    public ValueTask<ObjectProperties?> GetPropertiesAsync(ObjectName name, CancellationToken cancellationToken = default) =>
        _objects.GetPropertiesAsync(name, cancellationToken);

    /// <inheritdoc/>
    public IAsyncEnumerable<ListedObject> ListAsync(string prefix, string? startAfter = null, CancellationToken cancellationToken = default) =>
        _objects.ListAsync(prefix, startAfter, cancellationToken);

    /// <inheritdoc/>
    public ValueTask DeleteAsync(ObjectName name, CancellationToken cancellationToken = default) =>
        _objects.DeleteAsync(name, cancellationToken);

    /// <inheritdoc/>
    public ValueTask<VersionedObject?> ReadVersionedAsync(ObjectName name, CancellationToken cancellationToken = default) =>
        _objects.ReadVersionedAsync(name, cancellationToken);

    /// <inheritdoc/>
    public ValueTask<ObjectReplaceOutcome> ReplaceAsync(ObjectName name, ObjectVersion? expectedVersion, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default) =>
        _objects.ReplaceAsync(name, expectedVersion, content, crc32c, cancellationToken);

    /// <inheritdoc/>
    public ValueTask<ObjectCreateOutcome> CreateAppendableAsync(ObjectName name, CancellationToken cancellationToken = default) =>
        _appendable.CreateAppendableAsync(Inner(name), cancellationToken);

    /// <inheritdoc/>
    public ValueTask<ObjectAppendOutcome> AppendAsync(ObjectName name, long expectedLength, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default) =>
        _appendable.AppendAsync(Inner(name), expectedLength, content, crc32c, cancellationToken);

    /// <inheritdoc/>
    public ValueTask<ObjectSealOutcome> SealAsync(ObjectName name, CancellationToken cancellationToken = default) =>
        _appendable.SealAsync(Inner(name), cancellationToken);

    /// <inheritdoc/>
    public ValueTask ReplaceWithTombstoneAsync(ObjectName name, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default) =>
        _appendable.ReplaceWithTombstoneAsync(Inner(name), content, crc32c, cancellationToken);

    private ObjectName Inner(ObjectName name) => _objects.Inner(name);
}
