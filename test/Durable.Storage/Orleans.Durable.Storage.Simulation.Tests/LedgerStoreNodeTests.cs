using System.Globalization;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Simulation.Tests;

/// <summary>
/// A <see cref="LedgerStoreNode"/>'s crashes cut its WAL with writes in flight, and a restart recovers what its disk
/// kept: every acknowledged write, the replicas its catalog lists, and its identity.
/// </summary>
public sealed class LedgerStoreNodeTests
{
    private const int Acknowledged = 8;
    private const int InFlight = 8;

    // More slots than a restarted store loads at its open, so reads load leaves lazily.
    private const int LazilyLoaded = 1536;
    private static readonly LedgerReplicaOptions Options = new() { WindowSize = 64 };

    // A window which admits every address the fault-coverage workload writes.
    private static readonly LedgerReplicaOptions LargeWindow = new() { WindowSize = 8192 };

    /// <summary>
    /// Gets the seed corpus: odd seeds crash the operating system, and even seeds the process.
    /// </summary>
    public static TheoryData<int> Seeds => SeededSimulation.Corpus(1, 8);

    /// <summary>
    /// A process crash, or an operating-system crash which loses or tears the writes in flight, leaves every
    /// acknowledged write readable after the restart, and each unacknowledged write either kept, missing, or corrupt; the
    /// node answers nothing while it is down, and restarts with its identity and replica.
    /// </summary>
    /// <param name="seed">The seed, whose parity chooses the crash.</param>
    [Theory]
    [MemberData(nameof(Seeds))]
    public void ARestartRecoversWhatTheCrashedDiskKept(int seed) =>
        SeededSimulation.Run(seed, environment => CrashAndRestartAsync(environment, operatingSystem: seed % 2 == 1));

    /// <summary>
    /// A crash and restart replays from its seed and from its artifact.
    /// </summary>
    [Fact]
    public void ACrashAndRestartReplaysFromItsSeed() =>
        SeededSimulation.RunAndReplay(101, static environment => CrashAndRestartAsync(environment, operatingSystem: true));

    /// <summary>
    /// Each media damage the node does, to a page it chooses or to one its caller names, reaches its damage observers as
    /// it happens, so a ledger trace logs every damage (docs/spec/verification.md §5.1); damage which finds no page to
    /// damage reaches none.
    /// </summary>
    [Fact]
    public void EachDamageReachesTheDamageObservers() =>
        _ = SeededSimulation.Run(7, static async environment =>
        {
            await using var node = new LedgerStoreNode(environment, "replica-0", SimulatedStorage.Cluster);
            var damages = 0;
            node.OnDamaged(() => damages++);
            await node.StartAsync();
            await SimulationTasks.WhenAll(Enumerable.Range(0, Acknowledged).Select(address => WriteAsync(node, address)));
            Assert.False(node.Damage());
            Assert.Equal(0, damages);

            await node.CrashAsync(operatingSystem: false);
            await node.StartAsync();
            Assert.True(node.Damage());
            Assert.Equal(1, damages);

            var page = await node.Store.FindLive(SimulatedStorage.Ledger)!.RecordPageAsync(0, CancellationToken.None);
            Assert.True(page is { } recorded && node.Damage(recorded));
            Assert.Equal(2, damages);
        });

    /// <summary>
    /// A failed WAL write stops the node's process at once: the write and every later request answer nothing, the driver
    /// takes the failure's report, naming the node's root, and the node crashes and restarts with every acknowledged
    /// write (conventions.md §14).
    /// </summary>
    /// <param name="seed">The seed, which chooses the crash.</param>
    [Theory]
    [MemberData(nameof(Seeds))]
    public void AFailedWriteStopsTheProcessWhichRestartsWithEveryAcknowledgedWrite(int seed)
    {
        var run = SeededSimulation.Run(seed, async environment =>
        {
            await using var node = new LedgerStoreNode(environment, "replica-0", SimulatedStorage.Cluster);
            await node.StartAsync();
            await node.ServeAsync(static async store => _ = await store.CreateLiveAsync(SimulatedStorage.Ledger, Options.WindowSize, CancellationToken.None));
            await SimulationTasks.WhenAll(Enumerable.Range(0, Acknowledged).Select(address => WriteAsync(node, address)));

            node.FailNextStorageOperation(StorageFault.Write);
            _ = await Assert.ThrowsAsync<NodeDownException>(() => WriteAsync(node, Acknowledged));
            Assert.False(node.IsUp);
            _ = await Assert.ThrowsAsync<NodeDownException>(() => node.ServeAsync(static store => ValueTask.FromResult(store.Replicas)).AsTask());

            await node.FailStopped;
            Assert.Equal(1, node.FailStops);
            Assert.Equal(2, node.Incarnation);
            for (var address = 0; address < Acknowledged; address++)
            {
                Assert.Equal(ReplicaReadCase.Batch, await ReadAsync(node, address));
            }
        });

        Assert.Contains(run.Trace, static entry => entry.Contains("replica-0 reported a failed HandleWriteGather: its process stops", StringComparison.Ordinal));
    }

    /// <summary>
    /// Every kind of storage fault the node injects fires, stops the node's process with one report the driver takes,
    /// and the node restarts with every acknowledged write (conventions.md §14, verification.md §7.3). A node injects
    /// nothing while it starts, so a checkpoint's read fires only once the restarted store loads a leaf lazily: the
    /// workload writes more slots than a store loads at its open. A live replica pins the WAL from the file of its
    /// creation, so a WAL file's deletion fires only once a drop frees the files of an earlier replica.
    /// </summary>
    /// <param name="fault">The fault.</param>
    [Theory]
    [InlineData(StorageFault.Write)]
    [InlineData(StorageFault.TornWrite)]
    [InlineData(StorageFault.Read)]
    [InlineData(StorageFault.NamedWrite)]
    [InlineData(StorageFault.NamedRead)]
    [InlineData(StorageFault.Delete)]
    [InlineData(StorageFault.NamedDelete)]
    [InlineData(StorageFault.DirectoryFlush)]
    public void EveryStorageFaultFiresAndStopsTheProcess(StorageFault fault)
    {
        var run = SeededSimulation.Run(5, async environment =>
        {
            // A WAL file's deletion needs replicas spread over several files, so its case rolls smaller files.
            var options = fault == StorageFault.Delete ? LedgerStoreNode.DefaultOptions("replica-0") with { FileSize = 16 * 512 } : null;
            await using var node = new LedgerStoreNode(environment, "replica-0", SimulatedStorage.Cluster, options);
            await node.StartAsync();
            var ledger = SimulatedStorage.Ledger;
            await CreateAsync(node, ledger);

            // A read needs written slots; a checkpoint's read needs more than a restarted store loads at its open.
            var written = 0;
            if (fault == StorageFault.Read)
            {
                written = 64;
            }
            else if (fault == StorageFault.NamedRead)
            {
                written = LazilyLoaded;
            }

            for (var batch = 0; batch < written; batch += 64)
            {
                await SimulationTasks.WhenAll(Enumerable.Range(batch, Math.Min(64, written - batch)).Select(address => WriteAsync(node, address, LargeWindow, ledger)));
            }

            if (fault == StorageFault.NamedRead)
            {
                // The restarted store opens from a checkpoint whose leaves it loads as reads reach them.
                await Task.Delay(TimeSpan.FromSeconds(1), environment.TimeProvider);
                await node.CrashAsync(operatingSystem: false);
                await node.StartAsync();
            }

            if (fault == StorageFault.Delete)
            {
                // The first replica fills WAL files, a second takes the writes from a later file, and the first's drop
                // frees the files below the second's.
                for (var address = 0; address < 4096 && await CurrentFileAsync(node) < 3; address += 64)
                {
                    await SimulationTasks.WhenAll(Enumerable.Range(address, 64).Select(slot => WriteAsync(node, slot, LargeWindow, ledger)));
                }

                ledger = new LedgerId(2);
                await CreateAsync(node, ledger);
                await node.ServeAsync(static store => store.DropReplicaAsync(SimulatedStorage.Ledger, CancellationToken.None));
            }

            node.FailNextStorageOperation(fault);
            var acknowledged = Enumerable.Range(0, written).ToList();
            for (var round = 0; node.FailStops == 0 && round < 4096; round++)
            {
                try
                {
                    if (fault is StorageFault.Read or StorageFault.NamedRead)
                    {
                        _ = await ReadAsync(node, round % written, LargeWindow, ledger);
                    }
                    else if ((await WriteAsync(node, written + round, LargeWindow, ledger)).Value is ReplicaWriteOutcome.Written)
                    {
                        acknowledged.Add(written + round);
                    }
                }
                catch (NodeDownException)
                {
                    // The fault stopped the process.
                }

                if (round % 32 == 31)
                {
                    // Maintenance checkpoints, releases WAL files, and deletes old checkpoints between the batches.
                    await Task.Delay(TimeSpan.FromMilliseconds(150), environment.TimeProvider);
                }
            }

            Assert.Equal(1, node.FailStops);
            await node.FailStopped;
            Assert.True(node.IsUp);
            foreach (var address in acknowledged)
            {
                Assert.Equal(ReplicaReadCase.Batch, await ReadAsync(node, address, LargeWindow, ledger));
            }
        });

        Assert.Contains(run.Trace, entry => entry.Contains($"fail a {fault} on replica-0", StringComparison.Ordinal));
        Assert.Contains(run.Trace, static entry => entry.Contains("replica-0 reported a failed ", StringComparison.Ordinal));
    }

    /// <summary>
    /// A store woken by a write at a fraction of a millisecond waits out the rest of each checkpoint interval: timers
    /// count whole milliseconds, so a wait for a sub-millisecond remainder would complete at once and spin without virtual
    /// time passing (regression).
    /// </summary>
    [Fact]
    public void MaintenanceWaitsOutSubMillisecondRemaindersOfItsInterval()
    {
        var idle = TimeSpan.FromSeconds(1);
        var run = SeededSimulation.Run(3, async environment =>
        {
            await using var node = new LedgerStoreNode(environment, "replica-0", SimulatedStorage.Cluster);
            await node.StartAsync();
            await node.ServeAsync(static async store => _ = await store.CreateLiveAsync(SimulatedStorage.Ledger, Options.WindowSize, CancellationToken.None));
            Assert.Equal(ReplicaWriteCase.Written, (await WriteAsync(node, 0)).Case);
            await Task.Delay(idle, environment.TimeProvider);
        });

        // Each idle interval costs the maintenance loop a handful of steps.
        var intervals = idle / LedgerStoreNode.DefaultCheckpointOptions.CheckpointInterval;
        Assert.True(run.Steps < 100 * intervals, string.Create(CultureInfo.InvariantCulture, $"{run.Steps} steps for {intervals} idle intervals."));
    }

    private static async Task CrashAndRestartAsync(SimulationEnvironment environment, bool operatingSystem)
    {
        await using var node = new LedgerStoreNode(environment, "replica-0", SimulatedStorage.Cluster)
        {
            MinDiskLatency = TimeSpan.FromMilliseconds(5),
            MaxDiskLatency = TimeSpan.FromMilliseconds(20),
        };
        await node.StartAsync();
        var identity = node.NodeId;
        await node.ServeAsync(static async store => _ = await store.CreateLiveAsync(SimulatedStorage.Ledger, Options.WindowSize, CancellationToken.None));
        await SimulationTasks.WhenAll(Enumerable.Range(0, Acknowledged).Select(address => WriteAsync(node, address)));
        var inFlight = Enumerable.Range(Acknowledged, InFlight).Select(address => WriteAsync(node, address)).ToArray();
        for (var poll = 0; poll < 20 && node.Storage.PendingWriteCount == 0; poll++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1), environment.TimeProvider);
        }

        Assert.True(node.Storage.PendingWriteCount > 0, "No write was in flight when the node crashed.");

        await node.CrashAsync(operatingSystem);
        _ = await Assert.ThrowsAsync<NodeDownException>(() => node.ServeAsync(static store => ValueTask.FromResult(store.Replicas)).AsTask());
        var acknowledged = new HashSet<int>(Enumerable.Range(0, Acknowledged));
        for (var index = 0; index < InFlight; index++)
        {
            try
            {
                _ = await inFlight[index];
                _ = acknowledged.Add(Acknowledged + index);
            }
            catch (NodeDownException)
            {
                // The crashed process never answered the write.
            }
        }

        await node.StartAsync();
        Assert.Equal(2, node.Incarnation);
        Assert.Equal(identity, node.NodeId);
        Assert.Equal(identity, node.Store.NodeId);
        Assert.Equal([SimulatedStorage.Ledger], node.Store.Replicas);
        for (var address = 0; address < Acknowledged + InFlight; address++)
        {
            var kind = await ReadAsync(node, address);
            Assert.True(
                acknowledged.Contains(address) ? kind == ReplicaReadCase.Batch : kind is ReplicaReadCase.Batch or ReplicaReadCase.Missing or null,
                string.Create(CultureInfo.InvariantCulture, $"Address {address} reads as {kind?.ToString() ?? "corrupt"}."));
        }
    }

    private static async Task<ReplicaWriteOutcome> WriteAsync(LedgerStoreNode node, int address, LedgerReplicaOptions? options = null, LedgerId? ledger = null)
    {
        // The store completes the write, or fails it with the crash, before the request returns.
        using var payload = ArcBuffer.Wrap(SimulatedStorage.Payload(address));
        return await node.ServeReplicaAsync(
            ledger ?? SimulatedStorage.Ledger,
            options ?? Options,
            replica => replica.WriteAsync(new Slot(address), payload, Slot.Zero, recovery: false));
    }

    private static ValueTask CreateAsync(LedgerStoreNode node, LedgerId ledger) =>
        node.ServeAsync(async store => { _ = await store.CreateLiveAsync(ledger, LargeWindow.WindowSize, CancellationToken.None); });

    private static ValueTask<ulong> CurrentFileAsync(LedgerStoreNode node) =>
        node.ServeAsync(static store => ValueTask.FromResult(store.GetRetention().CurrentFile));

    /// <summary>
    /// Reads an address, returning its kind after checking any value, or <see langword="null"/> when its slot is faulty.
    /// </summary>
    private static async Task<ReplicaReadCase?> ReadAsync(LedgerStoreNode node, int address, LedgerReplicaOptions? options = null, LedgerId? ledger = null)
    {
        try
        {
            var result = await node.ServeReplicaAsync(ledger ?? SimulatedStorage.Ledger, options ?? Options, replica => replica.ReadAsync(new Slot(address), Slot.Zero));
            if (result.TryGetValue(out ArcBuffer value))
            {
                using (value)
                {
                    Assert.Equal(SimulatedStorage.Payload(address), value.ToArray());
                }
            }

            return result.Case;
        }
        catch (LedgerReplicaCorruptException)
        {
            // A torn write in flight may fault its slot until a later record re-establishes it (LS-I5).
            return null;
        }
    }
}
