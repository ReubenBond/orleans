using System.Buffers;

namespace Orleans.Durable.Storage.Engine.MetaStore;

/// <summary>
/// One versioned register replaced only by a write which names the version it expects.
/// </summary>
/// <remarks>
/// "The MetaStore component has a simple API: it is a single versioned register supporting a conditional write. Reading
/// the MetaStore returns a value with an attached version. Writing to it requires supplying a new value and an expected
/// existing version." See <c>research/papers/delos/paper.md</c>, section 3.2.
/// <para>
/// A write reports only what the backend established. <see cref="WriteAsync"/> answers
/// <see cref="RegisterWriteOutcome.Applied"/> with the register's new version when the write applied, and
/// <see cref="RegisterWriteOutcome.Mismatch"/> when the backend reported that the register's version did not match, or
/// when the expected version is a token this backend cannot have issued. It throws when the outcome is unknown; such a
/// write applies at most once, possibly later, and only at the version it named (MS-I3, metastore.md §5). After an applied
/// write, a caller which writes again names the version it returned. After a mismatch or an exception, a caller re-reads
/// the register and tests whether its goal holds, since a mismatch reported after a backend-SDK retry may follow an
/// earlier attempt of the same write which applied; after a mismatch the write can never land, and after an exception it
/// may (metastore.md §2.2). A mismatch is reported only while the caller's token is not cancelled.
/// </para>
/// </remarks>
public interface IConditionalRegister
{
    /// <summary>
    /// Reads the current value and its version, linearizably.
    /// </summary>
    /// <returns>
    /// <see langword="null"/> when the register does not exist; otherwise its value and version, which the caller owns
    /// and disposes. A present empty value reads as an empty value, never as <see langword="null"/> (MS-I2).
    /// </returns>
    /// <exception cref="InvalidDataException">The backend holds a malformed entry.</exception>
    ValueTask<MetaStoreEntry?> ReadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes <paramref name="value"/> only when the register's version is <paramref name="expectedVersion"/>, or
    /// the register does not exist when it is <see langword="null"/>.
    /// </summary>
    /// <param name="expectedVersion">
    /// <see langword="null"/> to create an absent register, or a version read from this register or returned by a write
    /// which applied to it.
    /// </param>
    /// <param name="value">
    /// The value, which the write reads only before its task completes: it is copied before the request is dispatched.
    /// </param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>
    /// <see cref="RegisterWriteOutcome.Applied"/> with the register's version after the write when it applied;
    /// <see cref="RegisterWriteOutcome.Mismatch"/> when the backend reported a version mismatch or
    /// <paramref name="expectedVersion"/> is a token this backend cannot have issued.
    /// </returns>
    ValueTask<RegisterWriteOutcome> WriteAsync(
        MetaStoreVersion? expectedVersion,
        ReadOnlySequence<byte> value,
        CancellationToken cancellationToken = default);
}
