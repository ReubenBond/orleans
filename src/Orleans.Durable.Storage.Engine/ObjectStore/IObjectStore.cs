using Orleans.Durable.Storage.Engine.Buffers;

namespace Orleans.Durable.Storage.Engine.ObjectStore;

/// <summary>
/// An object store (object-store.md §2.1): objects of up to 2³¹ − 1 bytes, possibly empty, named by
/// <see cref="ObjectName"/>; each created once, or written by conditional replaces of its version; read whole or by
/// range, by properties, and in listings in ordinal order of name; and deleted. A provider of appendable objects
/// implements <see cref="IAppendableObjectStore"/> over the same objects (object-store.md §2.3).
/// </summary>
/// <remarks>
/// <para>
/// Every answer reflects durable state, so no crash revokes it (conventions.md §13): <see cref="ObjectCreateOutcome.Created"/>,
/// <see cref="ObjectReplaceOutcome.Applied"/>, and a delete's completion are given only once the change is durable, and
/// <see cref="ObjectCreateOutcome.Exists"/>, <see cref="ObjectReplaceOutcome.Mismatch"/>, a read, and the names a listing
/// returns only from durable state. Read-after-write holds for creates, deletes, replaces, and listings which begin
/// after a write returned.
/// </para>
/// <para>
/// A service's client retries a request whose answer it lost, so <see cref="ObjectCreateOutcome.Exists"/> and
/// <see cref="ObjectReplaceOutcome.Mismatch"/> report the durable state a call observed, which may be its own earlier
/// attempt's: the object exists, or does not hold the expected version, possibly because this call created or replaced
/// it. A caller which acts on either reads the state it needs.
/// </para>
/// <para>
/// Objects are large payloads (conventions.md §1). A create or replace takes its content as an <see cref="ArcBuffer"/>,
/// which the caller keeps owning and disposes; a provider which reads the content after the call completes, as a write
/// whose outcome is unknown may, retains its own lease (<see cref="ArcBuffer.Retain"/>) and disposes it once its write
/// finishes. A read returns an <see cref="ArcBuffer"/>, which the caller owns and disposes. A provider over a service's
/// SDK converts at its own boundary to the type the SDK takes.
/// </para>
/// <para>
/// A writer which verified an object's bytes passes their CRC-32C with the create or replace, so the integrity check
/// covers the bytes from the writer's verification to where they are kept: S3 and Cloud Storage send it as the object's
/// checksum, which the service checks; Azure, whose <c>Put Blob</c> takes no CRC-32C, checks it over the bytes its SDK
/// reads and hashes; the file-system provider over the bytes it wrote, before it publishes them; and the memory provider
/// over its copy. Content which does not match it is refused in every build and on every provider, and the call writes
/// nothing: with <see cref="ObjectContentChangedException"/>, since the writer's buffer changed after verification, when
/// the provider's own check fails or a service reached over TLS refuses the bytes; a service's refusal over plain HTTP,
/// where the bytes may have changed in transit, is an <see cref="IOException"/>, an unknown outcome.
/// </para>
/// <para>
/// A version may be a digest of the content, as the file-system provider's SHA-256 and an S3 single-part ETag are, so
/// it names content, not a write: a replace whose expected version names content the object held before its current
/// content applies if the object holds that content again. A caller of <see cref="ReplaceAsync"/> therefore never writes
/// content which the object held before its current content.
/// </para>
/// <para>
/// <b>The callers' demand</b> (object-store.md §2.4). An object is one of three kinds by how its name is first written,
/// and a caller never writes one name two ways: created once by <see cref="CreateAsync"/>, it keeps its content until it
/// is deleted, and a name created again after its deletion holds the content it held, except in an object-store ledger
/// whose <c>dropped</c> marker exists, where a delayed SEAL may re-create a deleted batch's name empty and every read of
/// the name answers the ledger's drop (object-ledgers.md §1); written by
/// <see cref="ReplaceAsync"/>, it is read only with <see cref="ReadVersionedAsync"/>; created by
/// <see cref="IAppendableObjectStore.CreateAppendableAsync"/>, only <see cref="IAppendableObjectStore"/> writes it, its
/// tombstone included. A provider may rely on it, as the file-system provider does to answer a ranged
/// read or a properties read of an object it has already seen durable without making it durable again, so after another
/// process deletes and re-creates the name, such a read may answer before the re-creation is durable.
/// </para>
/// <para>
/// An exception from a create or a replace leaves its outcome unknown: the call lands at most once, possibly later,
/// and a replace only at its expected version. Cancellation stops the caller's wait and has the same effect.
/// </para>
/// </remarks>
public interface IObjectStore
{
    /// <summary>
    /// Creates <paramref name="name"/> with <paramref name="content"/>, never replacing an existing object.
    /// </summary>
    /// <param name="name">The object's name.</param>
    /// <param name="content">The content, possibly empty (<see langword="default"/>), which the caller keeps owning.</param>
    /// <param name="crc32c">The CRC-32C of <paramref name="content"/> its writer computed when it verified the bytes, or
    /// <see langword="null"/> when no writer verified them, so the provider checks with a checksum of its own.</param>
    /// <param name="cancellationToken">Stops the caller's wait; a create already sent may still land.</param>
    /// <returns>
    /// <see cref="ObjectCreateOutcome.Created"/> once the object is durable, a non-empty one with its CRC-32C recorded, or <see cref="ObjectCreateOutcome.Exists"/>
    /// when an object of the name durably exists, which may be this call's own earlier attempt. A provider which uploads
    /// sends an integrity checksum of <paramref name="content"/> computed before it leaves the process, so the service
    /// refuses bytes damaged on the way, and the call then fails having created nothing.
    /// </returns>
    /// <exception cref="ObjectContentChangedException">The content does not match <paramref name="crc32c"/>, by the
    /// provider's own check or a service's over TLS; the call
    /// created nothing.</exception>
    ValueTask<ObjectCreateOutcome> CreateAsync(ObjectName name, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads bytes <c>[offset, offset + length)</c> of an object.
    /// </summary>
    /// <param name="name">The object's name.</param>
    /// <param name="offset">The first byte's offset, at least 0.</param>
    /// <param name="length">The number of bytes, at least 1.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Exactly the requested bytes, which the caller owns, or <see langword="null"/> when the object is durably absent.</returns>
    /// <exception cref="EndOfStreamException">The object ends before <c>offset + length</c>.</exception>
    ValueTask<ArcBuffer?> ReadAsync(ObjectName name, long offset, int length, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads an object's properties with one metadata read: its length, and the CRC-32C its create recorded.
    /// </summary>
    /// <param name="name">The object's name.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The properties of the durable object, or <see langword="null"/> when the object is durably absent.</returns>
    ValueTask<ObjectProperties?> GetPropertiesAsync(ObjectName name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the objects whose names start with <paramref name="prefix"/> and lie after <paramref name="startAfter"/>, with
    /// their lengths, as a stream which a provider may fill page by page, as a service's listing pages do, rather than
    /// buffering the whole store at once (object-store.md §2.1).
    /// </summary>
    /// <param name="prefix">The prefix, which <see cref="ObjectName.IsValidPrefix"/> accepts; empty lists every object.</param>
    /// <param name="startAfter">
    /// A position in the ordinal order of names, which <see cref="ObjectName.IsValidPrefix"/> accepts, after which the
    /// listing starts, whether or not an object holds the name; or <see langword="null"/> from the prefix's first name.
    /// </param>
    /// <param name="cancellationToken">Stops the listing.</param>
    /// <returns>
    /// Every such object which durably existed from the call until the stream ended, with its length, in ordinal order of
    /// name, and possibly objects created or deleted meanwhile, which are hints.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="prefix"/> is not a listing prefix, or
    /// <paramref name="startAfter"/> not a listing start, at the call or once the stream is read.</exception>
    IAsyncEnumerable<ListedObject> ListAsync(string prefix, string? startAfter = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes an object; deleting an absent object succeeds.
    /// </summary>
    /// <param name="name">The object's name.</param>
    /// <param name="cancellationToken">Stops the caller's wait; a delete already sent may still land.</param>
    /// <returns>A task which completes once the object is durably absent.</returns>
    ValueTask DeleteAsync(ObjectName name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads an object's whole content and its version, linearizably.
    /// </summary>
    /// <param name="name">The object's name.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The content, which the caller owns, and its version; or <see langword="null"/> when the object is durably absent.</returns>
    ValueTask<VersionedObject?> ReadVersionedAsync(ObjectName name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces an object's content only when it holds <paramref name="expectedVersion"/>, or creates it only when it is
    /// absent and <paramref name="expectedVersion"/> is <see langword="null"/>.
    /// </summary>
    /// <param name="name">The object's name.</param>
    /// <param name="expectedVersion">A version read from, or applied to, this object; or <see langword="null"/> to create it.</param>
    /// <param name="content">The non-empty new content, which the caller keeps owning, and which the object has not held
    /// before its current content.</param>
    /// <param name="crc32c">The CRC-32C of <paramref name="content"/> its writer computed, or <see langword="null"/>, as
    /// for <see cref="CreateAsync"/>.</param>
    /// <param name="cancellationToken">Stops the caller's wait; a replace already sent may still land, only at <paramref name="expectedVersion"/>.</param>
    /// <returns>
    /// <see cref="ObjectReplaceOutcome.Applied"/> with the new version once the replace is durable, or
    /// <see cref="ObjectReplaceOutcome.Mismatch"/> when the object durably holds another version, or exists or is absent
    /// against the expectation, which may be this call's own earlier attempt.
    /// </returns>
    /// <exception cref="ObjectContentChangedException">The content does not match <paramref name="crc32c"/>, by the
    /// provider's own check or a service's over TLS; the call
    /// replaced nothing.</exception>
    ValueTask<ObjectReplaceOutcome> ReplaceAsync(
        ObjectName name,
        ObjectVersion? expectedVersion,
        ArcBuffer content,
        uint? crc32c,
        CancellationToken cancellationToken = default);
}
