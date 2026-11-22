using System.Diagnostics;
using System.Globalization;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Testing;

/// <summary>
/// Runs <see cref="LedgerReplicaSpec"/> requests against an <see cref="ILedgerReplica"/> and classifies their outcomes.
/// </summary>
/// <remarks>
/// It maps each case of the replica's answer unions (ledger-replicas.md §2.1), <c>Dropped</c> and <c>Backpressure</c>
/// among them, to its outcome, and recognizes the failure the contract defines, an <see cref="ArgumentException"/>
/// (§6.4). The caller classifies any other failure, such as a provider's transport failure, or it is
/// <see cref="LedgerReplicaOutcomeKind.Unexpected"/>.
/// </remarks>
internal static class LedgerReplicaExecutor
{
    /// <summary>
    /// Runs <paramref name="request"/> against <paramref name="replica"/>.
    /// </summary>
    /// <param name="replica">The replica under test.</param>
    /// <param name="request">A <see cref="LedgerReplicaSpec"/> request.</param>
    /// <param name="classify">Classifies a failure the contract does not define, or returns <see langword="null"/>.</param>
    /// <returns>The outcome.</returns>
    internal static async Task<LedgerReplicaOutcome> ExecuteAsync(
        ILedgerReplica replica, object request, Func<Exception, LedgerReplicaOutcome?> classify)
    {
        switch (request)
        {
            case LedgerReplicaWrite write:
                return await WriteAsync(
                    replica, write.Address, LedgerReplicaSpec.Payload(write.Address), new Slot(write.ContiguousTail), write.Recovery, classify);
            case LedgerReplicaWriteEmpty write:
                var empty = await WriteAsync(replica, write.Address, [], Slot.Zero, recovery: false, classify);
                return empty.Kind is LedgerReplicaOutcomeKind.Written or LedgerReplicaOutcomeKind.Sealed
                    ? new(LedgerReplicaOutcomeKind.Unexpected, Failure: "an empty write returned")
                    : empty;
            case LedgerReplicaRead read:
                return await CallAsync(
                    () => replica.ReadAsync(new Slot(read.Address), new Slot(read.ContiguousTail)), Classify, classify);
            case LedgerReplicaReadRange read:
                return await CallAsync(
                    () => replica.ReadAsync(new Slot(read.Address), read.Offset, read.Length, new Slot(read.ContiguousTail)),
                    Classify,
                    classify);
            case LedgerReplicaReadTail read:
                return await CallAsync(
                    () => replica.ReadTailAsync(new Slot(read.ContiguousTail), read.Seal),
                    static answer => answer switch
                    {
                        LedgerReplicaTail tail => new LedgerReplicaOutcome(LedgerReplicaOutcomeKind.Tail, tail.ContiguousTail.Value, tail.IsSealed),
                        ReplicaWriteOutcome.Dropped => new LedgerReplicaOutcome(LedgerReplicaOutcomeKind.Dropped),
                        ReplicaWriteOutcome.Backpressure => new LedgerReplicaOutcome(LedgerReplicaOutcomeKind.Backpressure),
                        null => throw new UnreachableException(),
                    },
                    classify);
            default:
                throw new UnreachableException(string.Create(CultureInfo.InvariantCulture, $"{request} is not a LedgerReplica request."));
        }
    }

    /// <summary>
    /// Runs a node's drop or restart, which <paramref name="call"/> performs, and classifies its outcome.
    /// </summary>
    internal static Task<LedgerReplicaOutcome> ExecuteNodeAsync(Func<ValueTask> call, Func<Exception, LedgerReplicaOutcome?> classify) =>
        CallAsync(call, classify);

    /// <summary>
    /// Runs the composition's recovery, which <paramref name="call"/> performs, and classifies its outcome.
    /// </summary>
    internal static Task<LedgerReplicaOutcome> ExecuteRecoverAsync(Func<ValueTask<RecoveryOutcome>> call, Func<Exception, LedgerReplicaOutcome?> classify) =>
        CallAsync(
            call,
            static outcome => outcome switch
            {
                RecoveryOutcome.Recovered recovered => new LedgerReplicaOutcome(LedgerReplicaOutcomeKind.End, recovered.End.Value),
                ReplicaWriteOutcome.Dropped => new LedgerReplicaOutcome(LedgerReplicaOutcomeKind.Dropped),
                null => throw new UnreachableException(),
            },
            classify);

    /// <summary>
    /// Runs a node's listing, which <paramref name="call"/> performs, and classifies the ledgers it lists by their
    /// indexes in <paramref name="ledgers"/>, the spec's ledgers.
    /// </summary>
    internal static Task<LedgerReplicaOutcome> ExecuteListingAsync(
        Func<ValueTask<IReadOnlyList<LedgerId>>> call, LedgerId[] ledgers, Func<Exception, LedgerReplicaOutcome?> classify) =>
        CallAsync(
            call,
            replicas => new LedgerReplicaOutcome(
                LedgerReplicaOutcomeKind.Listed,
                Listing: replicas.Aggregate(0, (listing, replica) => listing | (Array.IndexOf(ledgers, replica) is var index and >= 0 ? 1 << index : LedgerReplicaOutcome.UnknownLedger))),
            classify);

    /// <summary>
    /// Describes a failure, with every inner failure of an <see cref="AggregateException"/>.
    /// </summary>
    internal static string Describe(Exception exception) =>
        exception is AggregateException aggregate
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"{exception.GetType().Name} [{string.Join("; ", aggregate.InnerExceptions.Select(Describe))}]")
            : string.Create(CultureInfo.InvariantCulture, $"{exception.GetType().Name}: {exception.Message}");

    private static async Task<LedgerReplicaOutcome> WriteAsync(
        ILedgerReplica replica, long address, byte[] bytes, Slot contiguousTail, bool recovery, Func<Exception, LedgerReplicaOutcome?> classify)
    {
        using var writer = new ArcBufferWriter();
        writer.Write(bytes);
        using var payload = writer.PeekSlice(writer.Length);
        return await CallAsync(
            () => replica.WriteAsync(new Slot(address), payload, contiguousTail, recovery),
            static answer => new LedgerReplicaOutcome(answer switch
            {
                ReplicaWriteOutcome.Written => LedgerReplicaOutcomeKind.Written,
                ReplicaWriteOutcome.Sealed => LedgerReplicaOutcomeKind.Sealed,
                ReplicaWriteOutcome.Dropped => LedgerReplicaOutcomeKind.Dropped,
                ReplicaWriteOutcome.Backpressure => LedgerReplicaOutcomeKind.Backpressure,
                null => throw new UnreachableException(),
            }),
            classify);
    }

    private static async Task<LedgerReplicaOutcome> CallAsync(Func<ValueTask> call, Func<Exception, LedgerReplicaOutcome?> classify) =>
        await CallAsync(
            async () =>
            {
                await call();
                return true;
            },
            static _ => new LedgerReplicaOutcome(LedgerReplicaOutcomeKind.Done),
            classify);

    /// <summary>
    /// Runs the replica call <paramref name="call"/>, the only code whose failures the executor classifies, and maps its
    /// result outside it.
    /// </summary>
    private static async Task<LedgerReplicaOutcome> CallAsync<T>(
        Func<ValueTask<T>> call, Func<T, LedgerReplicaOutcome> map, Func<Exception, LedgerReplicaOutcome?> classify)
    {
        T result;
        try
        {
            result = await call();
        }
        catch (ArgumentException)
        {
            return new(LedgerReplicaOutcomeKind.InvalidArgument);
        }
#pragma warning disable CA1031 // Any other failure is an outcome: the caller's classification, or one the spec rejects.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            return classify(exception) ?? new(LedgerReplicaOutcomeKind.Unexpected, Failure: Describe(exception));
        }

        return map(result);
    }

    private static LedgerReplicaOutcome Classify(ReplicaRead read) => read switch
    {
        ArcBuffer batch => Data(batch),
        ReplicaRead.Missing => new(LedgerReplicaOutcomeKind.Missing),
        ReplicaRead.OutsideBatch outside => new(LedgerReplicaOutcomeKind.OutsideBatch, outside.Length),
        ReplicaWriteOutcome.Dropped => new(LedgerReplicaOutcomeKind.Dropped),
        ReplicaWriteOutcome.Backpressure => new(LedgerReplicaOutcomeKind.Backpressure),
        null => throw new UnreachableException(),
    };

    private static LedgerReplicaOutcome Data(ArcBuffer batch)
    {
        using (batch)
        {
            return new(LedgerReplicaOutcomeKind.Data, Bytes: batch.ToArray());
        }
    }
}

/// <summary>
/// Observes which calls on one system overlap in real time, and adds to each outcome the requests of the calls which
/// overlapped it (<see cref="LedgerReplicaOverlap"/>).
/// </summary>
internal sealed class LedgerReplicaOverlaps
{
    private readonly Lock _lock = new();
    private readonly List<Running> _running = [];

    /// <summary>
    /// Runs <paramref name="call"/>, which executes <paramref name="request"/>, and returns its outcome with its overlap.
    /// </summary>
    internal async Task<LedgerReplicaOutcome> RunAsync(object request, Func<Task<LedgerReplicaOutcome>> call)
    {
        var running = new Running(request);
        lock (_lock)
        {
            foreach (var other in _running)
            {
                other.Overlap = other.Overlap.With(request);
                running.Overlap = running.Overlap.With(other.Request);
            }

            _running.Add(running);
        }

        LedgerReplicaOutcome outcome;
        try
        {
            outcome = await call();
        }
        finally
        {
            lock (_lock)
            {
                _ = _running.Remove(running);
            }
        }

        return outcome with { Overlap = running.Overlap };
    }

    private sealed class Running(object request)
    {
        internal object Request { get; } = request;

        internal LedgerReplicaOverlap Overlap { get; set; } = LedgerReplicaOverlap.None;
    }
}
