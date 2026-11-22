using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Tests;

/// <summary>
/// The replica transport's measurements (orleans.md §5.12, §8.1, OR-I17, OR-I18), run explicitly on a four-silo
/// <c>Orleans.TestingHost</c> cluster whose silos share one process, each reported over three runs with their mean and
/// range: the latency of the local and remote paths; three silos each writing full 1 MiB batches through ledgers of three
/// replicas, its own node's and two others', with the remote calls of a batch sharing its sender copy and without, the
/// large-object-heap bytes per byte of ingress, the CPU per GiB, and the generation-2 collections; the callee's working
/// set, its host's peak bytes against its bound, and its throughput while the other three silos write to it at once; and
/// one recovery and one drain of a ledger of full batches under the default limits. Every silo shares the process, so the
/// working set, the CPU, and the allocations are the cluster's, the callers' included; one silo per process with Server GC
/// on a quiet machine qualifies them (row 5.4).
/// </summary>
[Orleans.Durable.Storage.Testing.NoPageLeakCheck("Explicit measurements, which time the transport; its other test classes check its pages.")]
[Collection(TestingHostCollection.Name)]
public sealed class OrleansReplicaTransportMeasurements : IAsyncLifetime
{
    private const int Mebibyte = MeasurementSample.Mebibyte;
    private const int Runs = 3;
    private static readonly LedgerReplicaOptions Window = new() { WindowSize = 64 };

    private ReplicaCluster? _cluster;
    private ulong _nextLedger = 100;

    private ReplicaCluster Cluster => _cluster!;

    public async ValueTask InitializeAsync() => _cluster = await ReplicaCluster.DeployAsync(4, static options => options.WalFileSize = 1L << 30);

    public async ValueTask DisposeAsync()
    {
        if (_cluster is not null)
        {
            await _cluster.DisposeAsync();
        }
    }

    /// <summary>The latency of writes and reads of 1 KiB, 64 KiB, and 1 MiB batches on the local path and the remote one.</summary>
    [Fact(Explicit = true)]
    public async Task LatencyOfTheLocalAndRemotePaths()
    {
        var token = TestContext.Current.CancellationToken;
        var caller = Cluster.Transport(Cluster.SiloNames[0]);
        Report($"GC: server {GCSettings.IsServerGC}, latency mode {GCSettings.LatencyMode}; {Environment.ProcessorCount} cores");
        foreach (var size in new[] { 1 << 10, 64 << 10, Mebibyte })
        {
            foreach (var (path, silo) in new[] { ("local", Cluster.SiloNames[0]), ("remote", Cluster.SiloNames[1]) })
            {
                var writes = new List<(double P50, double P99)>();
                var reads = new List<(double P50, double P99)>();
                for (var run = 0; run < Runs; run++)
                {
                    var iterations = size == Mebibyte ? 200 : 1000;
                    await using var replica = caller.Open(Cluster.Node(silo).Node, NextLedger(), Window);
                    using var payload = Payload(size);
                    var written = new List<double>();
                    var read = new List<double>();
                    for (var slot = 0; slot < iterations + 50; slot++)
                    {
                        var started = Stopwatch.GetTimestamp();
                        _ = await replica.WriteAsync(new Slot(slot), payload, Slot.Zero, recovery: false, token);
                        var wrote = Stopwatch.GetTimestamp();
                        using (Batch(await replica.ReadAsync(new Slot(slot), Slot.Zero, token)))
                        {
                        }

                        var ended = Stopwatch.GetTimestamp();
                        if (slot >= 50)
                        {
                            written.Add(Stopwatch.GetElapsedTime(started, wrote).TotalMicroseconds);
                            read.Add(Stopwatch.GetElapsedTime(wrote, ended).TotalMicroseconds);
                        }
                    }

                    writes.Add(Percentiles(written));
                    reads.Add(Percentiles(read));
                }

                Report($"latency {path} {size / 1024} KiB, n = {Runs} runs of {(size == Mebibyte ? 200 : 1000)}: write p50 {Spread(writes.Select(static run => run.P50))} µs, p99 {Spread(writes.Select(static run => run.P99))} µs; read p50 {Spread(reads.Select(static run => run.P50))} µs, p99 {Spread(reads.Select(static run => run.P99))} µs");
            }
        }
    }

    /// <summary>
    /// Three silos each write full 1 MiB batches for eight seconds through sixteen ledgers of three replicas, its own node's
    /// and two others', with a quorum of two: each batch crosses to two remote replicas, whose calls share one sender copy in
    /// Orleans' pool, which the messages' pins keep from it, so the cluster allocates about one byte on the large object heap
    /// per byte of ingress, while each callee receives into Orleans' pooled pages and copies into Clog's; without the
    /// sharing, two. It reports; it asserts only that the large object heap stays within one and a half bytes per byte.
    /// </summary>
    [Fact(Explicit = true)]
    public async Task FanOutOfThreeReplicas()
    {
        var shared = new List<MeasurementSample>();
        var unshared = new List<MeasurementSample>();
        for (var run = 0; run < Runs; run++)
        {
            shared.Add(await FanOutAsync(share: true, Mebibyte, TimeSpan.FromSeconds(8)));
            unshared.Add(await FanOutAsync(share: false, Mebibyte, TimeSpan.FromSeconds(8)));
        }

        Report(Describe("fan-out to 3 replicas, sender copies shared", shared));
        Report(Describe("fan-out to 3 replicas, each remote call its own copy", unshared));
        Assert.True(shared.Average(static sample => sample.LargeObjectBytesPerByte) <= 1.5);
    }

    /// <summary>
    /// The same fan-out, and a remote read stream, at batches of 1 KiB, 4 KiB, 64 KiB, and 600 KiB: what each byte of
    /// ingress, or of a remote read, allocates on the small object, large object, and pinned object heaps, and the
    /// collections of each generation, which show where the copies Orleans keeps land (orleans.md §5.12).
    /// </summary>
    [Fact(Explicit = true)]
    public async Task BatchSizesOnEveryHeap()
    {
        foreach (var size in (int[])[1 << 10, 4 << 10, 64 << 10, 600 << 10])
        {
            var writes = new List<MeasurementSample>();
            var reads = new List<MeasurementSample>();
            for (var run = 0; run < Runs; run++)
            {
                writes.Add(await FanOutAsync(share: true, size, TimeSpan.FromSeconds(4)));
                reads.Add(await RemoteReadsAsync(size, TimeSpan.FromSeconds(4)));
            }

            Report(Describe($"fan-out to 3 replicas of {size / 1024} KiB batches", writes));
            Report(Describe($"remote reads of {size / 1024} KiB batches", reads));
        }
    }

    /// <summary>
    /// The callee under a burst from the other three silos, each keeping 32 writers of full 1 MiB batches outstanding to the
    /// one node for ten seconds, its node budget's worth: the callee host's throughput and peak payload bytes against its
    /// bound, the process's working set, the CPU per GiB, and the large-object-heap allocation rate and generation-2
    /// collections.
    /// </summary>
    [Fact(Explicit = true)]
    public async Task TheCalleeUnderABurstFromTheOtherSilos()
    {
        var samples = new List<MeasurementSample>();
        for (var run = 0; run < Runs; run++)
        {
            samples.Add(await BurstAsync());
        }

        Report(Describe($"burst from {Cluster.SiloNames.Count - 1} silos to one node", samples));
    }

    /// <summary>
    /// One recovery and one drain under the default limits: a ledger of three replicas, its own node's and two others',
    /// holds 256 full 1 MiB batches; a recovery from contiguous tail zero seals it and reads its window's slots, each a whole
    /// read reserving the 1 MiB read reply; a drain then reads every slot with 64 reads in flight.
    /// </summary>
    [Fact(Explicit = true)]
    public async Task ARecoveryAndADrainUnderTheDefaultLimits()
    {
        var token = TestContext.Current.CancellationToken;
        const int Slots = 256;
        var recoveries = new List<double>();
        var drains = new List<double>();
        for (var run = 0; run < Runs; run++)
        {
            var silo = Cluster.SiloNames[0];
            var id = NextLedger();
            await using (var writer = OpenLedger(silo, id, share: true))
            {
                using var payload = Payload(Mebibyte);
                for (var slot = 0; slot < Slots; slot++)
                {
                    _ = Assert.IsType<ReplicaWriteOutcome.Written>((await writer.WriteAsync(new Slot(slot), payload, token)).Value);

                    // Written in order, each on a quorum, so the slots below the next are a contiguous tail (LP-I11).
                    _ = writer.RaiseKnownContiguousTail(new Slot(slot + 1));
                }
            }

            await using var ledger = OpenLedger(silo, id, share: true);
            var started = Stopwatch.GetTimestamp();
            var recovered = Assert.IsType<RecoveryOutcome.Recovered>((await ledger.RecoverAsync(token)).Value);
            recoveries.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            Assert.Equal(Slots, recovered.End.Value);
            ledger.ObserveRecordedTail(recovered.End);

            started = Stopwatch.GetTimestamp();
            var inFlight = new Queue<Task<ReplicaRead>>();
            for (var slot = 0; slot < Slots || inFlight.Count > 0;)
            {
                while (slot < Slots && inFlight.Count < 64)
                {
                    inFlight.Enqueue(ledger.ReadAsync(new Slot(slot++), token).AsTask());
                }

                Batch(await inFlight.Dequeue()).Dispose();
            }

            drains.Add(Slots / Stopwatch.GetElapsedTime(started).TotalSeconds);
        }

        Report($"recovery of a ledger of {Slots} 1 MiB batches, window {Window.WindowSize}, n = {Runs}: {Spread(recoveries)} ms; drain with 64 reads in flight: {Spread(drains)} MiB/s");
    }

    private static string Describe(string what, List<MeasurementSample> samples) => string.Create(
        CultureInfo.InvariantCulture,
        $"{what}, {Environment.ProcessorCount} cores, n = {samples.Count}: ingress {Spread(samples.Select(static sample => sample.MebibytesPerSecond))} MiB/s; per ingress byte, allocated {Spread(samples.Select(static sample => sample.TotalBytesPerByte), "F2")} bytes, sampled small-object-heap {Spread(samples.Select(static sample => sample.SmallObjectBytesPerByte), "F2")}, large-object-heap {Spread(samples.Select(static sample => sample.LargeObjectBytesPerByte), "F2")}, pinned-object-heap {Spread(samples.Select(static sample => sample.PinnedObjectBytesPerByte), "F2")}; CPU {Spread(samples.Select(static sample => sample.CpuSecondsPerGibibyte), "F1")} s per GiB; collections per s, generation 0 {Spread(samples.Select(static sample => sample.Gen0PerSecond), "F1")}, 1 {Spread(samples.Select(static sample => sample.Gen1PerSecond), "F1")}, 2 {Spread(samples.Select(static sample => sample.Gen2PerSecond), "F1")}; working set peak {Spread(samples.Select(static sample => (double)sample.PeakWorkingSet / Mebibyte))} MiB; callee host bytes peak {Spread(samples.Select(static sample => (double)sample.PeakBoundedBytes / Mebibyte))} MiB of {samples[0].Bound / Mebibyte} MiB");

    private static (double P50, double P99) Percentiles(List<double> microseconds)
    {
        microseconds.Sort();
        double At(double quantile) => microseconds[(int)Math.Min(microseconds.Count - 1, quantile * microseconds.Count)];
        return (At(0.5), At(0.99));
    }

    private static ArcBuffer Payload(int length)
    {
        using var writer = new ArcBufferWriter();
        writer.Write(new byte[length]);
        return writer.ConsumeSlice(length);
    }

    private static ArcBuffer Batch(ReplicaRead read) => read.TryGetValue(out ArcBuffer batch) ? batch : throw new InvalidOperationException($"The read answered {read}.");

    private static MeasurementProbe Probe(LedgerReplicaHost host) => new(() => host.Bytes, host.MaxBytes);

    private static string Spread(IEnumerable<double> values, string format = "F0") => MeasurementSample.Spread(values, format);

    private static void Report(string line) => MeasurementSample.Report(line);

    private LedgerId NextLedger() => new(Interlocked.Increment(ref _nextLedger));

    /// <summary>
    /// Opens a ledger over three replicas through <paramref name="silo"/>'s transport, its own node's and the next two
    /// silos' nodes', with a quorum of two; without <paramref name="share"/>, each remote replica copies the payload first,
    /// so its call carries a copy of its own.
    /// </summary>
    private Ledger OpenLedger(string silo, LedgerId id, bool share)
    {
        var names = Cluster.SiloNames;
        var first = names.ToList().IndexOf(silo);
        var transport = Cluster.Transport(silo);
        var replicas = Enumerable.Range(0, 3).Select(offset =>
        {
            var replica = transport.Open(Cluster.Node(names[(first + offset) % 3]).Node, id, Window);
            return share || offset == 0 ? replica : new CopyingReplica(replica);
        });
        ILedgerReplica[] opened = [.. replicas];
        var nodes = new LedgerLocation.Nodes([.. opened.Select(static _ => NodeId.New())]);
        return new Ledger(id, LedgerQuorum.ForNodes(id, nodes.Replicas, opened, quorum: 2, Window, TimeProvider.System, localNode: null), nodes);
    }

    private async Task<MeasurementSample> FanOutAsync(bool share, int size, TimeSpan duration)
    {
        var token = TestContext.Current.CancellationToken;
        var ledgers = Cluster.SiloNames.Take(3).SelectMany(silo => Enumerable.Range(0, 16).Select(_ => (Ledger: OpenLedger(silo, NextLedger(), share), Payload: Payload(size)))).ToList();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var written = 0L;
        using var probe = Probe(Cluster.Host(Cluster.SiloNames[0]));
        stop.CancelAfter(duration);
        var writers = ledgers.Select(writer => Task.Run(
            async () =>
            {
                using var payload = writer.Payload;
                await using (writer.Ledger)
                {
                    for (var slot = 0L; !stop.IsCancellationRequested; slot++)
                    {
                        _ = await writer.Ledger.WriteAsync(new Slot(slot), payload, token);
                        _ = writer.Ledger.RaiseKnownContiguousTail(new Slot(slot + 1));
                        _ = Interlocked.Add(ref written, size);
                    }
                }
            },
            token)).ToList();

        await probe.WatchAsync(stop.Token);
        var sample = probe.Sample(Interlocked.Read(ref written));
        await Task.WhenAll(writers);
        return sample;
    }

    /// <summary>
    /// Three silos each keep 16 whole reads outstanding of one batch of <paramref name="size"/> bytes on the next silo's
    /// node, each read's DATA crossing from that node's grain.
    /// </summary>
    private async Task<MeasurementSample> RemoteReadsAsync(int size, TimeSpan duration)
    {
        var token = TestContext.Current.CancellationToken;
        var names = Cluster.SiloNames;
        var readers = new List<(ILedgerReplica Replica, int Count)>();
        for (var index = 0; index < 3; index++)
        {
            var replica = Cluster.Transport(names[index]).Open(Cluster.Node(names[(index + 1) % 3]).Node, NextLedger(), Window);
            using var payload = Payload(size);
            _ = await replica.WriteAsync(new Slot(0), payload, Slot.Zero, recovery: false, token);
            readers.Add((replica, 16));
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var read = 0L;
        using var probe = Probe(Cluster.Host(names[1]));
        stop.CancelAfter(duration);
        var tasks = readers.SelectMany(static reader => Enumerable.Repeat(reader.Replica, reader.Count)).Select(replica => Task.Run(
            async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    Batch(await replica.ReadAsync(new Slot(0), Slot.Zero, token)).Dispose();
                    _ = Interlocked.Add(ref read, size);
                }
            },
            token)).ToList();

        await probe.WatchAsync(stop.Token);
        var sample = probe.Sample(Interlocked.Read(ref read));
        await Task.WhenAll(tasks);
        foreach (var (replica, _) in readers)
        {
            await replica.DisposeAsync();
        }

        return sample;
    }

    private async Task<MeasurementSample> BurstAsync()
    {
        var token = TestContext.Current.CancellationToken;
        var callee = Cluster.SiloNames[0];
        var node = Cluster.Node(callee).Node;

        // Each writer's batches are its own, as a ledger's are, so no two writers share a sender copy.
        var replicas = Cluster.SiloNames.Skip(1)
            .SelectMany(caller => Enumerable.Range(0, 32).Select(_ => (Replica: Cluster.Transport(caller).Open(node, NextLedger(), Window), Payload: Payload(Mebibyte))))
            .ToList();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var written = 0L;
        using var probe = Probe(Cluster.Host(callee));
        stop.CancelAfter(TimeSpan.FromSeconds(10));
        var writers = replicas.Select(writer => Task.Run(
            async () =>
            {
                using var payload = writer.Payload;
                await using (writer.Replica)
                {
                    for (var slot = 0L; !stop.IsCancellationRequested; slot++)
                    {
                        _ = await writer.Replica.WriteAsync(new Slot(slot), payload, Slot.Zero, recovery: false, token);
                        _ = Interlocked.Add(ref written, Mebibyte);
                    }
                }
            },
            token)).ToList();

        await probe.WatchAsync(stop.Token);
        var sample = probe.Sample(Interlocked.Read(ref written));
        await Task.WhenAll(writers);
        return sample;
    }

    /// <summary>A remote replica whose writes carry a copy of their payload, so no two remote calls share a sender copy.</summary>
    private sealed class CopyingReplica(ILedgerReplica inner) : ILedgerReplica
    {
        public async ValueTask<ReplicaWriteOutcome> WriteAsync(Slot slot, ArcBuffer payload, Slot contiguousTail, bool recovery, CancellationToken cancellationToken = default)
        {
            using var copy = Copy(payload);
            return await inner.WriteAsync(slot, copy, contiguousTail, recovery, cancellationToken);
        }

        public ValueTask<ReplicaRead> ReadAsync(Slot slot, Slot contiguousTail, CancellationToken cancellationToken = default) => inner.ReadAsync(slot, contiguousTail, cancellationToken);

        public ValueTask<ReplicaRead> ReadAsync(Slot slot, int offset, int length, Slot contiguousTail, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(slot, offset, length, contiguousTail, cancellationToken);

        public ValueTask<ReplicaTailRead> ReadTailAsync(Slot contiguousTail, bool seal, CancellationToken cancellationToken = default) => inner.ReadTailAsync(contiguousTail, seal, cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();

        private static ArcBuffer Copy(ArcBuffer payload)
        {
            using var writer = new ArcBufferWriter();
            foreach (var segment in payload)
            {
                writer.Write(segment);
            }

            return writer.ConsumeSlice(payload.Length);
        }
    }
}
