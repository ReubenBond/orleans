using System.Globalization;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Testing;

/// <summary>
/// The kinds of request a <c>Ledger</c> sends a replica.
/// </summary>
internal enum ReplicaCallKind
{
    /// <summary>A writer write.</summary>
    Write,

    /// <summary>A recovery write.</summary>
    RecoveryWrite,

    /// <summary>A read of one slot, or a range of its batch.</summary>
    Read,

    /// <summary>A contiguous-tail read.</summary>
    ReadTail,

    /// <summary>A seal.</summary>
    Seal,

    /// <summary>The replica's disposal.</summary>
    Dispose,
}

/// <summary>
/// One request a client's <c>Ledger</c> sent a replica: its kind, slot, range, and the contiguous tail it carried.
/// </summary>
/// <param name="Client">The client: the process whose <c>Ledger</c> sent it.</param>
/// <param name="Replica">The replica's index in the client's <c>Ledger</c>.</param>
/// <param name="Kind">The request's kind.</param>
/// <param name="Slot">The slot of a write or a read.</param>
/// <param name="CarriedTail">The contiguous tail the request carried (LR-I6), or <c>-1</c> for a disposal.</param>
/// <param name="Offset">The first byte of a range read.</param>
/// <param name="Length">The number of bytes of a range read.</param>
internal sealed record ReplicaCall(
    string Client, int Replica, ReplicaCallKind Kind, long? Slot, long CarriedTail, int? Offset = null, int? Length = null)
{
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"{Client} -> {Replica} {Kind}{(Slot is { } slot ? string.Create(CultureInfo.InvariantCulture, $" {slot}") : string.Empty)}{(Offset is { } offset ? string.Create(CultureInfo.InvariantCulture, $" [{offset}, +{Length})") : string.Empty)}{(Kind == ReplicaCallKind.Dispose ? string.Empty : string.Create(CultureInfo.InvariantCulture, $" tail {CarriedTail}"))}");
}

/// <summary>
/// A replica's answer to a <see cref="ReplicaCall"/>: the write's or read's result, the tail and seal a contiguous-tail
/// read or a seal reported, or the failure's type.
/// </summary>
/// <param name="Answer">The answer.</param>
/// <param name="Tail">The contiguous tail and seal a contiguous-tail read or a seal reported.</param>
internal sealed record ReplicaAnswer(string Answer, LedgerReplicaTail? Tail = null)
{
    /// <summary>
    /// Gets the answer of a request still in flight when the recording was read.
    /// </summary>
    public static ReplicaAnswer Pending { get; } = new("Pending");

    public override string ToString() => Tail is { } tail
        ? string.Create(CultureInfo.InvariantCulture, $"{Answer} tail {tail.ContiguousTail.Value} sealed {tail.IsSealed}")
        : Answer;
}

/// <summary>
/// A replica which records each request its client's <c>Ledger</c> sends it, and the answer, into a history of replica
/// calls, each an invocation and a response timestamped on one clock (verification.md §3.4): what a model of the ledger
/// protocol judges. Row B.11 records the simulations' ledger traces through it.
/// </summary>
/// <remarks>
/// Each request is its own client in the history, since a <c>Ledger</c> has many requests in flight at once. A failure
/// is a definite answer only when the replica contract makes it one (ledger-replicas.md §2.2): a ledger replica failure
/// type, a rejected argument, or a request the replica cannot serve. Every other failure is an unknown outcome, which may
/// still take effect, so its response time is unbounded.
/// </remarks>
/// <param name="inner">The replica, which the wrapper disposes.</param>
/// <param name="client">The client.</param>
/// <param name="replica">The replica's index in the client's <c>Ledger</c>.</param>
/// <param name="recording">The history the calls are recorded into.</param>
internal sealed class CallRecordingLedgerReplica(
    ILedgerReplica inner,
    string client,
    int replica,
    HistoryRecorder<ReplicaCall, ReplicaAnswer> recording) : ILedgerReplica
{
    public ValueTask<ReplicaWriteOutcome> WriteAsync(
        Slot slot, ArcBuffer payload, Slot contiguousTail, bool recovery, CancellationToken cancellationToken = default) =>
        RecordAsync(
            new ReplicaCall(client, replica, recovery ? ReplicaCallKind.RecoveryWrite : ReplicaCallKind.Write, slot.Value, contiguousTail.Value),
            () => inner.WriteAsync(slot, payload, contiguousTail, recovery, cancellationToken),
            static result => new ReplicaAnswer(result.Case.ToString()));

    public ValueTask<ReplicaRead> ReadAsync(Slot slot, Slot contiguousTail, CancellationToken cancellationToken = default) =>
        RecordAsync(
            new ReplicaCall(client, replica, ReplicaCallKind.Read, slot.Value, contiguousTail.Value),
            () => inner.ReadAsync(slot, contiguousTail, cancellationToken),
            static result => new ReplicaAnswer(result.Case.ToString()));

    public ValueTask<ReplicaRead> ReadAsync(
        Slot slot, int offset, int length, Slot contiguousTail, CancellationToken cancellationToken = default) =>
        RecordAsync(
            new ReplicaCall(client, replica, ReplicaCallKind.Read, slot.Value, contiguousTail.Value, offset, length),
            () => inner.ReadAsync(slot, offset, length, contiguousTail, cancellationToken),
            static result => new ReplicaAnswer(result.Case.ToString()));

    public ValueTask<ReplicaTailRead> ReadTailAsync(Slot contiguousTail, bool seal, CancellationToken cancellationToken = default) =>
        RecordAsync(
            new ReplicaCall(client, replica, seal ? ReplicaCallKind.Seal : ReplicaCallKind.ReadTail, Slot: null, contiguousTail.Value),
            () => inner.ReadTailAsync(contiguousTail, seal, cancellationToken),
            static answer => new ReplicaAnswer(answer.Name, answer.TryGetValue(out LedgerReplicaTail tail) ? tail : null));

    public async ValueTask DisposeAsync() =>
        _ = await RecordAsync(
            new ReplicaCall(client, replica, ReplicaCallKind.Dispose, Slot: null, CarriedTail: -1),
            async () =>
            {
                await inner.DisposeAsync();
                return true;
            },
            static _ => new ReplicaAnswer("Disposed"));

    private static bool IsDefinite(Exception failure) =>
        failure is ArgumentException
            or NotSupportedException
            or LedgerReplicaCorruptException
            or LedgerStoreClusterMismatchException;

    private async ValueTask<T> RecordAsync<T>(ReplicaCall call, Func<ValueTask<T>> invoke, Func<T, ReplicaAnswer> answer)
    {
        var invocation = recording.Invoke(call);
        T result;
        try
        {
            result = await invoke();
        }
        catch (Exception failure)
        {
            var failed = new ReplicaAnswer(failure.GetType().Name);
            if (IsDefinite(failure))
            {
                recording.Complete(invocation, failed);
            }
            else
            {
                recording.CompleteUnbounded(invocation, failed);
            }

            throw;
        }

        recording.Complete(invocation, answer(result));
        return result;
    }
}
