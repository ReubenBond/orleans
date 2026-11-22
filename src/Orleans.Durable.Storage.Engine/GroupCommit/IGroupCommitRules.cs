using System.Buffers;

namespace Orleans.Durable.Storage.Engine.GroupCommit;

/// <summary>
/// One group-committed value's binding to the <see cref="GroupCommitter{TValue, TVersion, TChange, TOutcome, TReply}"/>
/// (conventions.md §6): the store's read and conditional write of the value, its codec, and the fold of a batch of changes,
/// which decides the batch.
/// </summary>
/// <typeparam name="TValue">The decoded value: the ledger map, the node registry, or the manifest.</typeparam>
/// <typeparam name="TVersion">The store's version of the value; <see langword="null"/> while the value is absent.</typeparam>
/// <typeparam name="TChange">A submitted change; equal changes are merged while they are pending.</typeparam>
/// <typeparam name="TOutcome">A change's answer.</typeparam>
/// <typeparam name="TReply">The store's answer to a conditional write.</typeparam>
/// <remarks>
/// <para>
/// <see cref="ReadAsync"/> and <see cref="WriteAsync"/> are the committer's only I/O, which it calls one at a time. Every
/// other member is pure: an exception from one is a defect of the rules. A defect before the batch's write is dispatched
/// fails each change no write of the batch carried with <see cref="InvalidOperationException"/>, nothing written for it; a
/// change a dispatched write carried fails with <see cref="OutcomeUnknown"/>'s exception, since that write may land.
/// </para>
/// <para>
/// The answers rest on a caller precondition of every value the committer serves: each change derives from a value read,
/// or written by a write which landed, in which its rule holds; and a change's rule, once failed, fails against every
/// later value, since the value only advances past the values its changes derive from.
/// </para>
/// </remarks>
public interface IGroupCommitRules<TValue, TVersion, TChange, TOutcome, TReply>
    where TValue : class
    where TVersion : class
    where TChange : notnull
{
    /// <summary>Reads the value, linearizably after the call began.</summary>
    /// <param name="cancellationToken">Cancelled once the committer is disposed.</param>
    /// <returns>The value and its version, the empty value while it is absent; or its corruption, decided from the codec's
    /// rejection of the stored bytes.</returns>
    /// <exception cref="Exception">The read failed: the store is unavailable.</exception>
    ValueTask<GroupCommitRead<TValue, TVersion>> ReadAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Writes <paramref name="encoded"/> only while the store holds <paramref name="expectedVersion"/>, or holds no value
    /// when it is <see langword="null"/>.
    /// </summary>
    /// <param name="expectedVersion">The version of the value the write was decided against.</param>
    /// <param name="encoded">The value's encoding from <see cref="Encode"/>, whose bytes the committer owns and keeps until
    /// the write's task completes.</param>
    /// <param name="cancellationToken">Cancelled once the committer is disposed.</param>
    /// <returns>The store's answer, which <see cref="WriteOutcome"/> reads.</returns>
    /// <exception cref="Exception">The write's outcome is unknown: it lands at most once, possibly later, and only over
    /// <paramref name="expectedVersion"/> (MS-I3, metastore.md §5).</exception>
    ValueTask<TReply> WriteAsync(TVersion? expectedVersion, GroupCommitEncoding encoded, CancellationToken cancellationToken);

    /// <summary>
    /// Returns what the store's <paramref name="reply"/> to a write asserts: applied at a version, a mismatch, or a refusal
    /// which wrote nothing.
    /// </summary>
    GroupCommitWrite<TVersion> WriteOutcome(TReply reply);

    /// <summary>
    /// Encodes <paramref name="value"/> for the write which holds it: the finished bytes the write sends, any checksum the
    /// store verifies taken over them.
    /// </summary>
    /// <returns>The encoding, whose bytes the caller owns.</returns>
    GroupCommitEncoding Encode(TValue value);

    /// <summary>
    /// Returns whether <paramref name="encoded"/> decodes, as every reader decodes it, to <paramref name="value"/>. The
    /// committer checks the encoding <see cref="Encode"/> returned, after any checksum was taken and before the write, and
    /// writes nothing which does not decode: a change before the checksum fails this check, and one after it fails at the
    /// store, so no reader meets corruption the committer wrote.
    /// </summary>
    bool DecodesAs(ReadOnlySequence<byte> encoded, TValue value);

    /// <summary>
    /// Returns the exception which answers a change whose outcome is unknown, as its submitters recognise one, around
    /// <paramref name="cause"/>.
    /// </summary>
    Exception OutcomeUnknown(string message, Exception cause);

    /// <summary>
    /// Folds <paramref name="changes"/>, in submission order, over <paramref name="basis"/>: each is decided against the
    /// result of those before it.
    /// </summary>
    /// <param name="basis">The value the committer last read or wrote.</param>
    /// <param name="heldWriteMayLand">Whether a write of this committer whose outcome is unknown, conditioned on
    /// <paramref name="basis"/>'s version, may still land over it. Rules whose negative answers such a write could revoke
    /// give none while it may land: the batch writes <paramref name="basis"/> again at the next version, a fence after
    /// which the held write never lands, and the committer decides the batch again against the fence. A batch may write
    /// without carrying any change only so, while a held write may land.</param>
    /// <param name="changes">The batch's changes, each submitted once.</param>
    /// <returns>The batch's decision.</returns>
    IGroupCommitBatch<TValue, TVersion, TOutcome> Fold(TValue basis, bool heldWriteMayLand, IReadOnlyList<TChange> changes);
}
