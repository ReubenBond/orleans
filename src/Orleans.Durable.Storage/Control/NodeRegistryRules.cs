using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Objects;
using Orleans.Runtime;

namespace Orleans.Durable.Storage.Control;

/// <summary>
/// One silo's registration: the entry it would hold, active, the archive store it is configured with, the node, if any,
/// whose active entry at the registration's ordinal the operator intends it to replace, and the object-ledger store ID it
/// is configured with, if any (orleans.md §5.3, §5.14).
/// </summary>
/// <param name="Entry">The entry, active.</param>
/// <param name="Store">The silo's archive store.</param>
/// <param name="NodeToReplace">
/// The node the operator names for replacement (<see cref="DurableStorageOptions.NodeToReplace"/>), or
/// <see langword="null"/>. It matters only when that node's entry is the active one at the registration's ordinal.
/// </param>
/// <param name="ObjectLedgerStore">The silo's object-ledger store ID, or <see langword="null"/> when none is configured.</param>
internal sealed record NodeRegistrationRequest(
    NodeRegistryEntry Entry,
    ArchiveStoreIdentity Store,
    NodeId? NodeToReplace,
    ObjectLedgerStoreId? ObjectLedgerStore = null);

/// <summary>An operator's retirement of one node, by its identity (orleans.md §5.3).</summary>
/// <param name="Node">The node to retire.</param>
internal sealed record NodeRetirementRequest(NodeId Node);

/// <summary>Starts a planned whole-cluster drain off node-backed ledgers in the node registry (orleans.md §5.14).</summary>
internal sealed record NodeDrainAllStartRequest
{
    private NodeDrainAllStartRequest()
    {
    }

    /// <summary>Gets the one request.</summary>
    public static NodeDrainAllStartRequest Instance { get; } = new();
}

/// <summary>Cancels an active whole-cluster drain off node-backed ledgers in the node registry (orleans.md §5.14).</summary>
internal sealed record NodeDrainAllCancelRequest
{
    private NodeDrainAllCancelRequest()
    {
    }

    /// <summary>Gets the one request.</summary>
    public static NodeDrainAllCancelRequest Instance { get; } = new();
}

/// <summary>
/// Atomically completes drain-all Phase 2 in the node registry (orleans.md §5.14): retires every active diskful node at
/// the write's revision and resets <c>DrainAllRevision</c> to <c>0</c>, only while the registry's <c>DrainAllRevision</c>
/// still equals <paramref name="ExpectedDrainAllRevision"/>.
/// </summary>
/// <param name="ExpectedDrainAllRevision">The active drain-all episode revision being completed; must be positive.</param>
internal sealed record NodeDrainAllCompleteRequest(ulong ExpectedDrainAllRevision);

/// <summary>
/// A change the node registry committer folds: a silo's registration, a node's retirement, or a drain-all transition
/// (orleans.md §5.3, §5.14).
/// </summary>
internal readonly union NodeRegistryChange(
    NodeRegistrationRequest,
    NodeRetirementRequest,
    NodeDrainAllStartRequest,
    NodeDrainAllCancelRequest,
    NodeDrainAllCompleteRequest) : IEquatable<NodeRegistryChange>
{
    /// <inheritdoc/>
    public bool Equals(NodeRegistryChange other) => Equals(Value, other.Value);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is NodeRegistryChange other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => Value?.GetHashCode() ?? 0;
}

/// <summary>How a registry change fares against one registry value (orleans.md §5.3, §5.14).</summary>
internal enum NodeRegistryVerdict
{
    /// <summary>
    /// The value holds the change's goal: for a registration, the node's entry at the silo's address and generation, active
    /// or retired; for a retirement, the node's entry retired; for starting drain-all, <c>DrainAllRevision</c> non-zero;
    /// for cancelling drain-all, <c>DrainAllRevision</c> zero.
    /// </summary>
    GoalPresent,

    /// <summary>
    /// The commit rule holds and the goal is absent: the change applies to produce the next registry value.
    /// </summary>
    Applies,

    /// <summary>The node's entry is at a generation at or above the silo's at another address.</summary>
    Stale,

    /// <summary>The value records another archive store.</summary>
    WrongArchiveStore,

    /// <summary>The value records another object-ledger store ID (orleans.md §5.14).</summary>
    WrongObjectLedgerStore,

    /// <summary>
    /// The registration would make the value's reserved length, with every entry at its longest, exceed the register's
    /// value limit.
    /// </summary>
    TooLarge,

    /// <summary>
    /// The registration's ordinal belongs to another node's active entry, which the registration does not name as the node
    /// it replaces: taking it would retire that node without the operator's intent.
    /// </summary>
    OrdinalOccupied,

    /// <summary>
    /// The change applies, but not in this batch's write: taking it after the batch's earlier changes would make the write
    /// more than one OR-I2 step from the value read, as when a batch registers one node twice at different ordinals. It is
    /// answered as unknown, and its caller submits it again.
    /// </summary>
    Deferred,

    /// <summary>The value holds no entry of the node a retirement names.</summary>
    NotRegistered,

    /// <summary>The value records no object-ledger store ID, so drain-all cannot start.</summary>
    ObjectLedgersNotConfigured,

    /// <summary>The value's <c>DrainAllRevision</c> does not match the completion request's expected episode.</summary>
    DrainAllSuperseded,
}

/// <summary>A registration which retired, by ordinal succession, the active entry at its ordinal.</summary>
/// <param name="Successor">The registered node.</param>
/// <param name="Predecessor">The node it retired.</param>
/// <param name="Ordinal">Their ordinal.</param>
internal sealed record NodeRegistrySuccession(NodeId Successor, NodeId Predecessor, int Ordinal);

/// <summary>
/// The node registry's commit rules, its fold, and its invariants (orleans.md §5.3, OR-I2, OR-I20): the one definition the
/// committer decides, writes, and checks by.
/// </summary>
/// <remarks>
/// <para>
/// A registration's <see cref="NodeRegistryVerdict.Stale"/> or <see cref="NodeRegistryVerdict.WrongArchiveStore"/> rule,
/// once failed, stays failed: entries only advance, and the recorded archive store never changes, so either verdict holds
/// against every later value (conventions.md §6). <see cref="NodeRegistryVerdict.TooLarge"/>,
/// <see cref="NodeRegistryVerdict.OrdinalOccupied"/>, and a retirement's <see cref="NodeRegistryVerdict.NotRegistered"/>
/// are decided against the value at hand only, since an active occupant may retire and a node may register; a change any
/// of them refuses is never carried in a write.
/// </para>
/// <para>
/// A retired entry stays retired at its revision: a registration of its node advances its silo and generation, as its
/// silo restarts over the same disk, and leaves it retired, so the silo serves its replicas until its retirement gate
/// opens and is never active again (orleans.md §5.3). A retired entry holds no ordinal: it is no occupant, and its
/// registration retires none.
/// </para>
/// <para>
/// A registration applies only while the value it leaves fits the register's value limit with every entry at its
/// longest (<see cref="ReservedLength"/>): at the greatest generation and the longest endpoint a silo address holds, and
/// an active entry at the greatest ordinal, with a failure domain of <see cref="NodeRegistryEntry.MaxFailureDomainBytes"/>,
/// retired at the greatest revision. Every value the committer writes therefore has room for each of its retirements,
/// whose rule holds whenever the value holds the node, and for a restart of each of its nodes, which a retired entry
/// takes keeping its ordinal and failure domain; only a new identity is refused.
/// </para>
/// </remarks>
internal static class NodeRegistryRules
{
    // The longest endpoint text a silo address holds: IPv6 with every group four digits, no scope ID (CLNR), and the
    // greatest port.
    private static readonly IPEndPoint LongestEndpoint = new(IPAddress.Parse("ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff"), IPEndPoint.MaxPort);

    // The longest failure domain an active entry may take at its restart.
    private static readonly string LongestFailureDomain = new('x', NodeRegistryEntry.MaxFailureDomainBytes);

    // An object-ledger store ID reserved in the value's worst-case encoding so a cluster which first registers without one
    // still has room when a later registration records it.
    private static readonly ObjectLedgerStoreId ReservedObjectLedgerStore = new(UInt128.MaxValue);

    /// <summary>Decides <paramref name="change"/> against <paramref name="value"/>, within <paramref name="maxValueLength"/>.</summary>
    public static NodeRegistryVerdict Decide(NodeRegistryValue value, NodeRegistryChange change, int maxValueLength) =>
        Apply([.. value.Nodes], value.ArchiveStore, value.ObjectLedgerStore, value.DrainAllRevision, change, checked(value.Revision + 1), maxValueLength, out _, out _, out _);

    /// <summary>
    /// Returns the length of <paramref name="value"/>'s encoding with every entry at its longest, at the greatest revision
    /// of the value: each silo at the greatest generation and the longest endpoint; each active entry at the greatest
    /// ordinal, with the longest failure domain, diskless flag, and retired at the greatest revision; each retired entry
    /// with its own ordinal, failure domain, diskless flag, and revision, which its restarts keep; an object-ledger store
    /// ID reserved when not yet recorded; and <c>DrainAllRevision</c> reserved at <see cref="ulong.MaxValue"/>. It is the
    /// most the value's encoding can take once its retirements, drain-all transitions, and node restarts are written, which
    /// a registration keeps within the register's value limit, so those writes always fit.
    /// </summary>
    public static int ReservedLength(NodeRegistryValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var longest = SiloAddress.New(LongestEndpoint, int.MaxValue);
        return NodeRegistryCodec.Encode(new NodeRegistryValue(
            ulong.MaxValue,
            [.. value.Nodes.Select(entry => entry.IsRetired
                ? entry.WithSilo(longest)
                : new NodeRegistryEntry(entry.Node, longest, entry.Diskless ? null : int.MaxValue, LongestFailureDomain, ulong.MaxValue, entry.Diskless))],
            value.ArchiveStore,
            value.ObjectLedgerStore ?? ReservedObjectLedgerStore,
            drainAllRevision: ulong.MaxValue)).Length;
    }

    /// <summary>
    /// Folds <paramref name="changes"/> over <paramref name="value"/> in order, each decided against the result of those
    /// before it, and returns each verdict, the ordinal successions the fold made, and, when any change applied, the value
    /// the batch's write holds, at the next revision; an ordinal succession, a retirement, and drain-all completion retire
    /// their nodes at that revision. A change whose taking would make that value break <see cref="Violation"/> over
    /// <paramref name="value"/>, with the changes the batch carries, is <see cref="NodeRegistryVerdict.Deferred"/>, so the
    /// write is one OR-I2 step.
    /// </summary>
    public static (NodeRegistryValue? Written, NodeRegistryVerdict[] Verdicts, IReadOnlyList<NodeRegistrySuccession> Successions) Fold(
        NodeRegistryValue value,
        IReadOnlyList<NodeRegistryChange> changes,
        int maxValueLength)
    {
        var revision = checked(value.Revision + 1);
        List<NodeRegistryEntry> nodes = [.. value.Nodes];
        var store = value.ArchiveStore;
        var objectStore = value.ObjectLedgerStore;
        var drainAllRevision = value.DrainAllRevision;
        var verdicts = new NodeRegistryVerdict[changes.Count];
        var successions = new List<NodeRegistrySuccession>();
        var carried = new List<NodeRegistryChange>();
        for (var index = 0; index < changes.Count; index++)
        {
            var change = changes[index];
            verdicts[index] = Apply(nodes, store, objectStore, drainAllRevision, change, revision, maxValueLength, out var next, out var nextDrainAllRevision, out var succession);
            if (next is null)
            {
                continue;
            }

            var recorded = store ?? change switch
            {
                NodeRegistrationRequest request => (ArchiveStoreIdentity?)request.Store,
                NodeRetirementRequest or NodeDrainAllStartRequest or NodeDrainAllCancelRequest or NodeDrainAllCompleteRequest => null,
                null => throw new UnreachableException("conventions.md §15: a change holds a case."),
            };
            var recordedObjectStore = objectStore ?? change switch
            {
                NodeRegistrationRequest request => request.ObjectLedgerStore,
                NodeRetirementRequest or NodeDrainAllStartRequest or NodeDrainAllCancelRequest or NodeDrainAllCompleteRequest => null,
                null => throw new UnreachableException("conventions.md §15: a change holds a case."),
            };

            // ApplyRegistration already verified ReservedLength; retirements and drain-all changes never grow past the
            // reserved ceiling, so the inner per-item check skips re-encoding CLNR on every change.
            if (carried.Count > 0 && CheckViolation(value, new NodeRegistryValue(revision, [.. next], recorded, recordedObjectStore, nextDrainAllRevision), [.. carried, change], maxValueLength, checkReservedLength: false) is not null)
            {
                verdicts[index] = NodeRegistryVerdict.Deferred;
                continue;
            }

            carried.Add(change);
            nodes = next;
            store = recorded;
            objectStore = recordedObjectStore;
            drainAllRevision = nextDrainAllRevision;
            if (succession is not null)
            {
                successions.Add(succession);
            }
        }

        return (carried.Count > 0 ? new NodeRegistryValue(revision, [.. nodes], store, objectStore, drainAllRevision) : null, verdicts, successions);
    }

    /// <summary>
    /// Returns the OR-I2 or OR-I20 rule <paramref name="after"/>, written over <paramref name="before"/>, breaks, or
    /// <see langword="null"/>: the revision advances by one; the recorded archive store and object-ledger store, once set,
    /// stay; <c>DrainAllRevision</c> changes only as <paramref name="carried"/> directs; every entry stays: an active one
    /// unchanged, advanced to a strictly greater generation, or retired at this write's revision, and a retired one
    /// unchanged or advanced to a strictly greater generation, retired at its revision; every new entry is active or retired
    /// at this write's revision; every entry retired at this write is authorized by a retirement, a drain-all completion (for
    /// an active diskful entry in <paramref name="before"/>), or a registration naming it for replacement whose node holds
    /// the entry's ordinal active in <paramref name="after"/>; at most one active entry holds each ordinal; and the value's
    /// reserved length fits <paramref name="maxValueLength"/>.
    /// </summary>
    public static string? Violation(NodeRegistryValue before, NodeRegistryValue after, IReadOnlyList<NodeRegistryChange> carried, int maxValueLength) =>
        CheckViolation(before, after, carried, maxValueLength, checkReservedLength: true);

    private static string? CheckViolation(NodeRegistryValue before, NodeRegistryValue after, IReadOnlyList<NodeRegistryChange> carried, int maxValueLength, bool checkReservedLength)
    {
        if (after.Revision != before.Revision + 1)
        {
            return string.Create(CultureInfo.InvariantCulture, $"OR-I2: revision {after.Revision} does not follow {before.Revision}.");
        }

        if (checkReservedLength && ReservedLength(after) is var reserved && reserved > maxValueLength)
        {
            return string.Create(CultureInfo.InvariantCulture, $"the register's value limit: revision {after.Revision} reserves {reserved} bytes, beyond {maxValueLength}.");
        }

        if (before.ArchiveStore is { } recorded && recorded != after.ArchiveStore)
        {
            return "OR-I20: the recorded archive store changed.";
        }

        if (before.ObjectLedgerStore is { } recordedObjectStore && recordedObjectStore != after.ObjectLedgerStore)
        {
            return "OL-I1: the recorded object-ledger store changed.";
        }

        HashSet<NodeId>? retiredByRequest = null;
        Dictionary<NodeId, List<NodeId>>? replacedBy = null;
        var hasStartDrainAll = false;
        var hasCancelDrainAll = false;
        ulong? completeDrainAllExpected = null;
        foreach (var change in carried)
        {
            if (change.Value is NodeRetirementRequest retirement)
            {
                _ = (retiredByRequest ??= []).Add(retirement.Node);
            }
            else if (change.Value is NodeRegistrationRequest request)
            {
                if (request.NodeToReplace is { } target)
                {
                    if (!(replacedBy ??= []).TryGetValue(target, out var successors))
                    {
                        replacedBy[target] = successors = [];
                    }

                    successors.Add(request.Entry.Node);
                }
            }
            else if (change.Value is NodeDrainAllStartRequest)
            {
                hasStartDrainAll = true;
            }
            else if (change.Value is NodeDrainAllCancelRequest)
            {
                hasCancelDrainAll = true;
            }
            else if (change.Value is NodeDrainAllCompleteRequest complete)
            {
                if (completeDrainAllExpected is { } other && other != complete.ExpectedDrainAllRevision)
                {
                    return "OR-I2: a single write cannot complete two different drain-all revisions.";
                }

                completeDrainAllExpected = complete.ExpectedDrainAllRevision;
            }
            else
            {
                throw new UnreachableException("conventions.md §15: a change holds a case.");
            }
        }

        if (before.DrainAllRevision == 0)
        {
            if (hasCancelDrainAll || completeDrainAllExpected is not null)
            {
                return "OR-I2: drain-all cannot be cancelled or completed when it was not active.";
            }

            if (hasStartDrainAll)
            {
                if (after.DrainAllRevision != after.Revision || after.ObjectLedgerStore is null)
                {
                    return "OR-I2: starting drain-all sets DrainAllRevision to the write's revision with an object-ledger store recorded.";
                }
            }
            else if (after.DrainAllRevision != 0)
            {
                return "OR-I2: DrainAllRevision became non-zero without a drain-all start request.";
            }
        }
        else
        {
            if (hasStartDrainAll)
            {
                return "OR-I2: drain-all cannot start when it is already active.";
            }

            if (hasCancelDrainAll && completeDrainAllExpected is not null)
            {
                return "OR-I2: a single write cannot both cancel and complete drain-all.";
            }

            if (hasCancelDrainAll)
            {
                if (after.DrainAllRevision != 0)
                {
                    return "OR-I2: cancelling drain-all clears DrainAllRevision to 0.";
                }
            }
            else if (completeDrainAllExpected is { } expected)
            {
                if (expected != before.DrainAllRevision || after.DrainAllRevision != 0)
                {
                    return "OR-I2: completing drain-all requires matching the active DrainAllRevision and clears it to 0.";
                }
            }
            else if (after.DrainAllRevision != before.DrainAllRevision)
            {
                return "OR-I2: DrainAllRevision changed without a drain-all cancel or complete request.";
            }
        }

        foreach (var old in before.Nodes)
        {
            if (after.Find(old.Node) is not { } next)
            {
                return string.Create(CultureInfo.InvariantCulture, $"OR-I2: node {old.Node}'s entry left the registry.");
            }

            if (next != old && !Follows(old, next, after.Revision))
            {
                return string.Create(CultureInfo.InvariantCulture, $"OR-I2: node {old.Node}'s entry neither advanced to a greater generation, a retired entry keeping its other fields, nor retired at revision {after.Revision}.");
            }

            if (completeDrainAllExpected is not null && !old.IsRetired && !old.Diskless && next.RetiredRevision != after.Revision)
            {
                return string.Create(CultureInfo.InvariantCulture, $"OR-I2: drain-all completion did not retire active diskful node {old.Node} at revision {after.Revision}.");
            }
        }

        var active = new HashSet<int>();
        foreach (var entry in after.Nodes)
        {
            var prior = before.Find(entry.Node);
            if (prior is null && entry.IsRetired && entry.RetiredRevision != after.Revision)
            {
                return string.Create(CultureInfo.InvariantCulture, $"OR-I2: node {entry.Node} joined retired at revision {entry.RetiredRevision}, not this write's.");
            }

            if (entry.RetiredRevision == after.Revision
                && !IsAuthorizedRetirement(entry, prior, after, retiredByRequest, replacedBy, completeDrainAllExpected is not null))
            {
                return string.Create(CultureInfo.InvariantCulture, $"OR-I2: node {entry.Node} was retired at revision {after.Revision} by no retirement, drain-all completion, or replacement naming it.");
            }

            if (!entry.IsRetired && entry.Ordinal is { } held && !active.Add(held))
            {
                return string.Create(CultureInfo.InvariantCulture, $"OR-I2: ordinal {held} holds two active entries.");
            }
        }

        return null;
    }

    /// <summary>
    /// Returns the active entry of another node than <paramref name="entry"/>'s at <paramref name="entry"/>'s ordinal, the
    /// entry its registration's ordinal succession would retire, or <see langword="null"/> when it has no ordinal or the
    /// ordinal is free or retired. At most one active entry holds each ordinal (OR-I2).
    /// </summary>
    public static NodeRegistryEntry? FindOccupant(IReadOnlyList<NodeRegistryEntry> nodes, NodeRegistryEntry entry)
    {
        if (entry.Ordinal is not { } ordinal)
        {
            return null;
        }

        foreach (var candidate in nodes)
        {
            if (!candidate.IsRetired && candidate.Ordinal == ordinal && candidate.Node != entry.Node)
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Returns whether <paramref name="retired"/>, retired at <paramref name="after"/>'s revision, is authorized by an
    /// explicit retirement, by drain-all completion (when it was an active diskful node in the prior value), or by a
    /// registration naming it for replacement whose node holds its ordinal active in <paramref name="after"/>.
    /// </summary>
    private static bool IsAuthorizedRetirement(
        NodeRegistryEntry retired,
        NodeRegistryEntry? prior,
        NodeRegistryValue after,
        HashSet<NodeId>? retiredByRequest,
        Dictionary<NodeId, List<NodeId>>? replacedBy,
        bool completingDrainAll)
    {
        if (retiredByRequest?.Contains(retired.Node) == true)
        {
            return true;
        }

        if (completingDrainAll && prior is { IsRetired: false, Diskless: false })
        {
            return true;
        }

        if (retired.Ordinal is { } ordinal && replacedBy is not null && replacedBy.TryGetValue(retired.Node, out var successors))
        {
            foreach (var successorId in successors)
            {
                if (after.Find(successorId) is { IsRetired: false } successor && successor.Ordinal == ordinal)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Returns whether <paramref name="next"/> follows <paramref name="old"/>, which it differs from, at
    /// <paramref name="revision"/>: an active entry advanced to a strictly greater generation, retired at the revision, or
    /// both; a retired entry advanced to a strictly greater generation, its other fields kept.
    /// </summary>
    private static bool Follows(NodeRegistryEntry old, NodeRegistryEntry next, ulong revision) =>
        next.Diskless == old.Diskless
        && (old.IsRetired
            ? next == old.WithSilo(next.Silo) && next.Silo.Generation > old.Silo.Generation
            : next.IsRetired
                ? next.RetiredRevision == revision && (next == old.Retire(revision) || next.Silo.Generation > old.Silo.Generation)
                : next.Silo.Generation > old.Silo.Generation);

    /// <summary>
    /// Decides <paramref name="change"/> against <paramref name="nodes"/>, <paramref name="store"/>,
    /// <paramref name="objectStore"/>, and <paramref name="drainAllRevision"/>, and when it applies, gives the entries it
    /// leaves, <paramref name="next"/>, the next <paramref name="nextDrainAllRevision"/>, and the succession it makes at
    /// <paramref name="revision"/>, if any; <paramref name="next"/> is <see langword="null"/> otherwise.
    /// </summary>
    private static NodeRegistryVerdict Apply(
        List<NodeRegistryEntry> nodes,
        ArchiveStoreIdentity? store,
        ObjectLedgerStoreId? objectStore,
        ulong drainAllRevision,
        NodeRegistryChange change,
        ulong revision,
        int maxValueLength,
        out List<NodeRegistryEntry>? next,
        out ulong nextDrainAllRevision,
        out NodeRegistrySuccession? succession)
    {
        nextDrainAllRevision = drainAllRevision;
        return change switch
        {
            NodeRegistrationRequest request => ApplyRegistration(nodes, store, objectStore, request, revision, maxValueLength, out next, out succession),
            NodeRetirementRequest retirement => ApplyRetirement(nodes, store, objectStore, drainAllRevision, retirement, revision, maxValueLength, out next, out succession),
            NodeDrainAllStartRequest => ApplyStartDrainAll(nodes, store, objectStore, drainAllRevision, revision, maxValueLength, out next, out nextDrainAllRevision, out succession),
            NodeDrainAllCancelRequest => ApplyCancelDrainAll(nodes, drainAllRevision, out next, out nextDrainAllRevision, out succession),
            NodeDrainAllCompleteRequest complete => ApplyCompleteDrainAll(nodes, store, objectStore, drainAllRevision, complete, revision, maxValueLength, out next, out nextDrainAllRevision, out succession),
            null => throw new UnreachableException("conventions.md §15: a change holds a case."),
        };
    }

    private static NodeRegistryVerdict ApplyRetirement(
        List<NodeRegistryEntry> nodes,
        ArchiveStoreIdentity? store,
        ObjectLedgerStoreId? objectStore,
        ulong drainAllRevision,
        NodeRetirementRequest retirement,
        ulong revision,
        int maxValueLength,
        out List<NodeRegistryEntry>? next,
        out NodeRegistrySuccession? succession)
    {
        next = null;
        succession = null;
        var position = NodeRegistryValue.IndexOf(CollectionsMarshal.AsSpan(nodes), retirement.Node);
        if (position < 0)
        {
            return NodeRegistryVerdict.NotRegistered;
        }

        if (nodes[position].IsRetired)
        {
            return NodeRegistryVerdict.GoalPresent;
        }

        // Every registration kept room for this retirement: retiring an entry never lengthens its reservation.
        next = [.. nodes];
        next[position] = nodes[position].Retire(revision);
        Debug.Assert(ReservedLength(new NodeRegistryValue(revision, [.. next], store, objectStore, drainAllRevision)) <= maxValueLength, "Every registration reserves room for its retirement.");
        return NodeRegistryVerdict.Applies;
    }

    private static NodeRegistryVerdict ApplyStartDrainAll(
        List<NodeRegistryEntry> nodes,
        ArchiveStoreIdentity? store,
        ObjectLedgerStoreId? objectStore,
        ulong drainAllRevision,
        ulong revision,
        int maxValueLength,
        out List<NodeRegistryEntry>? next,
        out ulong nextDrainAllRevision,
        out NodeRegistrySuccession? succession)
    {
        next = null;
        nextDrainAllRevision = drainAllRevision;
        succession = null;
        if (objectStore is null)
        {
            return NodeRegistryVerdict.ObjectLedgersNotConfigured;
        }

        if (drainAllRevision > 0)
        {
            return NodeRegistryVerdict.GoalPresent;
        }

        next = [.. nodes];
        nextDrainAllRevision = revision;
        Debug.Assert(ReservedLength(new NodeRegistryValue(revision, [.. next], store, objectStore, nextDrainAllRevision)) <= maxValueLength, "Every registration reserves room for DrainAllRevision.");
        return NodeRegistryVerdict.Applies;
    }

    private static NodeRegistryVerdict ApplyCancelDrainAll(
        List<NodeRegistryEntry> nodes,
        ulong drainAllRevision,
        out List<NodeRegistryEntry>? next,
        out ulong nextDrainAllRevision,
        out NodeRegistrySuccession? succession)
    {
        next = null;
        nextDrainAllRevision = drainAllRevision;
        succession = null;
        if (drainAllRevision == 0)
        {
            return NodeRegistryVerdict.GoalPresent;
        }

        next = [.. nodes];
        nextDrainAllRevision = 0;
        return NodeRegistryVerdict.Applies;
    }

    private static NodeRegistryVerdict ApplyCompleteDrainAll(
        List<NodeRegistryEntry> nodes,
        ArchiveStoreIdentity? store,
        ObjectLedgerStoreId? objectStore,
        ulong drainAllRevision,
        NodeDrainAllCompleteRequest complete,
        ulong revision,
        int maxValueLength,
        out List<NodeRegistryEntry>? next,
        out ulong nextDrainAllRevision,
        out NodeRegistrySuccession? succession)
    {
        next = null;
        nextDrainAllRevision = drainAllRevision;
        succession = null;
        if (drainAllRevision != complete.ExpectedDrainAllRevision)
        {
            return NodeRegistryVerdict.DrainAllSuperseded;
        }

        next = nodes.ConvertAll(entry => !entry.IsRetired && !entry.Diskless ? entry.Retire(revision) : entry);

        nextDrainAllRevision = 0;
        Debug.Assert(ReservedLength(new NodeRegistryValue(revision, [.. next], store, objectStore, nextDrainAllRevision)) <= maxValueLength, "Every registration reserves room for drain-all completion.");
        return NodeRegistryVerdict.Applies;
    }

    private static NodeRegistryVerdict ApplyRegistration(
        List<NodeRegistryEntry> nodes,
        ArchiveStoreIdentity? store,
        ObjectLedgerStoreId? objectStore,
        NodeRegistrationRequest request,
        ulong revision,
        int maxValueLength,
        out List<NodeRegistryEntry>? next,
        out NodeRegistrySuccession? succession)
    {
        next = null;
        succession = null;
        var position = NodeRegistryValue.IndexOf(CollectionsMarshal.AsSpan(nodes), request.Entry.Node);
        var verdict = DecideRegistration(store, objectStore, position >= 0 ? nodes[position] : null, request);
        if (verdict != NodeRegistryVerdict.Applies)
        {
            return verdict;
        }

        // A retired node's silo restarts retired, and a same-silo object-ledger-store adoption keeps the entry unchanged:
        // neither takes an ordinal, so neither succeeds an occupant and none refuses it.
        var current = position >= 0 ? nodes[position] : null;
        var keepCurrent = current is not null && (current.IsRetired || current.Silo.Equals(request.Entry.Silo));
        var occupant = keepCurrent ? null : FindOccupant(nodes, request.Entry);
        if (occupant is not null && occupant.Node != request.NodeToReplace)
        {
            return NodeRegistryVerdict.OrdinalOccupied;
        }

        List<NodeRegistryEntry> result = [.. nodes];
        if (current is not null)
        {
            result[position] = keepCurrent
                ? current.WithSilo(request.Entry.Silo)
                : request.Entry;
        }
        else
        {
            result.Insert(~position, request.Entry);
        }

        if (occupant is { Ordinal: { } ordinal })
        {
            result[NodeRegistryValue.IndexOf(CollectionsMarshal.AsSpan(result), occupant.Node)] = occupant.Retire(revision);
            succession = new NodeRegistrySuccession(request.Entry.Node, occupant.Node, ordinal);
        }

        if (ReservedLength(new NodeRegistryValue(revision, [.. result], store ?? request.Store, objectStore ?? request.ObjectLedgerStore)) > maxValueLength)
        {
            succession = null;
            return NodeRegistryVerdict.TooLarge;
        }

        next = result;
        return NodeRegistryVerdict.Applies;
    }

    private static NodeRegistryVerdict DecideRegistration(
        ArchiveStoreIdentity? store,
        ObjectLedgerStoreId? objectStore,
        NodeRegistryEntry? current,
        NodeRegistrationRequest request)
    {
        if (store is not null && store != request.Store)
        {
            return NodeRegistryVerdict.WrongArchiveStore;
        }

        if (objectStore is not null && request.ObjectLedgerStore is { } provided && objectStore != provided)
        {
            return NodeRegistryVerdict.WrongObjectLedgerStore;
        }

        if (current is null)
        {
            return NodeRegistryVerdict.Applies;
        }

        if (current.Diskless != request.Entry.Diskless)
        {
            return NodeRegistryVerdict.Stale;
        }

        if (current.Silo.Equals(request.Entry.Silo))
        {
            return objectStore is null && request.ObjectLedgerStore is not null
                ? NodeRegistryVerdict.Applies
                : NodeRegistryVerdict.GoalPresent;
        }

        return current.Silo.Generation >= request.Entry.Silo.Generation ? NodeRegistryVerdict.Stale : NodeRegistryVerdict.Applies;
    }
}
