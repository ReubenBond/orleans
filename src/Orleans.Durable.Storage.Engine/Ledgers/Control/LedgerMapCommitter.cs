using System.Buffers;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using Orleans.Durable.Storage.Engine.GroupCommit;
using Orleans.Durable.Storage.Engine.MetaStore;

namespace Orleans.Durable.Storage.Engine.Ledgers.Control;

/// <summary>
/// Writes the <c>ledger-map</c> register: every map change goes through a committer, which batches concurrent
/// submissions, merges identical ones, folds each batch in submission order, and writes the result with one conditional
/// write (ledgers.md §2.6). The group committer, <see cref="GroupCommitter{TValue, TVersion, TChange, TOutcome, TReply}"/>,
/// decides each batch under the map's <see cref="CommitRules"/>, and its remarks state the policy every group committer
/// keeps (conventions.md §6).
/// </summary>
/// <remarks>
/// <para>
/// After a write whose outcome is unknown, the read which ends its batch answers each change it shows applied
/// <see cref="LedgerMapCommitOutcome.Applied"/>, each other change whose rule it shows failed
/// <see cref="LedgerMapCommitOutcome.Superseded"/>, and a refusal or a defect <see cref="LedgerMapCommitOutcome.Refused"/>
/// or <see cref="LedgerMapCommitOutcome.Rejected"/> only once no write the committer dispatched can still land. The
/// negative answers rest on the caller precondition that each change derives from a map value, read or written by a write
/// which landed, in which its rule holds (ledgers.md §3).
/// </para>
/// <para>
/// Neither a refusal nor a rejection is permanent: a drop which a held write carries can make room for the change, and a
/// held write can carry the very drop a later batch rejects, after the close or the earlier drop in its lineage which
/// makes it valid. So while a write whose outcome is unknown may still land, the committer answers no change
/// <see cref="LedgerMapCommitOutcome.Refused"/> or <see cref="LedgerMapCommitOutcome.Rejected"/> from the value that
/// write was conditioned on: it first fences the register, writing that value again at the next epoch over the held
/// write's version, after which the held write can never land. Every entry read is disposed (conventions.md §1).
/// </para>
/// </remarks>
public sealed class LedgerMapCommitter : IAsyncDisposable
{
    private readonly CommitRules _rules;
    private readonly GroupCommitter<LedgerMapValue, MetaStoreVersion, LedgerMapChange, LedgerMapCommitOutcome, RegisterWriteOutcome> _committer;

    /// <summary>Initializes a new instance of the <see cref="LedgerMapCommitter"/> class.</summary>
    /// <param name="register">The <c>ledger-map</c> register.</param>
    /// <param name="maxValueLength">
    /// The largest value, in bytes, the register's provider stores: its backend's limit, which the composition knows
    /// from the provider it builds (metastore.md §8, conventions.md §5).
    /// </param>
    /// <param name="view">The view the committer raises with each value it reads and each value its writes which landed
    /// hold, as a controller's view of the map it commits; <see langword="null"/> for none.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxValueLength"/> cannot hold the empty map.</exception>
    public LedgerMapCommitter(IConditionalRegister register, int maxValueLength, LedgerMapView? view = null)
    {
        ArgumentNullException.ThrowIfNull(register);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxValueLength, LedgerMapValue.Empty.EncodedLength);
        _rules = new CommitRules(register, maxValueLength, view);
        _committer = new(_rules, nameof(LedgerMapCommitter));
        MaxValueLength = maxValueLength;
    }

    /// <summary>Gets the largest value, in bytes, the register's provider stores, beyond which a change is refused.</summary>
    public int MaxValueLength { get; }

    /// <summary>Returns the bytes <paramref name="map"/> leaves free under <see cref="MaxValueLength"/> (orleans.md §5.6).</summary>
    public int Room(LedgerMapValue map)
    {
        ArgumentNullException.ThrowIfNull(map);
        return MaxValueLength - map.EncodedLength;
    }

    /// <summary>
    /// Returns the most open ranges, each with <paramref name="replicas"/> replicas, a map within
    /// <see cref="MaxValueLength"/> holds while each also lists one closed predecessor, the ranges split evenly: the target
    /// a controller splits toward at most (orleans.md §5.6).
    /// </summary>
    public int RangeCapacity(int replicas)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(replicas);
        return LedgerMapValue.RangeCapacity(MaxValueLength, replicas);
    }

    /// <summary>The fence's mutants (ledgers.md §10), which tests set to show the conformance oracle catches each.</summary>
    internal enum FenceMutant
    {
        /// <summary>The committer as specified.</summary>
        None,

        /// <summary>A batch which refuses or rejects a change while a held write may land answers it without a fence.</summary>
        NoFence,

        /// <summary>A read after an unknown outcome refuses or rejects a change while the held write may still land.</summary>
        AnswerWhileHeld,
    }

    /// <summary>Gets or sets the fence's mutant; for tests.</summary>
    internal FenceMutant Mutant
    {
        get => _rules.Mutant;
        set => _rules.Mutant = value;
    }

    /// <summary>
    /// Submits <paramref name="change"/> and returns its verdict, decided from a value read or written after the
    /// submission.
    /// </summary>
    /// <param name="change">The change.</param>
    /// <param name="cancellationToken">Ends the caller's wait only: the change stays in its batch and may apply.</param>
    /// <returns>
    /// <see cref="LedgerMapCommitOutcome.Applied"/>, <see cref="LedgerMapCommitOutcome.Superseded"/>,
    /// <see cref="LedgerMapCommitOutcome.Refused"/>, or <see cref="LedgerMapCommitOutcome.Rejected"/>.
    /// </returns>
    /// <exception cref="ObjectDisposedException">The committer was disposed before the change was decided.</exception>
    /// <exception cref="OverflowException">The change would advance <c>NextLedgerId</c> past 2^64 − 1.</exception>
    /// <exception cref="InvalidDataException">The register holds a value which is not a ledger map, and no write the change's
    /// batch dispatched carried it.</exception>
    /// <exception cref="InvalidOperationException">A value the committer was about to write broke an invariant, or its
    /// rules failed: a committer defect, and no write carried the change.</exception>
    /// <remarks>Any other exception, <see cref="GroupCommitOutcomeUnknownException"/> among them, leaves the outcome
    /// unknown: the MetaStore stayed unavailable, or a write which carried the change may land, and submitting the same
    /// change again decides it.</remarks>
    public Task<LedgerMapCommitOutcome> CommitAsync(LedgerMapChange change, CancellationToken cancellationToken = default)
    {
        if (change.Value is null)
        {
            throw new ArgumentException("A change holds a case.", nameof(change));
        }

        return _committer.CommitAsync(change, cancellationToken);
    }

    /// <inheritdoc cref="GroupCommitter{TValue, TVersion, TChange, TOutcome, TReply}.DisposeAsync"/>
    public ValueTask DisposeAsync() => _committer.DisposeAsync();

    private static GroupCommitAnswer<LedgerMapCommitOutcome> Answered(LedgerMapCommitOutcome outcome)
    {
        Debug.Assert(outcome.Value is not null, "conventions.md §15: an operation returns a union which holds a case.");
        return new GroupCommitAnswer<LedgerMapCommitOutcome>.Answered(outcome);
    }

    private static GroupCommitAnswer<LedgerMapCommitOutcome> Failed(Exception failure) => new GroupCommitAnswer<LedgerMapCommitOutcome>.Failed(failure);

    /// <summary>Gets the answer of a change a value leaves undecided, which fails with the write's exception.</summary>
    private static GroupCommitAnswer<LedgerMapCommitOutcome>? Undecided => null;

    /// <summary>The ledger map's binding to the group committer: its register and its commit rules (ledgers.md §2.6).</summary>
    private sealed class CommitRules(IConditionalRegister register, int maxValueLength, LedgerMapView? view)
        : IGroupCommitRules<LedgerMapValue, MetaStoreVersion, LedgerMapChange, LedgerMapCommitOutcome, RegisterWriteOutcome>
    {
        public FenceMutant Mutant { get; set; }

        public async ValueTask<GroupCommitRead<LedgerMapValue, MetaStoreVersion>> ReadAsync(CancellationToken cancellationToken)
        {
            using var entry = await register.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (entry is null)
            {
                return new GroupCommitRead<LedgerMapValue, MetaStoreVersion>.Found(LedgerMapValue.Empty, null);
            }

            var decoded = LedgerMapValue.Decode(entry.Value.AsReadOnlySequence());
            if (decoded.Value is LedgerMapValue read)
            {
                view?.Observe(read);
            }

            return decoded switch
            {
                LedgerMapValue value => new GroupCommitRead<LedgerMapValue, MetaStoreVersion>.Found(value, entry.Version),
                InvalidDataException rejection => new GroupCommitRead<LedgerMapValue, MetaStoreVersion>.Corrupt(rejection),
                null => throw new UnreachableException(),
            };
        }

        public ValueTask<RegisterWriteOutcome> WriteAsync(MetaStoreVersion? expectedVersion, GroupCommitEncoding encoded, CancellationToken cancellationToken) =>
            register.WriteAsync(expectedVersion, encoded.Content.AsReadOnlySequence(), cancellationToken);

        public GroupCommitWrite<MetaStoreVersion> WriteOutcome(RegisterWriteOutcome reply) => reply switch
        {
            RegisterWriteOutcome.Applied applied => new GroupCommitWrite<MetaStoreVersion>.Applied(applied.Version),
            RegisterWriteOutcome.Mismatch => new GroupCommitWrite<MetaStoreVersion>.Mismatch(),
            null => throw new InvalidOperationException("conventions.md §15: a write answers a union which holds a case."),
        };

        public GroupCommitEncoding Encode(LedgerMapValue value) => new(value.Encode());

        public bool DecodesAs(ReadOnlySequence<byte> encoded, LedgerMapValue value) =>
            LedgerMapValue.Decode(encoded).Value is LedgerMapValue decoded
            && decoded.Epoch == value.Epoch
            && decoded.NextLedgerId == value.NextLedgerId
            && decoded.RetirementFloor == value.RetirementFloor
            && decoded.Ledgers.AsSpan().SequenceEqual(value.Ledgers.AsSpan());

        public Exception OutcomeUnknown(string message, Exception cause) => new GroupCommitOutcomeUnknownException(message, cause);

        /// <summary>
        /// Folds the batch; when it applies nothing, but refuses or rejects a change while a held write may still land
        /// over <paramref name="basis"/>, writes <paramref name="basis"/> again as a fence first (conventions.md §6), since
        /// that write could revoke the answer.
        /// </summary>
        public IGroupCommitBatch<LedgerMapValue, MetaStoreVersion, LedgerMapCommitOutcome> Fold(LedgerMapValue basis, bool heldWriteMayLand, IReadOnlyList<LedgerMapChange> changes)
        {
            var (result, verdicts, applied) = LedgerMapRules.Fold(basis, changes, maxValueLength);
            var fence = ReferenceEquals(result, basis)
                && heldWriteMayLand
                && Mutant != FenceMutant.NoFence
                && Array.Exists(verdicts, static verdict => verdict.Value is LedgerMapFoldVerdict.TooLarge or LedgerMapFoldVerdict.Defect);
            var written = !ReferenceEquals(result, basis) || fence
                ? new LedgerMapValue(checked(basis.Epoch + 1), result.NextLedgerId, result.RetirementFloor, result.Ledgers)
                : null;
            return new Batch(changes, verdicts, applied, written, maxValueLength, Mutant == FenceMutant.AnswerWhileHeld, view);
        }
    }

    /// <summary>One batch's decision: each change's fold verdict, the changes the fold applied, and the value its write holds.</summary>
    private sealed class Batch(
        IReadOnlyList<LedgerMapChange> changes,
        LedgerMapFoldVerdict[] verdicts,
        ImmutableArray<LedgerMapAppliedChange> applied,
        LedgerMapValue? written,
        int maxValueLength,
        bool answerWhileHeld,
        LedgerMapView? view) : IGroupCommitBatch<LedgerMapValue, MetaStoreVersion, LedgerMapCommitOutcome>
    {
        public LedgerMapValue? Written => written;

        public bool Carries(int index) => verdicts[index].Value is LedgerMapFoldVerdict.Applies;

        /// <summary>Answers a change from its fold verdict against <paramref name="value"/>, the value which decides it.</summary>
        public GroupCommitAnswer<LedgerMapCommitOutcome> Settle(int index, LedgerMapValue value) => verdicts[index] switch
        {
            LedgerMapFoldVerdict.Overflowed overflowed => Failed(overflowed.Exception),
            LedgerMapFoldVerdict.TooLarge => Answered(LedgerMapCommitOutcome.Refused.Instance),
            LedgerMapFoldVerdict.Defect defect => Answered(new LedgerMapCommitOutcome.Rejected(defect.Reason)),
            LedgerMapFoldVerdict.GoalPresent or LedgerMapFoldVerdict.Applies or LedgerMapFoldVerdict.RuleFails => Answered(ToOutcome(value, changes[index])),
            null => throw new UnreachableException(),
        };

        public GroupCommitAnswer<LedgerMapCommitOutcome>? Applied(int index, LedgerMapValue read) =>
            LedgerMapRules.GoalHolds(read, changes[index]) ? Answered(new LedgerMapCommitOutcome.Applied(read.Epoch)) : Undecided;

        /// <summary>
        /// Answers a change no held write carried from <paramref name="read"/>: applied when the read shows its goal, and
        /// otherwise as <see cref="FromRead"/> decides it; an overflow fails it whatever the read shows.
        /// </summary>
        public GroupCommitAnswer<LedgerMapCommitOutcome>? AfterUnknownOutcome(int index, LedgerMapValue read, bool heldWriteMayLand)
        {
            var verdict = verdicts[index];
            if (verdict.Value is LedgerMapFoldVerdict.Overflowed overflowed)
            {
                return Failed(overflowed.Exception);
            }

            if (LedgerMapRules.GoalHolds(read, changes[index]))
            {
                return Answered(new LedgerMapCommitOutcome.Applied(read.Epoch));
            }

            // The fold refused it against the value the write replaced; the read decides it afresh.
            return FromRead(changes[index], read, heldWriteMayLand && !answerWhileHeld);
        }

        public string? Violation(LedgerMapValue before) =>
            written is not null && LedgerMapRules.CheckInvariants(before, written, applied) is { } violation
                ? string.Create(CultureInfo.InvariantCulture, $"The ledger map committer would write epoch {written.Epoch}, which breaks {violation}")
                : null;

        /// <summary>Raises the view with the value the landed write holds, before any submission's answer is released.</summary>
        public void OnApplied(MetaStoreVersion version)
        {
            Debug.Assert(written is not null, "Only a batch which writes has a write which lands.");
            view?.Observe(written);
        }

        /// <summary>
        /// Returns the outcome <paramref name="value"/>, which decided <paramref name="change"/>, gives a change whose goal
        /// was present, which applied, or whose rule failed: an install's goal turns absent once every ledger it opened has
        /// left, and it then reads superseded (ledgers.md §2.6).
        /// </summary>
        private static LedgerMapCommitOutcome ToOutcome(LedgerMapValue value, LedgerMapChange change) =>
            LedgerMapRules.GoalHolds(value, change) ? new LedgerMapCommitOutcome.Applied(value.Epoch) : new LedgerMapCommitOutcome.Superseded(value.Epoch);

        /// <summary>
        /// Answers a change no write carried from <paramref name="read"/>: superseded when its rule fails there, which a
        /// later value never revokes (ledgers.md §2.6); refused or rejected when it is too large or a defect there, once no
        /// write the committer dispatched can still land, since an earlier held write may make room for it, or carry it
        /// after the change which makes it valid; and undecided otherwise.
        /// </summary>
        private GroupCommitAnswer<LedgerMapCommitOutcome>? FromRead(LedgerMapChange change, LedgerMapValue read, bool heldWriteMayLand) =>
            LedgerMapRules.Decide(read, change, maxValueLength) switch
            {
                LedgerMapFoldVerdict.RuleFails => Answered(new LedgerMapCommitOutcome.Superseded(read.Epoch)),
                LedgerMapFoldVerdict.TooLarge => heldWriteMayLand ? Undecided : Answered(LedgerMapCommitOutcome.Refused.Instance),
                LedgerMapFoldVerdict.Defect defect => heldWriteMayLand ? Undecided : Answered(new LedgerMapCommitOutcome.Rejected(defect.Reason)),
                LedgerMapFoldVerdict.GoalPresent or LedgerMapFoldVerdict.Applies or LedgerMapFoldVerdict.Overflowed => Undecided,
                null => throw new UnreachableException(),
            };
    }
}
