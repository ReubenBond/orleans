using System.Globalization;
using Orleans.Durable.Storage.Engine.Archive.Lsm;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.ObjectStore;

namespace Orleans.Durable.Storage.Testing;

/// <summary>
/// Runs <see cref="ObjectStoreSpec"/> requests against an <see cref="IObjectStore"/>, and its appendable calls against the
/// <see cref="IAppendableObjectStore"/> over the same objects, when the case gives one.
/// </summary>
/// <remarks>
/// <para>
/// A replace names the version of the value its request expects. The executor learns each value's version from the
/// versioned reads which return it and from the replaces which apply it, and requires every later answer to give the
/// same version, since a caller writes each value to a name at most once. A replace which names a value whose version
/// the executor has not learned reads the object first: when the object holds the value, the read gives its version;
/// otherwise the executor names a version no write issued, which the store answers with a mismatch. A read changes
/// nothing, so the spec judges the replace alone.
/// </para>
/// <para>
/// A call which throws propagates its exception: its caller decides whether the call's outcome is unknown. The executor
/// disposes every buffer the store returns.
/// </para>
/// </remarks>
internal sealed class ObjectStoreExecutor
{
    private readonly IObjectStore _store;
    private readonly IAppendableObjectStore? _appendable;
    private readonly Dictionary<(int Name, byte Value), ObjectVersion> _versions;

    /// <summary>
    /// Initializes a new instance of the <see cref="ObjectStoreExecutor"/> class which knows no version yet.
    /// </summary>
    internal ObjectStoreExecutor(IObjectStore store, IAppendableObjectStore? appendable = null)
        : this(store, appendable, [])
    {
    }

    private ObjectStoreExecutor(IObjectStore store, IAppendableObjectStore? appendable, Dictionary<(int Name, byte Value), ObjectVersion> versions)
    {
        _store = store;
        _appendable = appendable;
        _versions = versions;
    }

    /// <summary>
    /// Returns an executor over <paramref name="store"/> and <paramref name="appendable"/>, other instances over the same
    /// objects, which knows every version this one learned and shares what it learns.
    /// </summary>
    internal ObjectStoreExecutor Over(IObjectStore store, IAppendableObjectStore? appendable = null) => new(store, appendable, _versions);

    private IAppendableObjectStore Appendable =>
        _appendable ?? throw new InvalidOperationException("The case gave the executor no appendable store.");

    /// <summary>
    /// Runs <paramref name="request"/>.
    /// </summary>
    internal Task<ObjectOutcome> RunAsync(object request, CancellationToken cancellationToken) => request switch
    {
        ObjectCreate create => CreateAsync(create, cancellationToken),
        ObjectRead read => ReadAsync(read, cancellationToken),
        ObjectReadVersioned read => ReadVersionedAsync(read, cancellationToken),
        ObjectReplace replace => ReplaceAsync(replace, cancellationToken),
        ObjectDelete delete => DeleteAsync(delete, cancellationToken),
        ObjectList list => ListAsync(list, cancellationToken),
        ObjectGetProperties properties => GetPropertiesAsync(properties, cancellationToken),
        ObjectCreateAppendable create => CreateAppendableAsync(create, cancellationToken),
        ObjectAppend append => AppendAsync(append, cancellationToken),
        ObjectSeal seal => SealAsync(seal, cancellationToken),
        ObjectTombstone tombstone => TombstoneAsync(tombstone, cancellationToken),
        _ => throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"{request} is not an object-store request."), nameof(request)),
    };

    /// <summary>
    /// Creates the request's name with its value's content.
    /// </summary>
    internal async Task<ObjectOutcome> CreateAsync(ObjectCreate create, CancellationToken cancellationToken)
    {
        using var content = ArcBuffer.Wrap(ObjectStoreSpec.Payload(create.Value));

        // A provider under test may return the default union, which names no case: its bug, so the null arm is reachable.
        return await _store.CreateAsync(NameOf(create.Name), content, null, cancellationToken) switch
        {
            ObjectCreateOutcome.Created => new ObjectOutcome(ObjectOutcomeKind.Created),
            ObjectCreateOutcome.Exists => new ObjectOutcome(ObjectOutcomeKind.Exists),
            null => throw new InvalidOperationException("A create answered no outcome."),
        };
    }

    /// <summary>
    /// Reads the request's range and names the value whose content the bytes are.
    /// </summary>
    /// <exception cref="InvalidDataException">The store returned bytes no write stores at the range.</exception>
    internal async Task<ObjectOutcome> ReadAsync(ObjectRead read, CancellationToken cancellationToken)
    {
        ArcBuffer? buffer;
        try
        {
            buffer = await _store.ReadAsync(NameOf(read.Name), read.Offset, read.Length, cancellationToken);
        }
        catch (EndOfStreamException)
        {
            return new ObjectOutcome(ObjectOutcomeKind.PastEnd);
        }

        if (buffer is not { } bytes)
        {
            return new ObjectOutcome(ObjectOutcomeKind.Absent);
        }

        using (bytes)
        {
            var copy = bytes.ToArray();
            if (ObjectStoreSpec.IsAppendable(read.Name) && copy.Length == read.Length)
            {
                return new ObjectOutcome(ObjectOutcomeKind.Present, Content: Convert.ToHexString(copy));
            }

            if (copy.Length != read.Length || copy.AsSpan().ContainsAnyExcept(copy[0]))
            {
                throw new InvalidDataException(string.Create(
                    CultureInfo.InvariantCulture, $"{read} returned {copy.Length} bytes, which no write stores at the range."));
            }

            return new ObjectOutcome(ObjectOutcomeKind.Present, copy[0]);
        }
    }

    /// <summary>
    /// Reads the object whole, names the value whose content it holds, and learns that value's version.
    /// </summary>
    /// <exception cref="InvalidDataException">The store holds bytes no write stores, or a value at another version than
    /// an earlier answer gave.</exception>
    internal async Task<ObjectOutcome> ReadVersionedAsync(ObjectReadVersioned read, CancellationToken cancellationToken)
    {
        if (await _store.ReadVersionedAsync(NameOf(read.Name), cancellationToken) is not { } found)
        {
            return new ObjectOutcome(ObjectOutcomeKind.Absent);
        }

        using var content = found.Content;
        var bytes = content.ToArray();
        var value = bytes.Length == 0 ? (byte)0 : bytes[0];
        if (!bytes.AsSpan().SequenceEqual(ObjectStoreSpec.Payload(value)))
        {
            throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture, $"{read} returned {bytes.Length} bytes, which no write stores."));
        }

        Learn(read.Name, value, found.Version);
        return new ObjectOutcome(ObjectOutcomeKind.Present, value);
    }

    /// <summary>
    /// Replaces the object with the request's value's content at the version of the value it expects, and learns the
    /// new value's version when the replace applies.
    /// </summary>
    internal async Task<ObjectOutcome> ReplaceAsync(ObjectReplace replace, CancellationToken cancellationToken)
    {
        var expected = await ExpectedAsync(replace, cancellationToken);
        using var content = ArcBuffer.Wrap(ObjectStoreSpec.Payload(replace.Value));

        // A provider under test may return the default union, which names no case: its bug, so the null arm is reachable.
        return await _store.ReplaceAsync(NameOf(replace.Name), expected, content, null, cancellationToken) switch
        {
            ObjectReplaceOutcome.Applied applied => Applied(replace, applied.Version),
            ObjectReplaceOutcome.Mismatch => new ObjectOutcome(ObjectOutcomeKind.Mismatch),
            null => throw new InvalidOperationException("A replace answered no outcome."),
        };
    }

    /// <summary>
    /// Deletes the request's name.
    /// </summary>
    internal async Task<ObjectOutcome> DeleteAsync(ObjectDelete delete, CancellationToken cancellationToken)
    {
        await _store.DeleteAsync(NameOf(delete.Name), cancellationToken);
        return new ObjectOutcome(ObjectOutcomeKind.Deleted);
    }

    /// <summary>
    /// Lists the request's prefix and names the listed objects.
    /// </summary>
    /// <exception cref="InvalidDataException">The listing returned a name no request addresses, an unordered list, or a
    /// length no value's content has.</exception>
    internal async Task<ObjectOutcome> ListAsync(ObjectList list, CancellationToken cancellationToken)
    {
        var objects = await _store.ListAsync(ObjectStoreSpec.Prefixes[list.Name], ObjectStoreSpec.Starts[list.Start], cancellationToken).ToListAsync(cancellationToken);
        var names = objects.Select(static listed => listed.Name).ToList();
        if (!names.SequenceEqual(names.Order()))
        {
            throw new InvalidDataException("A listing returned names out of ordinal order.");
        }

        var listed = 0;
        foreach (var (name, length) in objects)
        {
            var index = ObjectStoreSpec.IndexOfName(name.Value);
            if (index < 0)
            {
                throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture, $"A listing returned '{name}', which no write created."));
            }

            // The spec does not track which value a listed object holds, which concurrent writes leave open, so the length
            // is judged only as the length of some value's content.
            if (!ContentLengths.Contains(length))
            {
                throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture, $"A listing gave '{name}' a length of {length}, which no value's content has."));
            }

            listed |= 1 << index;
        }

        return new ObjectOutcome(ObjectOutcomeKind.Listed, Listed: listed);
    }

    /// <summary>
    /// Reads the request's object's properties.
    /// </summary>
    internal async Task<ObjectOutcome> GetPropertiesAsync(ObjectGetProperties properties, CancellationToken cancellationToken) =>
        await _store.GetPropertiesAsync(NameOf(properties.Name), cancellationToken) is { } found
            ? new ObjectOutcome(ObjectOutcomeKind.Properties, Length: found.Length, Crc32C: found.Crc32c)
            : new ObjectOutcome(ObjectOutcomeKind.Absent);

    /// <summary>
    /// Creates the request's name as an empty, open, appendable object.
    /// </summary>
    internal async Task<ObjectOutcome> CreateAppendableAsync(ObjectCreateAppendable create, CancellationToken cancellationToken) =>
        await Appendable.CreateAppendableAsync(NameOf(create.Name), cancellationToken) switch
        {
            ObjectCreateOutcome.Created => new ObjectOutcome(ObjectOutcomeKind.Created),
            ObjectCreateOutcome.Exists => new ObjectOutcome(ObjectOutcomeKind.Exists),
            null => throw new InvalidOperationException("An appendable create answered no outcome."),
        };

    /// <summary>
    /// Appends the request's chunk at its expected length.
    /// </summary>
    internal async Task<ObjectOutcome> AppendAsync(ObjectAppend append, CancellationToken cancellationToken)
    {
        using var content = ArcBuffer.Wrap(ObjectStoreSpec.Chunk(append.Value));

        // A provider under test may return the default union, which names no case: its bug, so the null arm is reachable.
        return await Appendable.AppendAsync(NameOf(append.Name), (long)append.Appends * ObjectStoreSpec.ChunkLength, content, null, cancellationToken) switch
        {
            ObjectAppendOutcome.Appended appended => new ObjectOutcome(ObjectOutcomeKind.Appended, Length: appended.Length),
            ObjectAppendOutcome.LengthMismatch mismatch => new ObjectOutcome(ObjectOutcomeKind.LengthMismatch, Length: mismatch.Length),
            ObjectAppendOutcome.Sealed => new ObjectOutcome(ObjectOutcomeKind.Sealed),
            ObjectAppendOutcome.Absent => new ObjectOutcome(ObjectOutcomeKind.Absent),
            null => throw new InvalidOperationException("An append answered no outcome."),
        };
    }

    /// <summary>
    /// Seals the request's name.
    /// </summary>
    internal async Task<ObjectOutcome> SealAsync(ObjectSeal seal, CancellationToken cancellationToken) =>
        await Appendable.SealAsync(NameOf(seal.Name), cancellationToken) switch
        {
            ObjectSealOutcome.Sealed => new ObjectOutcome(ObjectOutcomeKind.Sealed),
            ObjectSealOutcome.NotAppendable => new ObjectOutcome(ObjectOutcomeKind.NotAppendable),
            ObjectSealOutcome.Absent => new ObjectOutcome(ObjectOutcomeKind.Absent),
            null => throw new InvalidOperationException("A seal answered no outcome."),
        };

    /// <summary>
    /// Replaces the request's name with its tombstone.
    /// </summary>
    internal async Task<ObjectOutcome> TombstoneAsync(ObjectTombstone tombstone, CancellationToken cancellationToken)
    {
        using var content = ArcBuffer.Wrap(ObjectStoreSpec.Payload(ObjectStoreSpec.TombstoneValue));
        await Appendable.ReplaceWithTombstoneAsync(NameOf(tombstone.Name), content, null, cancellationToken);
        return new ObjectOutcome(ObjectOutcomeKind.Tombstoned);
    }

    /// <summary>
    /// The length of each value's content, as <see cref="ObjectStoreSpec.Payload"/> stores it, and of each appendable
    /// object of up to 64 appends.
    /// </summary>
    private static readonly HashSet<long> ContentLengths =
    [
        .. Enumerable.Range(0, 256).Select(static value => (long)ObjectStoreSpec.Payload((byte)value).Length),
        .. Enumerable.Range(0, 65).Select(static appends => (long)appends * ObjectStoreSpec.ChunkLength),
    ];

    private static ObjectName NameOf(int name) => new(ObjectStoreSpec.Names[name]);

    private ObjectOutcome Applied(ObjectReplace replace, ObjectVersion version)
    {
        Learn(replace.Name, replace.Value, version);
        return new ObjectOutcome(ObjectOutcomeKind.Applied);
    }

    private void Learn(int name, byte value, ObjectVersion version)
    {
        lock (_versions)
        {
            if (_versions.TryGetValue((name, value), out var known) && known != version)
            {
                throw new InvalidDataException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{ObjectStoreSpec.Names[name]} holds value {value} at version {version}, which an earlier answer gave as {known}."));
            }

            _versions[(name, value)] = version;
        }
    }

    private async Task<ObjectVersion?> ExpectedAsync(ObjectReplace replace, CancellationToken cancellationToken)
    {
        if (replace.Expected is not { } value)
        {
            return null;
        }

        lock (_versions)
        {
            if (_versions.TryGetValue((replace.Name, value), out var known))
            {
                return known;
            }
        }

        _ = await ReadVersionedAsync(new ObjectReadVersioned(replace.Name), cancellationToken);
        lock (_versions)
        {
            return _versions.TryGetValue((replace.Name, value), out var learned) ? learned : new ObjectVersion("unissued-" + Guid.NewGuid().ToString("N"));
        }
    }
}
