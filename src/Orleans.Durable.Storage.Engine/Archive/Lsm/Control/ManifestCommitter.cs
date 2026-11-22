using System.Buffers;
using System.Diagnostics;
using Orleans.Durable.Storage.Engine.GroupCommit;
using Orleans.Durable.Storage.Engine.ObjectStore;

namespace Orleans.Durable.Storage.Engine.Archive.Lsm.Control;

/// <summary>
/// Replaces the manifest (archive §2.9): every drain and compaction goes through it, and it batches concurrent
/// submissions into one conditional replace. The group committer,
/// <see cref="GroupCommitter{TValue, TVersion, TChange, TOutcome, TReply}"/>, decides each batch under the manifest's
/// <see cref="CommitRules"/>, and its remarks state the policy every group committer keeps (conventions.md §6).
/// </summary>
/// <remarks>
/// <para>
/// The committer reads the manifest when it first needs it, through the process's <see cref="ManifestView"/>, then
/// decides each batch by folding it, in submission order, over the value it last read or wrote
/// (<see cref="ManifestCommitRules.Fold"/>). One conditional replace writes the result at the next epoch; the committer
/// answers every change of the batch from the value that replace produced once it lands, and gives that value to the
/// view. A replace whose outcome is unknown ends its batch with one read, which answers each change whose goal or failed
/// rule the value read decides, a change the replace carried only by its goal.
/// </para>
/// <para>
/// Each replace starts at least the configured interval after the previous one completed; changes submitted meanwhile
/// join its batch. The committer reads and replaces only the manifest, and reads no other object.
/// </para>
/// </remarks>
public sealed class ManifestCommitter : IAsyncDisposable
{
    private readonly CommitRules _rules;
    private readonly GroupCommitter<ManifestValue, ObjectVersion, ManifestChange, ManifestCommitOutcome, GroupCommitWrite<ObjectVersion>> _committer;

    /// <summary>
    /// Initializes a new instance of the <see cref="ManifestCommitter"/> class.
    /// </summary>
    /// <param name="store">The object store holding the manifest.</param>
    /// <param name="view">The process's manifest view, which the committer reads through and gives each value it writes.</param>
    /// <param name="timeProvider">Spaces the replaces.</param>
    /// <param name="interval">The least time from one replace's completion to the next replace's start, no shorter than
    /// the store's per-object write interval.</param>
    public ManifestCommitter(IObjectStore store, ManifestView view, TimeProvider timeProvider, TimeSpan interval)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentOutOfRangeException.ThrowIfLessThan(interval, TimeSpan.Zero);
        _rules = new(store, view);
        _committer = new(_rules, nameof(ManifestCommitter), timeProvider, interval);
    }

    /// <summary>
    /// Gets or sets a value indicating whether a drain of an incomplete ledger skips its watermark check when folding a
    /// batch, allowing a stale drain to lower the recorded watermark, as the model's <c>stale-drain</c> mutant does (LSM-I1).
    /// </summary>
    internal bool StaleDrainMutant
    {
        get => _rules.StaleDrainMutant;
        set => _rules.StaleDrainMutant = value;
    }

    /// <summary>
    /// Commits <paramref name="change"/>, which its worker derived from a manifest value it read (archive §3): a drain
    /// from the watermark that value records, and a compaction of sections that value lists.
    /// </summary>
    /// <param name="change">The change.</param>
    /// <param name="cancellationToken">Stops the caller's wait; a submitted change may still apply, and submitting it
    /// again decides it.</param>
    /// <returns>
    /// <see cref="ManifestCommitOutcome.Applied"/> when the manifest at its epoch, a value written or read after the
    /// change was submitted, holds the change's goal; <see cref="ManifestCommitOutcome.Superseded"/> when that value lacks
    /// the goal and fails the change's rule, so no attempt of it applies from then on.
    /// </returns>
    /// <exception cref="ObjectDisposedException">The committer was disposed before the change was decided.</exception>
    /// <exception cref="ArchiveCorruptException">The manifest's bytes break its format's rules, and no replace the change's
    /// batch dispatched carried it.</exception>
    /// <exception cref="InvalidOperationException">The fold failed, or a value the committer was about to write did not
    /// decode as itself: a defect, and no replace carried the change.</exception>
    /// <exception cref="Exception">The object store failed, or a replace which carried the change may land
    /// (<see cref="GroupCommitOutcomeUnknownException"/>): the change's outcome is unknown.</exception>
    public Task<ManifestCommitOutcome> CommitAsync(ManifestChange change, CancellationToken cancellationToken = default)
    {
        if (change.Value is null)
        {
            throw new ArgumentException("A change is a drain or a compaction.", nameof(change));
        }

        return _committer.CommitAsync(change, cancellationToken);
    }

    /// <inheritdoc cref="GroupCommitter{TValue, TVersion, TChange, TOutcome, TReply}.DisposeAsync"/>
    public ValueTask DisposeAsync() => _committer.DisposeAsync();

    /// <summary>The manifest's binding to the group committer: the manifest object, read through the view, and its commit rules.</summary>
    private sealed class CommitRules(IObjectStore store, ManifestView view)
        : IGroupCommitRules<ManifestValue, ObjectVersion, ManifestChange, ManifestCommitOutcome, GroupCommitWrite<ObjectVersion>>
    {
        public async ValueTask<GroupCommitRead<ManifestValue, ObjectVersion>> ReadAsync(CancellationToken cancellationToken)
        {
            ManifestSnapshot snapshot;
            try
            {
                snapshot = await view.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ArchiveCorruptException corrupt)
            {
                // The view reports the manifest's bytes breaking its format, the decoder's rejection inside.
                return new GroupCommitRead<ManifestValue, ObjectVersion>.Corrupt(corrupt);
            }

            return new GroupCommitRead<ManifestValue, ObjectVersion>.Found(snapshot.Value, snapshot.Version);
        }

        public async ValueTask<GroupCommitWrite<ObjectVersion>> WriteAsync(ObjectVersion? expectedVersion, GroupCommitEncoding encoded, CancellationToken cancellationToken)
        {
            var crc32c = encoded.Crc32C ?? throw new InvalidOperationException("A manifest's encoding carries its CRC-32C.");
            ObjectReplaceOutcome outcome;
            try
            {
                outcome = await store.ReplaceAsync(ObjectName.Manifest, expectedVersion, encoded.Content, crc32c, cancellationToken).ConfigureAwait(false);
            }
            catch (ObjectContentChangedException changed)
            {
                // The store wrote nothing: the encoding changed after its CRC-32C was taken, which stops the process (archive §9).
                ObjectContentChanges.Stop(changed);
                return new GroupCommitWrite<ObjectVersion>.Refused(changed);
            }

            return outcome switch
            {
                ObjectReplaceOutcome.Applied applied => new GroupCommitWrite<ObjectVersion>.Applied(applied.Version),
                ObjectReplaceOutcome.Mismatch => new GroupCommitWrite<ObjectVersion>.Mismatch(),
                null => throw new InvalidOperationException("conventions.md §15: a replace answers a union which holds a case."),
            };
        }

        public GroupCommitWrite<ObjectVersion> WriteOutcome(GroupCommitWrite<ObjectVersion> reply) => reply;

        public GroupCommitEncoding Encode(ManifestValue value)
        {
            // The round trip the committer checks runs over the finished content, after the encoder took its CRC-32C.
            using var encoded = ManifestFormat.Encode(value);
            return new GroupCommitEncoding(encoded.Content.Retain(), encoded.Crc32C);
        }

        public bool DecodesAs(ReadOnlySequence<byte> encoded, ManifestValue value) =>
            ManifestFormat.Decode(encoded).Value is ManifestValue decoded && decoded.Equals(value);

        public Exception OutcomeUnknown(string message, Exception cause) => new GroupCommitOutcomeUnknownException(message, cause);

        public bool StaleDrainMutant { get; set; }

        public IGroupCommitBatch<ManifestValue, ObjectVersion, ManifestCommitOutcome> Fold(ManifestValue basis, bool heldWriteMayLand, IReadOnlyList<ManifestChange> changes)
        {
            var (result, carried) = ManifestCommitRules.FoldCore(basis, changes, StaleDrainMutant);
            return new Batch(view, changes, carried, Array.IndexOf(carried, true) >= 0 ? result.WithEpoch(checked(basis.Epoch + 1)) : null);
        }
    }

    /// <summary>One batch's decision: whether its replace carries each change, and the value it holds.</summary>
    private sealed class Batch(ManifestView view, IReadOnlyList<ManifestChange> changes, bool[] carried, ManifestValue? written)
        : IGroupCommitBatch<ManifestValue, ObjectVersion, ManifestCommitOutcome>
    {
        public ManifestValue? Written => written;

        public bool Carries(int index) => carried[index];

        public GroupCommitAnswer<ManifestCommitOutcome> Settle(int index, ManifestValue value) => Answered(Verdict(changes[index], value));

        public GroupCommitAnswer<ManifestCommitOutcome>? Applied(int index, ManifestValue read) =>
            ManifestCommitRules.GoalHolds(read, changes[index]) ? Answered(new ManifestCommitOutcome.Applied(read.Epoch)) : Undecided;

        /// <summary>
        /// Answers a change the replace did not carry when <paramref name="read"/> holds its goal or fails its rule, which
        /// every later value fails too; leaves it undecided otherwise.
        /// </summary>
        public GroupCommitAnswer<ManifestCommitOutcome>? AfterUnknownOutcome(int index, ManifestValue read, bool heldWriteMayLand) =>
            ManifestCommitRules.GoalHolds(read, changes[index]) || !ManifestCommitRules.RuleHolds(read, changes[index]) ? Answered(Verdict(changes[index], read)) : Undecided;

        public string? Violation(ManifestValue before) => null;

        public void OnApplied(ObjectVersion version) => view.Observe(new ManifestSnapshot(written!, version));

        /// <summary>Gets the answer of a change a value leaves undecided, which fails with the replace's exception.</summary>
        private static GroupCommitAnswer<ManifestCommitOutcome>? Undecided => null;

        private static GroupCommitAnswer<ManifestCommitOutcome> Answered(ManifestCommitOutcome outcome) => new GroupCommitAnswer<ManifestCommitOutcome>.Answered(outcome);

        /// <summary>
        /// Returns the verdict <paramref name="value"/>, which holds <paramref name="change"/>'s goal or fails its rule for
        /// good, gives.
        /// </summary>
        private static ManifestCommitOutcome Verdict(ManifestChange change, ManifestValue value)
        {
            if (ManifestCommitRules.GoalHolds(value, change))
            {
                return new ManifestCommitOutcome.Applied(value.Epoch);
            }

            Debug.Assert(FailsForGood(change, value), "A Superseded change's rule fails against every later manifest value.");
            return new ManifestCommitOutcome.Superseded(value.Epoch);
        }

        /// <summary>
        /// Returns whether <paramref name="change"/>'s rule fails against <paramref name="value"/> and every later value: a
        /// drain's ledger lies below <c>DrainedBelow</c>, is complete, or its watermark passed the drain's start, and a
        /// compaction's input was removed (LSM-I1, LSM-I7). Every change derives from a value at or before the one it is
        /// decided against, so a failed rule is one of these.
        /// </summary>
        private static bool FailsForGood(ManifestChange change, ManifestValue value) =>
            ManifestCommitRules.ConditionIsFalse(value, ManifestCommitRules.ConditionOf(change));
    }
}
