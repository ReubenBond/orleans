using System.Buffers;
using System.Collections.Concurrent;
using CsCheck;
using Orleans.Durable.Storage.Control;
using Orleans.Durable.Storage.Engine.Ledgers.Objects;
using Orleans.Durable.Storage.Engine.MetaStore;
using Orleans.Durable.Storage.Testing;
using Orleans.Runtime;
using static Orleans.Durable.Storage.Tests.NodeRegistrySamples;

namespace Orleans.Durable.Storage.Tests;

/// <summary>
/// The node registry committer's conformance (orleans.md §9, conventions.md §6): over generated histories, two committers,
/// as two controller-grain activations, share one scripted register, each registration or operator's retirement submitted
/// to either, concurrently within a round, some registrations naming a node for replacement at their ordinal, while writes
/// meet scripted outcomes (answered,
/// applied and answered mismatched, applied and then advanced by another writer before the mismatch is answered, failed
/// after applying, failed without applying, or failed and landed later, before the next round's reads or never) and reads
/// fail. Some histories run under a value limit of a few entries, so registrations are refused. <see cref="NodeRegistryHistoryOracle"/>
/// judges every answer against the register's values between its submission and its answer, and every step of the
/// history against OR-I2 and OR-I20. <c>DURABLE_STORAGE_TEST_SEED</c> replays a case.
/// </summary>
public sealed class NodeRegistryCommitterConformanceTests : IDisposable
{
    private static readonly ObjectLedgerStoreId ObjectStoreA = new(new UInt128(0, 0xAA01));
    private static readonly ObjectLedgerStoreId ObjectStoreB = new(new UInt128(0, 0xBB02));
    private static readonly int ThreeEntries = NodeRegistryRules.ReservedLength(new NodeRegistryValue(1, [Entry(1, 1), Entry(2, 1), Entry(3, 1)], StoreA));

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    private enum Fault
    {
        None,
        Answer,
        AppliedMismatch,
        AppliedMismatchThenSuperseded,
        FailApplied,
        FailUnapplied,
        LandLater,
        ReadFails,
    }

    public void Dispose() => _leaks.Dispose();

    [Fact]
    public async Task EveryAnswerOfTwoCommittersOnOneRegisterHoldsInItsHistory()
    {
        var kinds = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        var coverage = new Coverage();
        var stores = Gen.Frequency((8, Gen.Const(StoreA)), (1, Gen.Const(StoreB)), (1, Gen.Const(StoreC)), (1, Gen.Const(StoreD)));
        var objectStores = Gen.Frequency((3, Gen.Const(default(ObjectLedgerStoreId?))), (6, Gen.Const<ObjectLedgerStoreId?>(ObjectStoreA)), (2, Gen.Const<ObjectLedgerStoreId?>(ObjectStoreB)));
        var nodesToReplace = Gen.Int[-3, 4].Select(static node => node > 0 ? Node(node) : (Orleans.Durable.Storage.Engine.Ledgers.NodeId?)null);
        // Node 4 is diskless (ordinal: null, diskless: true); nodes 1–3 are disk-backed.
        var registrations = Gen.Select(Gen.Int[1, 4], Gen.Int[1, 4], Gen.Int[1, 3], Gen.Int[-1, 1], stores, nodesToReplace, objectStores)
            .Select(static (node, generation, host, ordinal, store, nodeToReplace, objectStore) =>
                (NodeRegistryChange)new NodeRegistrationRequest(
                    new NodeRegistryEntry(
                        Node(node),
                        Silo(host, generation),
                        node == 4 ? null : ordinal < 0 ? null : ordinal,
                        failureDomain: null,
                        retiredRevision: 0,
                        diskless: node == 4),
                    store,
                    nodeToReplace,
                    objectStore));
        var retirements = Gen.Int[1, 5].Select(static node => (NodeRegistryChange)new NodeRetirementRequest(Node(node)));
        var drainAllStarts = Gen.Const((NodeRegistryChange)NodeDrainAllStartRequest.Instance);
        var drainAllCancels = Gen.Const((NodeRegistryChange)NodeDrainAllCancelRequest.Instance);
        // ExpectedDrainAllRevision == 0 tells SubmitAsync to target the register's current active DrainAllRevision (or 1).
        var drainAllCompletes = Gen.Frequency((3, Gen.Const(0ul)), (1, Gen.ULong[1, 5]))
            .Select(static expected => (NodeRegistryChange)new NodeDrainAllCompleteRequest(expected));
        var requests = Gen.Frequency((12, registrations), (2, retirements), (2, drainAllStarts), (1, drainAllCancels), (2, drainAllCompletes));
        var steps = Gen.Select(requests, Gen.Enum<Fault>(), Gen.Int[0, 1], Gen.Bool, Gen.Bool).Select(static (request, fault, committer, endsRound, landsHeld) => new Step(request, fault, committer, endsRound, landsHeld)).Array[1, 35];
        var limits = Gen.Frequency((3, Gen.Const(1 << 20)), (1, Gen.Const(ThreeEntries)));
        await Gen.Select(steps, limits).SampleAsync(
            async sample =>
            {
                var (generated, limit) = sample;
                HashSet<string> seen = [];
                await _leaks.InScopeAsync(async () => seen = await RunAsync(generated, limit, coverage));
                foreach (var kind in seen)
                {
                    _ = kinds.AddOrUpdate(kind, 1, static (_, count) => count + 1);
                }
            },
            iter: 300,
            seed: Environment.GetEnvironmentVariable("DURABLE_STORAGE_TEST_SEED") is { Length: > 0 } seed ? seed : null);

        foreach (var kind in new[]
        {
            "Registered",
            "Stale",
            "WrongArchiveStore",
            "WrongObjectLedgerStore",
            "Refused",
            "OrdinalOccupied",
            "unknown",
            "Retired",
            "NotRegistered",
            "retirement unknown",
            "Started",
            "ObjectLedgersNotConfigured",
            "Cancelled",
            "NotActive",
            "Completed",
            "Superseded",
            "drain-all unknown",
        })
        {
            Assert.True(kinds.ContainsKey(kind), $"No change was answered {kind}.");
        }

        Assert.True(coverage.AppliedMismatches > 0, "No write applied and answered a mismatch.");
        Assert.True(coverage.Landed > 0, "No write whose outcome was unknown landed later.");
        Assert.True(coverage.FailedReads > 0, "No read failed.");
        Assert.True(coverage.RegisteredAfterUnknown > 0, "No registration was answered Registered after one of its entry ended unknown.");
        Assert.True(coverage.RevocationCandidates > 0, "No negative answer followed a write its committer dispatched which held its goal.");
        Assert.True(coverage.NamedRetirements > 0, "No registration named for replacement retired an active node.");
        Assert.True(coverage.OperatorRetirements > 0, "No operator's retirement retired an active node.");
        Assert.True(coverage.RetiredRestarts > 0, "No retired node's registration advanced its retired entry.");
        Assert.True(coverage.WrongNodeNamed > 0, "No registration which named another node than its ordinal's occupant was answered OrdinalOccupied.");
    }

    /// <summary>
    /// The oracle excuses a retirement only by a registration still pending when the retiring write was dispatched. Node 3
    /// names node 2 before node 2 exists and is registered; node 2 registers at ordinal 1; then a write retires node 2 on
    /// node 3's re-registration at ordinal 1, which names no node: the earlier, answered registration names node 2, but no
    /// write after its answer carried it.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ARetirementIsExcusedOnlyByARegistrationPendingWhenItsWriteWasDispatched(bool reregistrationNamesNode2)
    {
        NodeRegistryValue[] history =
        [
            NodeRegistryValue.Empty,
            new(1, [Entry(3, 1, ordinal: 0)], StoreA),
            new(2, [Entry(2, 1, ordinal: 1), Entry(3, 1, ordinal: 0)], StoreA),
            new(3, [Entry(2, 1, ordinal: 1).Retire(3), Entry(3, 2, ordinal: 1)], StoreA),
        ];
        long[] dispatchedAt = [-1, 0, 1, 2];
        int[] writtenBy = [-1, 0, 0, 0];
        NodeRegistrySubmission[] submissions =
        [
            new(Entry(3, 1, ordinal: 0), StoreA, Node(2), 0, 0, 0, new NodeRegistrationOutcome.Registered(1), AnsweredAfterDispatches: 1, AnsweredAtRevision: 1),
            new(Entry(2, 1, ordinal: 1), StoreA, null, 0, 1, 1, new NodeRegistrationOutcome.Registered(2), AnsweredAfterDispatches: 2, AnsweredAtRevision: 2),
            new(Entry(3, 2, ordinal: 1), StoreA, reregistrationNamesNode2 ? Node(2) : null, 0, 2, 2, new NodeRegistrationOutcome.Registered(3), AnsweredAfterDispatches: 3, AnsweredAtRevision: 3),
        ];

        var violations = NodeRegistryHistoryOracle.Violations(history, dispatchedAt, writtenBy, submissions, int.MaxValue);

        if (reregistrationNamesNode2)
        {
            Assert.Empty(violations);
        }
        else
        {
            Assert.Contains(violations, violation => violation.StartsWith("Revision 3:", StringComparison.Ordinal) && violation.Contains(Node(2).ToString(), StringComparison.Ordinal));
        }
    }

    private static async Task<HashSet<string>> RunAsync(Step[] steps, int limit, Coverage coverage)
    {
        var register = new ScriptedConditionalRegister();
        var views = new[] { new FlakyReads(register.Writer(0)), new FlakyReads(register.Writer(1)) };
        var submissions = new List<NodeRegistrySubmission>();
        var retirements = new List<NodeRetirementSubmission>();
        var drainAllStarts = new List<NodeDrainAllStartSubmission>();
        var drainAllCancels = new List<NodeDrainAllCancelSubmission>();
        var drainAllCompletes = new List<NodeDrainAllCompleteSubmission>();
        var landings = new List<Func<bool>>();
        var decisions = new List<Task>();
        var holds = new List<ScriptedConditionalRegister.HeldWrite>();
        await using var first = Committer(views[0], limit);
        await using var second = Committer(views[1], limit);
        var round = new List<Task<object>>();
        for (var index = 0; index < steps.Length; index++)
        {
            var step = steps[index];
            if (step.Fault is Fault.ReadFails)
            {
                views[step.Committer].FailNextRead();
            }
            else if (step.Fault is not Fault.None)
            {
                var held = register.HoldNextWrite();
                holds.Add(held);
                decisions.Add(DecideAsync(held, step.Fault, landings));
            }

            round.Add(SubmitAsync(step.Committer == 0 ? first : second, step.Committer, register, step.Request));
            if (step.EndsRound || index == steps.Length - 1)
            {
                foreach (var answered in await Task.WhenAll(round))
                {
                    switch (answered)
                    {
                        case NodeRegistrySubmission registration:
                            submissions.Add(registration);
                            break;
                        case NodeRetirementSubmission retirement:
                            retirements.Add(retirement);
                            break;
                        case NodeDrainAllStartSubmission start:
                            drainAllStarts.Add(start);
                            break;
                        case NodeDrainAllCancelSubmission cancel:
                            drainAllCancels.Add(cancel);
                            break;
                        case NodeDrainAllCompleteSubmission complete:
                            drainAllCompletes.Add(complete);
                            break;
                        default:
                            throw new InvalidOperationException("A submission is a registration's, retirement's, or drain-all transition's.");
                    }
                }

                round.Clear();

                // Some writes whose outcome was unknown land now, after the reads which ended their batches.
                if (step.LandsHeld)
                {
                    lock (landings)
                    {
                        coverage.AddLanded(landings.Count(static land => land()));
                        landings.Clear();
                    }
                }
            }
        }

        // Every registration is answered, so no write is in flight and none arrives: a hold no write reached is abandoned,
        // and every decision, a script's fault among them, surfaces here.
        foreach (var held in holds)
        {
            _ = held.Abandon();
        }

        await Task.WhenAll(decisions);

        var history = new List<NodeRegistryValue> { NodeRegistryValue.Empty };
        history.AddRange(register.AppliedWrites.Select(static bytes => Assert.IsType<NodeRegistryValue>(NodeRegistryCodec.Decode(bytes.AsSpan()).Value)));
        long[] dispatchedAt = [-1, .. register.AppliedDispatches];
        int[] writtenBy = [-1, .. register.AppliedWriters];
        var violations = NodeRegistryHistoryOracle.Violations(history, dispatchedAt, writtenBy, submissions, limit, retirements, drainAllStarts, drainAllCancels, drainAllCompletes);
        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, [
            .. violations,
            "History:",
            .. history.Select(static (value, revision) => $"  {revision} (drainAll={value.DrainAllRevision}): {string.Join(", ", value.Nodes.Select(static entry => $"{entry.Node.ToString()[^1..]}@{entry.Silo}{(entry.IsRetired ? $" retired@{entry.RetiredRevision}" : "")}"))}"),
            "Submissions:",
            .. submissions.Select(static submission => $"  {submission.Committer}: {submission.Entry.Node.ToString()[^1..]}@{submission.Entry.Silo} ord {submission.Entry.Ordinal} at {submission.SubmittedAtRevision} -> {submission.Outcome?.Value?.ToString() ?? submission.Failure?.GetType().Name} after {submission.AnsweredAfterDispatches}"),
            "Retirements:",
            .. retirements.Select(static retirement => $"  {retirement.Committer}: {retirement.Node.ToString()[^1..]} at {retirement.SubmittedAtRevision} -> {retirement.Outcome?.Value?.ToString() ?? retirement.Failure?.GetType().Name} after {retirement.AnsweredAfterDispatches}"),
            "DrainAll:",
            .. drainAllStarts.Select(static s => $"  {s.Committer}: start at {s.SubmittedAtRevision} -> {s.Outcome?.Value?.ToString() ?? s.Failure?.GetType().Name} after {s.AnsweredAfterDispatches}"),
            .. drainAllCancels.Select(static c => $"  {c.Committer}: cancel at {c.SubmittedAtRevision} -> {c.Outcome?.Value?.ToString() ?? c.Failure?.GetType().Name} after {c.AnsweredAfterDispatches}"),
            .. drainAllCompletes.Select(static c => $"  {c.Committer}: complete({c.ExpectedDrainAllRevision}) at {c.SubmittedAtRevision} -> {c.Outcome?.Value?.ToString() ?? c.Failure?.GetType().Name} after {c.AnsweredAfterDispatches}"),
        ]));
        coverage.AddOperatorRetirements(NodeRegistryHistoryOracle.OperatorRetirements(history, dispatchedAt, writtenBy, retirements));
        coverage.AddRetiredRestarts(Enumerable.Range(1, history.Count - 1).Sum(revision => history[revision].Nodes.Count(entry =>
            history[revision - 1].Find(entry.Node) is { IsRetired: true } old && old.Silo.Generation < entry.Silo.Generation)));
        coverage.Add(register.AppliedMismatches, views.Sum(static view => view.Failed));
        coverage.AddRevocationCandidates(NodeRegistryHistoryOracle.RevocationCandidates(history, dispatchedAt, writtenBy, submissions, limit));
        coverage.AddNamedRetirements(NodeRegistryHistoryOracle.NamedRetirements(history, dispatchedAt, writtenBy, submissions));
        coverage.AddWrongNodeNamed(submissions.Count(static submission =>
            submission is { NodeToReplace: { } named, Outcome.Value: NodeRegistrationOutcome.OrdinalOccupied occupied } && occupied.Occupant != named));
        coverage.AddRegisteredAfterUnknown(submissions.Where(static submission => submission.Outcome?.Value is NodeRegistrationOutcome.Registered)
            .Count(registered => submissions.TakeWhile(earlier => !ReferenceEquals(earlier, registered)).Any(earlier => earlier.Outcome is null && earlier.Entry == registered.Entry)));
        return
        [
            .. submissions.Select(static submission => submission.Outcome?.Value?.GetType().Name ?? "unknown"),
            .. retirements.Select(static retirement => retirement.Outcome?.Value?.GetType().Name ?? "retirement unknown"),
            .. drainAllStarts.Select(static start => start.Outcome?.Value?.GetType().Name ?? "drain-all unknown"),
            .. drainAllCancels.Select(static cancel => cancel.Outcome?.Value?.GetType().Name ?? "drain-all unknown"),
            .. drainAllCompletes.Select(static complete => complete.Outcome?.Value?.GetType().Name ?? "drain-all unknown"),
        ];
    }

    private static async Task DecideAsync(ScriptedConditionalRegister.HeldWrite held, Fault fault, List<Func<bool>> landings)
    {
        try
        {
            await held.Arrived;
        }
        catch (OperationCanceledException)
        {
            // No write reached the hold.
            return;
        }

        switch (fault)
        {
            case Fault.Answer:
                held.Answer();
                break;
            case Fault.AppliedMismatch:
                held.ApplyAndAnswerMismatch();
                break;
            case Fault.AppliedMismatchThenSuperseded:
                held.ApplyAndAnswerMismatch(AdvanceEveryActiveEntry);
                break;
            case Fault.FailApplied:
                held.Fail(apply: true);
                break;
            case Fault.FailUnapplied:
                held.Fail(apply: false);
                break;
            case Fault.LandLater:
                var landing = held.FailAndLandLater();
                lock (landings)
                {
                    landings.Add(landing);
                }

                break;
            case Fault.None:
            case Fault.ReadFails:
            default:
                throw new ArgumentOutOfRangeException(nameof(fault), fault, "This fault holds no write.");
        }
    }

    /// <summary>
    /// Returns the value another writer, such as a second controller-grain activation, writes over <paramref name="written"/>:
    /// every active entry advanced to its next generation at the same address, one OR-I2 step on.
    /// </summary>
    private static byte[] AdvanceEveryActiveEntry(byte[] written)
    {
        var value = Assert.IsType<NodeRegistryValue>(NodeRegistryCodec.Decode(written.AsSpan()).Value);
        NodeRegistryEntry[] nodes = [.. value.Nodes.Select(static entry => entry.IsRetired
            ? entry
            : new NodeRegistryEntry(entry.Node, SiloAddress.New(entry.Silo.Endpoint, entry.Silo.Generation + 1), entry.Ordinal, entry.FailureDomain, retiredRevision: 0, diskless: entry.Diskless))];
        return NodeRegistryCodec.Encode(new NodeRegistryValue(value.Revision + 1, [.. nodes], value.ArchiveStore, value.ObjectLedgerStore, value.DrainAllRevision));
    }

    private static async Task<object> SubmitAsync(NodeRegistryCommitter committer, int writer, ScriptedConditionalRegister register, NodeRegistryChange change) => change switch
    {
        NodeRegistrationRequest request => await RegisterAsync(committer, writer, register, request),
        NodeRetirementRequest retirement => await RetireAsync(committer, writer, register, retirement.Node),
        NodeDrainAllStartRequest => await StartDrainAllAsync(committer, writer, register),
        NodeDrainAllCancelRequest => await CancelDrainAllAsync(committer, writer, register),
        NodeDrainAllCompleteRequest complete => await CompleteDrainAllAsync(committer, writer, register, complete.ExpectedDrainAllRevision),
        null => throw new InvalidOperationException("A change holds a case."),
    };

    private static async Task<NodeDrainAllStartSubmission> StartDrainAllAsync(NodeRegistryCommitter committer, int writer, ScriptedConditionalRegister register)
    {
        var submittedAt = (ulong)register.AppliedWrites.Count;
        var dispatchedBefore = register.Dispatches;
        try
        {
            var outcome = await committer.StartDrainAllAsync(TestContext.Current.CancellationToken);
            return new(writer, submittedAt, dispatchedBefore, outcome, AnsweredAfterDispatches: register.Dispatches, AnsweredAtRevision: (ulong)register.AppliedWrites.Count);
        }
        catch (NodeRegistryUnavailableException failure)
        {
            return new(writer, submittedAt, dispatchedBefore, null, failure, register.Dispatches, (ulong)register.AppliedWrites.Count);
        }
    }

    private static async Task<NodeDrainAllCancelSubmission> CancelDrainAllAsync(NodeRegistryCommitter committer, int writer, ScriptedConditionalRegister register)
    {
        var submittedAt = (ulong)register.AppliedWrites.Count;
        var dispatchedBefore = register.Dispatches;
        try
        {
            var outcome = await committer.CancelDrainAllAsync(TestContext.Current.CancellationToken);
            return new(writer, submittedAt, dispatchedBefore, outcome, AnsweredAfterDispatches: register.Dispatches, AnsweredAtRevision: (ulong)register.AppliedWrites.Count);
        }
        catch (NodeRegistryUnavailableException failure)
        {
            return new(writer, submittedAt, dispatchedBefore, null, failure, register.Dispatches, (ulong)register.AppliedWrites.Count);
        }
    }

    private static async Task<NodeDrainAllCompleteSubmission> CompleteDrainAllAsync(NodeRegistryCommitter committer, int writer, ScriptedConditionalRegister register, ulong requestedExpected)
    {
        var submittedAt = (ulong)register.AppliedWrites.Count;
        var dispatchedBefore = register.Dispatches;
        var expected = requestedExpected;
        if (expected == 0)
        {
            var latest = register.AppliedWrites;
            expected = latest.Count > 0 && NodeRegistryCodec.Decode(latest[^1].AsSpan()).Value is NodeRegistryValue { DrainAllRevision: > 0 } current
                ? current.DrainAllRevision
                : 1ul;
        }

        try
        {
            var outcome = await committer.CompleteDrainAllAsync(expected, TestContext.Current.CancellationToken);
            return new(expected, writer, submittedAt, dispatchedBefore, outcome, AnsweredAfterDispatches: register.Dispatches, AnsweredAtRevision: (ulong)register.AppliedWrites.Count);
        }
        catch (NodeRegistryUnavailableException failure)
        {
            return new(expected, writer, submittedAt, dispatchedBefore, null, failure, register.Dispatches, (ulong)register.AppliedWrites.Count);
        }
    }

    private static async Task<NodeRetirementSubmission> RetireAsync(NodeRegistryCommitter committer, int writer, ScriptedConditionalRegister register, Orleans.Durable.Storage.Engine.Ledgers.NodeId node)
    {
        var submittedAt = (ulong)register.AppliedWrites.Count;
        var dispatchedBefore = register.Dispatches;
        try
        {
            var outcome = await committer.RetireAsync(node, TestContext.Current.CancellationToken);
            return new(node, writer, submittedAt, dispatchedBefore, outcome, AnsweredAfterDispatches: register.Dispatches, AnsweredAtRevision: (ulong)register.AppliedWrites.Count);
        }
        catch (NodeRegistryUnavailableException failure)
        {
            return new(node, writer, submittedAt, dispatchedBefore, null, failure, register.Dispatches, (ulong)register.AppliedWrites.Count);
        }
    }

    private static async Task<NodeRegistrySubmission> RegisterAsync(NodeRegistryCommitter committer, int writer, ScriptedConditionalRegister register, NodeRegistrationRequest request)
    {
        var submittedAt = (ulong)register.AppliedWrites.Count;
        var dispatchedBefore = register.Dispatches;
        try
        {
            var outcome = await committer.RegisterAsync(request.Entry, request.Store, request.NodeToReplace, request.ObjectLedgerStore, TestContext.Current.CancellationToken);
            return new(request.Entry, request.Store, request.NodeToReplace, writer, submittedAt, dispatchedBefore, outcome, AnsweredAfterDispatches: register.Dispatches, AnsweredAtRevision: (ulong)register.AppliedWrites.Count, ObjectLedgerStore: request.ObjectLedgerStore);
        }
        catch (NodeRegistryUnavailableException failure)
        {
            return new(request.Entry, request.Store, request.NodeToReplace, writer, submittedAt, dispatchedBefore, null, failure, register.Dispatches, (ulong)register.AppliedWrites.Count, ObjectLedgerStore: request.ObjectLedgerStore);
        }
    }

    /// <summary>One generated step: a registration or a retirement, the fault its write or read meets, its committer, and the round's end.</summary>
    private sealed record Step(NodeRegistryChange Request, Fault Fault, int Committer, bool EndsRound, bool LandsHeld);

    /// <summary>The faults the generated histories reached, across every case.</summary>
    private sealed class Coverage
    {
        private int _appliedMismatches;
        private int _landed;
        private int _failedReads;
        private int _registeredAfterUnknown;
        private int _revocationCandidates;
        private int _namedRetirements;
        private int _wrongNodeNamed;
        private int _operatorRetirements;
        private int _retiredRestarts;

        public int OperatorRetirements => Volatile.Read(ref _operatorRetirements);

        public int RetiredRestarts => Volatile.Read(ref _retiredRestarts);

        public void AddRetiredRestarts(int count) => _ = Interlocked.Add(ref _retiredRestarts, count);

        public void AddOperatorRetirements(int count) => _ = Interlocked.Add(ref _operatorRetirements, count);

        public int AppliedMismatches => Volatile.Read(ref _appliedMismatches);

        public int Landed => Volatile.Read(ref _landed);

        public int FailedReads => Volatile.Read(ref _failedReads);

        public int RegisteredAfterUnknown => Volatile.Read(ref _registeredAfterUnknown);

        public void AddRegisteredAfterUnknown(int count) => _ = Interlocked.Add(ref _registeredAfterUnknown, count);

        public int RevocationCandidates => Volatile.Read(ref _revocationCandidates);

        public void AddRevocationCandidates(int count) => _ = Interlocked.Add(ref _revocationCandidates, count);

        public int NamedRetirements => Volatile.Read(ref _namedRetirements);

        public void AddNamedRetirements(int count) => _ = Interlocked.Add(ref _namedRetirements, count);

        public int WrongNodeNamed => Volatile.Read(ref _wrongNodeNamed);

        public void AddWrongNodeNamed(int count) => _ = Interlocked.Add(ref _wrongNodeNamed, count);

        public void AddLanded(int landed) => _ = Interlocked.Add(ref _landed, landed);

        public void Add(int appliedMismatches, int failedReads)
        {
            _ = Interlocked.Add(ref _appliedMismatches, appliedMismatches);
            _ = Interlocked.Add(ref _failedReads, failedReads);
        }
    }

    /// <summary>A committer's view of the register whose next read fails once asked to, as an unavailable MetaStore's does.</summary>
    private sealed class FlakyReads(IConditionalRegister register) : IConditionalRegister
    {
        private int _failNext;
        private int _failed;

        public int Failed => Volatile.Read(ref _failed);

        public void FailNextRead() => Volatile.Write(ref _failNext, 1);

        public ValueTask<MetaStoreEntry?> ReadAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _failNext, 0) == 1)
            {
                _ = Interlocked.Increment(ref _failed);
                return ValueTask.FromException<MetaStoreEntry?>(new IOException("The scripted read failed."));
            }

            return register.ReadAsync(cancellationToken);
        }

        public ValueTask<RegisterWriteOutcome> WriteAsync(MetaStoreVersion? expectedVersion, ReadOnlySequence<byte> value, CancellationToken cancellationToken = default) =>
            register.WriteAsync(expectedVersion, value, cancellationToken);
    }
}
