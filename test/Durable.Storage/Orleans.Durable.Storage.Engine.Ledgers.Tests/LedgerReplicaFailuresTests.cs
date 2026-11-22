using Orleans.Durable.Storage.Engine.Ledgers.Store;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests;

/// <summary>
/// Pins the one classifier of ledger replica failures (ledger-replicas.md §6.4), which a recovery's replica operations and an
/// ledger writer's repeated writes share, so the two agree on which failures a repetition waits out (ledger-replicas.md
/// §6.5, LP-I12).
/// </summary>
[NoPageLeakCheck("Failures are classified by type; the tests rent no pages.")]
public sealed class LedgerReplicaFailuresTests
{
    public static TheoryData<string> Unchangeable() =>
    [
        nameof(ArgumentException),
        nameof(ArgumentOutOfRangeException),
        nameof(ObjectDisposedException),
        nameof(NotSupportedException),
        nameof(InvalidDataException),
        nameof(LedgerReplicaCorruptException),
        nameof(LedgerStorageCorruptException),
        nameof(LedgerStoreClusterMismatchException),
        nameof(InsufficientMemoryException),
    ];

    public static TheoryData<string> Unknown() =>
    [
        nameof(IOException),
        nameof(TimeoutException),
        nameof(InvalidOperationException),
        nameof(UnauthorizedAccessException),
        nameof(LedgerReplicaQueueTimeoutException),
    ];

    /// <summary>
    /// ledger-replicas.md §6.4: a failure repetition does not change ends the repetition, whatever the caller's cancellation.
    /// </summary>
    [Theory]
    [MemberData(nameof(Unchangeable))]
    public void AFailureRepetitionCannotChangeIsNotTransient(string kind)
    {
        var failure = Failure(kind);
        Assert.False(LedgerReplicaFailures.IsTransient(failure, CancellationToken.None));
        Assert.False(LedgerReplicaFailures.IsTransient(failure, new CancellationToken(canceled: true)));
    }

    /// <summary>
    /// ledger-replicas.md §2.2, §6.4: an I/O error, a timeout, or any other failure leaves the outcome unknown, so a
    /// repetition may succeed; a call which timed out in its node's queue never left its caller, so it may too.
    /// </summary>
    [Theory]
    [MemberData(nameof(Unknown))]
    public void AFailureWhoseOutcomeIsUnknownIsTransient(string kind) =>
        Assert.True(LedgerReplicaFailures.IsTransient(Failure(kind), CancellationToken.None));

    /// <summary>
    /// A cancellation is the caller's own only once its token is cancelled, which ends the repetition; one the operation
    /// raised itself, such as a transport's timeout, is an unknown outcome.
    /// </summary>
    [Fact]
    public void OnlyTheCallersOwnCancellationEndsTheRepetition()
    {
        using var stop = new CancellationTokenSource();
        Assert.True(LedgerReplicaFailures.IsTransient(new OperationCanceledException("The transport timed out."), stop.Token));
        Assert.True(LedgerReplicaFailures.IsTransient(new TaskCanceledException("The transport timed out."), stop.Token));

        stop.Cancel();
        Assert.False(LedgerReplicaFailures.IsTransient(new OperationCanceledException(stop.Token), stop.Token));
        Assert.False(LedgerReplicaFailures.IsTransient(new TaskCanceledException("The transport timed out."), stop.Token));
    }

    /// <summary>
    /// LP-I12, ledger-protocol.md §6: an <see cref="AggregateException"/> is transient when any of its failures is, nested ones
    /// included, as a quorum may form once one replica recovers, and otherwise not.
    /// </summary>
    [Fact]
    public void AnAggregateIsTransientWhenAnyOfItsFailuresIs()
    {
        static Exception Transient() => new TimeoutException("The replica timed out.");
        static Exception Terminal() => new LedgerReplicaCorruptException(Slot.Zero, "corrupt");

        Assert.True(LedgerReplicaFailures.IsTransient(new AggregateException(Terminal(), Transient()), CancellationToken.None));
        Assert.True(LedgerReplicaFailures.IsTransient(new AggregateException(Transient(), Transient()), CancellationToken.None));
        Assert.False(LedgerReplicaFailures.IsTransient(new AggregateException(Terminal(), new ObjectDisposedException("replica")), CancellationToken.None));
        Assert.True(LedgerReplicaFailures.IsTransient(new AggregateException(Terminal(), new AggregateException(Terminal(), Transient())), CancellationToken.None));
        Assert.False(LedgerReplicaFailures.IsTransient(new AggregateException(Terminal(), new AggregateException(Terminal(), Terminal())), CancellationToken.None));
        Assert.False(LedgerReplicaFailures.IsTransient(new AggregateException(Failure(nameof(InsufficientMemoryException)), Terminal()), CancellationToken.None));

        // A cancellation within an aggregate is the caller's once its token is cancelled.
        using var stop = new CancellationTokenSource();
        Assert.True(LedgerReplicaFailures.IsTransient(new AggregateException(Terminal(), new OperationCanceledException()), stop.Token));
        stop.Cancel();
        Assert.False(LedgerReplicaFailures.IsTransient(new AggregateException(Terminal(), new OperationCanceledException(stop.Token)), stop.Token));
    }

    [Fact]
    public void AMissingFailureIsRejected() =>
        _ = Assert.Throws<ArgumentNullException>(() => LedgerReplicaFailures.IsTransient(null!, CancellationToken.None));

    /// <summary>
    /// ledger-replicas.md §6.4: an <see cref="InvalidDataException"/> does not change on repetition whatever raised it: the
    /// quorum's, a decoder's over bytes which fail their format, one carrying an I/O error, or a store's which records
    /// that its checkpoint generations are exhausted.
    /// </summary>
    [Fact]
    public void AnInvalidDataExceptionIsPermanentFromAnySource()
    {
        Exception[] failures =
        [
            new InvalidDataException("A read below the known contiguous tail found every replica missing."),
            Assert.Throws<InvalidDataException>(() => ReplicaCatalog.Decode(new byte[3]).Checkpoint()),
            new InvalidDataException("The checkpoint object could not be read.", new IOException("The device failed the read.")),
            SlotCheckpoints.GenerationsExhausted(),
        ];
        Assert.All(failures, static failure => Assert.False(LedgerReplicaFailures.IsTransient(failure, CancellationToken.None)));
    }

    private static Exception Failure(string kind) => kind switch
    {
        nameof(ArgumentException) => new ArgumentException("rejected"),
        nameof(ArgumentOutOfRangeException) => new ArgumentOutOfRangeException(nameof(kind), "The address lies beyond the slot space."),
        nameof(ObjectDisposedException) => new ObjectDisposedException("replica"),
        nameof(NotSupportedException) => new NotSupportedException("unsupported"),
        nameof(InvalidDataException) => new InvalidDataException("A read below the known contiguous tail found every replica missing."),
        nameof(LedgerReplicaCorruptException) => new LedgerReplicaCorruptException(Slot.Zero, "corrupt"),
        nameof(LedgerStorageCorruptException) => new LedgerStorageCorruptException("corrupt"),
        nameof(LedgerStoreClusterMismatchException) => new LedgerStoreClusterMismatchException("cluster", LedgerReplicaTestData.Cluster, "other"),
        nameof(InsufficientMemoryException) => new InsufficientMemoryException("The process ran out of memory."),
        nameof(IOException) => new IOException("The replica is unreachable."),
        nameof(TimeoutException) => new TimeoutException("The replica timed out."),
        nameof(InvalidOperationException) => new InvalidOperationException("The replica is restarting."),
        nameof(UnauthorizedAccessException) => new UnauthorizedAccessException("The replica refused the request."),
        nameof(LedgerReplicaQueueTimeoutException) => new LedgerReplicaQueueTimeoutException(LedgerReplicaTestData.Node, TimeSpan.FromSeconds(1)),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "No such failure."),
    };
}
