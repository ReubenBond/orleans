using System.Buffers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Orleans.Durable.Jobs;
using Orleans.Durable.Journaling;
using Orleans.Durable.Storage.Engine.Archive.Memory;
using Orleans.Durable.Storage.Engine.Journals;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Control;
using Orleans.Durable.Storage.Engine.Ledgers.Memory;
using Orleans.Durable.Storage.Engine.MetaStore;
using Orleans.Durable.Storage.Testing;
using Orleans.Runtime;

namespace Orleans.Durable.Storage.Tests;

/// <summary>
/// Unit tests for <see cref="DurableJournalStorage"/> implementing <see cref="ICompanionJournalStorage"/>:
/// argument validation, rebase-retry preserving companion payloads verbatim across ledger drops, and atomic
/// delivery to <see cref="WakeupStreamHandler"/> (orleans.md §5.5, §5.15).
/// </summary>
public sealed class DurableJournalCompanionTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _leaks.Dispose();

    [Fact]
    public async Task DurableJournalStoragePreservesCompanionsAcrossRebaseRetryAndValidatesArguments()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));
        var dispatched = new TaskCompletionSource<IJobRunContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new WakeupStreamHandler(
            new WakeupOptions(),
            new CallbackDispatcher((_, ctx, _) =>
            {
                dispatched.TrySetResult(ctx);
                return ValueTask.FromResult(DurableJobRunResult.Completed);
            }),
            time);

        using var archive = new InMemoryArchiveStore();
        var register = new InMemoryConditionalRegister();
        var map = await LedgerMapView.OpenAsync(register, Token);
        await using var committer = new LedgerMapCommitter(register, int.MaxValue, map);
        var node = NodeId.New();
        using var transport = new InMemoryLedgerReplicaTransport();
        await using var reader = new LedgerReader(map, transport, time, node);
        await using var registry = new LedgerWriterRegistry(reader, 32 << 20, LedgerWriterOptions.Default, time, NullLoggerFactory.Instance);
        var executor = new TransitionExecutor(registry, NullLogger<TransitionExecutor>.Instance);

        var bootChoice = new TransitionChoice(node, new LedgerLocation.Nodes([node]), k: 4, chosenAt: 0, epoch: map.Current.Epoch);
        Assert.IsType<LedgerMapCommitOutcome.Applied>((await executor.ExecuteAsync(new Transition.Bootstrap(bootChoice), committer.CommitAsync, Token)).Value);
        await registry.ReconciledAsync(map.Current.Epoch, Token);

        await using var journals = new JournalStore(
            registry,
            archive,
            new JournalStoreOptions { WarmStateBytes = 0 },
            time,
            stateDatabase: null,
            localStreamHandlers: [handler]);

        var localRouting = new LocalRouting();
        localRouting.Publish(node, map, archive);
        var pressure = new NodePressure(TimeSpan.FromSeconds(1), time, NullLogger<NodePressure>.Instance);
        var localJournals = new LocalJournals(pressure);
        Assert.True(localJournals.Serve(journals, map, node));

        var nodeOptions = new DurableStorageOptions { Root = Path.GetTempPath(), Cluster = Guid.NewGuid(), ArchiveStore = Engine.Archive.ArchiveStoreKind.Lsm };
        var limits = ReplicaCallLimits.From(nodeOptions, new Orleans.Configuration.SiloMessagingOptions());
        var router = new JournalRouter(
            localRouting,
            localJournals,
            static () => { },
            new ForwardingBudgets(nodeOptions, time),
            limits,
            static _ => throw new NotSupportedException("Local-only test."),
            time,
            NullLogger<JournalRouter>.Instance);

        var storage = new DurableJournalStorage(new JournalId("g/1"), router, localRouting, maxBodyBytes: 1 << 20, format: "json", time);

        // Read to establish Origin precondition.
        var consumer = new CapturingConsumer();
        await storage.ReadAsync(consumer, Token);
        Assert.Empty(consumer.Bytes);

        // Validation: empty Append throws ArgumentOutOfRangeException; default(JournalCompanionRecord) throws ArgumentException.
        var wakeupPayload = WakeupCompanionCodec.Encode(new WakeupRecord(GrainId.Create("g", "1"), "orleans.messaging.pump", (time.GetUtcNow() + TimeSpan.FromSeconds(5)).UtcTicks));
        var companion = new JournalCompanionRecord(WakeupCompanionCodec.StreamName, wakeupPayload);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => storage.AppendWithCompanionsAsync(ReadOnlySequence<byte>.Empty, new[] { companion }, Token).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => storage.AppendWithCompanionsAsync(new ReadOnlySequence<byte>("a"u8.ToArray()), new[] { default(JournalCompanionRecord) }, Token).AsTask());

        // Write #1: initial Append so we have content before triggering a ledger drop/rebase.
        await storage.AppendAsync(new ReadOnlySequence<byte>("hello-"u8.ToArray()), Token);

        // Close, drain, and drop ledger 1 so the journal's version rebases on the new ledger!
        var closedLedger = map.Current.Ledgers.Single(static l => l.IsOpen).Id;
        var replaceChoice = new TransitionChoice(node, new LedgerLocation.Nodes([node]), k: 4, chosenAt: 0, epoch: map.Current.Epoch);
        Assert.IsType<LedgerMapCommitOutcome.Applied>((await executor.ExecuteAsync(new Transition.Replace(closedLedger, replaceChoice), committer.CommitAsync, Token)).Value);
        _ = await map.ReadAsync(Token);
        await registry.ReconciledAsync(map.Current.Epoch, Token);

        var closedMeta = map.Current.Get(closedLedger)!.Value;
        var closedTail = Assert.IsType<LedgerStatus.Closed>(closedMeta.Status.Value).T;
        var rowsByKey = new Dictionary<string, (byte[] Key, List<Engine.Archive.ArchiveRow> Rows)>(StringComparer.Ordinal);
        await foreach (var item in reader.ScanAsync(closedMeta, Slot.Zero, closedTail, Token))
        {
            Assert.True(item.Read.TryGetValue(out LedgerRead.Data data));
            using (data)
            {
                foreach (var row in item.Rows)
                {
                    if (!Engine.Ledgers.RowKinds.IsArchiveKind(row.Kind))
                    {
                        continue;
                    }

                    var keyBytes = row.Key.ToArray();
                    var keyHex = Convert.ToHexString(keyBytes);
                    if (!rowsByKey.TryGetValue(keyHex, out var bucket))
                    {
                        bucket = (keyBytes, []);
                        rowsByKey[keyHex] = bucket;
                    }

                    using var bodyWriter = new Engine.Buffers.ArcBufferWriter();
                    bodyWriter.Write(row.Body);
                    using var body = bodyWriter.Reader.ConsumeSlice(bodyWriter.Length);
                    bucket.Rows.Add(Engine.Archive.ArchiveRow.Live(
                        row.Tag,
                        row.Kind,
                        row.Version,
                        new LedgerSegmentId(closedLedger, Slot.Zero),
                        row.Token,
                        body));
                }
            }
        }

        foreach (var (_, (keyBytes, rows)) in rowsByKey)
        {
            archive.Drain(keyBytes, rows.ToArray());
            foreach (var r in rows)
            {
                r.Dispose();
            }
        }

        _ = await committer.CommitAsync(new LedgerMapChange.Drop(closedLedger), Token);
        _ = await map.ReadAsync(Token);

        // Write #2: AppendWithCompanionsAsync hits Rejected on stale pre-drop version, reads unchanged content, rebases,
        // and resends with the EXACT companion payload preserved!
        await storage.AppendWithCompanionsAsync(new ReadOnlySequence<byte>("world"u8.ToArray()), new[] { companion }, Token);

        // Advance FakeTimeProvider by 5s -> WakeupLedgerSession on the new open ledger MUST fire with IsEphemeralWakeup = true!
        time.Advance(TimeSpan.FromSeconds(5));
        var fired = await dispatched.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.True(fired.IsEphemeralWakeup);
        Assert.Equal(GrainId.Create("g", "1"), fired.Job.TargetGrainId);
        Assert.Equal("orleans.messaging.pump", fired.Job.Name);

        // Confirm journal content is intact ("hello-world").
        var readBack = new CapturingConsumer();
        var storage2 = new DurableJournalStorage(new JournalId("g/1"), router, localRouting, maxBodyBytes: 1 << 20, format: "json", time);
        await storage2.ReadAsync(readBack, Token);
        Assert.Equal("hello-world", System.Text.Encoding.UTF8.GetString(readBack.Bytes));

        await handler.StopAsync();
    }

    private sealed class CallbackDispatcher(Func<GrainId, IJobRunContext, CancellationToken, ValueTask<DurableJobRunResult>> callback) : IGrainWakeupDispatcher
    {
        public ValueTask<DurableJobRunResult> DispatchAsync(GrainId target, IJobRunContext context, CancellationToken cancellationToken) =>
            callback(target, context, cancellationToken);
    }

    private sealed class CapturingConsumer : IJournalStorageConsumer
    {
        private readonly MemoryStream _stream = new();
        public byte[] Bytes => _stream.ToArray();

        public void Read(JournalBufferReader buffer, IJournalMetadata? metadata)
        {
            if (buffer.Length > 0)
            {
                var data = new byte[buffer.Length];
                buffer.Read(data);
                _stream.Write(data);
            }
        }
    }
}
