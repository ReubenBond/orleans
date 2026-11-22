using System.Runtime.CompilerServices;
using Orleans.Durable.Storage.Engine.Buffers;

namespace Orleans.Durable.Storage.Engine.ObjectStore;

/// <summary>
/// An object store under a prefix of another (orleans.md §5.13): the object <c>name</c> is the inner store's
/// <c>{prefix}{name}</c>, and a listing lists only the objects under the prefix, naming them without it, so a store
/// placed under a cluster's prefix sees only its own objects in a bucket or container several clusters share. It makes no
/// request of its own.
/// </summary>
public sealed class PrefixedObjectStore : IObjectStore
{
    private readonly IObjectStore _inner;
    private readonly string _prefix;

    /// <summary>Initializes a new instance of the <see cref="PrefixedObjectStore"/> class.</summary>
    /// <param name="inner">The store the objects lie in.</param>
    /// <param name="prefix">
    /// The prefix: one or more object-name components (<see cref="ObjectName"/>'s rule), each followed by <c>/</c>.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="prefix"/> is not components each followed by <c>/</c>.</exception>
    public PrefixedObjectStore(IObjectStore inner, string prefix)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(prefix);
        if (prefix.Length < 2 || prefix[^1] != '/' || !ObjectName.IsValid(prefix[..^1]))
        {
            throw new ArgumentException("A prefix is one or more object-name components, each followed by '/'.", nameof(prefix));
        }

        _inner = inner;
        _prefix = prefix;
    }

    /// <inheritdoc/>
    public ValueTask<ObjectCreateOutcome> CreateAsync(ObjectName name, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default) =>
        _inner.CreateAsync(Inner(name), content, crc32c, cancellationToken);

    /// <inheritdoc/>
    public ValueTask<ArcBuffer?> ReadAsync(ObjectName name, long offset, int length, CancellationToken cancellationToken = default) =>
        _inner.ReadAsync(Inner(name), offset, length, cancellationToken);

    /// <inheritdoc/>
    public ValueTask<ObjectProperties?> GetPropertiesAsync(ObjectName name, CancellationToken cancellationToken = default) =>
        _inner.GetPropertiesAsync(Inner(name), cancellationToken);

    /// <inheritdoc/>
    public IAsyncEnumerable<ListedObject> ListAsync(string prefix, string? startAfter = null, CancellationToken cancellationToken = default)
    {
        ObjectStoreArguments.ThrowIfNotPrefix(prefix);
        ObjectStoreArguments.ThrowIfNotStart(startAfter);
        return ListUnderAsync(prefix, startAfter, cancellationToken);
    }

    /// <inheritdoc/>
    public ValueTask DeleteAsync(ObjectName name, CancellationToken cancellationToken = default) =>
        _inner.DeleteAsync(Inner(name), cancellationToken);

    /// <inheritdoc/>
    public ValueTask<VersionedObject?> ReadVersionedAsync(ObjectName name, CancellationToken cancellationToken = default) =>
        _inner.ReadVersionedAsync(Inner(name), cancellationToken);

    /// <inheritdoc/>
    public ValueTask<ObjectReplaceOutcome> ReplaceAsync(ObjectName name, ObjectVersion? expectedVersion, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default) =>
        _inner.ReplaceAsync(Inner(name), expectedVersion, content, crc32c, cancellationToken);

    /// <summary>Returns the inner store's name of <paramref name="name"/>: <c>{prefix}{name}</c>.</summary>
    internal ObjectName Inner(ObjectName name) => ObjectName.WithValidatedPrefix(_prefix, name);

    /// <summary>
    /// Lists the inner store under the prefix from the inner start, cut to an object name's longest, which no inner name
    /// passes; each name is checked against the start itself, which the cut may precede.
    /// </summary>
    private async IAsyncEnumerable<ListedObject> ListUnderAsync(string prefix, string? startAfter, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var innerStart = startAfter is null ? null : (_prefix + startAfter)[..Math.Min(_prefix.Length + startAfter.Length, ObjectName.MaxLength)];
        await foreach (var listed in _inner.ListAsync(_prefix + prefix, innerStart, cancellationToken).ConfigureAwait(false))
        {
            var name = listed.Name.Value[_prefix.Length..];
            if (ObjectStoreArguments.IsAfter(name, startAfter))
            {
                yield return listed with { Name = new ObjectName(name) };
            }
        }
    }
}
