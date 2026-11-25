using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Archive.Control;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Simulation;

/// <summary>
/// Orleans-free simulated <see cref="IMessageProcessor"/> for Clockwork simulation and Quint trace validation of
/// single-pass <c>DrainScan</c> message draining across <c>DrainWorker</c> and <c>ArchiveLogDrainWorker</c>
/// (verification.md §5.1, §7.1). Models idempotent per-(ledger, segmentStart) durable-sink commits so racing
/// drainers and crash-retries write at most once per segment.
/// </summary>
public sealed class SimulatedMessageProcessor : IMessageProcessor
{
    /// <summary>The canonical channel name for simulated atomic-message records.</summary>
    public static readonly MessageChannel DefaultChannel = MessageChannel.Parse("sim.messages");

    private readonly long _bytesPerEntry;
    private readonly Func<LedgerId, LedgerSegmentId, IReadOnlyCollection<byte>, CancellationToken, Task>? _onCommitAsync;
    private readonly ConcurrentDictionary<(LedgerId Ledger, Slot Start), byte[]> _committedBatches = new();
    private readonly ConcurrentDictionary<byte, int> _committedCounts = new();
    private int _commitCalls;
    private int _createdBatches;
    private int _duplicateBatches;
    private int _zeroEntryCommits;

    /// <summary>Initializes a new instance of the <see cref="SimulatedMessageProcessor"/> class.</summary>
    /// <param name="channel">Channel name (defaults to <see cref="DefaultChannel"/>).</param>
    /// <param name="bytesPerEntry">Approximate memory weight charged per live entry in the fold.</param>
    /// <param name="onCommitAsync">Optional asynchronous hook invoked on non-empty commits (for fault/crash/race injection).</param>
    public SimulatedMessageProcessor(
        MessageChannel? channel = null,
        long bytesPerEntry = 64,
        Func<LedgerId, LedgerSegmentId, IReadOnlyCollection<byte>, CancellationToken, Task>? onCommitAsync = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bytesPerEntry, 1, nameof(bytesPerEntry));
        Channel = channel ?? DefaultChannel;
        _bytesPerEntry = bytesPerEntry;
        _onCommitAsync = onCommitAsync;
    }

    /// <inheritdoc />
    public MessageChannel Channel { get; }

    /// <summary>Gets the number of non-empty durable-sink commit calls executed.</summary>
    public int CommitCalls => Volatile.Read(ref _commitCalls);

    /// <summary>Gets the number of distinct per-segment durable batches created (unique (ledger, segmentStart) keys).</summary>
    public int CreatedBatches => Volatile.Read(ref _createdBatches);

    /// <summary>Gets the number of idempotent duplicate commits (racing drainer / crash retry hitting existing batch).</summary>
    public int DuplicateBatches => Volatile.Read(ref _duplicateBatches);

    /// <summary>Gets the number of empty commits (0 surviving entries, 0 sink writes).</summary>
    public int ZeroEntryCommits => Volatile.Read(ref _zeroEntryCommits);

    /// <summary>Gets the set of message keys durably committed at least once.</summary>
    public IReadOnlyCollection<byte> CommittedKeys => [.. _committedCounts.Keys];

    /// <summary>Encodes a simulated atomic-message record (2 bytes: key, 1 = emit or 0 = retract).</summary>
    public static byte[] Encode(byte key, bool emit) => [key, emit ? (byte)1 : (byte)0];

    /// <inheritdoc />
    public IMessageFold BeginSegmentFold(LedgerMetadata ledger, LedgerSegmentId segment) =>
        new Fold(this, ledger.Id, segment);

    private sealed class Fold(SimulatedMessageProcessor owner, LedgerId ledger, LedgerSegmentId segment) : IMessageFold
    {
        private readonly Dictionary<byte, bool> _entries = [];

        public long MemoryBytes => _entries.Count * owner._bytesPerEntry;

        public void Add(Slot slot, int rowIndex, ReadOnlySequence<byte> payload)
        {
            if (payload.Length != 2)
            {
                throw new ArchiveCorruptException(
                    string.Create(CultureInfo.InvariantCulture, $"Simulated message payload at ledger {ledger} slot {slot} row {rowIndex} has invalid length {payload.Length}."));
            }

            Span<byte> buf = stackalloc byte[2];
            payload.CopyTo(buf);
            var key = buf[0];
            var flag = buf[1];
            if (flag is not (0 or 1))
            {
                throw new ArchiveCorruptException(
                    string.Create(CultureInfo.InvariantCulture, $"Simulated message payload at ledger {ledger} slot {slot} row {rowIndex} has invalid flag {flag}."));
            }

            if (flag == 1)
            {
                _entries[key] = true;
            }
            else
            {
                _ = _entries.Remove(key);
            }
        }

        public void Clear() => _entries.Clear();

        public async Task CommitAsync(CancellationToken cancellationToken)
        {
            if (_entries.Count == 0)
            {
                _ = Interlocked.Increment(ref owner._zeroEntryCommits);
                return;
            }

            var keys = _entries.Keys.Order().ToArray();
            if (owner._onCommitAsync is not null)
            {
                await owner._onCommitAsync(ledger, segment, keys, cancellationToken).ConfigureAwait(false);
            }

            if (owner._committedBatches.TryAdd((ledger, segment.Start), keys))
            {
                _ = Interlocked.Increment(ref owner._createdBatches);
                foreach (var key in keys)
                {
                    _ = owner._committedCounts.AddOrUpdate(key, 1, static (_, c) => c + 1);
                }
            }
            else
            {
                _ = Interlocked.Increment(ref owner._duplicateBatches);
            }

            _entries.Clear();
            _ = Interlocked.Increment(ref owner._commitCalls);
        }

        public void Dispose() => _entries.Clear();
    }
}
