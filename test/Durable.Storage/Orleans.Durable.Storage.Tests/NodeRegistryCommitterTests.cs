using System.Buffers;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Orleans.Durable.Storage.Control;
using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Cluster.Control;
using Orleans.Durable.Storage.Engine.MetaStore;
using Orleans.Durable.Storage.Testing;
using static Orleans.Durable.Storage.Tests.NodeRegistrySamples;

namespace Orleans.Durable.Storage.Tests;

/// <summary>
/// The node registry committer (orleans.md §5.3, OR-I2, OR-I19, OR-I20): registration advances an entry only to a newer
/// generation and retires an ordinal's predecessor at the write's revision; a retired identity or an older or equal
/// generation is stale, another archive store in kind or ID is refused, and so is an entry past the register's value
/// limit; concurrent registrations share one write, each decided against those before it; a write whose outcome is
/// unknown ends its batch with one read; every answer comes from a value from after the submission; an entry or store
/// <c>CLNR</c> cannot hold is refused before it is folded; and disposal and a caller's cancellation end only what they
/// own.
/// </summary>
public sealed class NodeRegistryCommitterTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    [Fact]
    public async Task AFirstRegistrationRecordsItsEntryAndTheArchiveStore()
    {
        var register = new ScriptedConditionalRegister();
        await using var committer = Committer(register);

        var answer = await committer.RegisterAsync(Entry(1, 1, ordinal: 0), StoreA, nodeToReplace: null, TestContext.Current.CancellationToken);

        Assert.Equal(new NodeRegistrationOutcome.Registered(1), answer);
        var value = await ReadAsync(register);
        Assert.Equal(1UL, value.Revision);
        Assert.Equal(StoreA, value.ArchiveStore);
        Assert.Equal(Entry(1, 1, ordinal: 0), Assert.Single(value.Nodes));
    }

    [Fact]
    public async Task AnEntryAdvancesOnlyToANewerGeneration()
    {
        var register = new ScriptedConditionalRegister();
        await using var committer = Committer(register);
        var cancellationToken = TestContext.Current.CancellationToken;

        Assert.Equal(new NodeRegistrationOutcome.Registered(1), await committer.RegisterAsync(Entry(1, 2), StoreA, nodeToReplace: null, cancellationToken));
        Assert.Equal(NodeRegistrationOutcome.Stale.Instance, await committer.RegisterAsync(Entry(1, 1), StoreA, nodeToReplace: null, cancellationToken));
        Assert.Equal(NodeRegistrationOutcome.Stale.Instance, await committer.RegisterAsync(Entry(1, 2, host: 9), StoreA, nodeToReplace: null, cancellationToken));
        Assert.Equal(new NodeRegistrationOutcome.Registered(2), await committer.RegisterAsync(Entry(1, 3, host: 9), StoreA, nodeToReplace: null, cancellationToken));

        Assert.Equal(Entry(1, 3, host: 9), Assert.Single((await ReadAsync(register)).Nodes));
        Assert.Equal(2, register.Writes);
    }

    /// <summary>
    /// A registration at another node's active ordinal is answered <c>OrdinalOccupied</c> with no write unless it names
    /// that node; naming it retires it at the write's revision, and a name left configured acts no more, since the named
    /// node is then retired (orleans.md §5.3).
    /// </summary>
    [Fact]
    public async Task AnOrdinalSuccessionRetiresOnlyTheNamedPredecessorAtItsWritesRevision()
    {
        var register = new ScriptedConditionalRegister();
        var logger = new RecordingLogger();
        await using var committer = new NodeRegistryCommitter(register, 1 << 20, logger);
        var cancellationToken = TestContext.Current.CancellationToken;
        _ = await committer.RegisterAsync(Entry(1, 1, ordinal: 0), StoreA, nodeToReplace: null, cancellationToken);
        _ = await committer.RegisterAsync(Entry(2, 1, ordinal: 1), StoreA, nodeToReplace: null, cancellationToken);

        Assert.Equal(new NodeRegistrationOutcome.OrdinalOccupied(0, Node(1), HasEntry: false, EntryOrdinal: null), await committer.RegisterAsync(Entry(3, 1, ordinal: 0, host: 1), StoreA, nodeToReplace: null, cancellationToken));
        Assert.Equal(new NodeRegistrationOutcome.OrdinalOccupied(0, Node(1), HasEntry: false, EntryOrdinal: null), await committer.RegisterAsync(Entry(3, 1, ordinal: 0, host: 1), StoreA, Node(2), cancellationToken));

        // A known node whose volume is bound to another ordinal's pod is told so.
        Assert.Equal(new NodeRegistrationOutcome.OrdinalOccupied(0, Node(1), HasEntry: true, EntryOrdinal: 1), await committer.RegisterAsync(Entry(2, 2, ordinal: 0), StoreA, nodeToReplace: null, cancellationToken));
        Assert.Equal(2, register.Writes);
        Assert.Empty(logger.Entries);

        Assert.Equal(new NodeRegistrationOutcome.Registered(3), await committer.RegisterAsync(Entry(3, 1, ordinal: 0, host: 1), StoreA, Node(1), cancellationToken));

        var value = await ReadAsync(register);
        Assert.Equal(3UL, value.Find(Node(1))!.RetiredRevision);
        Assert.False(value.Find(Node(2))!.IsRetired);
        Assert.False(value.Find(Node(3))!.IsRetired);
        var warning = Assert.Single(logger.Entries, static entry => entry.Level == LogLevel.Warning);
        Assert.Contains(Node(1).ToString(), warning.Message, StringComparison.Ordinal);

        // The successor restarts with the name still configured: a restart takes nothing.
        Assert.Equal(new NodeRegistrationOutcome.Registered(4), await committer.RegisterAsync(Entry(3, 2, ordinal: 0, host: 1), StoreA, Node(1), cancellationToken));

        // The name acts once: another new node naming the retired predecessor finds the successor active.
        Assert.Equal(new NodeRegistrationOutcome.OrdinalOccupied(0, Node(3), HasEntry: false, EntryOrdinal: null), await committer.RegisterAsync(Entry(4, 1, ordinal: 0, host: 1), StoreA, Node(1), cancellationToken));

        // A retired identity's silo registers again retired at its revision, and takes nothing.
        Assert.Equal(new NodeRegistrationOutcome.Registered(5), await committer.RegisterAsync(Entry(1, 5, ordinal: 0), StoreA, nodeToReplace: null, cancellationToken));
        var restarted = await ReadAsync(register);
        Assert.Equal((3UL, 5), (restarted.Find(Node(1))!.RetiredRevision, restarted.Find(Node(1))!.Silo.Generation));
        Assert.False(restarted.Find(Node(3))!.IsRetired);
    }

    /// <summary>A store differing in kind alone, in ID alone, or in both is another store (OR-I20).</summary>
    [Fact]
    public async Task ARegistrationWithAnotherArchiveStoreIsRefused()
    {
        var register = new ScriptedConditionalRegister();
        await using var committer = Committer(register);
        var cancellationToken = TestContext.Current.CancellationToken;
        _ = await committer.RegisterAsync(Entry(1, 1), StoreA, nodeToReplace: null, cancellationToken);

        foreach (var store in new[] { StoreB, StoreC, StoreD })
        {
            Assert.Equal(NodeRegistrationOutcome.WrongArchiveStore.Instance, await committer.RegisterAsync(Entry(2, 1), store, nodeToReplace: null, cancellationToken));
            Assert.Equal(NodeRegistrationOutcome.WrongArchiveStore.Instance, await committer.RegisterAsync(Entry(1, 2), store, nodeToReplace: null, cancellationToken));
        }

        Assert.Equal(1UL, (await ReadAsync(register)).Revision);
    }

    /// <summary>
    /// A registration whose entry would take the registry, with every entry retired, past the register's value limit is
    /// refused against the value read, and no write carries it; a retirement of the registered node still fits.
    /// </summary>
    [Fact]
    public async Task ARegistrationPastTheValueLimitIsRefused()
    {
        var register = new ScriptedConditionalRegister();
        var oneEntry = NodeRegistryRules.ReservedLength(new NodeRegistryValue(1, [Entry(1, 1)], StoreA));
        await using var committer = Committer(register, oneEntry);
        var cancellationToken = TestContext.Current.CancellationToken;

        Assert.Equal(new NodeRegistrationOutcome.Registered(1), await committer.RegisterAsync(Entry(1, 1), StoreA, nodeToReplace: null, cancellationToken));
        Assert.Equal(NodeRegistrationOutcome.Refused.Instance, await committer.RegisterAsync(Entry(2, 1), StoreA, nodeToReplace: null, cancellationToken));
        Assert.Equal(NodeRegistrationOutcome.Refused.Instance, await committer.RegisterAsync(Entry(2, 1), StoreA, nodeToReplace: null, cancellationToken));
        Assert.Equal(1, register.Writes);

        Assert.Equal(new NodeRetirementOutcome.Retired(2), await committer.RetireAsync(Node(1), cancellationToken));
        Assert.True(NodeRegistryCodec.Encode(await ReadAsync(register)).Length <= oneEntry);
    }

    /// <summary>
    /// orleans.md §5.3: an operator's retirement retires the node's active entry at its write's revision, logging it; a
    /// repeat is answered from the retired entry with no write; a retirement of a node the registry does not hold is
    /// answered <c>NotRegistered</c> with no write; the retired identity's silo registers again staying retired, and its
    /// ordinal is free, so a new node registers there without naming it.
    /// </summary>
    [Fact]
    public async Task ARetirementRetiresTheNodesEntryAtItsWritesRevisionAndFreesItsOrdinal()
    {
        var register = new ScriptedConditionalRegister();
        var logger = new RecordingLogger();
        await using var committer = new NodeRegistryCommitter(register, 1 << 20, logger);
        var cancellationToken = TestContext.Current.CancellationToken;
        _ = await committer.RegisterAsync(Entry(1, 1, ordinal: 0), StoreA, nodeToReplace: null, cancellationToken);

        Assert.Equal(new NodeRetirementOutcome.Retired(2), await committer.RetireAsync(Node(1), cancellationToken));
        Assert.Equal(new NodeRetirementOutcome.Retired(2), await committer.RetireAsync(Node(1), cancellationToken));
        Assert.Equal(NodeRetirementOutcome.NotRegistered.Instance, await committer.RetireAsync(Node(2), cancellationToken));
        Assert.Equal(2, register.Writes);
        Assert.Contains(Node(1).ToString(), Assert.Single(logger.Entries, static entry => entry.Level == LogLevel.Warning).Message, StringComparison.Ordinal);

        Assert.Equal(new NodeRegistrationOutcome.Registered(3), await committer.RegisterAsync(Entry(1, 2, ordinal: 0), StoreA, nodeToReplace: null, cancellationToken));
        Assert.Equal(new NodeRegistrationOutcome.Registered(4), await committer.RegisterAsync(Entry(2, 1, ordinal: 0, host: 1), StoreA, nodeToReplace: null, cancellationToken));
        var value = await ReadAsync(register);
        Assert.Equal((2UL, 2), (value.Find(Node(1))!.RetiredRevision, value.Find(Node(1))!.Silo.Generation));
        Assert.False(value.Find(Node(2))!.IsRetired);
    }

    /// <summary>
    /// A retirement which a write whose outcome is unknown carried is answered <c>Retired</c> from the one read which ends
    /// its batch once that write landed, and fails as unknown, for its caller to retire again, when it did not.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ARetirementWhoseWritesOutcomeIsUnknownIsAnsweredFromTheRead(bool landed)
    {
        var register = new ScriptedConditionalRegister();
        await using var committer = Committer(register);
        var cancellationToken = TestContext.Current.CancellationToken;
        _ = await committer.RegisterAsync(Entry(1, 1), StoreA, nodeToReplace: null, cancellationToken);
        var held = register.HoldNextWrite();
        var retirement = committer.RetireAsync(Node(1), cancellationToken);
        await held.Arrived;

        held.Fail(apply: landed);

        if (landed)
        {
            Assert.Equal(new NodeRetirementOutcome.Retired(2), await retirement);
        }
        else
        {
            _ = await Assert.ThrowsAsync<NodeRegistryUnavailableException>(() => retirement);
            Assert.False((await ReadAsync(register)).Find(Node(1))!.IsRetired);
        }
    }

    /// <summary>A retirement names a node.</summary>
    [Fact]
    public async Task ARetirementOfTheDefaultIdentityIsRefused()
    {
        await using var committer = Committer(new ScriptedConditionalRegister());

        _ = await Assert.ThrowsAsync<ArgumentException>(() => committer.RetireAsync(default, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// orleans.md §5.3, §5.14: <see cref="NodeRegistryCommitter.StartDrainAllAsync"/>,
    /// <see cref="NodeRegistryCommitter.CancelDrainAllAsync"/>, and
    /// <see cref="NodeRegistryCommitter.CompleteDrainAllAsync"/> enforce object-ledger store configuration, idempotency,
    /// atomic diskful-only retirement + <c>DrainAllRevision</c> reset in one write, and atomic cancel-vs-complete
    /// serialization.
    /// </summary>
    [Fact]
    public async Task DrainAllCommitterOperationsEnforceDurabilityAtomicityAndCancelRace()
    {
        var register = new ScriptedConditionalRegister();
        await using var committer = Committer(register);
        var ct = TestContext.Current.CancellationToken;
        var objectStore = new Orleans.Durable.Storage.Engine.Ledgers.Objects.ObjectLedgerStoreId(new UInt128(0, 0xCC01));

        // Without ObjectLedgerStore recorded in CLNR, StartDrainAllAsync returns ObjectLedgersNotConfigured with no write.
        _ = await committer.RegisterAsync(Entry(1, 1, ordinal: 0), StoreA, nodeToReplace: null, ct);
        Assert.Equal(NodeDrainAllStartOutcome.ObjectLedgersNotConfigured.Instance, await committer.StartDrainAllAsync(ct));
        Assert.Equal(NodeDrainAllCancelOutcome.NotActive.Instance, await committer.CancelDrainAllAsync(ct));
        _ = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => committer.CompleteDrainAllAsync(0, ct));
        Assert.Equal(1, register.Writes);

        // Adopt ObjectLedgerStore and register a diskless node and a second diskful node.
        _ = await committer.RegisterAsync(Entry(1, 1, ordinal: 0), StoreA, nodeToReplace: null, objectStore, ct);
        var diskless = new NodeRegistryEntry(Node(2), Silo(2, 1), ordinal: null, failureDomain: null, retiredRevision: 0, diskless: true);
        _ = await committer.RegisterAsync(diskless, StoreA, nodeToReplace: null, objectStore, ct);
        _ = await committer.RegisterAsync(Entry(3, 1, ordinal: 1), StoreA, nodeToReplace: null, objectStore, ct);
        var writesBeforeDrain = register.Writes;

        // StartDrainAllAsync records DrainAllRevision durably; repeating it is idempotent with no additional write.
        var started = Assert.IsType<NodeDrainAllStartOutcome.Started>((await committer.StartDrainAllAsync(ct)).Value);
        Assert.Equal(writesBeforeDrain + 1, register.Writes);
        Assert.Equal(started, Assert.IsType<NodeDrainAllStartOutcome.Started>((await committer.StartDrainAllAsync(ct)).Value));
        Assert.Equal(writesBeforeDrain + 1, register.Writes);

        // CancelDrainAllAsync clears DrainAllRevision = 0 with zero retirements; a stale CompleteDrainAllAsync returns Superseded.
        _ = Assert.IsType<NodeDrainAllCancelOutcome.Cancelled>((await committer.CancelDrainAllAsync(ct)).Value);
        Assert.Equal(NodeDrainAllCancelOutcome.NotActive.Instance, await committer.CancelDrainAllAsync(ct));
        Assert.Equal(NodeDrainAllCompleteOutcome.Superseded.Instance, await committer.CompleteDrainAllAsync(started.Revision, ct));
        var afterCancel = await ReadAsync(register);
        Assert.Equal(0UL, afterCancel.DrainAllRevision);
        Assert.False(afterCancel.Find(Node(1))!.IsRetired);
        Assert.False(afterCancel.Find(Node(3))!.IsRetired);

        // Restart drain-all, then race CancelDrainAllAsync and CompleteDrainAllAsync in one batch behind a held write.
        var restarted = Assert.IsType<NodeDrainAllStartOutcome.Started>((await committer.StartDrainAllAsync(ct)).Value);
        var held = register.HoldNextWrite();
        var barrier = committer.RegisterAsync(Entry(4, 1, ordinal: 2), StoreA, nodeToReplace: null, objectStore, ct);
        await held.Arrived;
        var cancelTask = committer.CancelDrainAllAsync(ct);
        var completeTask = committer.CompleteDrainAllAsync(restarted.Revision, ct);
        held.Answer();
        _ = await barrier;
        // Cancel queued before Complete: Cancel clears DrainAllRevision and Complete returns Superseded with zero retirements.
        _ = Assert.IsType<NodeDrainAllCancelOutcome.Cancelled>((await cancelTask).Value);
        Assert.Equal(NodeDrainAllCompleteOutcome.Superseded.Instance, await completeTask);
        var afterRace = await ReadAsync(register);
        Assert.False(afterRace.Find(Node(1))!.IsRetired);
        Assert.False(afterRace.Find(Node(3))!.IsRetired);
        Assert.False(afterRace.Find(Node(4))!.IsRetired);

        // Start once more and commit CompleteDrainAllAsync: ONE write retires all active diskful nodes (1, 3, 4),
        // leaves diskless node 2 active, and resets DrainAllRevision = 0.
        var third = Assert.IsType<NodeDrainAllStartOutcome.Started>((await committer.StartDrainAllAsync(ct)).Value);
        var writesBeforeComplete = register.Writes;
        var completed = Assert.IsType<NodeDrainAllCompleteOutcome.Completed>((await committer.CompleteDrainAllAsync(third.Revision, ct)).Value);
        Assert.Equal(writesBeforeComplete + 1, register.Writes);
        Assert.Equal(NodeDrainAllCompleteOutcome.Superseded.Instance, await committer.CompleteDrainAllAsync(third.Revision, ct));
        Assert.Equal(NodeDrainAllCancelOutcome.NotActive.Instance, await committer.CancelDrainAllAsync(ct));

        var final = await ReadAsync(register);
        Assert.Equal(completed.Revision, final.Revision);
        Assert.Equal(0UL, final.DrainAllRevision);
        Assert.Equal(completed.Revision, final.Find(Node(1))!.RetiredRevision);
        Assert.False(final.Find(Node(2))!.IsRetired);
        Assert.Equal(completed.Revision, final.Find(Node(3))!.RetiredRevision);
        Assert.Equal(completed.Revision, final.Find(Node(4))!.RetiredRevision);

        // When zero active diskful nodes remain, Start -> Complete still converges cleanly back to DrainAllRevision = 0.
        var fourth = Assert.IsType<NodeDrainAllStartOutcome.Started>((await committer.StartDrainAllAsync(ct)).Value);
        var completedEmpty = Assert.IsType<NodeDrainAllCompleteOutcome.Completed>((await committer.CompleteDrainAllAsync(fourth.Revision, ct)).Value);
        Assert.Equal(0UL, (await ReadAsync(register)).DrainAllRevision);
        Assert.True(completedEmpty.Revision > fourth.Revision);
    }

    [Fact]
    public async Task ConcurrentRegistrationsShareOneWriteEachDecidedAgainstThoseBeforeIt()
    {
        var register = new ScriptedConditionalRegister();
        await using var committer = Committer(register);
        var cancellationToken = TestContext.Current.CancellationToken;
        var held = register.HoldNextWrite();
        var first = committer.RegisterAsync(Entry(1, 1), StoreA, nodeToReplace: null, cancellationToken);
        await held.Arrived;

        var older = committer.RegisterAsync(Entry(2, 2), StoreA, nodeToReplace: null, cancellationToken);
        var newer = committer.RegisterAsync(Entry(2, 3), StoreA, nodeToReplace: null, cancellationToken);
        var stale = committer.RegisterAsync(Entry(2, 2, host: 7), StoreA, nodeToReplace: null, cancellationToken);
        var merged = committer.RegisterAsync(Entry(3, 1), StoreA, nodeToReplace: null, cancellationToken);
        var duplicate = committer.RegisterAsync(Entry(3, 1), StoreA, nodeToReplace: null, cancellationToken);
        held.Answer();

        Assert.Equal(new NodeRegistrationOutcome.Registered(1), await first);
        Assert.Equal(NodeRegistrationOutcome.Stale.Instance, await older);
        Assert.Equal(new NodeRegistrationOutcome.Registered(2), await newer);
        Assert.Equal(NodeRegistrationOutcome.Stale.Instance, await stale);
        Assert.Equal(new NodeRegistrationOutcome.Registered(2), await merged);
        Assert.Equal(new NodeRegistrationOutcome.Registered(2), await duplicate);
        Assert.Equal(2, register.Writes);
    }

    /// <summary>
    /// A registration which a later registration of its batch retires by ordinal succession is registered, retired: its
    /// entry holds the silo's address and generation, retired at the batch's revision.
    /// </summary>
    [Fact]
    public async Task ARegistrationWhichALaterOneOfItsBatchRetiresIsRegisteredRetired()
    {
        var register = new ScriptedConditionalRegister();
        await using var committer = Committer(register);
        var cancellationToken = TestContext.Current.CancellationToken;
        var held = register.HoldNextWrite();
        var first = committer.RegisterAsync(Entry(9, 1), StoreA, nodeToReplace: null, cancellationToken);
        await held.Arrived;

        var predecessor = committer.RegisterAsync(Entry(1, 1, ordinal: 0), StoreA, nodeToReplace: null, cancellationToken);
        var successor = committer.RegisterAsync(Entry(2, 1, ordinal: 0), StoreA, Node(1), cancellationToken);
        held.Answer();
        _ = await first;

        Assert.Equal(new NodeRegistrationOutcome.Registered(2), await predecessor);
        Assert.Equal(new NodeRegistrationOutcome.Registered(2), await successor);
        Assert.Equal(2UL, (await ReadAsync(register)).Find(Node(1))!.RetiredRevision);
    }

    [Fact]
    public async Task ARepeatedRegistrationFindsItsGoalWithoutAWrite()
    {
        var register = new ScriptedConditionalRegister();
        await using var committer = Committer(register);
        var cancellationToken = TestContext.Current.CancellationToken;
        _ = await committer.RegisterAsync(Entry(1, 1), StoreA, nodeToReplace: null, cancellationToken);
        var reads = register.Reads;

        Assert.Equal(new NodeRegistrationOutcome.Registered(1), await committer.RegisterAsync(Entry(1, 1), StoreA, nodeToReplace: null, cancellationToken));

        Assert.Equal(1, register.Writes);
        Assert.Equal(reads + 1, register.Reads);
    }

    /// <summary>
    /// A committer which answered from the value it knew, from before the submission, would answer a registration whose
    /// node another committer advanced meanwhile <c>Registered</c>; the committer reads first and answers it stale.
    /// </summary>
    [Fact]
    public async Task ARegistrationIsAnsweredOnlyFromAValueFromAfterItsSubmission()
    {
        var register = new ScriptedConditionalRegister();
        await using var committer = Committer(register.Writer(0));
        await using var other = Committer(register.Writer(1));
        var cancellationToken = TestContext.Current.CancellationToken;
        _ = await committer.RegisterAsync(Entry(1, 1), StoreA, nodeToReplace: null, cancellationToken);
        _ = await other.RegisterAsync(Entry(1, 5), StoreA, nodeToReplace: null, cancellationToken);

        Assert.Equal(NodeRegistrationOutcome.Stale.Instance, await committer.RegisterAsync(Entry(1, 1), StoreA, nodeToReplace: null, cancellationToken));
    }

    [Fact]
    public async Task AWriteLostWithoutApplyingLeavesTheRegistrationItCarriedUnknown()
    {
        var register = new ScriptedConditionalRegister();
        await using var committer = Committer(register);
        var cancellationToken = TestContext.Current.CancellationToken;
        _ = await committer.RegisterAsync(Entry(1, 3), StoreA, nodeToReplace: null, cancellationToken);
        var held = register.HoldNextWrite();
        var carried = committer.RegisterAsync(Entry(2, 1), StoreA, nodeToReplace: null, cancellationToken);
        await held.Arrived;
        var reads = register.Reads;

        held.Fail(apply: false);

        var failure = await Assert.ThrowsAsync<NodeRegistryUnavailableException>(() => carried);
        _ = Assert.IsType<IOException>(failure.InnerException);
        Assert.Equal(reads + 1, register.Reads);
    }

    [Fact]
    public async Task AWriteLostAfterApplyingIsAnsweredFromOneRead()
    {
        var register = new ScriptedConditionalRegister();
        await using var committer = Committer(register);
        var cancellationToken = TestContext.Current.CancellationToken;
        _ = await committer.RegisterAsync(Entry(1, 3), StoreA, nodeToReplace: null, cancellationToken);

        // Two registrations in one batch, which waits behind a held write: one the write carries, one it does not.
        var gate = register.HoldNextWrite();
        var head = committer.RegisterAsync(Entry(4, 1), StoreA, nodeToReplace: null, cancellationToken);
        await gate.Arrived;
        var held = register.HoldNextWrite();
        var carried = committer.RegisterAsync(Entry(2, 1), StoreA, nodeToReplace: null, cancellationToken);
        var notCarried = committer.RegisterAsync(Entry(1, 2), StoreA, nodeToReplace: null, cancellationToken);
        gate.Answer();
        Assert.Equal(new NodeRegistrationOutcome.Registered(2), await head);
        await held.Arrived;
        var reads = register.Reads;
        held.Fail(apply: true);

        Assert.Equal(new NodeRegistrationOutcome.Registered(3), await carried);
        Assert.Equal(NodeRegistrationOutcome.Stale.Instance, await notCarried);
        Assert.Equal(reads + 1, register.Reads);
    }

    /// <summary>
    /// A registration the lost write carried, applied, and which a later value superseded before the read, is unknown, not
    /// stale: the committer answers a carried registration only as registered.
    /// </summary>
    [Fact]
    public async Task ACarriedRegistrationSupersededBeforeTheReadIsUnknown()
    {
        var register = new ScriptedConditionalRegister();
        await using var committer = Committer(register);
        var cancellationToken = TestContext.Current.CancellationToken;
        _ = await committer.RegisterAsync(Entry(9, 1), StoreA, nodeToReplace: null, cancellationToken);
        var held = register.HoldNextWrite();
        var carried = committer.RegisterAsync(Entry(1, 1), StoreA, nodeToReplace: null, cancellationToken);
        await held.Arrived;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        register.ReadGate = gate;
        var reads = register.Reads;
        held.Fail(apply: true);

        var applied = LastApplied(register);
        register.Put(NodeRegistryCodec.Encode(new NodeRegistryValue(applied.Revision + 1, [.. applied.Nodes.Select(static entry => entry.Node == Node(1) ? Entry(1, 2) : entry)], applied.ArchiveStore)));
        register.ReadGate = null;
        gate.SetResult();

        _ = await Assert.ThrowsAsync<NodeRegistryUnavailableException>(() => carried);
        Assert.Equal(reads + 1, register.Reads);
    }

    [Fact]
    public async Task AWriteWhichAppliedAndAnsweredMismatchIsRegisteredFromTheRead()
    {
        var register = new ScriptedConditionalRegister();
        await using var committer = Committer(register);
        var cancellationToken = TestContext.Current.CancellationToken;
        var held = register.HoldNextWrite();
        var registration = committer.RegisterAsync(Entry(1, 1), StoreA, nodeToReplace: null, cancellationToken);
        await held.Arrived;
        held.ApplyAndAnswerMismatch();

        Assert.Equal(new NodeRegistrationOutcome.Registered(1), await registration);
        Assert.Equal(1, register.AppliedMismatches);
        Assert.Equal(1, register.Writes);
    }

    [Fact]
    public async Task AFailedReadAfterAnUnknownOutcomeLeavesTheBatchUnknown()
    {
        var register = new ScriptedConditionalRegister();
        await using var committer = Committer(register);
        var cancellationToken = TestContext.Current.CancellationToken;
        var held = register.HoldNextWrite();
        var carried = committer.RegisterAsync(Entry(1, 1), StoreA, nodeToReplace: null, cancellationToken);
        await held.Arrived;
        register.ReadFailure = new TimeoutException("The read failed.");
        held.Fail(apply: true);

        var failure = await Assert.ThrowsAsync<NodeRegistryUnavailableException>(() => carried);
        _ = Assert.IsType<IOException>(failure.InnerException);
        register.ReadFailure = null;
        Assert.Equal(new NodeRegistrationOutcome.Registered(1), await committer.RegisterAsync(Entry(1, 1), StoreA, nodeToReplace: null, cancellationToken));
    }

    /// <summary>
    /// A register which holds no node registry fails a registration with the rejection, which the silo does not retry.
    /// </summary>
    [Fact]
    public async Task ACorruptRegistryFailsARegistrationWithTheRejection()
    {
        var register = new ScriptedConditionalRegister();
        register.Put([1, 2, 3]);
        await using var committer = Committer(register);

        var corrupt = await Assert.ThrowsAsync<InvalidDataException>(() => committer.RegisterAsync(Entry(1, 1), StoreA, nodeToReplace: null, TestContext.Current.CancellationToken));

        Assert.False(Hosting.DurableStorageNode.IsUnknownOutcome(corrupt, CancellationToken.None));
        Assert.Equal(0, register.Writes);
    }

    /// <summary>
    /// A read after an unknown outcome which finds the register corrupt leaves the registration the write carried unknown,
    /// the rejection inside, since the write may have landed first; its silo registers again (conventions.md §6).
    /// </summary>
    [Fact]
    public async Task ACorruptReadAfterAnUnknownOutcomeLeavesTheCarriedRegistrationUnknown()
    {
        var register = new ScriptedConditionalRegister();
        await using var committer = Committer(register);
        var held = register.HoldNextWrite();
        var carried = committer.RegisterAsync(Entry(1, 1), StoreA, nodeToReplace: null, TestContext.Current.CancellationToken);
        await held.Arrived;
        register.Put([1, 2, 3]);
        held.Fail(apply: false);

        var unknown = await Assert.ThrowsAsync<NodeRegistryUnavailableException>(() => carried);
        _ = Assert.IsType<InvalidDataException>(unknown.InnerException);
        Assert.True(Hosting.DurableStorageNode.IsUnknownOutcome(unknown, CancellationToken.None));
    }

    /// <summary>
    /// A MetaStore client's own timeout, a cancellation the committer did not cause, leaves the outcome unknown; it never
    /// reaches the caller as a cancellation.
    /// </summary>
    [Fact]
    public async Task ACancellationFromTheMetaStoreIsAnUnknownOutcome()
    {
        var scripted = new ScriptedConditionalRegister();
        var register = new FailingRegister(scripted) { WriteFailure = new TaskCanceledException("The client timed out.") };
        await using var committer = Committer(register);
        var cancellationToken = TestContext.Current.CancellationToken;

        var failure = await Assert.ThrowsAsync<NodeRegistryUnavailableException>(() => committer.RegisterAsync(Entry(1, 1), StoreA, nodeToReplace: null, cancellationToken));
        _ = Assert.IsType<TaskCanceledException>(failure.InnerException);

        var unreadable = new FailingRegister(new ScriptedConditionalRegister()) { ReadFailure = new TaskCanceledException("The client timed out.") };
        await using var reading = Committer(unreadable);
        failure = await Assert.ThrowsAsync<NodeRegistryUnavailableException>(() => reading.RegisterAsync(Entry(2, 1), StoreA, nodeToReplace: null, cancellationToken));
        _ = Assert.IsType<TaskCanceledException>(failure.InnerException);
    }

    [Fact]
    public async Task AMismatchDecidesTheBatchAgainAgainstTheValueRead()
    {
        var register = new ScriptedConditionalRegister();
        await using var committer = Committer(register.Writer(0));
        await using var other = Committer(register.Writer(1));
        var cancellationToken = TestContext.Current.CancellationToken;
        _ = await committer.RegisterAsync(Entry(1, 1), StoreA, nodeToReplace: null, cancellationToken);
        _ = await other.RegisterAsync(Entry(2, 1), StoreA, nodeToReplace: null, cancellationToken);

        // The committer's known version is stale, so its write mismatches; it reads, and writes again over revision 2.
        var held = register.HoldNextWrite();
        var registration = committer.RegisterAsync(Entry(1, 2), StoreA, nodeToReplace: null, cancellationToken);
        await held.Arrived;
        held.Answer();

        Assert.Equal(new NodeRegistrationOutcome.Registered(3), await registration);
        Assert.Equal(2, (await ReadAsync(register)).Nodes.Length);
    }

    /// <summary>
    /// An exception of the fold is a committer defect, not an unknown outcome: a register at the last revision makes the
    /// fold's checked increment overflow, the batch fails with <see cref="InvalidOperationException"/>, nothing is written,
    /// and the silo does not register again.
    /// </summary>
    [Fact]
    public async Task AnExceptionOfTheFoldFailsTheBatchAsADefect()
    {
        var register = new ScriptedConditionalRegister();
        register.Put(NodeRegistryCodec.Encode(new NodeRegistryValue(ulong.MaxValue, [Entry(1, 1)], StoreA)));
        await using var committer = Committer(register);

        var defect = await Assert.ThrowsAsync<InvalidOperationException>(() => committer.RegisterAsync(Entry(2, 1), StoreA, nodeToReplace: null, TestContext.Current.CancellationToken));

        _ = Assert.IsType<OverflowException>(defect.InnerException);
        Assert.Equal(0, register.Writes);
        Assert.False(Hosting.DurableStorageNode.IsUnknownOutcome(defect, CancellationToken.None));
    }

    /// <summary>
    /// A value which would break OR-I2 is never written: over a register another writer left holding two active entries at
    /// one ordinal, any write would keep both, so the check refuses it and the batch fails with
    /// <see cref="InvalidOperationException"/>, with no write.
    /// </summary>
    [Fact]
    public async Task AValueWhichBreaksTheStepIsNeverWritten()
    {
        var register = new ScriptedConditionalRegister();
        register.Put(NodeRegistryCodec.Encode(new NodeRegistryValue(1, [Entry(1, 1, ordinal: 0), Entry(2, 1, ordinal: 0)], StoreA)));
        await using var committer = Committer(register);

        var defect = await Assert.ThrowsAsync<InvalidOperationException>(() => committer.RegisterAsync(Entry(3, 1), StoreA, nodeToReplace: null, TestContext.Current.CancellationToken));

        Assert.Contains("OR-I2", defect.Message, StringComparison.Ordinal);
        Assert.Equal(0, register.Writes);
        Assert.Equal(1UL, (await ReadAsync(register)).Revision);
    }

    /// <summary>
    /// Orleans builds a deserialized entry without its constructor, and carries the default store identity, which names no
    /// store, so the committer checks each: an entry <c>CLNR</c> cannot hold and the default store are refused before they
    /// are folded, and nothing is written.
    /// </summary>
    [Fact]
    public async Task AnEntryOrStoreCLNRCannotHoldIsRefusedBeforeItIsFolded()
    {
        var register = new ScriptedConditionalRegister();
        await using var committer = Committer(register);
        var cancellationToken = TestContext.Current.CancellationToken;
        var unconstructed = (NodeRegistryEntry)RuntimeHelpers.GetUninitializedObject(typeof(NodeRegistryEntry));
        var noGeneration = Bypass(Entry(1, 1), "Silo", Silo(1, 0));
        var negativeOrdinal = Bypass(Entry(1, 1), "Ordinal", (int?)-1);
        var loneSurrogate = Bypass(Entry(1, 1), "FailureDomain", "zone-\ud800");
        var noStore = default(ArchiveStoreIdentity);

        foreach (var entry in new[] { unconstructed, noGeneration, negativeOrdinal, loneSurrogate, Entry(1, 1).Retire(1) })
        {
            _ = await Assert.ThrowsAnyAsync<ArgumentException>(() => committer.RegisterAsync(entry, StoreA, nodeToReplace: null, cancellationToken));
        }

        var noStoreFailure = await Assert.ThrowsAnyAsync<ArgumentException>(() => committer.RegisterAsync(Entry(1, 1), noStore, nodeToReplace: null, cancellationToken));
        Assert.Equal("store", noStoreFailure.ParamName);

        // Orleans carries a default node identity named for replacement as it does any other.
        _ = await Assert.ThrowsAnyAsync<ArgumentException>(() => committer.RegisterAsync(Entry(1, 1), StoreA, default(Orleans.Durable.Storage.Engine.Ledgers.NodeId), cancellationToken));

        Assert.Equal(0, register.Writes);
        Assert.Equal(new NodeRegistrationOutcome.Registered(1), await committer.RegisterAsync(Entry(1, 1), StoreA, nodeToReplace: null, cancellationToken));
    }

    /// <summary>
    /// Disposal answers a queued registration with <see cref="ObjectDisposedException"/>, cancels the write in flight,
    /// whose registration is answered so too, and completes once every admitted registration has its answer.
    /// </summary>
    [Fact]
    public async Task DisposalAnswersEveryAdmittedRegistration()
    {
        var register = new ScriptedConditionalRegister();
        var committer = Committer(register);
        var cancellationToken = TestContext.Current.CancellationToken;
        var held = register.HoldNextWrite();
        var inFlight = committer.RegisterAsync(Entry(1, 1), StoreA, nodeToReplace: null, cancellationToken);
        await held.Arrived;
        var queued = committer.RegisterAsync(Entry(2, 1), StoreA, nodeToReplace: null, cancellationToken);

        await committer.DisposeAsync();

        _ = await Assert.ThrowsAsync<ObjectDisposedException>(() => queued);
        _ = await Assert.ThrowsAsync<ObjectDisposedException>(() => inFlight);
        _ = await Assert.ThrowsAsync<ObjectDisposedException>(() => committer.RegisterAsync(Entry(3, 1), StoreA, nodeToReplace: null, cancellationToken));
    }

    /// <summary>A caller's cancellation ends its wait only: the registration stays in its batch, and applies.</summary>
    [Fact]
    public async Task ACallersCancellationEndsOnlyItsWait()
    {
        var register = new ScriptedConditionalRegister();
        await using var committer = Committer(register);
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var held = register.HoldNextWrite();
        var cancelled = committer.RegisterAsync(Entry(1, 1), StoreA, nodeToReplace: null, caller.Token);
        await held.Arrived;

        await caller.CancelAsync();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        held.Answer();

        Assert.Equal(new NodeRegistrationOutcome.Registered(1), await committer.RegisterAsync(Entry(1, 1), StoreA, nodeToReplace: null, TestContext.Current.CancellationToken));
        Assert.Equal(1, register.Writes);
    }

    private static T Bypass<T>(T value, string property, object? field)
        where T : class
    {
        var copy = (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
        foreach (var backing in typeof(T).GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
        {
            backing.SetValue(copy, backing.Name == $"<{property}>k__BackingField" ? field : backing.GetValue(value));
        }

        return copy;
    }

    private static NodeRegistryValue LastApplied(ScriptedConditionalRegister register) =>
        Assert.IsType<NodeRegistryValue>(NodeRegistryCodec.Decode(register.AppliedWrites[^1].AsSpan()).Value);

    /// <summary>A register whose reads or writes throw a set exception, as a client's own timeout does.</summary>
    private sealed class FailingRegister(ScriptedConditionalRegister register) : IConditionalRegister
    {
        public Exception? WriteFailure { get; set; }

        public Exception? ReadFailure { get; set; }

        public ValueTask<MetaStoreEntry?> ReadAsync(CancellationToken cancellationToken = default) =>
            ReadFailure is { } failure ? ValueTask.FromException<MetaStoreEntry?>(failure) : register.ReadAsync(cancellationToken);

        public ValueTask<RegisterWriteOutcome> WriteAsync(MetaStoreVersion? expectedVersion, ReadOnlySequence<byte> value, CancellationToken cancellationToken = default) =>
            WriteFailure is { } failure ? ValueTask.FromException<RegisterWriteOutcome>(failure) : register.WriteAsync(expectedVersion, value, cancellationToken);
    }
}
