using System.Collections.Immutable;
using System.Security.Cryptography;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Durable.Jobs;
using Orleans.Durable.Storage.Engine.Archive.Memory;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Journals;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Control;
using Orleans.Durable.Storage.Engine.MetaStore;
using Orleans.Runtime;

namespace Orleans.Durable.Storage.Benchmarks;

/// <summary>
/// Measures <see cref="JournalStore"/> warm-primary <c>Append</c> write cost with 0 attached messages (ordinary path,
/// confirming zero regression) versus 1 atomic <c>orleans.reminder</c> message record (1-RTT path) over a 3-replica
/// in-memory quorum ledger (verification.md §8.4).
/// </summary>
[MemoryDiagnoser]
public class MessageWriteBenchmarks : IAsyncDisposable
{
    private const int OperationsPerInvoke = 32;
    private static readonly ImmutableArray<NodeId> Nodes = [MakeNodeId(1), MakeNodeId(2), MakeNodeId(3)];

    private NullDiskLedgerReplicaTransport? _transport;
    private LedgerMapCommitter? _committer;
    private LedgerReader? _reader;
    private LedgerWriterRegistry? _registry;
    private InMemoryArchiveStore? _archive;
    private ReminderMessageHandler? _reminderHandler;
    private JournalStore? _journals;
    private ArcBuffer _payload;
    private JournalRowKey _key = null!;
    private JournalVersion _version;
    private byte[] _reminderPayload = [];

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _transport = new NullDiskLedgerReplicaTransport();
        var register = new InMemoryConditionalRegister();
        var map = await LedgerMapView.OpenAsync(register, CancellationToken.None).ConfigureAwait(false);
        _committer = new LedgerMapCommitter(register, int.MaxValue);
        _reader = new LedgerReader(map, _transport, TimeProvider.System, Nodes[0]);
        var writerOptions = new LedgerWriterOptions(
            maxSegmentBytes: 1L << 30,
            maxSegmentKeys: int.MaxValue,
            maxSegmentAge: TimeSpan.FromHours(1),
            maxProposalBytes: 32L << 20);
        _registry = new LedgerWriterRegistry(_reader, 1 << 25, writerOptions, TimeProvider.System, NullLoggerFactory.Instance);
        var executor = new TransitionExecutor(_registry, NullLogger<TransitionExecutor>.Instance);
        var choice = new TransitionChoice(Nodes[0], Nodes, 64, 0, map.Current.Epoch);
        _ = await executor.ExecuteAsync(new Transition.Bootstrap(choice), _committer.CommitAsync, CancellationToken.None).ConfigureAwait(false);
        _ = await map.ReadAsync(CancellationToken.None).ConfigureAwait(false);
        await _registry.ReconciledAsync(map.Current.Epoch, CancellationToken.None).ConfigureAwait(false);

        _reminderHandler = new ReminderMessageHandler(
            new ReminderOptions(),
            new NoOpDispatcher(),
            TimeProvider.System);
        _archive = new InMemoryArchiveStore();
        _journals = new JournalStore(
            _registry,
            _archive,
            new JournalStoreOptions { MaxBodyBytes = 16 << 20, MaxQueuedBytes = 128 << 20, WarmStateBytes = 128 << 20 },
            TimeProvider.System,
            stateDatabase: null,
            messageHandlers: [_reminderHandler]);

        var bytes = new byte[1024];
        RandomNumberGenerator.Fill(bytes);
        _payload = ArcBuffer.Wrap(bytes);
        _key = JournalRowKey.Encode("bench-message");
        _reminderPayload = ReminderMessageCodec.Encode(new ReminderRecord(
            GrainId.Create("bench", "1"),
            "orleans.messaging.pump",
            (DateTimeOffset.UtcNow + TimeSpan.FromHours(1)).UtcTicks));

        using var seed = _journals.EncodeReplace(_key, JournalVersion.Origin, _payload);
        var outcome = await _journals.WriteAsync(seed, CancellationToken.None).ConfigureAwait(false);
        _version = ((JournalWriteOutcome.Accepted)outcome.Value!).Version;
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = OperationsPerInvoke)]
    public async Task AppendZeroMessagesAsync()
    {
        for (var i = 0; i < OperationsPerInvoke; i++)
        {
            using var record = _journals!.EncodeAppend(_key, _version, _payload);
            var outcome = await _journals.WriteAsync(record, CancellationToken.None).ConfigureAwait(false);
            _version = ((JournalWriteOutcome.Accepted)outcome.Value!).Version;
        }
    }

    [Benchmark(OperationsPerInvoke = OperationsPerInvoke)]
    public async Task AppendOneReminderMessageAsync()
    {
        var message = new MessageRecord(ReminderMessageHandler.ReminderChannel, _reminderPayload);
        for (var i = 0; i < OperationsPerInvoke; i++)
        {
            using var record = _journals!.EncodeAppend(_key, _version, _payload, [message]);
            var outcome = await _journals.WriteAsync(record, CancellationToken.None).ConfigureAwait(false);
            _version = ((JournalWriteOutcome.Accepted)outcome.Value!).Version;
        }
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        _payload.Dispose();
        _payload = default;
        if (_reminderHandler is not null)
        {
            await _reminderHandler.StopAsync().ConfigureAwait(false);
        }

        if (_journals is not null)
        {
            await _journals.DisposeAsync().ConfigureAwait(false);
        }

        if (_registry is not null)
        {
            await _registry.DisposeAsync().ConfigureAwait(false);
        }

        if (_reader is not null)
        {
            await _reader.DisposeAsync().ConfigureAwait(false);
        }

        if (_committer is not null)
        {
            await _committer.DisposeAsync().ConfigureAwait(false);
        }

        _archive?.Dispose();
        _transport?.Dispose();
    }

    public ValueTask DisposeAsync() => new(CleanupAsync());

    private static NodeId MakeNodeId(byte index)
    {
        Span<byte> bytes = stackalloc byte[16];
        bytes[15] = index;
        return new NodeId(new Guid(bytes));
    }

    private sealed class NoOpDispatcher : IGrainReminderDispatcher
    {
        public ValueTask<DurableJobRunResult> DispatchAsync(GrainId target, IJobRunContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(DurableJobRunResult.Completed);
    }
}
