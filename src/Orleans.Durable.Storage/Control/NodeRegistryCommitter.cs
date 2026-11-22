using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Cluster.Control;
using Orleans.Durable.Storage.Engine.GroupCommit;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Objects;
using Orleans.Durable.Storage.Engine.MetaStore;

namespace Orleans.Durable.Storage.Control;

/// <summary>
/// Writes the <c>node-registry</c> register: every registration and retirement goes through the committer, which batches
/// concurrent submissions, merges identical ones, folds each batch in submission order, and writes the result with one
/// conditional write (orleans.md §5.3, OR-I2). The group committer, <see cref="GroupCommitter{TValue, TVersion, TChange, TOutcome, TReply}"/>,
/// decides each batch under the registry's <see cref="CommitRules"/>, and its remarks state the policy every group
/// committer keeps (conventions.md §6), answering a change only from a value read or written after it was submitted
/// (OR-I19).
/// </summary>
/// <remarks>
/// <para>
/// Every failure whose outcome is unknown is <see cref="NodeRegistryUnavailableException"/>, so its caller submits again.
/// After a write whose outcome is unknown, a change whose goal the value read shows is answered from it, and one the write
/// did not carry is answered from the read when its rule failed there. No fence is needed, as the ledger map's committer
/// needs one: a change a write carried is answered only from its goal or with the write's exception; <c>Stale</c> and
/// <c>WrongArchiveStore</c> are permanent (OR-I2, OR-I20); <c>Refused</c>, <c>OrdinalOccupied</c>, and a retirement's
/// <c>NotRegistered</c> are decided against the value read and never carried in a write; and <c>Registered(r)</c> and
/// <c>Retired(r)</c> claim only a value which landed.
/// </para>
/// <para>
/// Every registration is checked as <c>CLNR</c> can hold it before it is folded, and every value is checked against
/// OR-I2 before it is written. Every entry read is disposed (conventions.md §1).
/// </para>
/// </remarks>
internal sealed partial class NodeRegistryCommitter : IAsyncDisposable
{
    private readonly GroupCommitter<NodeRegistryValue, MetaStoreVersion, NodeRegistryChange, NodeRegistryOutcome, RegisterWriteOutcome> _committer;
    private readonly CommitRules _rules;

    /// <summary>Initializes a new instance of the <see cref="NodeRegistryCommitter"/> class.</summary>
    /// <param name="register">The <c>node-registry</c> register.</param>
    /// <param name="maxValueLength">
    /// The register's value limit, in bytes: <see cref="NodeRegistryCodec.MaxValueLength"/>, the format's, which every
    /// committer of the cluster shares (orleans.md §5.3); a test passes a smaller one.
    /// </param>
    /// <param name="logger">The logger for ordinal successions and retirements.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxValueLength"/> cannot hold the empty registry.</exception>
    public NodeRegistryCommitter(IConditionalRegister register, int maxValueLength, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(register);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxValueLength, NodeRegistryCodec.HeaderLength);
        _rules = new CommitRules(register, maxValueLength, logger);
        _committer = new(_rules, nameof(NodeRegistryCommitter));
    }

    /// <summary>
    /// Gets the newest registry value the committer read from the register or wrote with a write which landed, or
    /// <see langword="null"/> before either: never older than a value the committer answered a change from.
    /// </summary>
    public NodeRegistryValue? Latest => _rules.Latest;

    /// <summary>Reads the register, raising <see cref="Latest"/> with the value read, and returns it.</summary>
    /// <exception cref="InvalidDataException">The register holds a value which is not a node registry.</exception>
    /// <exception cref="NodeRegistryUnavailableException">The read failed.</exception>
    public async Task<NodeRegistryValue> ReadAsync(CancellationToken cancellationToken = default) =>
        await _rules.ReadAsync(cancellationToken).ConfigureAwait(false) switch
        {
            GroupCommitRead<NodeRegistryValue, MetaStoreVersion>.Found found => found.Current,
            GroupCommitRead<NodeRegistryValue, MetaStoreVersion>.Corrupt corrupt => throw new InvalidDataException("The node-registry register holds no node registry.", corrupt.Failure),
            null => throw new UnreachableException("conventions.md §15: a read answers a union which holds a case."),
        };

    /// <summary>Submits a silo's registration with no object-ledger store and returns its answer.</summary>
    public Task<NodeRegistrationOutcome> RegisterAsync(
        NodeRegistryEntry entry,
        ArchiveStoreIdentity store,
        NodeId? nodeToReplace,
        CancellationToken cancellationToken) =>
        RegisterAsync(entry, store, nodeToReplace, objectLedgerStore: null, cancellationToken);

    /// <summary>
    /// Submits a silo's registration and returns its answer, decided from a value read or written after the submission.
    /// </summary>
    /// <param name="entry">The entry the silo would hold: its node, address and generation, ordinal, and failure domain, active.</param>
    /// <param name="store">The archive store the silo is configured with.</param>
    /// <param name="nodeToReplace">
    /// The node the operator names for replacement: the registration may retire that node's active entry at its ordinal,
    /// and no other. <see langword="null"/> names none.
    /// </param>
    /// <param name="objectLedgerStore">The silo's object-ledger store ID, or <see langword="null"/> when the silo configures no object-ledger store.</param>
    /// <param name="cancellationToken">Ends the caller's wait only: the registration stays in its batch and may apply.</param>
    /// <returns>
    /// <see cref="NodeRegistrationOutcome.Registered"/>, <see cref="NodeRegistrationOutcome.Stale"/>,
    /// <see cref="NodeRegistrationOutcome.WrongArchiveStore"/>, <see cref="NodeRegistrationOutcome.WrongObjectLedgerStore"/>,
    /// <see cref="NodeRegistrationOutcome.Refused"/>, or <see cref="NodeRegistrationOutcome.OrdinalOccupied"/>.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="entry"/> is one <c>CLNR</c> cannot hold or retired, <paramref name="store"/> is the default identity, or <paramref name="nodeToReplace"/> is the default identity.</exception>
    /// <exception cref="ObjectDisposedException">The committer was disposed before the registration was decided.</exception>
    /// <exception cref="InvalidDataException">The register holds a value which is not a node registry, and no write the
    /// registration's batch dispatched carried it.</exception>
    /// <exception cref="InvalidOperationException">A value the committer was about to write broke OR-I2 or <c>CLNR</c>, or
    /// its rules failed: a committer defect, and no write carried the registration.</exception>
    /// <exception cref="NodeRegistryUnavailableException">The outcome is unknown: registering again decides it.</exception>
    public Task<NodeRegistrationOutcome> RegisterAsync(
        NodeRegistryEntry entry,
        ArchiveStoreIdentity store,
        NodeId? nodeToReplace,
        ObjectLedgerStoreId? objectLedgerStore = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        // Orleans builds a deserialized entry without its constructor, and carries the default store identity, which
        // names no store, as it does any other.
        entry.Validate();
        if (store == default)
        {
            throw new ArgumentException("A registration names its archive store: the default identity names none.", nameof(store));
        }

        if (objectLedgerStore == default(ObjectLedgerStoreId))
        {
            throw new ArgumentException("A registration names its object-ledger store, or none.", nameof(objectLedgerStore));
        }

        if (entry.IsRetired)
        {
            throw new ArgumentException("A registration carries its entry active.", nameof(entry));
        }

        if (nodeToReplace == default(NodeId))
        {
            throw new ArgumentException("A registration names the node it replaces, or none.", nameof(nodeToReplace));
        }

        return CommitAsync<NodeRegistrationOutcome>(new NodeRegistrationRequest(entry, store, nodeToReplace, objectLedgerStore), cancellationToken);
    }

    /// <summary>
    /// Submits an operator's retirement of <paramref name="node"/> and returns its answer, decided from a value read or
    /// written after the submission (orleans.md §5.3): its entry retired, at the revision of the write which retired it, or
    /// no entry of it in the value read.
    /// </summary>
    /// <param name="node">The node to retire.</param>
    /// <param name="cancellationToken">Ends the caller's wait only: the retirement stays in its batch and may apply.</param>
    /// <returns><see cref="NodeRetirementOutcome.Retired"/> or <see cref="NodeRetirementOutcome.NotRegistered"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="node"/> is the default identity.</exception>
    /// <exception cref="ObjectDisposedException">The committer was disposed before the retirement was decided.</exception>
    /// <exception cref="InvalidDataException">The register holds a value which is not a node registry, and no write the
    /// retirement's batch dispatched carried it.</exception>
    /// <exception cref="InvalidOperationException">A value the committer was about to write broke OR-I2 or <c>CLNR</c>, or
    /// its rules failed: a committer defect, and no write carried the retirement.</exception>
    /// <exception cref="NodeRegistryUnavailableException">The outcome is unknown: retiring again decides it.</exception>
    public Task<NodeRetirementOutcome> RetireAsync(NodeId node, CancellationToken cancellationToken = default) =>
        node == default
            ? throw new ArgumentException("A retirement names a node.", nameof(node))
            : CommitAsync<NodeRetirementOutcome>(new NodeRetirementRequest(node), cancellationToken);

    /// <summary>
    /// Durably starts a planned whole-cluster drain off node-backed ledgers by setting <c>DrainAllRevision</c> in the node
    /// registry (orleans.md §5.14), or returns the existing active revision if drain-all is already active.
    /// </summary>
    public Task<NodeDrainAllStartOutcome> StartDrainAllAsync(CancellationToken cancellationToken = default) =>
        CommitAsync<NodeDrainAllStartOutcome>(NodeDrainAllStartRequest.Instance, cancellationToken);

    /// <summary>
    /// Durably cancels an active drain-all by clearing <c>DrainAllRevision</c> to <c>0</c> in the node registry
    /// (orleans.md §5.14).
    /// </summary>
    public Task<NodeDrainAllCancelOutcome> CancelDrainAllAsync(CancellationToken cancellationToken = default) =>
        CommitAsync<NodeDrainAllCancelOutcome>(NodeDrainAllCancelRequest.Instance, cancellationToken);

    /// <summary>
    /// Atomically completes drain-all Phase 2 in one registry write (orleans.md §5.14): retires every active diskful node
    /// at the write's revision and resets <c>DrainAllRevision</c> to <c>0</c>, only while the registry's
    /// <c>DrainAllRevision</c> still equals <paramref name="expectedDrainAllRevision"/>.
    /// </summary>
    public Task<NodeDrainAllCompleteOutcome> CompleteDrainAllAsync(ulong expectedDrainAllRevision, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfZero(expectedDrainAllRevision);
        return CommitAsync<NodeDrainAllCompleteOutcome>(new NodeDrainAllCompleteRequest(expectedDrainAllRevision), cancellationToken);
    }

    /// <inheritdoc cref="GroupCommitter{TValue, TVersion, TChange, TOutcome, TReply}.DisposeAsync"/>
    public ValueTask DisposeAsync() => _committer.DisposeAsync();

    /// <summary>Submits <paramref name="change"/> and unwraps its answer, which a change of its kind receives.</summary>
    private async Task<TOutcome> CommitAsync<TOutcome>(NodeRegistryChange change, CancellationToken cancellationToken)
    {
        var outcome = await _committer.CommitAsync(change, cancellationToken).ConfigureAwait(false);
        return outcome.Value is TOutcome answer
            ? answer
            : throw new UnreachableException("conventions.md §15: a change is answered by its own kind's union, which holds a case.");
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Node {Successor} registered at ordinal {Ordinal} and, as its registration named it for replacement, retired node {Predecessor}, active at that ordinal, at revision {Revision}. A retired identity is never active again: the predecessor's volume is taken for lost.")]
    private static partial void LogSuccession(ILogger logger, NodeId successor, int ordinal, NodeId predecessor, ulong revision);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Node {Node} was retired at revision {Revision} by an operator's request. A retired identity is never active again; its silo serves its replicas until its retirement gate opens, and its disk is released only then.")]
    private static partial void LogRetirement(ILogger logger, NodeId node, ulong revision);

    [LoggerMessage(Level = LogLevel.Information, Message = "Drain-all started durably in the node registry at revision {Revision} (orleans.md §5.14).")]
    private static partial void LogDrainAllStarted(ILogger logger, ulong revision);

    [LoggerMessage(Level = LogLevel.Information, Message = "Drain-all cancelled durably in the node registry at revision {Revision} (orleans.md §5.14).")]
    private static partial void LogDrainAllCancelled(ILogger logger, ulong revision);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Drain-all completed durably in the node registry at revision {Revision}: all active diskful nodes retired and DrainAllRevision cleared (orleans.md §5.14).")]
    private static partial void LogDrainAllCompleted(ILogger logger, ulong revision);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The node registry reserves {Reserved} bytes of its {Limit}-byte value limit at revision {Revision}: each new node identity takes room which retired identities keep for the cluster's life (orleans.md §8.1).")]
    private static partial void LogRegistryFilling(ILogger logger, int reserved, int limit, ulong revision);

    /// <summary>
    /// The node registry's binding to the group committer: its register and its commit rules (orleans.md §5.3). Every
    /// failure of the register is <see cref="NodeRegistryUnavailableException"/>, except the committer's own stop and a
    /// malformed entry.
    /// </summary>
    private sealed class CommitRules(IConditionalRegister register, int maxValueLength, ILogger logger)
        : IGroupCommitRules<NodeRegistryValue, MetaStoreVersion, NodeRegistryChange, NodeRegistryOutcome, RegisterWriteOutcome>
    {
        private NodeRegistryValue? _latest;

        public NodeRegistryValue? Latest => Volatile.Read(ref _latest);

        /// <summary>Raises <see cref="Latest"/> to <paramref name="value"/> when it is newer, and warns as the registry fills.</summary>
        public void Raise(NodeRegistryValue value)
        {
            NodeRegistryValue? current;
            do
            {
                current = Volatile.Read(ref _latest);
                if (current is not null && current.Revision >= value.Revision)
                {
                    return;
                }
            }
            while (Interlocked.CompareExchange(ref _latest, value, current) != current);

            // Each new identity's room stays reserved for the cluster's life: three quarters of the limit warns of the end.
            if (NodeRegistryRules.ReservedLength(value) is var reserved && reserved > maxValueLength / 4 * 3)
            {
                LogRegistryFilling(logger, reserved, maxValueLength, value.Revision);
            }
        }

        public async ValueTask<GroupCommitRead<NodeRegistryValue, MetaStoreVersion>> ReadAsync(CancellationToken cancellationToken)
        {
            MetaStoreEntry? entry;
            try
            {
                entry = await register.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is not InvalidDataException && !(failure is OperationCanceledException && cancellationToken.IsCancellationRequested))
            {
                throw NodeRegistryUnavailableException.Of(failure);
            }

            using (entry)
            {
                if (entry is null)
                {
                    return new GroupCommitRead<NodeRegistryValue, MetaStoreVersion>.Found(NodeRegistryValue.Empty, null);
                }

                return NodeRegistryCodec.Decode(entry.Value.AsReadOnlySequence()) switch
                {
                    NodeRegistryValue value => Found(value, entry.Version),
                    InvalidDataException rejection => new GroupCommitRead<NodeRegistryValue, MetaStoreVersion>.Corrupt(rejection),
                    null => throw new UnreachableException(),
                };
            }
        }

        public async ValueTask<RegisterWriteOutcome> WriteAsync(MetaStoreVersion? expectedVersion, GroupCommitEncoding encoded, CancellationToken cancellationToken)
        {
            try
            {
                return await register.WriteAsync(expectedVersion, encoded.Content.AsReadOnlySequence(), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception unknown) when (!cancellationToken.IsCancellationRequested)
            {
                throw NodeRegistryUnavailableException.Of(unknown);
            }
        }

        public GroupCommitWrite<MetaStoreVersion> WriteOutcome(RegisterWriteOutcome reply) => reply switch
        {
            RegisterWriteOutcome.Applied applied => new GroupCommitWrite<MetaStoreVersion>.Applied(applied.Version),
            RegisterWriteOutcome.Mismatch => new GroupCommitWrite<MetaStoreVersion>.Mismatch(),
            null => throw new InvalidOperationException("conventions.md §15: a write answers a union which holds a case."),
        };

        public GroupCommitEncoding Encode(NodeRegistryValue value) => new(ArcBuffer.Wrap(NodeRegistryCodec.Encode(value)));

        public bool DecodesAs(ReadOnlySequence<byte> encoded, NodeRegistryValue value) =>
            NodeRegistryCodec.Decode(encoded).Value is NodeRegistryValue decoded && decoded.ContentEquals(value);

        public Exception OutcomeUnknown(string message, Exception cause) => new NodeRegistryUnavailableException(message, cause);

        public IGroupCommitBatch<NodeRegistryValue, MetaStoreVersion, NodeRegistryOutcome> Fold(NodeRegistryValue basis, bool heldWriteMayLand, IReadOnlyList<NodeRegistryChange> changes)
        {
            var (written, verdicts, successions) = NodeRegistryRules.Fold(basis, changes, maxValueLength);
            return new Batch(changes, verdicts, successions, written, maxValueLength, this, logger);
        }

        private GroupCommitRead<NodeRegistryValue, MetaStoreVersion>.Found Found(NodeRegistryValue value, MetaStoreVersion version)
        {
            Raise(value);
            return new(value, version);
        }
    }

    /// <summary>
    /// One batch's decision: each change's verdict, the ordinal successions its write makes, and the value it holds.
    /// </summary>
    private sealed class Batch(
        IReadOnlyList<NodeRegistryChange> changes,
        NodeRegistryVerdict[] verdicts,
        IReadOnlyList<NodeRegistrySuccession> successions,
        NodeRegistryValue? written,
        int maxValueLength,
        CommitRules rules,
        ILogger logger) : IGroupCommitBatch<NodeRegistryValue, MetaStoreVersion, NodeRegistryOutcome>
    {
        public NodeRegistryValue? Written => written;

        public bool Carries(int index) => verdicts[index] == NodeRegistryVerdict.Applies;

        /// <summary>
        /// Answers a change from <paramref name="value"/>, the value its batch's write produced or the one it applied nothing
        /// against: a registration the batch applied is registered unless a later change of the batch advanced or retired
        /// its entry, a retirement is retired, and a failed rule stays failed.
        /// </summary>
        public GroupCommitAnswer<NodeRegistryOutcome> Settle(int index, NodeRegistryValue value)
        {
            var change = changes[index];
            var carried = verdicts[index] == NodeRegistryVerdict.Applies;
            if (carried && written is not null)
            {
                switch (change.Value)
                {
                    case NodeDrainAllCancelRequest:
                        return Answered(new NodeDrainAllCancelOutcome.Cancelled(value.Revision));
                    case NodeDrainAllCompleteRequest:
                        return Answered(new NodeDrainAllCompleteOutcome.Completed(value.Revision));
                    default:
                        break;
                }
            }

            return Answer(change, NodeRegistryRules.Decide(value, change, maxValueLength), value);
        }

        public GroupCommitAnswer<NodeRegistryOutcome>? Applied(int index, NodeRegistryValue read)
        {
            var change = changes[index];
            return change switch
            {
                NodeDrainAllCancelRequest => written is not null && read.ContentEquals(written)
                    ? Answered(new NodeDrainAllCancelOutcome.Cancelled(read.Revision))
                    : Undecided,
                NodeDrainAllCompleteRequest => written is not null && read.ContentEquals(written)
                    ? Answered(new NodeDrainAllCompleteOutcome.Completed(read.Revision))
                    : Undecided,
                NodeRegistrationRequest or NodeRetirementRequest or NodeDrainAllStartRequest =>
                    NodeRegistryRules.Decide(read, change, maxValueLength) == NodeRegistryVerdict.GoalPresent
                        ? Answer(change, NodeRegistryVerdict.GoalPresent, read)
                        : Undecided,
                null => throw new UnreachableException("conventions.md §15: a change holds a case."),
            };
        }

        /// <summary>
        /// Answers a change the write did not carry as <paramref name="read"/> decides it, a failed rule being permanent;
        /// leaves it undecided when the read would apply it.
        /// </summary>
        public GroupCommitAnswer<NodeRegistryOutcome>? AfterUnknownOutcome(int index, NodeRegistryValue read, bool heldWriteMayLand)
        {
            var decided = NodeRegistryRules.Decide(read, changes[index], maxValueLength);
            return decided == NodeRegistryVerdict.Applies ? Undecided : Answer(changes[index], decided, read);
        }

        public string? Violation(NodeRegistryValue before) =>
            written is not null && NodeRegistryRules.Violation(before, written, Carried(), maxValueLength) is { } violation
                ? string.Create(CultureInfo.InvariantCulture, $"The node registry committer would write revision {written.Revision}, which breaks {violation}")
                : null;

        public void OnApplied(MetaStoreVersion version)
        {
            rules.Raise(written!);
            foreach (var succession in successions)
            {
                LogSuccession(logger, succession.Successor, succession.Ordinal, succession.Predecessor, written!.Revision);
            }

            foreach (var change in Carried())
            {
                switch (change.Value)
                {
                    case NodeRetirementRequest retirement:
                        LogRetirement(logger, retirement.Node, written!.Revision);
                        break;
                    case NodeDrainAllStartRequest:
                        LogDrainAllStarted(logger, written!.Revision);
                        break;
                    case NodeDrainAllCancelRequest:
                        LogDrainAllCancelled(logger, written!.Revision);
                        break;
                    case NodeDrainAllCompleteRequest:
                        LogDrainAllCompleted(logger, written!.Revision);
                        break;
                    default:
                        break;
                }
            }
        }

        /// <summary>Gets the answer of a change a value leaves undecided, which fails with the write's exception.</summary>
        private static GroupCommitAnswer<NodeRegistryOutcome>? Undecided => null;

        /// <summary>Returns the changes the batch's write carries.</summary>
        private NodeRegistryChange[] Carried() => [.. changes.Where((_, index) => verdicts[index] == NodeRegistryVerdict.Applies)];

        // A case converts only to its own union, so each kind enters the one answer through its union.
        private static GroupCommitAnswer<NodeRegistryOutcome> Answered(NodeRegistrationOutcome outcome) => Answered((NodeRegistryOutcome)outcome);

        private static GroupCommitAnswer<NodeRegistryOutcome> Answered(NodeRetirementOutcome outcome) => Answered((NodeRegistryOutcome)outcome);

        private static GroupCommitAnswer<NodeRegistryOutcome> Answered(NodeDrainAllStartOutcome outcome) => Answered((NodeRegistryOutcome)outcome);

        private static GroupCommitAnswer<NodeRegistryOutcome> Answered(NodeDrainAllCancelOutcome outcome) => Answered((NodeRegistryOutcome)outcome);

        private static GroupCommitAnswer<NodeRegistryOutcome> Answered(NodeDrainAllCompleteOutcome outcome) => Answered((NodeRegistryOutcome)outcome);

        private static GroupCommitAnswer<NodeRegistryOutcome> Answered(NodeRegistryOutcome outcome)
        {
            Debug.Assert(
                outcome.Value is NodeRegistrationOutcome { Value: not null }
                    or NodeRetirementOutcome { Value: not null }
                    or NodeDrainAllStartOutcome { Value: not null }
                    or NodeDrainAllCancelOutcome { Value: not null }
                    or NodeDrainAllCompleteOutcome { Value: not null },
                "conventions.md §15: an operation returns a union which holds a case.");
            return new GroupCommitAnswer<NodeRegistryOutcome>.Answered(outcome);
        }

        /// <summary>Returns the answer a deciding value gives <paramref name="change"/>, which it decided as <paramref name="verdict"/>.</summary>
        private static GroupCommitAnswer<NodeRegistryOutcome> Answer(NodeRegistryChange change, NodeRegistryVerdict verdict, NodeRegistryValue value) => change switch
        {
            NodeRegistrationRequest request => Answer(request, verdict, value),
            NodeRetirementRequest retirement => Answer(retirement, verdict, value),
            NodeDrainAllStartRequest => AnswerStartDrainAll(verdict, value),
            NodeDrainAllCancelRequest => AnswerCancelDrainAll(verdict),
            NodeDrainAllCompleteRequest => AnswerCompleteDrainAll(verdict),
            null => throw new UnreachableException("conventions.md §15: a change holds a case."),
        };

        private static GroupCommitAnswer<NodeRegistryOutcome> Answer(NodeRegistrationRequest request, NodeRegistryVerdict verdict, NodeRegistryValue value) => verdict switch
        {
            NodeRegistryVerdict.GoalPresent => Answered(new NodeRegistrationOutcome.Registered(value.Revision)),
            NodeRegistryVerdict.Stale => Answered(NodeRegistrationOutcome.Stale.Instance),
            NodeRegistryVerdict.WrongArchiveStore => Answered(NodeRegistrationOutcome.WrongArchiveStore.Instance),
            NodeRegistryVerdict.WrongObjectLedgerStore => Answered(NodeRegistrationOutcome.WrongObjectLedgerStore.Instance),
            NodeRegistryVerdict.TooLarge => Answered(NodeRegistrationOutcome.Refused.Instance),
            NodeRegistryVerdict.OrdinalOccupied => Answered(Occupied(request, value)),

            // The written value would take a registration its batch deferred, or one a larger value refused, as a value
            // which shrank an entry would: its outcome is unknown, and the silo registers again.
            NodeRegistryVerdict.Applies or NodeRegistryVerdict.Deferred => NotWritten("registration"),
            NodeRegistryVerdict.NotRegistered or NodeRegistryVerdict.ObjectLedgersNotConfigured or NodeRegistryVerdict.DrainAllSuperseded =>
                throw new UnreachableException("A registration is never decided as a retirement or drain-all change."),
            _ => throw new UnreachableException(),
        };

        private static GroupCommitAnswer<NodeRegistryOutcome> Answer(NodeRetirementRequest retirement, NodeRegistryVerdict verdict, NodeRegistryValue value) => verdict switch
        {
            NodeRegistryVerdict.GoalPresent => Answered(new NodeRetirementOutcome.Retired(value.Find(retirement.Node)!.RetiredRevision)),
            NodeRegistryVerdict.NotRegistered => Answered(NodeRetirementOutcome.NotRegistered.Instance),

            // The written value would take a retirement its batch deferred: its outcome is unknown, and its caller retires
            // again.
            NodeRegistryVerdict.Applies or NodeRegistryVerdict.Deferred => NotWritten("retirement"),
            NodeRegistryVerdict.Stale or NodeRegistryVerdict.WrongArchiveStore or NodeRegistryVerdict.WrongObjectLedgerStore
                or NodeRegistryVerdict.TooLarge or NodeRegistryVerdict.OrdinalOccupied
                or NodeRegistryVerdict.ObjectLedgersNotConfigured or NodeRegistryVerdict.DrainAllSuperseded =>
                throw new UnreachableException("A retirement is never decided as a registration or drain-all change."),
            _ => throw new UnreachableException(),
        };

        private static GroupCommitAnswer<NodeRegistryOutcome> AnswerStartDrainAll(NodeRegistryVerdict verdict, NodeRegistryValue value) => verdict switch
        {
            NodeRegistryVerdict.GoalPresent => Answered(new NodeDrainAllStartOutcome.Started(value.DrainAllRevision)),
            NodeRegistryVerdict.ObjectLedgersNotConfigured => Answered(NodeDrainAllStartOutcome.ObjectLedgersNotConfigured.Instance),
            NodeRegistryVerdict.Applies or NodeRegistryVerdict.Deferred => NotWritten("drain-all start"),
            NodeRegistryVerdict.Stale or NodeRegistryVerdict.WrongArchiveStore or NodeRegistryVerdict.WrongObjectLedgerStore
                or NodeRegistryVerdict.TooLarge or NodeRegistryVerdict.OrdinalOccupied
                or NodeRegistryVerdict.NotRegistered or NodeRegistryVerdict.DrainAllSuperseded =>
                throw new UnreachableException("A drain-all start is never decided as another change kind."),
            _ => throw new UnreachableException(),
        };

        private static GroupCommitAnswer<NodeRegistryOutcome> AnswerCancelDrainAll(NodeRegistryVerdict verdict) => verdict switch
        {
            NodeRegistryVerdict.GoalPresent => Answered(NodeDrainAllCancelOutcome.NotActive.Instance),
            NodeRegistryVerdict.Applies or NodeRegistryVerdict.Deferred => NotWritten("drain-all cancellation"),
            NodeRegistryVerdict.Stale or NodeRegistryVerdict.WrongArchiveStore or NodeRegistryVerdict.WrongObjectLedgerStore
                or NodeRegistryVerdict.TooLarge or NodeRegistryVerdict.OrdinalOccupied
                or NodeRegistryVerdict.NotRegistered or NodeRegistryVerdict.ObjectLedgersNotConfigured or NodeRegistryVerdict.DrainAllSuperseded =>
                throw new UnreachableException("A drain-all cancellation is never decided as another change kind."),
            _ => throw new UnreachableException(),
        };

        private static GroupCommitAnswer<NodeRegistryOutcome> AnswerCompleteDrainAll(NodeRegistryVerdict verdict) => verdict switch
        {
            NodeRegistryVerdict.DrainAllSuperseded => Answered(NodeDrainAllCompleteOutcome.Superseded.Instance),
            NodeRegistryVerdict.Applies or NodeRegistryVerdict.Deferred => NotWritten("drain-all completion"),
            NodeRegistryVerdict.GoalPresent or NodeRegistryVerdict.Stale or NodeRegistryVerdict.WrongArchiveStore or NodeRegistryVerdict.WrongObjectLedgerStore
                or NodeRegistryVerdict.TooLarge or NodeRegistryVerdict.OrdinalOccupied
                or NodeRegistryVerdict.NotRegistered or NodeRegistryVerdict.ObjectLedgersNotConfigured =>
                throw new UnreachableException("A drain-all completion is never decided as another change kind."),
            _ => throw new UnreachableException(),
        };

        private static GroupCommitAnswer<NodeRegistryOutcome> NotWritten(string kind) =>
            new GroupCommitAnswer<NodeRegistryOutcome>.Failed(new NodeRegistryUnavailableException($"The {kind} was not written in its batch; its outcome is unknown."));

        /// <summary>
        /// Returns the <see cref="NodeRegistrationOutcome.OrdinalOccupied"/> answer <paramref name="value"/> gives
        /// <paramref name="request"/>: the occupant of its ordinal, and the registering node's own entry, which, when
        /// present, is active, since a retired one holds no ordinal and meets no occupant.
        /// </summary>
        private static NodeRegistrationOutcome.OrdinalOccupied Occupied(NodeRegistrationRequest request, NodeRegistryValue value)
        {
            var own = value.Find(request.Entry.Node);
            return NodeRegistryRules.FindOccupant(value.Nodes, request.Entry) is { Ordinal: { } ordinal } occupant
                ? new(ordinal, occupant.Node, own is not null, own?.Ordinal)
                : throw new UnreachableException("The verdict OrdinalOccupied comes from an active entry at the registration's ordinal in the same value.");
        }
    }
}

/// <summary>A node registry change's answer: a registration's, a retirement's, or a drain-all transition's (orleans.md §5.3, §5.14).</summary>
internal readonly union NodeRegistryOutcome(
    NodeRegistrationOutcome,
    NodeRetirementOutcome,
    NodeDrainAllStartOutcome,
    NodeDrainAllCancelOutcome,
    NodeDrainAllCompleteOutcome);
