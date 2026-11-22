using System.Globalization;
using Orleans.Durable.Storage.Diagnostics;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers.Store;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests;

/// <summary>
/// Takes the local storage failures a test injected, each of which would have stopped the process (conventions.md §14).
/// </summary>
internal static class StorageFailures
{
    /// <summary>
    /// Takes the one failure the current test reported since the first <paramref name="since"/> reports, and checks its
    /// reporting site and that the path it names lies at or beneath <paramref name="path"/>.
    /// </summary>
    internal static ReportedFatalError TakeOne(long since, FatalErrorSite operation, string path)
    {
        var failure = Assert.Single(FatalErrors.TakeMine(since));
        Assert.Equal(operation, failure.Site);
        Assert.StartsWith(path, failure.Path, StringComparison.Ordinal);
        return failure;
    }

    /// <summary>
    /// Takes the one defect the current test reported since the first <paramref name="since"/> reports, and checks its
    /// name and that it reported <paramref name="defect"/>.
    /// </summary>
    internal static ReportedFatalError TakeDefect(long since, string name, Exception defect)
    {
        var failure = Assert.Single(FatalErrors.TakeMine(since));
        Assert.True(failure.Failure.IsDefect);
        Assert.Equal(name, failure.Failure.Defect);
        Assert.Same(defect, failure.Failure.Exception);
        return failure;
    }
}

/// <summary>
/// Checks what a stopped LedgerStore answers: nothing (conventions.md §14).
/// </summary>
internal static class StoppedStore
{
    private static readonly LedgerReplicaOptions Window = new() { WindowSize = LedgerStoreTestExtensions.DefaultWindow };

    /// <summary>
    /// Asserts that <paramref name="store"/> stopped at <paramref name="failure"/>, a reported failure, and that every
    /// operation of the store, and of <paramref name="replica"/> over one of its replicas, fails with it.
    /// </summary>
    internal static async Task AssertAnswersNothingAsync(LedgerStore store, LedgerStoreReplica replica, Exception failure)
    {
        var token = TestContext.Current.CancellationToken;
        Assert.Same(failure, store.Stopped);
        Assert.True(FatalErrorHandler.IsReported(failure), "The store stopped at a failure no one reported.");
        var unheld = new LedgerId(ulong.MaxValue);
        Assert.Same(failure, Assert.ThrowsAny<Exception>(() => store.OpenReplica(unheld, Window)));
        Assert.Same(failure, await Assert.ThrowsAnyAsync<Exception>(async () => await store.ListReplicasAsync(token)));
        Assert.Same(failure, Assert.ThrowsAny<Exception>(() => store.GetRetention()));
        Assert.Same(failure, Assert.ThrowsAny<Exception>(() => store.ListRetention()));
        Assert.Same(failure, Assert.ThrowsAny<Exception>(() => store.ListCorruptReplicas()));
        Assert.Same(failure, await Assert.ThrowsAnyAsync<Exception>(async () => await store.CreateReplicaAsync(unheld, token)));
        Assert.Same(failure, await Assert.ThrowsAnyAsync<Exception>(async () => await store.DropReplicaAsync(unheld, token)));
        Assert.Same(failure, await Assert.ThrowsAnyAsync<Exception>(() => LedgerStoreReplicaTests.WriteAsync(replica, 1_000, [1])));
        Assert.Same(failure, await Assert.ThrowsAnyAsync<Exception>(() => LedgerStoreReplicaTests.RecoveryWriteAsync(replica, 1_000, [1])));
        Assert.Same(failure, await Assert.ThrowsAnyAsync<Exception>(async () => await replica.ReadAsync(Slot.Zero, token)));
        Assert.Same(failure, await Assert.ThrowsAnyAsync<Exception>(async () => await replica.ReadAsync(Slot.Zero, 0, 1, token)));
        Assert.Same(failure, await Assert.ThrowsAnyAsync<Exception>(async () => await replica.ReadTailAsync(token)));
        Assert.Same(failure, await Assert.ThrowsAnyAsync<Exception>(async () => await replica.SealAsync(token)));
        Assert.Same(failure, await Assert.ThrowsAnyAsync<Exception>(async () => await replica.CheckpointAsync(token)));
    }
}

internal static class LedgerReplicaTestData
{
    /// <summary>
    /// Gets the cluster which owns the replicas created by the tests.
    /// </summary>
    internal static Guid Cluster { get; } = Guid.ParseExact("0123456789abcdef0123456789abcdef", "N");

    /// <summary>
    /// Gets a cluster which differs from <see cref="Cluster"/>.
    /// </summary>
    internal static Guid OtherCluster { get; } = Guid.ParseExact("fedcba9876543210fedcba9876543210", "N");

    /// <summary>
    /// Gets a node identity for tests which name one node.
    /// </summary>
    internal static NodeId Node { get; } = new(Guid.ParseExact("11111111222233334444555555555555", "N"));

    /// <summary>
    /// Gets the ledger ID of tests which name one ledger.
    /// </summary>
    internal static LedgerId Ledger { get; } = new(7);

    /// <summary>
    /// Returns the ledger IDs of <paramref name="values"/>, in order.
    /// </summary>
    internal static LedgerId[] Ledgers(params ulong[] values) => [.. values.Select(static value => new LedgerId(value))];

    /// <summary>
    /// Sends a writer write which carries no known contiguous tail.
    /// </summary>
    internal static ValueTask<ReplicaWriteOutcome> WriteAsync(
        this ILedgerReplica replica,
        long slot,
        ArcBuffer payload,
        CancellationToken cancellationToken = default) =>
        replica.WriteAsync(new Slot(slot), payload, Slot.Zero, recovery: false, cancellationToken);

    /// <inheritdoc cref="WriteAsync(ILedgerReplica, long, ArcBuffer, CancellationToken)"/>
    internal static ValueTask<ReplicaWriteOutcome> WriteAsync(
        this ILedgerReplica replica,
        Slot slot,
        ArcBuffer payload,
        CancellationToken cancellationToken = default) =>
        replica.WriteAsync(slot, payload, Slot.Zero, recovery: false, cancellationToken);

    /// <summary>
    /// Sends a recovery write which carries no known contiguous tail (LR-I5).
    /// </summary>
    internal static ValueTask<ReplicaWriteOutcome> RecoveryWriteAsync(
        this ILedgerReplica replica,
        long slot,
        ArcBuffer payload,
        CancellationToken cancellationToken = default) =>
        replica.WriteAsync(new Slot(slot), payload, Slot.Zero, recovery: true, cancellationToken);

    /// <summary>
    /// Reads an address, carrying no known contiguous tail.
    /// </summary>
    internal static ValueTask<ReplicaRead> ReadAsync(
        this ILedgerReplica replica,
        long slot,
        CancellationToken cancellationToken = default) =>
        replica.ReadAsync(new Slot(slot), Slot.Zero, cancellationToken);

    /// <inheritdoc cref="ReadAsync(ILedgerReplica, long, CancellationToken)"/>
    internal static ValueTask<ReplicaRead> ReadAsync(
        this ILedgerReplica replica,
        Slot slot,
        CancellationToken cancellationToken = default) =>
        replica.ReadAsync(slot, Slot.Zero, cancellationToken);

    /// <summary>
    /// Reads a range of an address's DATA, carrying no known contiguous tail.
    /// </summary>
    internal static ValueTask<ReplicaRead> ReadAsync(
        this ILedgerReplica replica,
        Slot slot,
        int offset,
        int length,
        CancellationToken cancellationToken = default) =>
        replica.ReadAsync(slot, offset, length, Slot.Zero, cancellationToken);

    /// <summary>
    /// Reads the replica's contiguous tail and seal without sealing, carrying no known contiguous tail.
    /// </summary>
    internal static ValueTask<ReplicaTailRead> ReadTailAsync(this ILedgerReplica replica, CancellationToken cancellationToken = default) =>
        replica.ReadTailAsync(Slot.Zero, seal: false, cancellationToken);

    /// <summary>
    /// Seals the replica, carrying no known contiguous tail, and returns its contiguous tail (LR-I8).
    /// </summary>
    internal static ValueTask<ReplicaTailRead> SealAsync(this ILedgerReplica replica, CancellationToken cancellationToken = default) =>
        replica.ReadTailAsync(Slot.Zero, seal: true, cancellationToken);

    internal static byte[] CreatePayload(int length, int seed = 17) =>
        [.. Enumerable.Range(0, length).Select(index => (byte)((index * 31 + seed) & byte.MaxValue))];

    /// <summary>
    /// Copies <paramref name="value"/> into an owned buffer.
    /// </summary>
    internal static ArcBuffer Buffer(ReadOnlySpan<byte> value)
    {
        using var writer = new ArcBufferWriter();
        writer.Write(value);
        return writer.ConsumeSlice(writer.Length);
    }

    /// <summary>
    /// Reads the DATA payload at <paramref name="slot"/>, or <see langword="null"/> when the address is missing.
    /// </summary>
    internal static async Task<byte[]?> ReadBytesAsync(
        ILedgerReplica replica,
        long slot,
        CancellationToken cancellationToken)
    {
        var result = await replica.ReadAsync(new Slot(slot), Slot.Zero, cancellationToken);
        if (result.Case == ReplicaReadCase.Missing)
        {
            return null;
        }

        using var payload = result.Batch;
        return payload.ToArray();
    }
}

/// <summary>
/// Holds a node root's cluster claim in memory, beside a simulated WAL storage, so a store opened over
/// <see cref="Wal.Simulation.SimulatedWalStorage"/> verifies and publishes its claim as the file-system root does
/// (LS-I12). The claim survives simulated crashes, as a published claim survives a real one.
/// </summary>
internal sealed class InMemoryNodeRootClaim : Store.INodeRootClaim
{
    private readonly Lock _lock = new();
    private byte[]? _content;

    internal InMemoryNodeRootClaim(Guid? cluster = null)
    {
        if (cluster is { } claimed)
        {
            _content = LedgerStoreClusterClaims.Encode(claimed);
        }
    }

    internal byte[]? Content
    {
        get
        {
            lock (_lock)
            {
                return _content?.ToArray();
            }
        }
    }

    public ValueTask<byte[]?> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Content);
    }

    public ValueTask<bool> CreateIfAbsentAsync(byte[] content, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            if (_content is not null)
            {
                return ValueTask.FromResult(false);
            }

            _content = [.. content];
        }

        return ValueTask.FromResult(true);
    }
}

internal sealed class TemporaryDirectory : IDisposable
{
    internal TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            string.Concat("clog-ledger-replicas-tests-", Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)));

        // As an operator does, the test creates the node root before a store opens on it (ledgerstore.md §3.3).
        _ = Directory.CreateDirectory(Path);
    }

    internal string Path { get; }

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
