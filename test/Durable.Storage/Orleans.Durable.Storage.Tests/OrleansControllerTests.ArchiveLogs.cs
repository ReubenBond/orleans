using System.Buffers;
using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Core.Internal;
using Orleans.Durable.Storage.AzureStorage.Archive;
using Orleans.Durable.Storage.Control;
using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Archive.Logs;
using Orleans.Durable.Storage.Engine.Archive.Lsm;
using Orleans.Durable.Storage.Engine.Archive.Lsm.Control;
using Orleans.Durable.Storage.Engine.Cluster.Control;
using Orleans.Durable.Storage.Engine.Journals;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Control;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Durable.Storage.Engine.ObjectStore.Memory;
using Orleans.Durable.Storage.Hosting;
using Orleans.Durable.Storage.Testing;
using Orleans.Durable.Journaling;
using Orleans.Runtime;
using Orleans.TestingHost;

namespace Orleans.Durable.Storage.Tests;

/// <summary>
/// The composition over its archive store (orleans.md §5.3, §5.6, §5.13, OR-I8, OR-I20): a silo over another store of the
/// cluster's layout, refused, which serves and drains nothing and hands its ledgers off; the store's record across an
/// unknown outcome and a controller's restart; a silo joining with another store prefix; two clusters sharing one bucket
/// under their own prefixes; a blocked key on archive logs, reported and resumed; and the replace requests archive logs'
/// tokens make.
/// </summary>
public sealed partial class OrleansControllerTests
{
    /// <summary>
    /// OR-I20, OR-I8: a silo joining over another store of the cluster's layout, whose record holds the same store fields
    /// and parameters and another ID, registers with the registry's identity and is refused by its background check,
    /// with event 6241 naming the record's <c>id</c>: OR-I8's check alone, which compares store fields and parameters, would
    /// pass it. It serves its replicas and its control plane, and nothing which reads or writes the archive: its gate is
    /// refused, it writes no archive log, a sweep asked of its host lists and deletes nothing, ending at the refusal with
    /// event 6245 once, and on archive logs it creates nothing in the other container, which it read without creating
    /// anything.
    /// </summary>
    [Theory(Timeout = 120_000)]
    [InlineData(ArchiveStoreKind.Lsm)]
    [InlineData(ArchiveStoreKind.Logs)]
    public async Task ASiloOverAnotherStoreOfTheSameLayoutIsRefused(ArchiveStoreKind kind)
    {
        _state.ArchiveStore = kind;
        var cluster = await DeployAsync(3, TestContext.Current.CancellationToken);
        var other = OtherStore("Secondary_3");
        var otherRecord = await CreateRecordAsync(kind, other, _state.OtherStores["Secondary_3"].Logs);
        var registered = await RegisteredStoreAsync();
        Assert.NotEqual(registered.Id, otherRecord.Id);
        var before = await ListAsync(other);

        var added = await JoinAsync(cluster);

        await AssertRefusedAsync(added.Name, 6241);
        Assert.Contains(_state.StopEvents, line => line.Contains($"{added.Name}: Critical 6241:", StringComparison.Ordinal)
            && line.Contains($"holds id as {otherRecord.Id}, where {registered.Id} is expected", StringComparison.Ordinal));
        Assert.Equal(registered, RegisteredWith(added));
        Assert.True(Archive(added).Gate.Passed.IsFaulted);

        // A sweep asked of the refused silo's host sweeps nothing: the host ends at the refusal, logging 6245 once.
        var sweepRefusal = _state.EventsAsync(line => line.Contains($"{added.Name}: Critical 6245:", StringComparison.Ordinal));
        var host = Services(added).GetRequiredService<SweepHost>();
        host.Request();
        await Bounded(sweepRefusal, "The refused silo's sweep refusal event");
        await EventuallyAsync(() => host.Ended || host.Sweeps > 0, "the refused silo's sweep host to end, or to sweep");
        Assert.Equal(0, host.Sweeps);
        Assert.True(host.Ended);
        Assert.Single(_state.StopEvents, line => line.Contains($"{added.Name}: Critical 6245:", StringComparison.Ordinal));
        Assert.Equal((0, 0), (_state.StoreSweepCalls.GetValueOrDefault(added.Name), _state.LogWrites.GetValueOrDefault(added.Name)));
        Assert.Equal(before, await ListAsync(other));
    }

    /// <summary>
    /// OR-I20, orleans.md §5.13: a silo of the other kind joining a cluster over the cluster's own container, an LSM silo
    /// beside archive logs or the reverse, registers with the registry's identity and is refused by its kind before its
    /// check makes any request: event 6241 names the field <c>kind</c>, its gate faults, and it creates, writes, lists,
    /// and deletes nothing, so the container holds nothing of the joiner's kind: no object under <c>durable-storage/</c> beside the
    /// LSM, and only those beside archive logs.
    /// </summary>
    [Theory(Timeout = 120_000)]
    [InlineData(ArchiveStoreKind.Logs, ArchiveStoreKind.Lsm)]
    [InlineData(ArchiveStoreKind.Lsm, ArchiveStoreKind.Logs)]
    public async Task ASiloOfTheOtherKindOverTheClustersContainerIsRefusedByItsKind(ArchiveStoreKind cluster, ArchiveStoreKind joiner)
    {
        const string Joiner = "Secondary_3";
        _state.ArchiveStore = cluster;
        _state.Node = (silo, options) => options.ArchiveStore = silo == Joiner ? joiner : cluster;
        var deployed = await DeployAsync(3, TestContext.Current.CancellationToken);
        _ = await ServedAsync(deployed, ranges: 3);
        var before = await ListAsync(_state.Objects);
        Assert.All(before, name => Assert.Equal(cluster == ArchiveStoreKind.Logs, name.StartsWith("durable-storage/", StringComparison.Ordinal)));

        var added = await JoinAsync(deployed);

        Assert.Equal(Joiner, added.Name);
        await AssertRefusedAsync(Joiner, 6241);
        Assert.Contains(_state.StopEvents, line => line.Contains($"{Joiner}: Critical 6241:", StringComparison.Ordinal)
            && line.Contains($"holds kind as {joiner}, where {cluster} is expected", StringComparison.Ordinal));
        Assert.True(Archive(added).Gate.Passed.IsFaulted);
        Assert.Equal((0, 0, 0), (_state.StoreWrites.GetValueOrDefault(Joiner), _state.StoreSweepCalls.GetValueOrDefault(Joiner), _state.LogWrites.GetValueOrDefault(Joiner)));
        Assert.All(await ListAsync(_state.Objects), name => Assert.Equal(cluster == ArchiveStoreKind.Logs, name.StartsWith("durable-storage/", StringComparison.Ordinal)));
    }

    /// <summary>
    /// OR-I20, orleans.md §5.13: a silo joining with another store prefix than the one the node registry records is
    /// refused by its prefix before its check makes any request, with event 6241 naming the field <c>prefix</c>, though the
    /// test's stores show it the cluster's own objects, whose record the ID check alone would pass: its gate faults, and it
    /// creates, writes, lists, and deletes nothing, as its store calls count them.
    /// </summary>
    [Theory(Timeout = 120_000)]
    [InlineData(ArchiveStoreKind.Lsm)]
    [InlineData(ArchiveStoreKind.Logs)]
    public async Task ASiloJoiningWithAnotherStorePrefixIsRefused(ArchiveStoreKind kind)
    {
        const string Joiner = "Secondary_3";
        _state.ArchiveStore = kind;
        _state.Node = (silo, options) => options.ArchiveStorePrefix = silo == Joiner ? "other" : "t";
        var deployed = await DeployAsync(3, TestContext.Current.CancellationToken);
        _ = await ServedAsync(deployed, ranges: 3);

        var added = await JoinAsync(deployed);

        Assert.Equal(Joiner, added.Name);
        await AssertRefusedAsync(Joiner, 6241);
        Assert.Contains(_state.StopEvents, line => line.Contains($"{Joiner}: Critical 6241:", StringComparison.Ordinal)
            && line.Contains("holds prefix as other, where t is expected", StringComparison.Ordinal));
        Assert.Equal(new ArchiveStorePrefix("t"), RegisteredWith(added).Prefix);
        Assert.True(Archive(added).Gate.Passed.IsFaulted);
        Assert.Equal((0, 0, 0), (_state.StoreWrites.GetValueOrDefault(Joiner), _state.StoreSweepCalls.GetValueOrDefault(Joiner), _state.LogWrites.GetValueOrDefault(Joiner)));
    }

    /// <summary>
    /// orleans.md §5.13, archive-lsm.md §9, archive-logs.md §9: two clusters configured on one bucket, each with its own
    /// Orleans service ID and so its own derived store prefix, run independently: each records its own store, under its
    /// own prefix, with its own ID, each silo's archive gate passes over its own store, and each cluster drains a write of
    /// a key of its own and drops the ledger which held it. Every object in the shared bucket lies under one of the two
    /// prefixes, and each prefix holds its cluster's drain: on the LSM its manifest, and on archive logs its drain markers
    /// and, in its logs, its own key's log alone.
    /// </summary>
    [Theory(Timeout = 180_000)]
    [InlineData(ArchiveStoreKind.Lsm)]
    [InlineData(ArchiveStoreKind.Logs)]
    public async Task TwoClustersSharingOneBucketKeepEachToItsOwnPrefix(ArchiveStoreKind kind)
    {
        var objects = new WriteRecordingObjectStore(new InMemoryObjectStore());
        var container = new MemoryArchiveLogContainer(objects);
        (IObjectStore, IArchiveLogContainer) shared = (objects, container);
        _state.ArchiveStore = kind;
        _state.Prefix = null;
        _state.ServiceId = "clog-shared-a";
        _state.Shared = shared;
        _state.Control = static options => options.Replicas = 1;
        var other = new TestState { ArchiveStore = kind, Prefix = null, ServiceId = "clog-shared-b", Shared = shared, Control = static options => options.Replicas = 1 };
        States[other.Id] = other;
        TestCluster? second = null;
        try
        {
            var first = await DeployAsync(1, TestContext.Current.CancellationToken);
            second = await DeployOtherAsync(other);
            _ = await Archive(Assert.IsType<SiloHandle>(first.Primary, exactMatch: false)).Gate.Passed.WaitAsync(Patience, Token);
            _ = await Archive(Assert.IsType<SiloHandle>(second.Primary, exactMatch: false)).Gate.Passed.WaitAsync(Patience, Token);

            // The stores are each cluster's own before either drains, so a store shared by a bypassed prefix fails here.
            var (a, b) = (ArchiveStorePrefix.Of("clog-shared-a"), ArchiveStorePrefix.Of("clog-shared-b"));
            Assert.NotEqual(a, b);
            var ours = await RegisteredStoreAsync();
            var theirs = Assert.NotNull((await NodeRegistryCodec.ReadAsync(await other.MetaStore.GetRegisterAsync(other.Cluster, NodeRegistryValue.RegisterName, Token), Token)).Value.ArchiveStore);
            Assert.Equal((kind, a, kind, b), (ours.Kind, ours.Prefix, theirs.Kind, theirs.Prefix));
            Assert.NotEqual(ours.Id, theirs.Id);
            var record = kind == ArchiveStoreKind.Lsm ? ObjectName.Store.Value : ArchiveLogNames.StoreRecord;
            var recorded = await ListAsync(objects);
            Assert.Contains($"{a}/{record}", recorded);
            Assert.Contains($"{b}/{record}", recorded);
            Assert.All(recorded, name => Assert.True(name.StartsWith($"{a}/", StringComparison.Ordinal) || name.StartsWith($"{b}/", StringComparison.Ordinal), $"'{name}' lies under neither cluster's prefix."));

            var ourKey = await DrainAKeyAsync(first, _state, "cluster-a");
            var theirKey = await DrainAKeyAsync(second, other, "cluster-b");

            var names = await ListAsync(objects);
            Assert.All(names.Concat(objects.Written), name => Assert.True(name.StartsWith($"{a}/", StringComparison.Ordinal) || name.StartsWith($"{b}/", StringComparison.Ordinal), $"'{name}' lies under neither cluster's prefix."));
            foreach (var prefix in new[] { a, b })
            {
                Assert.Contains($"{prefix}/{record}", names);
                if (kind == ArchiveStoreKind.Lsm)
                {
                    Assert.Contains($"{prefix}/{ObjectName.Manifest.Value}", names);
                }
                else
                {
                    // A dropped ledger's drain markers are swept, so the test looks for their writes, not for the markers.
                    Assert.Contains(objects.Written, name => name.StartsWith($"{prefix}/{ArchiveLogNames.DrainMarkerPrefix}", StringComparison.Ordinal));
                }
            }

            if (kind == ArchiveStoreKind.Logs)
            {
                await AssertHoldsOnlyAsync(container.LogsOf(a.Value), ourKey, "cluster-a", theirKey);
                await AssertHoldsOnlyAsync(container.LogsOf(b.Value), theirKey, "cluster-b", ourKey);
            }
        }
        finally
        {
            if (second is not null)
            {
                await second.StopAllSilosAsync(Token).WaitAsync(Patience, Token);
                await second.DisposeAsync();
            }

            _ = States.TryRemove(other.Id, out _);
        }

        // A cluster's logs hold its own key's log, with its write, and not the other cluster's.
        static async Task AssertHoldsOnlyAsync(MemoryArchiveLogStore logs, string own, string body, string others)
        {
            var read = Assert.IsType<ArchiveLogRead.Read>((await logs.ReadAsync(OrleansJournalLogNames.Names.Of(JournalRowKey.Encode(own).Span), Token)).Value);
            using (read)
            {
                Assert.True(read.Content.ToArray().AsSpan().IndexOf(Encoding.UTF8.GetBytes(body)) >= 0, $"The log of '{own}' does not hold its write.");
            }

            _ = Assert.IsType<ArchiveLogRead.Absent>((await logs.ReadAsync(OrleansJournalLogNames.Names.Of(JournalRowKey.Encode(others).Span), Token)).Value);
        }
    }

    /// <summary>
    /// Appends a write to a key of <paramref name="journal"/>'s own in <paramref name="cluster"/>, whose state
    /// <paramref name="state"/> holds, rolls the ledger which holds it, and waits for that ledger's drop, which its drain
    /// into the cluster's archive store precedes; returns the key.
    /// </summary>
    private static async Task<string> DrainAKeyAsync(TestCluster cluster, TestState state, string journal)
    {
        var primary = Assert.IsType<SiloHandle>(cluster.Primary, exactMatch: false);
        LedgerMetadata? served = null;
        await EventuallyAsync(
            async () => state.Journals.TryGetValue(primary.Name, out var journals) && journals.State == LocalJournals.JournalsState.Serving
                && (served = (await ReadMapAsync(state)).Ledgers.FirstOrDefault(ledger => ledger.IsOpen && Runtime(primary)?.Writers.TryGet(ledger.Id) is not null)) is not null,
            $"{journal}'s cluster to serve its journals and a ledger");
        var range = served!.Value.Range;
        var key = Enumerable.Range(0, int.MaxValue).Select(index => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{journal}-{index}"))
            .First(key => range.Contains(JournalRowKey.Encode(key).Span));
        await AppendAsync(cluster, key, Encoding.UTF8.GetBytes(journal));
        var holding = (await ReadMapAsync(state)).Ledgers.First(open => open.IsOpen && open.Range.Contains(JournalRowKey.Encode(key).Span));
        var dropped = state.DroppedAsync(holding.Id);
        await Controller(cluster).RequestRollAsync(holding.Id);
        await Bounded(dropped, $"The drop of {journal}'s ledger");
        return key;
    }

    /// <summary>
    /// OR-I20, orleans.md §5.6, #274's D2 and D3: a silo whose record check finds another store of the cluster's layout
    /// hands its ledgers off and drains nothing: its check is held while it is checking, which leaves it eligible, until
    /// balancing makes it a primary; its writer then takes a journal's append and its ledger rolls, so the node owes the
    /// closed ledger's drain. Once released, the check finds the record's ID another, logs event 6241, and the node asks
    /// to hand its ledgers off at once and each control interval, so its ledgers move to other nodes and none comes back.
    /// It submits no drain and no drop of the ledger it owed and lists or writes nothing in the other store, which keeps
    /// its listing; another node drains the ledger, which drops, and the journal reads its append.
    /// </summary>
    [Theory(Timeout = 180_000)]
    [InlineData(ArchiveStoreKind.Lsm)]
    [InlineData(ArchiveStoreKind.Logs)]
    public async Task ASiloWhoseRecordNamesAnotherStoreHandsItsLedgersOff(ArchiveStoreKind kind)
    {
        const string Refused = "Secondary_3";
        _state.ArchiveStore = kind;
        var other = OtherStore(Refused);
        _ = await CreateRecordAsync(kind, other, _state.OtherStores[Refused].Logs);
        _state.Control = static options => options.ClosedDrainTimeout = TimeSpan.FromSeconds(2);
        var reads = _state.RecordReads.Hold(Refused);
        var cluster = await DeployAsync(4, TestContext.Current.CancellationToken);
        await reads.WaitAsync(Patience, Token);
        var node = _state.LocalNodes[Refused].Node;
        LedgerMetadata owed;
        string key;
        while (true)
        {
            // Balancing may move the ledger off the node between the map read and the proposal, which fences the writer;
            // the node is then made primary of another, which the test takes instead.
            var primaryOf = await ServedAsync(cluster, ranges: 4, done: value => value.Ledgers.Any(ledger => ledger.IsOpen && ledger.Primary.Node == node));
            Assert.Equal(LocalJournals.JournalsState.Checking, _state.Journals[Refused].State);
            owed = primaryOf.Ledgers.First(ledger => ledger.IsOpen && ledger.Primary.Node == node);
            key = KeyIn(owed);
            if (Runtime(cluster.Silos.Single(silo => silo.Name == Refused))!.Writers.TryGet(owed.Id) is not LedgerWriter writer)
            {
                continue;
            }

            using var payload = Payload("owed"u8.ToArray());
            using var row = new Row(JournalRowKey.Encode(key).Span.ToArray(), RowKind.Append, payload);
            var proposed = (await writer.ProposeAsync([row], Token)).Value;
            if (proposed is ProposalOutcome.Durable)
            {
                break;
            }

            _ = Assert.IsType<ProposalOutcome.Fenced>(proposed);
        }

        await Controller(cluster).RequestRollAsync(owed.Id);
        await EventuallyAsync(async () => (await ReadMapAsync()).Get(owed.Id) is { IsOpen: false }, $"s{owed.Id} to close");
        var before = await ListAsync(other);
        var dropped = _state.DroppedAsync(owed.Id);

        _state.RecordReads.Release();

        await AssertRefusedAsync(Refused, 6241);
        var handedOff = await ServedAsync(cluster, ranges: 4, done: value => !value.Ledgers.Any(ledger => ledger.IsOpen && ledger.Primary.Node == node));

        // The node keeps asking, and the controller keeps passing, each of which could give it a ledger back.
        var handOffs = _state.HandOffRequests.GetValueOrDefault(node);
        var passes = Passes(cluster);
        await EventuallyAsync(() => _state.HandOffRequests.GetValueOrDefault(node) >= handOffs + 2 && Passes(cluster) >= passes + 2, "two more hand-off requests and controller passes");
        var later = await ReadMapAsync();
        Assert.DoesNotContain(later.Ledgers, ledger => ledger.IsOpen && ledger.Primary.Node == node);
        Assert.DoesNotContain(later.Ledgers, ledger => ledger.Id.Value >= handedOff.NextLedgerId.Value && ledger.Replicas.Contains(node));
        Assert.Equal(0, _state.LogWrites.GetValueOrDefault(Refused));

        // V8, D16: the refused node reports its hand-off, a gate not passed, and, though it owes the closed ledger's drain,
        // no undrained keys and no rate, which its drains, stopped by the refused gate, would never work off.
        var report = _state.LatestReports[node];
        Assert.Equal((true, false, 0L), (report.HandingOff, report.ArchiveGatePassed, report.UndrainedKeys));
        Assert.Null(report.ArchiveLogRequestsPerSecond);

        await Bounded(dropped, "The ledger's drop");
        Assert.DoesNotContain(_state.ArchiveSubmissions, submission => submission.Silo == Refused);
        Assert.Equal(0, _state.StoreSweepCalls.GetValueOrDefault(Refused));
        Assert.Equal(before, await ListAsync(other));
        await AssertJournalAsync(cluster, key, "owed"u8.ToArray());
    }

    /// <summary>
    /// OR-I20: a new cluster's first registration records its store's record, read again when the answer of its create is
    /// lost, so the registry holds the record's identity; a controller grain's restart and a silo which joins after it
    /// take that identity from the registry, and the store holds the one record it always held.
    /// </summary>
    [Theory(Timeout = 120_000)]
    [InlineData(ArchiveStoreKind.Lsm)]
    [InlineData(ArchiveStoreKind.Logs)]
    public async Task TheFirstRegistrationsRecordHoldsAcrossALostAnswerAndAControllerRestart(ArchiveStoreKind kind)
    {
        _state.ArchiveStore = kind;
        _state.LoseRecordCreates = 1;
        var cluster = await DeployAsync(1, TestContext.Current.CancellationToken);
        Assert.Equal(0, _state.LoseRecordCreates);
        var record = await StoredRecordAsync(kind);
        Assert.Equal(new ArchiveStoreIdentity(kind, new ArchiveStorePrefix("t"), record.Id), await RegisteredStoreAsync());

        await EventuallyAsync(() => _state.ControllerActivation is not null, "a call to the controller grain");
        var restarted = _state.ControllerActivation;
        await Controller(cluster).AsReference<IGrainManagementExtension>().DeactivateOnIdle(Token);
        await EventuallyAsync(() => _state.ControllerActivation is { } activation && activation != restarted, "the controller grain's next activation");
        var added = await JoinAsync(cluster);

        Assert.Equal(new ArchiveStoreIdentity(kind, new ArchiveStorePrefix("t"), record.Id), RegisteredWith(added));
        Assert.Equal(new ArchiveStoreIdentity(kind, new ArchiveStorePrefix("t"), record.Id), await RegisteredStoreAsync());
        Assert.Equal(record.Encode(), (await StoredRecordAsync(kind)).Encode());
        await JournalsServeAsync(cluster);
    }

    /// <summary>
    /// orleans.md §5.13, OR-I20: a definite answer of the store is definite. A silo joining a cluster on archive logs over a
    /// container which does not exist, on the fake Azure transport, reads the store's record as absent, since no object
    /// lies in a missing container, and its gate refuses it as another store's (event 6241 naming the record's <c>id</c>):
    /// it hands its ledgers off and writes nothing, rather than retrying the store's 404 as an unknown outcome.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ASiloJoiningOverAMissingContainerIsRefusedAndHandsItsLedgersOff()
    {
        _state.ArchiveStore = ArchiveStoreKind.Logs;
        _state.Azure = new FakeAzureBlobService();
        var cluster = await DeployAsync(3, TestContext.Current.CancellationToken);
        _ = await ServedAsync(cluster, ranges: 3);
        var missing = _state.Azure.MissingContainerClient();
        _state.OtherStores["Secondary_3"] = (new AzureBlobObjectStore(missing), new AzureArchiveLogStore(missing, new JournaledStateManagerOptions().JournalFormatKey));
        var refused = _state.EventsAsync(static line => line.Contains("Secondary_3: Critical 6241:", StringComparison.Ordinal));

        var added = await JoinAsync(cluster);

        await Bounded(refused, "The silo's refusal");
        await AssertRefusedAsync(added.Name, 6241);
        Assert.Contains(_state.StopEvents, line => line.Contains($"{added.Name}: Critical 6241:", StringComparison.Ordinal) && line.Contains("holds id as nothing", StringComparison.Ordinal));
        Assert.True(Archive(added).Gate.Passed.IsFaulted);
        Assert.Equal(0, _state.LogWrites.GetValueOrDefault(added.Name));
    }

    /// <summary>
    /// orleans.md §5.3, §5.13: a new cluster's first silo over a container which does not exist cannot create the store's
    /// record, its create refused with 404 <c>ContainerNotFound</c>, a definite failure which no retry changes, so the silo
    /// does not start, its failure naming the container, and the registry records no store.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AFirstSiloOverAMissingContainerDoesNotStart()
    {
        _state.ArchiveStore = ArchiveStoreKind.Logs;
        _state.Azure = new FakeAzureBlobService();
        var missing = _state.Azure.MissingContainerClient();
        _state.OtherStores["Primary"] = (new AzureBlobObjectStore(missing), new AzureArchiveLogStore(missing, new JournaledStateManagerOptions().JournalFormatKey));

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => DeployAsync(1, TestContext.Current.CancellationToken));

        Assert.Contains(Chain(failure), static exception => exception is ObjectStoreRefusedException refusal
            && refusal.Message.Contains(FakeAzureBlobService.MissingContainer, StringComparison.Ordinal));
        Assert.Null((await ReadRegistryAsync()).ArchiveStore);
    }

    /// <summary>
    /// orleans.md §5.13, §6: a silo whose Orleans ServiceId is one Orleans gives by default, its own or localhost
    /// clustering's, and which configures no store prefix, does not start, its failure naming both settings which remedy
    /// it: two clusters which kept the default on one bucket would otherwise share one store. It writes nothing to the
    /// bucket, and the registry records no store.
    /// </summary>
    [Theory(Timeout = 120_000)]
    [InlineData(ArchiveStoreKind.Lsm, ClusterOptions.DefaultServiceId)]
    [InlineData(ArchiveStoreKind.Logs, "dev")]
    public async Task ASiloWithADefaultServiceIdAndNoStorePrefixDoesNotStart(ArchiveStoreKind kind, string serviceId)
    {
        var objects = new InMemoryObjectStore();
        _state.ArchiveStore = kind;
        _state.Prefix = null;
        _state.ServiceId = serviceId;
        _state.Shared = (objects, new MemoryArchiveLogContainer(objects));

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => DeployAsync(1, TestContext.Current.CancellationToken));

        Assert.Contains(Chain(failure), exception => exception is InvalidOperationException
            && exception.Message.Contains($"'{serviceId}'", StringComparison.Ordinal)
            && exception.Message.Contains("ClusterOptions.ServiceId", StringComparison.Ordinal)
            && exception.Message.Contains("DurableStorageOptions.ArchiveStorePrefix", StringComparison.Ordinal));
        Assert.Empty(await ListAsync(objects));
        Assert.Null((await ReadRegistryAsync()).ArchiveStore);
    }

    /// <summary>
    /// orleans.md §5.6, §5.13, OR-I20: the controller grain's archive work follows its silo's archive gate. Activated on a
    /// new cluster's only silo while that silo's check of the store's record waits, it commits no manifest change, accepts
    /// no drop, and asks for no sweep; once the check refuses the store, the grain logs it and deactivates, as each later
    /// activation there does, the silo reporting the refusal as critical once, the sweep host
    /// logs event 6245 once, and the refused silo has listed and deleted nothing in either store. When healthy silos
    /// join, the grain's next activation is placed on one of them (<c>ControllerPlacementDirector</c>), and sweeps and drops
    /// proceed there.
    /// </summary>
    [Theory(Timeout = 180_000)]
    [InlineData(ArchiveStoreKind.Lsm)]
    [InlineData(ArchiveStoreKind.Logs)]
    public async Task AControllerActivatedOnARefusedSiloDoesNoArchiveWorkAndMovesToAHealthySilo(ArchiveStoreKind kind)
    {
        _state.ArchiveStore = kind;
        var check = _state.CheckRecordReads.Hold("Primary");
        var cluster = await DeployAsync(1, TestContext.Current.CancellationToken);
        await check.WaitAsync(Patience, Token);
        var primary = cluster.Primary!;
        var gate = Archive(primary).Gate;
        Assert.False(gate.Passed.IsCompleted);

        var controller = Controller(cluster);
        var management = cluster.Client.GetGrain<IManagementGrain>(0);
        await EventuallyAsync(async () => await management.GetActivationAddress(controller, Token) == primary.SiloAddress, "the controller grain to activate on the only silo");
        _ = await Assert.ThrowsAsync<ControllerInactiveException>(() => controller.CommitManifestAsync(new ManifestChange.Drain(new LedgerId(1), Slot.Zero, new Slot(1), [], complete: true)));
        _ = await Assert.ThrowsAsync<ControllerInactiveException>(() => controller.CommitLedgerMapAsync(new LedgerMapChange.Drop(new LedgerId(1))));

        var deactivated = _state.EventsAsync(line => line.Contains($"{primary.Name}: Critical", StringComparison.Ordinal) && line.Contains("controller grain's silo refused", StringComparison.Ordinal));
        var sweepRefusal = _state.EventsAsync(line => line.Contains($"{primary.Name}: Critical 6245:", StringComparison.Ordinal));
        _state.CheckRecordReadFaults[primary.Name] = new ArchiveCorruptException("The test's record reads as corrupt to the first silo's check.");
        _state.CheckRecordReads.Release();
        await Bounded(deactivated, "The controller grain's deactivation");
        await Bounded(sweepRefusal, "The sweep host's refusal event");
        Assert.True(gate.Passed.IsFaulted);

        // A call activates the grain again on the refused silo, the only one, and that activation deactivates again; the
        // silo reports the refusal as critical once however often that repeats.
        for (var activation = 0; activation < 2; activation++)
        {
            await EventuallyAsync(async () => await management.GetActivationAddress(controller, Token) is null, "the refused silo's activation to deactivate");
            // The call activates the grain; the activation refuses it, or deactivates before it runs and Orleans rejects it.
            var activated = await Assert.ThrowsAnyAsync<Exception>(() => controller.CommitManifestAsync(new ManifestChange.Drain(new LedgerId(1), Slot.Zero, new Slot(1), [], complete: true)));
            Assert.True(activated is ControllerInactiveException or OrleansMessageRejectionException, activated.ToString());
        }

        await EventuallyAsync(async () => await management.GetActivationAddress(controller, Token) is null, "the refused silo's last activation to deactivate");
        Assert.Single(_state.StopEvents, line => line.Contains($"{primary.Name}: Critical", StringComparison.Ordinal) && line.Contains("controller grain's silo refused", StringComparison.Ordinal));

        _ = await cluster.StartAdditionalSilosAsync(3, startAdditionalSiloOnNewPort: false, Token);
        await EveryResolverListsEveryNodeAsync(cluster);
        var healthy = cluster.Silos.Where(silo => silo != primary).ToArray();
        await EventuallyAsync(async () => await management.GetActivationAddress(controller, Token) is { } host && host != primary.SiloAddress, "the controller grain to activate on a healthy silo");
        var map = await ServedAsync(cluster, ranges: 3);
        await EventuallyAsync(() => healthy.Any(silo => Services(silo).GetRequiredService<SweepHost>().Sweeps > 0), "a healthy silo to sweep");
        var ledger = map.Ledgers.First(static ledger => ledger.IsOpen);
        var dropped = _state.DroppedAsync(ledger.Id);
        await Controller(cluster).RequestRollAsync(ledger.Id);
        await Bounded(dropped, "The ledger's drop");

        Assert.Equal(0, Services(primary).GetRequiredService<SweepHost>().Sweeps);
        Assert.Equal(0, _state.StoreSweepCalls.GetValueOrDefault(primary.Name));
        Assert.Single(_state.StopEvents, line => line.Contains($"{primary.Name}: Critical 6245:", StringComparison.Ordinal));

        // The refused silo stops first, while healthy silos can take its ledgers, so its stop's hand-off is placed at once
        // rather than waiting out the host's stop timeout as the cluster's last silo would. The test cluster's membership
        // table lives on that first silo, so each healthy silo's graceful stop after it would wait out connection attempts
        // to it, seconds each; the test's assertions are done, so the healthy silos stop at once, ungracefully.
        await cluster.StopSiloAsync(primary, Token).WaitAsync(Patience, Token);
        await Task.WhenAll(healthy.Select(silo => cluster.KillSiloAsync(silo, Token))).WaitAsync(Patience, Token);
    }

    /// <summary>
    /// orleans.md §5.6, OR-I20: a controller grain activated on a silo whose archive gate stays pending, here a new cluster's
    /// first silo whose check of the store's record is held, does not hold the cluster's archive work: once healthy silos
    /// join and a node reports that its gate passed, the grain migrates to that node's silo, and a ledger rolled on a
    /// healthy primary drains and drops, through the manifest on the LSM and its drain worker's drop on archive logs.
    /// </summary>
    [Theory(Timeout = 180_000)]
    [InlineData(ArchiveStoreKind.Lsm)]
    [InlineData(ArchiveStoreKind.Logs)]
    public async Task AControllerOnASiloWhoseGateStaysPendingMovesToOneWhoseGatePassed(ArchiveStoreKind kind)
    {
        _state.ArchiveStore = kind;
        // The rolled ledger's successor may land on the pending silo, which then owes the closed ledger's drain and cannot
        // drain it; another node takes the drain once it made no progress for ClosedDrainTimeout, kept short here.
        _state.Control = static options => options.ClosedDrainTimeout = TimeSpan.FromSeconds(2);
        var check = _state.CheckRecordReads.Hold("Primary");
        var cluster = await DeployAsync(1, TestContext.Current.CancellationToken);
        await check.WaitAsync(Patience, Token);
        var primary = cluster.Primary!;
        var controller = Controller(cluster);
        Assert.IsType<ControllerPlacement>(Services(primary).GetRequiredService<global::Orleans.Runtime.Placement.PlacementStrategyResolver>().GetPlacementStrategy(controller.GetGrainId().Type));
        var management = cluster.Client.GetGrain<IManagementGrain>(0);
        await EventuallyAsync(async () => await management.GetActivationAddress(controller, Token) == primary.SiloAddress, "the controller grain to activate on the only silo");

        _ = await cluster.StartAdditionalSilosAsync(3, startAdditionalSiloOnNewPort: false, Token);
        await EveryResolverListsEveryNodeAsync(cluster);
        await EventuallyAsync(async () => await management.GetActivationAddress(controller, Token) is { } host && host != primary.SiloAddress, "the controller grain to migrate off the pending silo");
        Assert.False(Archive(primary).Gate.Passed.IsCompleted);
        var node = _state.LocalNodes[primary.Name].Node;
        var map = await ServedAsync(cluster, ranges: 3, done: value => value.Ledgers.Any(ledger => ledger.IsOpen && ledger.Primary.Node != node));
        var ledger = map.Ledgers.First(ledger => ledger.IsOpen && ledger.Primary.Node != node);
        var dropped = _state.DroppedAsync(ledger.Id);
        await Controller(cluster).RequestRollAsync(ledger.Id);
        await Bounded(dropped, "The rolled ledger's drop");

        _state.CheckRecordReads.Release();
    }

    /// <summary>
    /// orleans.md §5.6, OR-I20: the controller grain migrates only to a node whose archive gate passed. With every silo's
    /// check of the store's record held, the grain on a new cluster's first silo stays there while every node, the joiners
    /// among them, reports its gate not passed through two more reports each, and logs no migration; once one joiner's
    /// check is released and its gate passes, the grain moves to that joiner's silo. An activation which migrated on any
    /// report fails the first part.
    /// </summary>
    [Theory(Timeout = 180_000)]
    [InlineData(ArchiveStoreKind.Lsm)]
    [InlineData(ArchiveStoreKind.Logs)]
    public async Task TheControllerMigratesOnlyToANodeWhoseGatePassed(ArchiveStoreKind kind)
    {
        _state.ArchiveStore = kind;
        var check = _state.CheckRecordReads.Hold();
        try
        {
            var cluster = await DeployAsync(1, TestContext.Current.CancellationToken);
            await check.WaitAsync(Patience, Token);
            var primary = cluster.Primary!;
            var controller = Controller(cluster);
            var management = cluster.Client.GetGrain<IManagementGrain>(0);
            await EventuallyAsync(async () => await management.GetActivationAddress(controller, Token) == primary.SiloAddress, "the controller grain to activate on the first silo");

            _ = await cluster.StartAdditionalSilosAsync(3, startAdditionalSiloOnNewPort: false, Token);
            await EveryResolverListsEveryNodeAsync(cluster);
            NodeId[] nodes = [.. Nodes(cluster).Select(static entry => entry.Node)];
            await EventuallyAsync(() => nodes.All(_state.LatestReports.ContainsKey), "every node to report");
            var reported = nodes.ToDictionary(static node => node, node => _state.LatestReports[node].Sequence);
            await EventuallyAsync(() => nodes.All(node => _state.LatestReports[node].Sequence >= reported[node] + 2), "two more reports of every node");

            Assert.All(nodes, node => Assert.False(_state.LatestReports[node].ArchiveGatePassed));
            Assert.All(cluster.Silos, silo => Assert.False(Archive(silo).Gate.Passed.IsCompleted));
            Assert.Equal(primary.SiloAddress, await management.GetActivationAddress(controller, Token));
            Assert.DoesNotContain(_state.StopEvents, static line => line.Contains("the activation migrates", StringComparison.Ordinal));

            var joiner = cluster.Silos.First(silo => silo != primary);
            _state.CheckRecordReads.Release(joiner.Name);
            _ = await Archive(joiner).Gate.Passed.WaitAsync(Patience, Token);
            await EventuallyAsync(async () => await management.GetActivationAddress(controller, Token) == joiner.SiloAddress, $"the controller grain to migrate to {joiner.Name}, whose gate passed");
            Assert.All(cluster.Silos.Where(silo => silo != joiner), silo => Assert.False(Archive(silo).Gate.Passed.IsCompleted));
        }
        finally
        {
            _state.CheckRecordReads.Release();
        }
    }
    /// <summary>
    /// orleans.md §5.6, §8, archive-logs.md §2.3: on archive logs, a closed ledger's drain blocked at a key whose log the
    /// store refuses is reported, its key in <c>orleans.durable.storage.archive.logs.drain.blocked</c> by ledger and cause and a critical event
    /// 6244 once, however often the drain tries again, and the ledger stays in the map; once the log is restored, the drain
    /// resumes at its next attempt, the ledger drops, and the key's log holds its write. The store refuses every write of
    /// the key's log, so the block is certain, and the test waits on the events themselves, the critical event, the drain's
    /// next failed attempt, and the drop's map write, never on a clock: each round of the nodes' loops brings the next.
    /// The blocked ledger is the one the drains report, whichever closed ledger holds the key's row: the one the test rolls,
    /// or one balancing handed off before it.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task ABlockedKeyIsReportedAndItsLedgersDrainResumesOnceItsLogIsRestored()
    {
        _state.ArchiveStore = ArchiveStoreKind.Logs;
        _state.Node = static (_, options) => options.BlockedDrainRetryInterval = TimeSpan.Zero;
        var cluster = await DeployAsync(3, TestContext.Current.CancellationToken);
        var map = await ServedAsync(cluster, ranges: 3, done: value => Balanced(cluster, value));
        await JournalsServeAsync(cluster);

        var key = KeyIn(map.Ledgers.First(static ledger => ledger.IsOpen));
        _state.RefusedLogs[key] = true;
        await AppendAsync(cluster, key, "blocked"u8.ToArray());
        var gauges = new ConcurrentDictionary<(string Instrument, string? Node, string? Tag), long>();
        using var listener = Listen(gauges);
        var critical = _state.EventsAsync(static line => line.Contains("Critical 6244:", StringComparison.Ordinal));

        var holding = (await ReadMapAsync()).Ledgers.First(open => open.IsOpen && open.Range.Contains(JournalRowKey.Encode(key).Span));
        await Controller(cluster).RequestRollAsync(holding.Id);
        await Bounded(critical, "The blocked drain's critical event");

        var blocked = cluster.Silos.SelectMany(silo => Runtime(silo)!.LogDrains!.Blocked.Keys).Distinct().Single();
        listener.RecordObservableInstruments();
        Assert.Contains(gauges, gauge => gauge.Key.Instrument == ArchiveLogNodeDrains.BlockedName && gauge.Key.Tag == $"{blocked}:refused" && gauge.Value == 1);
        await Bounded(_state.EventsAsync(line => line.Contains($"drain of ledger {blocked} failed", StringComparison.Ordinal), count: 2), "Two failures of the blocked ledger's drain");
        Assert.NotNull((await ReadMapAsync()).Get(blocked));
        Assert.Single(_state.StopEvents, static line => line.Contains("Critical 6244:", StringComparison.Ordinal));

        var dropped = _state.DroppedAsync(blocked);
        _ = _state.RefusedLogs.TryRemove(key, out _);

        await Bounded(dropped, "The ledger's drop");
        Assert.Null((await ReadMapAsync()).Get(blocked));
        Assert.All(cluster.Silos, silo => Assert.Empty(Runtime(silo)!.LogDrains!.Blocked));
        gauges.Clear();
        listener.RecordObservableInstruments();
        Assert.DoesNotContain(gauges, gauge => gauge.Key.Instrument == ArchiveLogNodeDrains.BlockedName);
        // Report replies assign each node its share at the controller's pace, and withhold one while the others'
        // reservations hold the budget (OR-I21), as the blocked drain's node may until it reports its keys drained
        // (ClusterControllerTests.ANodeTheBudgetHasNoRoomForIsAssignedAShareOnlyOnceAnotherReleasesIt), so each silo's
        // share is awaited, not read at the drop.
        await EventuallyAsync(() => cluster.Silos.All(silo => Runtime(silo)!.LogDrains!.RequestsPerSecond is not null), "every silo's archive-log share");
        var read = Assert.IsType<ArchiveLogRead.Read>((await _state.Logs.ReadAsync(OrleansJournalLogNames.Names.Of(JournalRowKey.Encode(key).Span), Token)).Value);
        using (read)
        {
            Assert.True(read.Content.ToArray().AsSpan().IndexOf("blocked"u8) >= 0);
        }
    }

    /// <summary>
    /// orleans.md §5.2, §5.6: on archive logs, a silo stopped mid-drain, its drain's append held by a request which ends only
    /// when released or cancelled, stops within its bounds. Step 2 waits for the drain round admitted before the hand-off,
    /// for at most <c>TransitionTimeout</c>, and the held append keeps it waiting that long; step 4 then cancels the round,
    /// whose held append ends with the cancellation, so the runtime's cancelled work ends within the second bound. Its
    /// disposal, step 5, ends within what step 4 left of the bound, or is deferred with its warning to the silo's disposal,
    /// which ends it within a bound of its own: neither the stop nor the silo's disposal logs a defect.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task ASiloStoppedMidDrainCancelsItsHeldAppendWithinItsBound()
    {
        _state.ArchiveStore = ArchiveStoreKind.Logs;
        var bound = TimeSpan.FromSeconds(3);
        _state.Control = options => options.TransitionTimeout = bound;
        var cluster = await DeployAsync(3, TestContext.Current.CancellationToken);
        _ = await ServedAsync(cluster, ranges: 3, done: value => Balanced(cluster, value));
        await JournalsServeAsync(cluster);
        var (node, handle) = await PrimaryAsync(cluster, silo => silo != cluster.Primary);
        var ledger = (await ReadMapAsync()).Ledgers.First(ledger => ledger.IsOpen && ledger.Primary.Node == node);
        await AppendAsync(cluster, KeyIn(ledger), "held"u8.ToArray());
        var held = _state.LogAppends.Hold(handle.Name);

        await Controller(cluster).RequestRollAsync(ledger.Id);
        await held.WaitAsync(Patience, Token);
        try
        {
            await cluster.StopSiloAsync(handle, TestContext.Current.CancellationToken).WaitAsync(Patience, Token);
        }
        finally
        {
            _state.LogAppends.Release();
        }

        Assert.InRange(StopTime(handle.Name, nameof(NodeLedgers), nameof(ServiceLifecycleStage.Active)), bound, bound * 2);
        Assert.Contains(_state.StopEvents, line => line.Contains($"{handle.Name}:", StringComparison.Ordinal) && line.Contains("admitted control work outlasted", StringComparison.Ordinal));
        Assert.DoesNotContain(_state.StopEvents, line => line.Contains($"{handle.Name}:", StringComparison.Ordinal)
            && (line.Contains("Journals' outstanding writes outlasted", StringComparison.Ordinal)
                || line.Contains("spent the stop's whole bound", StringComparison.Ordinal)
                || line.Contains("did not end within", StringComparison.Ordinal)));
    }

    /// <summary>
    /// orleans.md §5.2, §5.6: on archive logs a node's drains run per ledger: a drain whose store writes are held for many
    /// control intervals holds no other ledger's drain, whose drop applies meanwhile; and the silo's stop waits for the
    /// held drain, which outlives the stop's cancellation, to end before it disposes the runtime, so no drain the node
    /// started runs past its stop. The held write ignores the cancellation as a request no cancellation reaches does.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task AHeldDrainHoldsNoOtherLedgersDropAndTheStopWaitsForIt()
    {
        _state.ArchiveStore = ArchiveStoreKind.Logs;
        var bound = TimeSpan.FromSeconds(5);
        _state.Control = options =>
        {
            options.TransitionTimeout = bound;
            options.LedgersPerNode = 2;
        };
        var cluster = await DeployAsync(3, TestContext.Current.CancellationToken);

        // Primary balance hands a ledger off while two nodes' counts differ by more than one, so the test picks its ledgers
        // only once the six ranges are balanced, two on each node, when the controller moves none of them
        // (ClusterControllerTests.APassAtTheTargetWithBalancedPrimariesSendsNothing).
        var map = await ServedAsync(cluster, ranges: 6, done: value => value.Ledgers.Count(static ledger => ledger.IsOpen) == 6 && Balanced(cluster, value));
        await JournalsServeAsync(cluster);
        var (node, _, handle) = Nodes(cluster).First(entry => entry.Handle != cluster.Primary && map.Ledgers.Count(ledger => ledger.IsOpen && ledger.Primary.Node == entry.Node) >= 2);
        var (slow, other) = map.Ledgers.Where(ledger => ledger.IsOpen && ledger.Primary.Node == node).Take(2).ToArray() switch
        {
            [var first, var second] => (first, second),
            _ => throw new InvalidOperationException("The node is primary of two ledgers."),
        };
        var (slowKey, otherKey) = (KeyIn(slow), KeyIn(other));
        await AppendAsync(cluster, slowKey, "held"u8.ToArray());
        await AppendAsync(cluster, otherKey, "dropped"u8.ToArray());
        var slowLog = OrleansJournalLogNames.Names.Of(JournalRowKey.Encode(slowKey).Span).Value;
        var held = _state.LogAppends.Hold(handle.Name, holds: call => call as string == slowLog, outlivesCancellation: true);
        try
        {
            await Controller(cluster).RequestRollAsync(slow.Id);
            await held.WaitAsync(Patience, Token);
            var dropped = _state.DroppedAsync(other.Id);
            await Controller(cluster).RequestRollAsync(other.Id);
            await Bounded(dropped, "The other ledger's drop while the held drain runs");
            Assert.NotNull((await ReadMapAsync()).Get(slow.Id));

            var drains = Runtime(handle)!.Controller;
            var stop = cluster.StopSiloAsync(handle, TestContext.Current.CancellationToken);
            await _state.LogAppends.Cancelled.WaitAsync(Patience, Token);
            _ = await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(2), TimeProvider.System, Token));
            Assert.False(stop.IsCompleted, "The stop ended while a drain the node started still ran.");
            Assert.False(drains.DrainsEnded.IsCompleted);

            _state.LogAppends.Release();
            await stop.WaitAsync(Patience, Token);
            Assert.True(drains.DrainsEnded.IsCompleted);
            Assert.DoesNotContain(_state.StopEvents, line => line.Contains($"{handle.Name}:", StringComparison.Ordinal) && line.Contains("cancelled work", StringComparison.Ordinal));
        }
        finally
        {
            _state.LogAppends.Release();
        }
    }

    /// <summary>
    /// orleans.md §5.5, §5.13, OR-I7: on archive logs, <c>IsCompactionRequested</c> follows the store's tokens: a layout of
    /// 64-byte blocks which asks for a <c>Replace</c> at 3 units asks once a journal's appends, each a block of its own
    /// within warm state's §8.7 base-size budget, reach 3, not before, and an accepted <c>Replace</c> clears it.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task OnArchiveLogsTheTokensAskForAReplace()
    {
        const int Block = 64;
        _state.ArchiveStore = ArchiveStoreKind.Logs;
        _state.Logs = new MemoryArchiveLogStore(new ArchiveLogLayout(MemoryArchiveLogStore.LayoutName, ArchiveLogPacking.Blocks, Block, maxUnits: 64, replaceRequestUnits: 3, MemoryArchiveLogStore.NameRule));
        _state.Node = static (_, node) => node.Journals.MaxBodyBytes = Block;
        var cluster = await DeployAsync(3, TestContext.Current.CancellationToken);
        _ = await ServedAsync(cluster, ranges: 3);
        await JournalsServeAsync(cluster);
        var provider = Services(cluster.Primary!).GetRequiredService<DurableJournalStorageProvider>();

        // Establish a 3-block archived base (192 bytes, 0 append units) so 3 subsequent 64-byte appends stay within
        // warm state's §8.7 size budget (BytesSinceBase <= BaseLength) and isolate the archive store's token threshold.
        var seed = provider.CreateStorage(new JournalId("tokens"));
        await seed.ReadAsync(new SkippingConsumer(), Token);
        await seed.ReplaceAsync(new ReadOnlySequence<byte>(new byte[Block]), Token);
        await seed.AppendAsync(new ReadOnlySequence<byte>(new byte[Block]), Token);
        await seed.AppendAsync(new ReadOnlySequence<byte>(new byte[Block]), Token);
        var holding = (await ReadMapAsync()).Ledgers.First(open => open.IsOpen && open.Range.Contains(JournalRowKey.Encode("tokens").Span));
        var dropped = _state.DroppedAsync(holding.Id);
        await Controller(cluster).RequestRollAsync(holding.Id);
        await Bounded(dropped, "The base ledger's drop");

        var storage = provider.CreateStorage(new JournalId("tokens"));
        await storage.ReadAsync(new SkippingConsumer(), Token);

        for (var append = 1; append <= 3; append++)
        {
            Assert.False(storage.IsCompactionRequested, $"A Replace was asked for after {append - 1} appends.");
            await storage.AppendAsync(new ReadOnlySequence<byte>(new byte[Block]), Token);
        }

        Assert.True(storage.IsCompactionRequested);

        await storage.ReplaceAsync(new ReadOnlySequence<byte>("compacted"u8.ToArray()), Token);
        Assert.False(storage.IsCompactionRequested);
    }

    /// <summary>
    /// orleans.md §5.5, §5.13: over Azure's layout, here on the fake Azure transport, a journal whose identity Azure's name
    /// rule refuses, one which is not printable ASCII, is refused before admission, without effect, through the storage
    /// and through the Journals' own admission, while a journal it names is written, drained into its write-ahead blob, and
    /// read back.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task OnAzureAJournalWhoseNameAzureRefusesIsRefusedAtAdmission()
    {
        _state.ArchiveStore = ArchiveStoreKind.Logs;
        _state.Azure = new FakeAzureBlobService();
        var cluster = await DeployAsync(3, TestContext.Current.CancellationToken);
        var map = await ServedAsync(cluster, ranges: 3, done: value => Balanced(cluster, value));
        await JournalsServeAsync(cluster);
        var provider = Services(cluster.Primary!).GetRequiredService<DurableJournalStorageProvider>();

        var unnamable = provider.CreateStorage(new JournalId("caf\u00e9"));
        await unnamable.ReadAsync(new SkippingConsumer(), Token);
        _ = await Assert.ThrowsAsync<ArgumentException>(async () => await unnamable.AppendAsync(new ReadOnlySequence<byte>([1]), Token));
        var owner = Runtime(cluster.Primary!)!;
        Assert.False(owner.Archive.AdmitsKey(JournalRowKey.Encode("caf\u00e9").Span));

        var key = KeyIn(map.Ledgers.First(static ledger => ledger.IsOpen));
        await AppendAsync(cluster, key, "named"u8.ToArray());

        // The ledger which holds the key now drops only after every older ledger overlapping it (LG-I10), the one which holds
        // the row among them, should balancing have handed the key's ledger off since the map was read.
        var holding = (await ReadMapAsync()).Ledgers.First(open => open.IsOpen && open.Range.Contains(JournalRowKey.Encode(key).Span));
        var dropped = _state.DroppedAsync(holding.Id);
        await Controller(cluster).RequestRollAsync(holding.Id);
        await Bounded(dropped, "The ledger's drop");
        Assert.NotNull(_state.Azure.Peek("wal/" + key));
        Assert.Null(_state.Azure.Peek("wal/caf\u00e9"));
        await AssertJournalAsync(cluster, key, "named"u8.ToArray());
    }

    /// <summary>Appends <paramref name="body"/> to the journal <paramref name="key"/> through the primary silo's router, at the version a read gave.</summary>
    private static async Task AppendAsync(TestCluster cluster, string key, byte[] body)
    {
        var router = Services(cluster.Primary!).GetRequiredService<JournalRouter>();
        var rowKey = JournalRowKey.Encode(key);
        var read = await router.ReadAsync(key, rowKey, Token);
        read.Content.Dispose();
        using var payload = Payload(body);
        using var record = JournalEntryRecord.Append(rowKey, read.Version, payload);
        _ = Assert.IsType<JournalWriteOutcome.Accepted>((await router.WriteAsync(key, rowKey, record, Token)).Value);
    }

    /// <summary>
    /// Creates, in a store apart from the cluster's, a record of the cluster's layout, store fields, and parameters with
    /// another ID: another store which OR-I8's check alone would pass.
    /// </summary>
    private static async Task<ArchiveStoreRecord> CreateRecordAsync(ArchiveStoreKind kind, IObjectStore objects, IArchiveLogStore logs)
    {
        var parameters = NodeRuntime.RecordParametersOf(new JournalStoreOptions(), new JournaledStateManagerOptions().JournalFormatKey);
        IArchiveStore store = kind == ArchiveStoreKind.Logs
            ? new LogArchiveStore(logs, objects, OrleansJournalLogNames.Names)
            : new LsmArchiveStore(objects, new ManifestView(objects), new TailCache(objects, new ArchiveOptions()), new ArchiveOptions(), TimeProvider.System);
        return await store.StoreRecordAsync(parameters, Token);
    }

    /// <summary>Returns the store's record the cluster's store holds, as its kind names it.</summary>
    private async Task<ArchiveStoreRecord> StoredRecordAsync(ArchiveStoreKind kind)
    {
        var name = kind == ArchiveStoreKind.Logs ? new ObjectName(ArchiveLogNames.StoreRecord) : ObjectName.Store;
        var stored = Assert.IsType<VersionedObject>(await _state.ClusterObjects.ReadVersionedAsync(name, Token), exactMatch: false);
        using var content = stored.Content;
        return Assert.IsType<ArchiveStoreRecord>(ArchiveStoreRecord.Decode(content.ToArray()).Value);
    }

    /// <summary>Returns how many passes the controller grain made on the cluster's silos once their gates passed: each asks for one sweep.</summary>
    private static long Passes(TestCluster cluster) => cluster.Silos.Sum(silo => Services(silo).GetRequiredService<SweepHost>().Requests);

    /// <summary>Returns the names of the objects <paramref name="objects"/> holds, in order.</summary>
    private static async Task<List<string>> ListAsync(IObjectStore objects)
    {
        var names = new List<string>();
        await foreach (var listed in objects.ListAsync(string.Empty, cancellationToken: Token))
        {
            names.Add(listed.Name.Value);
        }

        names.Sort(StringComparer.Ordinal);
        return names;
    }

    /// <summary>Deploys one silo of another cluster, whose state <paramref name="state"/> holds, with its own service ID.</summary>
    private static async Task<TestCluster> DeployOtherAsync(TestState state)
    {
        var builder = new TestClusterBuilder(1);
        builder.Properties[TestKey] = state.Id;
        if (state.ServiceId is { } serviceId)
        {
            builder.Options.ServiceId = serviceId;
        }

        _ = builder.AddSiloBuilderConfigurator<NodeConfigurator>();
        var cluster = builder.Build();
        try
        {
            await cluster.DeployAsync(Token).WaitAsync(Patience, Token);
            return cluster;
        }
        catch
        {
            await cluster.DisposeAsync();
            throw;
        }
    }
}
