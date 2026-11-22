using Orleans.Durable.Storage.Engine.Buffers;

namespace Orleans.Durable.Storage.Engine.ObjectStore;

/// <summary>
/// The appendable objects of an object store (object-store.md §2.2): each created empty and open, extended by appends each
/// conditioned on the object's length, sealed so that no append lands on it again, and replaced at most once by a
/// tombstone, a non-appendable object. A provider implements it together with <see cref="IObjectStore"/>, over the same
/// objects, which reads, lists, and deletes appendable objects as it does every other (object-store.md §2.3).
/// </summary>
/// <remarks>
/// <para>
/// An appendable object is the third kind of the callers' demand (object-store.md §3): its name is first written by
/// <see cref="CreateAppendableAsync"/>, and only this interface writes it, its tombstone included, until it is deleted.
/// A caller appends to one object at most 50,000 times, Azure's limit of blocks per append blob, and never past
/// 2³¹ − 1 bytes. A read of it reflects every append whose answer left before the read began. Its properties record no
/// CRC-32C while it is appendable; a tombstone records the CRC-32C of its content.
/// </para>
/// <para>
/// Every answer reflects durable state (conventions.md §13), and a call whose answer is lost, by an exception or by
/// cancellation, lands at most once, possibly later: an append only at its expected length and only while the object is
/// open. Exactly one append lands at each length. A service's client retries a request whose answer it lost, so
/// <see cref="ObjectCreateOutcome.Exists"/> and <see cref="ObjectAppendOutcome.LengthMismatch"/> report the durable state a
/// call observed, which may be its own earlier attempt's. <see cref="ObjectAppendOutcome.Sealed"/> is definite: an
/// attempt which landed before a seal answers <see cref="ObjectAppendOutcome.Appended"/>, since the object holds the call's
/// bytes at its expected length.
/// </para>
/// <para>
/// Content is a large payload (conventions.md §1), passed as an <see cref="ArcBuffer"/> the caller keeps owning, with the
/// CRC-32C its writer verified, as <see cref="IObjectStore.CreateAsync"/> takes it; content which does not match it is
/// refused with <see cref="ObjectContentChangedException"/>, and the call writes nothing.
/// </para>
/// </remarks>
public interface IAppendableObjectStore
{
    /// <summary>
    /// The most bytes one append carries: 100 MiB, Azure's largest <c>Append Block</c> from service version 2022-11-02.
    /// </summary>
    const int MaxAppendLength = 100 * 1024 * 1024;

    /// <summary>
    /// Creates <paramref name="name"/> as an empty, open, appendable object, only if no object of the name exists.
    /// </summary>
    /// <param name="name">The object's name.</param>
    /// <param name="cancellationToken">Stops the caller's wait; a create already sent may still land.</param>
    /// <returns>
    /// <see cref="ObjectCreateOutcome.Created"/> once the object is durable, or <see cref="ObjectCreateOutcome.Exists"/>
    /// when an object of the name durably exists, of any kind, which may be this call's own earlier attempt.
    /// </returns>
    ValueTask<ObjectCreateOutcome> CreateAppendableAsync(ObjectName name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends <paramref name="content"/> to <paramref name="name"/> only when it is open and holds exactly
    /// <paramref name="expectedLength"/> bytes.
    /// </summary>
    /// <param name="name">The object's name.</param>
    /// <param name="expectedLength">The object's length the append lands at, at least 0.</param>
    /// <param name="content">The bytes, from 1 to <see cref="MaxAppendLength"/>, which the caller keeps owning, ending at
    /// most at 2³¹ − 1 bytes.</param>
    /// <param name="crc32c">The CRC-32C of <paramref name="content"/> its writer computed when it verified the bytes, or
    /// <see langword="null"/>, as for <see cref="IObjectStore.CreateAsync"/>.</param>
    /// <param name="cancellationToken">Stops the caller's wait; an append already sent may still land, only at
    /// <paramref name="expectedLength"/> and only while the object is open.</param>
    /// <returns>
    /// <see cref="ObjectAppendOutcome.Appended"/> once the bytes lie durably at <paramref name="expectedLength"/>, this
    /// attempt's or an earlier one's which landed before a seal; <see cref="ObjectAppendOutcome.LengthMismatch"/> with
    /// the object's durable length when it is open at another length, which may be this call's own earlier attempt's
    /// doing; <see cref="ObjectAppendOutcome.Sealed"/> when it is durably sealed or not appendable and does not hold the
    /// bytes there; or <see cref="ObjectAppendOutcome.Absent"/>.
    /// </returns>
    /// <exception cref="ObjectContentChangedException">The content does not match <paramref name="crc32c"/>; the call
    /// appended nothing.</exception>
    ValueTask<ObjectAppendOutcome> AppendAsync(ObjectName name, long expectedLength, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default);

    /// <summary>
    /// Seals <paramref name="name"/>, so that every append which lands afterwards answers
    /// <see cref="ObjectAppendOutcome.Sealed"/>, whatever its expected length. Sealing a sealed object changes nothing, and
    /// so does sealing a non-appendable one.
    /// </summary>
    /// <param name="name">The object's name.</param>
    /// <param name="cancellationToken">Stops the caller's wait; a seal already sent may still land.</param>
    /// <returns><see cref="ObjectSealOutcome.Sealed"/> once the appendable object is durably sealed,
    /// <see cref="ObjectSealOutcome.NotAppendable"/> when it is not appendable, as a tombstone is, or
    /// <see cref="ObjectSealOutcome.Absent"/>.</returns>
    ValueTask<ObjectSealOutcome> SealAsync(ObjectName name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically replaces <paramref name="name"/>, whatever it holds, with a non-appendable object holding
    /// <paramref name="content"/>, on which every append answers <see cref="ObjectAppendOutcome.Sealed"/>; it creates the
    /// object when it is absent.
    /// </summary>
    /// <param name="name">The object's name.</param>
    /// <param name="content">The tombstone's content, possibly empty, which the caller keeps owning; every replace of one
    /// name carries the same content.</param>
    /// <param name="crc32c">The CRC-32C of <paramref name="content"/> its writer computed, or <see langword="null"/>, as
    /// for <see cref="IObjectStore.CreateAsync"/>.</param>
    /// <param name="cancellationToken">Stops the caller's wait; a replace already sent may still land.</param>
    /// <returns>A task which completes once the tombstone is durable.</returns>
    /// <exception cref="ObjectContentChangedException">The content does not match <paramref name="crc32c"/>; the call
    /// replaced nothing.</exception>
    ValueTask ReplaceWithTombstoneAsync(ObjectName name, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default);
}
