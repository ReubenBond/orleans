using Orleans.Durable.Storage.Control;
using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Cluster.Control;
using Orleans.Durable.Storage.Engine.Ledgers.Objects;

namespace Orleans.Durable.Storage.Testing;

/// <summary>
/// One registration submitted to a node registry committer: the entry, archive store, and node named for replacement; the
/// committer it was submitted to, named as the register names that committer's writes; the register's revision and the
/// number of writes it had seen dispatched when the registration was submitted, read just before it, so never later; its
/// answer, an outcome or the exception which left it unknown; and the number of writes the register had seen dispatched,
/// and its revision, when the answer arrived.
/// </summary>
internal sealed record NodeRegistrySubmission(
    NodeRegistryEntry Entry,
    ArchiveStoreIdentity Store,
    Orleans.Durable.Storage.Engine.Ledgers.NodeId? NodeToReplace,
    int Committer,
    ulong SubmittedAtRevision,
    long SubmittedAfterDispatches,
    NodeRegistrationOutcome? Outcome,
    Exception? Failure = null,
    long AnsweredAfterDispatches = long.MaxValue,
    ulong AnsweredAtRevision = ulong.MaxValue,
    ObjectLedgerStoreId? ObjectLedgerStore = null);

/// <summary>
/// One retirement submitted to a node registry committer, recorded as <see cref="NodeRegistrySubmission"/> records a
/// registration: the node, the committer, the register's revision and dispatched writes at the submission, its answer, and
/// the dispatched writes and revision when the answer arrived.
/// </summary>
internal sealed record NodeRetirementSubmission(
    Orleans.Durable.Storage.Engine.Ledgers.NodeId Node,
    int Committer,
    ulong SubmittedAtRevision,
    long SubmittedAfterDispatches,
    NodeRetirementOutcome? Outcome,
    Exception? Failure = null,
    long AnsweredAfterDispatches = long.MaxValue,
    ulong AnsweredAtRevision = ulong.MaxValue);

/// <summary>One drain-all start submitted to a node registry committer (orleans.md §5.14).</summary>
internal sealed record NodeDrainAllStartSubmission(
    int Committer,
    ulong SubmittedAtRevision,
    long SubmittedAfterDispatches,
    NodeDrainAllStartOutcome? Outcome,
    Exception? Failure = null,
    long AnsweredAfterDispatches = long.MaxValue,
    ulong AnsweredAtRevision = ulong.MaxValue);

/// <summary>One drain-all cancel submitted to a node registry committer (orleans.md §5.14).</summary>
internal sealed record NodeDrainAllCancelSubmission(
    int Committer,
    ulong SubmittedAtRevision,
    long SubmittedAfterDispatches,
    NodeDrainAllCancelOutcome? Outcome,
    Exception? Failure = null,
    long AnsweredAfterDispatches = long.MaxValue,
    ulong AnsweredAtRevision = ulong.MaxValue);

/// <summary>One drain-all Phase 2 completion submitted to a node registry committer (orleans.md §5.14).</summary>
internal sealed record NodeDrainAllCompleteSubmission(
    ulong ExpectedDrainAllRevision,
    int Committer,
    ulong SubmittedAtRevision,
    long SubmittedAfterDispatches,
    NodeDrainAllCompleteOutcome? Outcome,
    Exception? Failure = null,
    long AnsweredAfterDispatches = long.MaxValue,
    ulong AnsweredAtRevision = ulong.MaxValue);

/// <summary>
/// The node registry committer's conformance oracle (orleans.md §5.3, §9; conventions.md §6): it keeps the register's
/// values, one per revision, with the dispatch order and the writer of the write which produced each, and judges each
/// answer against the values the register held between the registration's submission and its answer, by the commit rule
/// as §5.3 states it, and each step of the history against OR-I2 and OR-I20.
/// </summary>
/// <remarks>
/// <para>
/// <c>Registered(r)</c> holds the goal, the node's entry at the silo's address and generation, active or, for a retired
/// node, retired at its revision, at revision <c>r</c>; <c>Stale</c> finds the node's entry at a generation at or above the
/// silo's at another address; <c>WrongArchiveStore</c> finds another archive store recorded; <c>Refused</c> finds the
/// value too large to take the entry; and <c>OrdinalOccupied(o, n)</c> finds node <c>n</c>'s entry active at the
/// registration's ordinal <c>o</c>, which the registration does not name for replacement, and whether the registering node
/// has an active entry, and its ordinal; a retired node's registration meets no occupant. A registration applies only
/// while the value it leaves fits the limit with every entry at its longest: retired at the greatest revision, its silo at
/// the greatest generation and the longest IPv6 endpoint. A retirement's
/// <c>Retired(r)</c> finds the node's entry retired at <c>r</c>, and <c>NotRegistered</c> finds no entry of it. Each
/// revision an answer rests on is one the register held at or after
/// the submission and by the answer, produced by a write dispatched before the answer arrived, so an answer from a value
/// from before the submission, from a write which never landed or landed after the answer, or from one written after the
/// answer, fails.
/// </para>
/// <para>
/// A negative answer is revoked when a value produced after the submission, by a write the answering committer dispatched
/// before the answer arrived, holds the goal, and no later value the answer could rest on shows the answer: that write
/// carried the registration, and only a later write, such as one of the node's later generation, makes it stale. Every
/// entry retired at a revision must be named, by a retirement or for replacement by a registration, submitted to the
/// committer which wrote that revision before the write was dispatched and answered after it; a registration's node holds
/// the entry's ordinal active in that value. An exception leaves the outcome unknown and asserts nothing.
/// </para>
/// </remarks>
internal static class NodeRegistryHistoryOracle
{
    /// <summary>A registration's standing against one value, as §5.3 states the rule.</summary>
    public enum Standing
    {
        /// <summary>The value holds the goal.</summary>
        Goal,

        /// <summary>The rule holds, and the goal is absent.</summary>
        Applies,

        /// <summary>The node's entry is at a generation at or above the silo's at another address.</summary>
        Stale,

        /// <summary>The value records another archive store.</summary>
        WrongStore,

        /// <summary>The value records another object-ledger store ID.</summary>
        WrongObjectStore,

        /// <summary>Taking the entry would make the value too large for the register.</summary>
        TooLarge,

        /// <summary>Another node's entry is active at the registration's ordinal, and the registration does not name it.</summary>
        OrdinalOccupied,
    }

    /// <summary>Returns a description of each answer, and each step of the history, which breaks the registry's rules.</summary>
    /// <param name="history">The register's values: the absent register's, then each applied write's, in revision order.</param>
    /// <param name="dispatchedAt">
    /// For each value of <paramref name="history"/>, the number of writes dispatched before the write which produced it; −1
    /// for the absent register's.
    /// </param>
    /// <param name="writtenBy">
    /// For each value of <paramref name="history"/>, the committer whose write produced it; −1 for the absent register's.
    /// </param>
    /// <param name="submissions">The registrations and their answers.</param>
    /// <param name="maxValueLength">The register's value limit the committers ran under.</param>
    /// <param name="retirements">The retirements and their answers.</param>
    /// <param name="drainAllStarts">The drain-all starts and their answers.</param>
    /// <param name="drainAllCancels">The drain-all cancels and their answers.</param>
    /// <param name="drainAllCompletes">The drain-all completions and their answers.</param>
    public static IReadOnlyList<string> Violations(
        IReadOnlyList<NodeRegistryValue> history,
        IReadOnlyList<long> dispatchedAt,
        IReadOnlyList<int> writtenBy,
        IEnumerable<NodeRegistrySubmission> submissions,
        int maxValueLength,
        IEnumerable<NodeRetirementSubmission>? retirements = null,
        IEnumerable<NodeDrainAllStartSubmission>? drainAllStarts = null,
        IEnumerable<NodeDrainAllCancelSubmission>? drainAllCancels = null,
        IEnumerable<NodeDrainAllCompleteSubmission>? drainAllCompletes = null)
    {
        Assert.Equal(history.Count, dispatchedAt.Count);
        Assert.Equal(history.Count, writtenBy.Count);
        var violations = new List<string>();
        var all = submissions.ToArray();
        var retiring = (retirements ?? []).ToArray();
        var starts = (drainAllStarts ?? []).ToArray();
        var cancels = (drainAllCancels ?? []).ToArray();
        var completes = (drainAllCompletes ?? []).ToArray();
        for (var revision = 1; revision < history.Count; revision++)
        {
            var canStart = starts.Any(start => start.Committer == writtenBy[revision] && start.SubmittedAfterDispatches <= dispatchedAt[revision] && dispatchedAt[revision] < start.AnsweredAfterDispatches);
            var canCancel = cancels.Any(cancel => cancel.Committer == writtenBy[revision] && cancel.SubmittedAfterDispatches <= dispatchedAt[revision] && dispatchedAt[revision] < cancel.AnsweredAfterDispatches);
            var completing = Completing(dispatchedAt, writtenBy, completes, revision);
            if (Step(history[revision - 1], history[revision], Naming(dispatchedAt, writtenBy, all, revision), Retiring(dispatchedAt, writtenBy, retiring, revision), canStart, canCancel, completing) is { } broken)
            {
                violations.Add($"Revision {revision}: {broken}");
            }
        }

        foreach (var start in starts)
        {
            var deciding = Enumerable.Range(0, history.Count)
                .Where(revision => IsDeciding(history, dispatchedAt, start.SubmittedAtRevision, start.AnsweredAtRevision, start.AnsweredAfterDispatches, revision))
                .ToArray();
            var verdict = start.Outcome?.Value switch
            {
                null when start.Failure is null => "has neither an outcome nor an exception",
                null => null,
                NodeDrainAllStartOutcome.Started started when started.Revision == 0 || !deciding.Any(revision => history[revision].DrainAllRevision == started.Revision) =>
                    $"was answered Started({started.Revision}), which no value between its submission at {start.SubmittedAtRevision} and its answer shows",
                NodeDrainAllStartOutcome.ObjectLedgersNotConfigured when !deciding.Any(revision => history[revision].ObjectLedgerStore is null) =>
                    $"was answered ObjectLedgersNotConfigured, but every value between its submission at {start.SubmittedAtRevision} and its answer records an object-ledger store",
                _ => null,
            };
            if (verdict is not null)
            {
                violations.Add($"Drain-all start {verdict}.");
            }
        }

        foreach (var cancel in cancels)
        {
            var deciding = Enumerable.Range(0, history.Count)
                .Where(revision => IsDeciding(history, dispatchedAt, cancel.SubmittedAtRevision, cancel.AnsweredAtRevision, cancel.AnsweredAfterDispatches, revision))
                .ToArray();
            var verdict = cancel.Outcome?.Value switch
            {
                null when cancel.Failure is null => "has neither an outcome nor an exception",
                null => null,
                NodeDrainAllCancelOutcome.Cancelled cancelled when cancelled.Revision == 0 || cancelled.Revision > int.MaxValue
                    || !IsDeciding(history, dispatchedAt, cancel.SubmittedAtRevision, cancel.AnsweredAtRevision, cancel.AnsweredAfterDispatches, (int)cancelled.Revision)
                    || history[(int)cancelled.Revision - 1].DrainAllRevision == 0
                    || history[(int)cancelled.Revision].DrainAllRevision != 0 =>
                    $"was answered Cancelled({cancelled.Revision}), which did not clear an active DrainAllRevision between its submission at {cancel.SubmittedAtRevision} and its answer",
                NodeDrainAllCancelOutcome.NotActive when !deciding.Any(revision => history[revision].DrainAllRevision == 0) =>
                    $"was answered NotActive, but every value between its submission at {cancel.SubmittedAtRevision} and its answer holds DrainAllRevision > 0",
                _ => null,
            };
            if (verdict is not null)
            {
                violations.Add($"Drain-all cancel {verdict}.");
            }
        }

        foreach (var complete in completes)
        {
            var deciding = Enumerable.Range(0, history.Count)
                .Where(revision => IsDeciding(history, dispatchedAt, complete.SubmittedAtRevision, complete.AnsweredAtRevision, complete.AnsweredAfterDispatches, revision))
                .ToArray();
            var verdict = complete.Outcome?.Value switch
            {
                null when complete.Failure is null => "has neither an outcome nor an exception",
                null => null,
                NodeDrainAllCompleteOutcome.Completed completed when completed.Revision == 0 || completed.Revision > int.MaxValue
                    || !IsDeciding(history, dispatchedAt, complete.SubmittedAtRevision, complete.AnsweredAtRevision, complete.AnsweredAfterDispatches, (int)completed.Revision)
                    || history[(int)completed.Revision - 1].DrainAllRevision != complete.ExpectedDrainAllRevision
                    || history[(int)completed.Revision].DrainAllRevision != 0
                    || !history[(int)completed.Revision - 1].Nodes.Where(static entry => !entry.IsRetired && !entry.Diskless).All(entry => history[(int)completed.Revision].Find(entry.Node)?.RetiredRevision == completed.Revision) =>
                    $"was answered Completed({completed.Revision}) for expected {complete.ExpectedDrainAllRevision}, which did not atomically complete that episode and retire all active diskful nodes",
                NodeDrainAllCompleteOutcome.Superseded when !deciding.Any(revision => history[revision].DrainAllRevision != complete.ExpectedDrainAllRevision) =>
                    $"was answered Superseded for expected {complete.ExpectedDrainAllRevision}, but every value between its submission at {complete.SubmittedAtRevision} and its answer held that DrainAllRevision",
                _ => null,
            };
            if (verdict is not null)
            {
                violations.Add($"Drain-all complete({complete.ExpectedDrainAllRevision}) {verdict}.");
            }
        }

        foreach (var retirement in retiring)
        {
            var deciding = Enumerable.Range(0, history.Count)
                .Where(revision => IsDeciding(history, dispatchedAt, retirement.SubmittedAtRevision, retirement.AnsweredAtRevision, retirement.AnsweredAfterDispatches, revision))
                .ToArray();
            var verdict = retirement.Outcome?.Value switch
            {
                null when retirement.Failure is null => "has neither an outcome nor an exception",
                null => null,
                NodeRetirementOutcome.Retired retired when !deciding.Any(revision => history[revision].Find(retirement.Node)?.RetiredRevision is { } at && at != 0 && at == retired.Revision) =>
                    $"was answered retired at {retired.Revision}, which no value between its submission at {retirement.SubmittedAtRevision} and its answer shows",
                NodeRetirementOutcome.NotRegistered when !deciding.Any(revision => history[revision].Find(retirement.Node) is null) =>
                    $"was answered NotRegistered, but every value between its submission at {retirement.SubmittedAtRevision} and its answer holds the node",
                _ => null,
            };
            if (verdict is not null)
            {
                violations.Add($"The retirement of {retirement.Node} {verdict}.");
            }
        }

        foreach (var submission in all)
        {
            var name = $"{submission.Entry.Node} at {submission.Entry.Silo}";
            Standing At(int revision) => Decide(history[revision], submission.Entry, submission.Store, maxValueLength, submission.NodeToReplace, submission.ObjectLedgerStore);
            bool Shows(int revision, object negative) => negative switch
            {
                NodeRegistrationOutcome.OrdinalOccupied occupied => At(revision) == Standing.OrdinalOccupied && occupied == OccupiedAt(history[revision], submission.Entry),
                _ => At(revision) == Expected(negative),
            };
            bool Deciding(int revision) => IsDeciding(history, dispatchedAt, submission, revision);
            var deciding = Enumerable.Range(0, history.Count).Where(Deciding).ToArray();
            var carried = CarriedRevision(history, dispatchedAt, writtenBy, submission, maxValueLength);
            bool Revoked(object negative) => carried >= 0 && !deciding.Any(revision => revision > carried && Shows(revision, negative));
            switch (submission.Outcome?.Value)
            {
                case null when submission.Failure is null:
                    violations.Add($"{name} has neither an outcome nor an exception.");
                    break;
                case null:
                    break;
                case NodeRegistrationOutcome.Registered registered when registered.Revision > int.MaxValue || !Deciding((int)registered.Revision):
                    violations.Add($"{name} was registered at revision {registered.Revision}, which the register did not hold between its submission at {submission.SubmittedAtRevision} and its answer.");
                    break;
                case NodeRegistrationOutcome.Registered registered when At((int)registered.Revision) != Standing.Goal:
                    violations.Add($"{name} was registered at revision {registered.Revision}, against which it is {At((int)registered.Revision)}.");
                    break;
                case NodeRegistrationOutcome.Registered:
                    break;
                case var negative when !deciding.Any(revision => Shows(revision, negative)):
                    violations.Add($"{name} was answered {negative.GetType().Name}, but no value between its submission at {submission.SubmittedAtRevision} and its answer shows it.");
                    break;
                case var negative when Revoked(negative):
                    violations.Add($"{name} was answered {negative.GetType().Name}, but a write its committer dispatched before the answer registered it.");
                    break;
                default:
                    break;
            }
        }

        return violations;
    }

    /// <summary>
    /// Returns how many negative answers the revocation check of <see cref="Violations"/> had to judge: those for which a
    /// write their committer dispatched after the submission and before the answer held the goal.
    /// </summary>
    public static int RevocationCandidates(
        IReadOnlyList<NodeRegistryValue> history,
        IReadOnlyList<long> dispatchedAt,
        IReadOnlyList<int> writtenBy,
        IEnumerable<NodeRegistrySubmission> submissions,
        int maxValueLength) =>
        submissions.Count(submission =>
            submission.Outcome?.Value is NodeRegistrationOutcome.Stale or NodeRegistrationOutcome.WrongArchiveStore or NodeRegistrationOutcome.WrongObjectLedgerStore or NodeRegistrationOutcome.Refused or NodeRegistrationOutcome.OrdinalOccupied
            && CarriedRevision(history, dispatchedAt, writtenBy, submission, maxValueLength) >= 0);

    /// <summary>
    /// Returns how many entries the history retired, each named for replacement by a registration as
    /// <see cref="Violations"/> requires: the ordinal successions the registrations asked for.
    /// </summary>
    public static int NamedRetirements(
        IReadOnlyList<NodeRegistryValue> history,
        IReadOnlyList<long> dispatchedAt,
        IReadOnlyList<int> writtenBy,
        IEnumerable<NodeRegistrySubmission> submissions)
    {
        var all = submissions.ToArray();
        return Enumerable.Range(1, Math.Max(0, history.Count - 1)).Sum(revision =>
        {
            var naming = Naming(dispatchedAt, writtenBy, all, revision);
            return history[revision].Nodes.Count(entry => entry.RetiredRevision == (ulong)revision && IsNamed(entry, history[revision], naming));
        });
    }

    /// <summary>
    /// Returns how many retirements the history wrote, each named by a retirement submission as <see cref="Violations"/>
    /// requires.
    /// </summary>
    public static int OperatorRetirements(
        IReadOnlyList<NodeRegistryValue> history,
        IReadOnlyList<long> dispatchedAt,
        IReadOnlyList<int> writtenBy,
        IEnumerable<NodeRetirementSubmission> retirements)
    {
        var all = retirements.ToArray();
        return Enumerable.Range(1, Math.Max(0, history.Count - 1)).Sum(revision =>
        {
            var retiring = Retiring(dispatchedAt, writtenBy, all, revision);
            return history[revision].Nodes.Count(entry => entry.RetiredRevision == (ulong)revision && retiring.Contains(entry.Node));
        });
    }

    /// <summary>
    /// Decides a registration, which names <paramref name="nodeToReplace"/> for replacement, against a value as orleans.md
    /// §5.3 states the rule.
    /// </summary>
    public static Standing Decide(
        NodeRegistryValue value,
        NodeRegistryEntry entry,
        ArchiveStoreIdentity store,
        int maxValueLength,
        Orleans.Durable.Storage.Engine.Ledgers.NodeId? nodeToReplace,
        ObjectLedgerStoreId? objectLedgerStore = null)
    {
        if (value.ArchiveStore is { } recorded && recorded != store)
        {
            return Standing.WrongStore;
        }

        if (value.ObjectLedgerStore is { } recordedObjectStore && objectLedgerStore is { } provided && recordedObjectStore != provided)
        {
            return Standing.WrongObjectStore;
        }

        var current = value.Nodes.FirstOrDefault(candidate => candidate.Node == entry.Node);
        if (current is not null)
        {
            if (current.Diskless != entry.Diskless)
            {
                return Standing.Stale;
            }

            if (current.Silo.Equals(entry.Silo))
            {
                return value.ObjectLedgerStore is null && objectLedgerStore is not null ? Standing.Applies : Standing.Goal;
            }

            if (current.Silo.Generation >= entry.Silo.Generation)
            {
                return Standing.Stale;
            }
        }

        // A retired node's silo restarts retired at its revision, and a same-silo object-ledger-store adoption keeps
        // the entry unchanged: neither takes an ordinal, so neither meets or retires an occupant.
        var keepCurrent = current is not null && (current.IsRetired || current.Silo.Equals(entry.Silo));
        if (!keepCurrent && FindOccupant(value, entry) is { } occupant && occupant.Node != nodeToReplace)
        {
            return Standing.OrdinalOccupied;
        }

        // Taking the entry replaces the node's, retires every other active entry at its ordinal, and records the store.
        var revision = value.Revision + 1;
        var nodes = value.Nodes
            .Where(candidate => candidate.Node != entry.Node)
            .Select(candidate => !keepCurrent && !candidate.IsRetired && entry.Ordinal is { } ordinal && candidate.Ordinal == ordinal ? candidate.Retire(revision) : candidate)
            .Append(keepCurrent
                ? new NodeRegistryEntry(entry.Node, entry.Silo, current!.Ordinal, current.FailureDomain, current.RetiredRevision, current.Diskless)
                : entry)
            .OrderBy(candidate => candidate.Node, Comparer<Orleans.Durable.Storage.Engine.Ledgers.NodeId>.Create(NodeRegistryValue.CompareNodes))
            .ToArray();

        // The value taken must keep room for every retirement and every restart: each entry at its longest, an active one
        // at any ordinal (when disk-backed) and with any failure domain the format holds, a retired one with the fields
        // its restarts keep.
        var longest = global::Orleans.Runtime.SiloAddress.New(new System.Net.IPEndPoint(System.Net.IPAddress.Parse("ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff"), 65535), int.MaxValue);
        var domain = string.Concat(Enumerable.Repeat("z", 64));
        var reserved = new NodeRegistryValue(
            ulong.MaxValue,
            [.. nodes.Select(candidate => candidate.IsRetired
                ? new NodeRegistryEntry(candidate.Node, longest, candidate.Ordinal, candidate.FailureDomain, candidate.RetiredRevision, candidate.Diskless)
                : new NodeRegistryEntry(candidate.Node, longest, candidate.Diskless ? null : int.MaxValue, domain, ulong.MaxValue, candidate.Diskless))],
            value.ArchiveStore ?? store,
            value.ObjectLedgerStore ?? objectLedgerStore ?? new ObjectLedgerStoreId(UInt128.MaxValue),
            drainAllRevision: ulong.MaxValue);
        return NodeRegistryCodec.Encode(reserved).Length > maxValueLength ? Standing.TooLarge : Standing.Applies;
    }

    /// <summary>
    /// Returns the OR-I2 or OR-I20 rule the step from <paramref name="before"/> to <paramref name="after"/> breaks, or
    /// <see langword="null"/>, given the registrations which could have named a node for replacement in that write: the one
    /// OR-I2 step oracle, against which the committer's own check is held.
    /// </summary>
    public static string? Step(
        NodeRegistryValue before,
        NodeRegistryValue after,
        IReadOnlyList<(NodeRegistryEntry Entry, Orleans.Durable.Storage.Engine.Ledgers.NodeId? NodeToReplace)> naming,
        IReadOnlyCollection<Orleans.Durable.Storage.Engine.Ledgers.NodeId>? retiring = null,
        bool canStartDrainAll = false,
        bool canCancelDrainAll = false,
        IReadOnlyCollection<ulong>? completingDrainAll = null)
    {
        retiring ??= [];
        completingDrainAll ??= [];
        if (after.Revision != before.Revision + 1)
        {
            return $"the revision went from {before.Revision} to {after.Revision}.";
        }

        if (before.ArchiveStore is not null && before.ArchiveStore != after.ArchiveStore)
        {
            return "the recorded archive store changed.";
        }

        if (before.ObjectLedgerStore is not null && before.ObjectLedgerStore != after.ObjectLedgerStore)
        {
            return "the recorded object-ledger store changed.";
        }

        if (before.DrainAllRevision == 0)
        {
            if (after.DrainAllRevision != 0 && (!canStartDrainAll || after.DrainAllRevision != after.Revision || after.ObjectLedgerStore is null))
            {
                return $"DrainAllRevision became {after.DrainAllRevision} at revision {after.Revision} without an authorized start over a recorded object-ledger store.";
            }
        }
        else if (after.DrainAllRevision == 0)
        {
            if (!canCancelDrainAll && !completingDrainAll.Contains(before.DrainAllRevision))
            {
                return $"DrainAllRevision cleared from {before.DrainAllRevision} to 0 without a pending cancel or matching complete.";
            }
        }
        else if (after.DrainAllRevision != before.DrainAllRevision)
        {
            return $"DrainAllRevision changed from {before.DrainAllRevision} to {after.DrainAllRevision} while active.";
        }

        var completedDrainAll = before.DrainAllRevision > 0
            && after.DrainAllRevision == 0
            && completingDrainAll.Contains(before.DrainAllRevision);
        var allActiveDiskfulRetired = before.Nodes
            .Where(static entry => !entry.IsRetired && !entry.Diskless)
            .All(entry => after.Find(entry.Node)?.RetiredRevision == after.Revision);
        if (completedDrainAll && !canCancelDrainAll && !allActiveDiskfulRetired)
        {
            return $"DrainAllRevision {before.DrainAllRevision} completed without retiring every active diskful node at revision {after.Revision}.";
        }

        var excusedByCompleteDrainAll = completedDrainAll && allActiveDiskfulRetired;

        var olds = before.Nodes.ToDictionary(static entry => entry.Node);
        foreach (var old in before.Nodes)
        {
            var next = after.Nodes.FirstOrDefault(candidate => candidate.Node == old.Node);
            if (next is null)
            {
                return $"node {old.Node} left the registry.";
            }

            if (old.IsRetired && next != old && !(next.IsRetired && next.RetiredRevision == old.RetiredRevision && next.Ordinal == old.Ordinal && next.FailureDomain == old.FailureDomain && next.Diskless == old.Diskless && next.Silo.Generation > old.Silo.Generation))
            {
                return $"retired node {old.Node} changed from {old} to {next}, other than to a greater generation, its other fields kept.";
            }

            if (!old.IsRetired && next != old)
            {
                if (next.Diskless != old.Diskless)
                {
                    return $"node {old.Node} changed its diskless flag from {old.Diskless} to {next.Diskless}.";
                }

                var advanced = next.Silo.Generation > old.Silo.Generation;
                var retiredInPlace = next.IsRetired && next == old.Retire(after.Revision);
                if (next.IsRetired ? next.RetiredRevision != after.Revision || !(advanced || retiredInPlace) : !advanced)
                {
                    return $"node {old.Node} moved from {old} to {next}.";
                }
            }
        }

        foreach (var entry in after.Nodes)
        {
            if (!olds.ContainsKey(entry.Node) && entry.IsRetired && entry.RetiredRevision != after.Revision)
            {
                return $"node {entry.Node} joined retired at {entry.RetiredRevision}.";
            }

            if (entry.RetiredRevision == after.Revision
                && !retiring.Contains(entry.Node)
                && !IsNamed(entry, after, naming)
                && !(excusedByCompleteDrainAll && olds.TryGetValue(entry.Node, out var oldEntry) && !oldEntry.IsRetired && !oldEntry.Diskless))
            {
                return $"node {entry.Node} was retired at {after.Revision}, and no retirement, drain-all completion, or replacement registration named it.";
            }
        }

        var shared = after.Nodes.Where(static entry => !entry.IsRetired && entry.Ordinal is not null).GroupBy(static entry => entry.Ordinal).FirstOrDefault(static group => group.Count() > 1);
        return shared is null ? null : $"ordinal {shared.Key} has {shared.Count()} active nodes.";
    }

    /// <summary>
    /// Returns whether the value at <paramref name="revision"/> can decide <paramref name="submission"/>'s answer: the
    /// register held it at or after the submission and by the answer, and the write which produced it was dispatched
    /// before the answer arrived.
    /// </summary>
    private static bool IsDeciding(IReadOnlyList<NodeRegistryValue> history, IReadOnlyList<long> dispatchedAt, NodeRegistrySubmission submission, int revision) =>
        IsDeciding(history, dispatchedAt, submission.SubmittedAtRevision, submission.AnsweredAtRevision, submission.AnsweredAfterDispatches, revision);

    private static bool IsDeciding(IReadOnlyList<NodeRegistryValue> history, IReadOnlyList<long> dispatchedAt, ulong submittedAt, ulong answeredAt, long answeredAfterDispatches, int revision) =>
        revision >= (int)submittedAt && revision < history.Count && (ulong)revision <= answeredAt
        && dispatchedAt[revision] < answeredAfterDispatches;

    /// <summary>
    /// Returns the expected drain-all revisions whose completions could have completed in the write which produced
    /// <paramref name="revision"/>: those submitted to its writer before it was dispatched and answered after.
    /// </summary>
    private static HashSet<ulong> Completing(
        IReadOnlyList<long> dispatchedAt,
        IReadOnlyList<int> writtenBy,
        NodeDrainAllCompleteSubmission[] completes,
        int revision) =>
        [.. completes
            .Where(complete => complete.Committer == writtenBy[revision] && complete.SubmittedAfterDispatches <= dispatchedAt[revision]
                && dispatchedAt[revision] < complete.AnsweredAfterDispatches)
            .Select(static complete => complete.ExpectedDrainAllRevision)];

    /// <summary>
    /// Returns the nodes whose retirements could have retired them in the write which produced <paramref name="revision"/>:
    /// those submitted to its writer before it was dispatched and answered after.
    /// </summary>
    private static HashSet<Orleans.Durable.Storage.Engine.Ledgers.NodeId> Retiring(
        IReadOnlyList<long> dispatchedAt,
        IReadOnlyList<int> writtenBy,
        NodeRetirementSubmission[] retirements,
        int revision) =>
        [.. retirements
            .Where(retirement => retirement.Committer == writtenBy[revision] && retirement.SubmittedAfterDispatches <= dispatchedAt[revision]
                && dispatchedAt[revision] < retirement.AnsweredAfterDispatches)
            .Select(static retirement => retirement.Node)];

    /// <summary>
    /// Returns the latest deciding revision after the submission which <paramref name="submission"/>'s committer wrote and
    /// which holds its goal, a write which carried it, or −1 when there is none.
    /// </summary>
    private static int CarriedRevision(
        IReadOnlyList<NodeRegistryValue> history,
        IReadOnlyList<long> dispatchedAt,
        IReadOnlyList<int> writtenBy,
        NodeRegistrySubmission submission,
        int maxValueLength) =>
        Enumerable.Range(0, history.Count)
            .Where(revision => revision > (int)submission.SubmittedAtRevision && IsDeciding(history, dispatchedAt, submission, revision)
                && writtenBy[revision] == submission.Committer
                && Decide(history[revision], submission.Entry, submission.Store, maxValueLength, submission.NodeToReplace, submission.ObjectLedgerStore) == Standing.Goal)
            .DefaultIfEmpty(-1)
            .Max();

    /// <summary>
    /// Returns the registrations which could have named a node for replacement in the write which produced
    /// <paramref name="revision"/>: those submitted to its writer before it was dispatched and answered after, so still
    /// pending when it was; a registration answered before the write could not have been carried by it.
    /// </summary>
    private static (NodeRegistryEntry Entry, Orleans.Durable.Storage.Engine.Ledgers.NodeId? NodeToReplace)[] Naming(
        IReadOnlyList<long> dispatchedAt,
        IReadOnlyList<int> writtenBy,
        NodeRegistrySubmission[] submissions,
        int revision) =>
        [.. submissions
            .Where(submission => submission.Committer == writtenBy[revision] && submission.SubmittedAfterDispatches <= dispatchedAt[revision]
                && dispatchedAt[revision] < submission.AnsweredAfterDispatches)
            .Select(static submission => (submission.Entry, submission.NodeToReplace))];

    /// <summary>
    /// Returns whether a registration in <paramref name="naming"/> names <paramref name="retired"/> for replacement, and its
    /// node holds <paramref name="retired"/>'s ordinal active in <paramref name="after"/>.
    /// </summary>
    private static bool IsNamed(NodeRegistryEntry retired, NodeRegistryValue after, IReadOnlyList<(NodeRegistryEntry Entry, Orleans.Durable.Storage.Engine.Ledgers.NodeId? NodeToReplace)> naming) =>
        retired.Ordinal is { } ordinal
        && naming.Any(candidate => candidate.NodeToReplace == retired.Node
            && after.Nodes.Any(successor => successor.Node == candidate.Entry.Node && !successor.IsRetired && successor.Ordinal == ordinal));

    /// <summary>Returns the answer <c>OrdinalOccupied</c> which <paramref name="value"/> gives <paramref name="entry"/>'s registration.</summary>
    private static NodeRegistrationOutcome.OrdinalOccupied? OccupiedAt(NodeRegistryValue value, NodeRegistryEntry entry)
    {
        var own = value.Nodes.FirstOrDefault(candidate => candidate.Node == entry.Node);
        return FindOccupant(value, entry) is { Ordinal: { } ordinal } occupant ? new(ordinal, occupant.Node, own is not null, own?.Ordinal) : null;
    }

    /// <summary>Returns the active entry of another node than <paramref name="entry"/>'s at its ordinal, or <see langword="null"/>.</summary>
    private static NodeRegistryEntry? FindOccupant(NodeRegistryValue value, NodeRegistryEntry entry) =>
        entry.Ordinal is { } ordinal
            ? value.Nodes.FirstOrDefault(candidate => !candidate.IsRetired && candidate.Ordinal == ordinal && candidate.Node != entry.Node)
            : null;

    private static Standing Expected(object negative) => negative switch
    {
        NodeRegistrationOutcome.Stale => Standing.Stale,
        NodeRegistrationOutcome.WrongArchiveStore => Standing.WrongStore,
        NodeRegistrationOutcome.WrongObjectLedgerStore => Standing.WrongObjectStore,
        NodeRegistrationOutcome.Refused => Standing.TooLarge,
        NodeRegistrationOutcome.OrdinalOccupied => Standing.OrdinalOccupied,
        _ => throw new ArgumentOutOfRangeException(nameof(negative), negative, "An unknown negative answer."),
    };
}
