using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Cluster.Control;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Simulation;

/// <summary>
/// Orleans-free simulated <see cref="ILocalStreamProcessor"/> for Clockwork simulation and Quint trace validation of
/// <see cref="LocalStreamDrainCoordinator"/>, <c>DrainWorker</c>, <c>ArchiveLogDrainWorker</c>, and
/// <c>NodeController</c> (verification.md §5.1, §7.1).
/// </summary>
public sealed class SimulatedWakeupStreamProcessor : ILocalStreamProcessor
{
    /// <summary>The canonical stream name for simulated wakeup records.</summary>
    public static readonly LocalStreamName DefaultStream = LocalStreamName.Parse("sim.wakeup");

    private readonly long _bytesPerEntry;
    private readonly Func<LedgerId, IReadOnlyCollection<byte>, CancellationToken, Task>? _onCommitAsync;
    private readonly ConcurrentDictionary<byte, int> _committedCounts = new();
    private int _commitCalls;
    private int _zeroEntryCommits;

    /// <summary>Initializes a new instance of the <see cref="SimulatedWakeupStreamProcessor"/> class.</summary>
    /// <param name="stream">Stream name (defaults to <see cref="DefaultStream"/>).</param>
    /// <param name="bytesPerEntry">Approximate memory weight charged per live entry in the fold.</param>
    /// <param name="onCommitAsync">Optional asynchronous hook invoked on non-empty commits (for fault/crash/race injection).</param>
    public SimulatedWakeupStreamProcessor(
        LocalStreamName? stream = null,
        long bytesPerEntry = 64,
        Func<LedgerId, IReadOnlyCollection<byte>, CancellationToken, Task>? onCommitAsync = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bytesPerEntry, 1, nameof(bytesPerEntry));
        Stream = stream ?? DefaultStream;
        _bytesPerEntry = bytesPerEntry;
        _onCommitAsync = onCommitAsync;
    }

    /// <inheritdoc />
    public LocalStreamName Stream { get; }

    /// <summary>Gets the number of non-empty backing commit calls executed.</summary>
    public int CommitCalls => Volatile.Read(ref _commitCalls);

    /// <summary>Gets the number of empty commits (0 surviving entries, 0 backing writes).</summary>
    public int ZeroEntryCommits => Volatile.Read(ref _zeroEntryCommits);

    /// <summary>Gets the set of wakeup keys durably committed at least once.</summary>
    public IReadOnlyCollection<byte> CommittedKeys => [.. _committedCounts.Keys];

    /// <summary>Encodes a simulated SetWakeup record (2 bytes: key, 1 = set or 0 = clear).</summary>
    public static byte[] Encode(byte key, bool set) => [key, set ? (byte)1 : (byte)0];

    /// <inheritdoc />
    public ILocalStreamFold BeginClosedLedgerFold(LedgerMetadata ledger, Slot tail) =>
        new Fold(this, ledger.Id);

    private sealed class Fold(SimulatedWakeupStreamProcessor owner, LedgerId ledger) : ILocalStreamFold
    {
        private readonly Dictionary<byte, bool> _entries = [];

        public long MemoryBytes => _entries.Count * owner._bytesPerEntry;

        public void Add(Slot slot, int rowIndex, ReadOnlySequence<byte> payload)
        {
            if (payload.Length != 2)
            {
                throw new ArchiveCorruptException(
                    string.Create(CultureInfo.InvariantCulture, $"Simulated wakeup payload at ledger {ledger} slot {slot} row {rowIndex} has invalid length {payload.Length}."));
            }

            Span<byte> buf = stackalloc byte[2];
            payload.CopyTo(buf);
            var key = buf[0];
            var flag = buf[1];
            if (flag is not (0 or 1))
            {
                throw new ArchiveCorruptException(
                    string.Create(CultureInfo.InvariantCulture, $"Simulated wakeup payload at ledger {ledger} slot {slot} row {rowIndex} has invalid flag {flag}."));
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

            var keys = _entries.Keys.ToArray();
            if (owner._onCommitAsync is not null)
            {
                await owner._onCommitAsync(ledger, keys, cancellationToken).ConfigureAwait(false);
            }

            foreach (var key in keys)
            {
                _ = owner._committedCounts.AddOrUpdate(key, 1, static (_, c) => c + 1);
                _ = _entries.Remove(key);
            }

            _ = Interlocked.Increment(ref owner._commitCalls);
        }

        public void Dispose() => _entries.Clear();
    }
}

/// <summary>
/// Orleans-free simulated <see cref="ILocalKeyPresenceLookup"/> for verifying O(1) zero-read fast path equivalence.
/// </summary>
public sealed class SimulatedLocalKeyPresenceLookup(Func<LedgerMetadata, ReadOnlyMemory<byte>, Slot, LocalKeyPresenceRead> probe) : ILocalKeyPresenceLookup
{
    private readonly Func<LedgerMetadata, ReadOnlyMemory<byte>, Slot, LocalKeyPresenceRead> _probe = probe ?? throw new ArgumentNullException(nameof(probe));

    /// <summary>Gets the number of times <see cref="TryHasKeyEntryAsync"/> was called.</summary>
    public int Probes { get; private set; }

    /// <inheritdoc />
    public ValueTask<LocalKeyPresenceRead> TryHasKeyEntryAsync(
        LedgerMetadata ledger,
        ReadOnlyMemory<byte> localKey,
        Slot tail,
        CancellationToken cancellationToken)
    {
        Probes++;
        return ValueTask.FromResult(_probe(ledger, localKey, tail));
    }
}
