using System.Globalization;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Threading;
using Orleans.Runtime;

namespace Orleans.Durable.Storage.Tests;

/// <summary>
/// The rows of orleans.md §5.12's result table which a callee's answer produces, or which arise at the caller around any
/// callee, each as a call and what the caller reports. The grain double and an <c>Orleans.TestingHost</c> cluster run the
/// same rows, the double through its answer and the cluster through its callee filter, and must report alike.
/// </summary>
internal static class ReplicaResultTable
{
    private const string Damaged = "A damaged value.";

    /// <summary>
    /// Returns the rows a callee's answer produces, a value of the call's answer union or an exception it throws, where a
    /// whole read reserves <paramref name="readReply"/> bytes.
    /// </summary>
    public static IReadOnlyList<Row> AnswerRows(int readReply) =>
    [
        new("store outcome: Written", _ => ReplicaWriteOutcome.Written.Instance, Write),
        new("store outcome: Sealed", _ => ReplicaWriteOutcome.Sealed.Instance, Write),
        new("store outcome: Missing", _ => ReplicaRead.Missing.Instance, Read),
        new("store outcome: a batch", _ => WireBytes.Data(1, 2, 3), Read),
        new("store outcome: a byte-range read's range past its batch", _ => new ReplicaRead.OutsideBatch(2), Ranged),
        new("store outcome: Dropped", _ => ReplicaWriteOutcome.Dropped.Instance, Write),
        new("store outcome: a contiguous tail and its sealed flag", _ => new RemoteReplicaTailRead.Tail(3, IsSealed: true), (context, token) => Tail(context, token)),
        new("store outcome: a sealing tail read's tail", _ => new RemoteReplicaTailRead.Tail(0, IsSealed: true), (context, token) => Tail(context, token, seal: true)),
        new(
            "store outcome: a batch longer than the reply reserved, TooLarge, then read again reserving its length",
            request => ((LedgerReplicaReadRequest)request).MaxReplyBytes is var reserved && reserved == readReply ? new RemoteReplicaRead.TooLarge(reserved + 10) : WireBytes.Data(new byte[reserved]),
            ReadPastTheReservedReply),
        new("the store's LedgerReplicaCorruptException", _ => new ReplicaCorrupt(4, Damaged), Read),
        new("a refusal: ArgumentException, from the host or its store", _ => new ReplicaCallRefused(ReplicaRefusal.Argument, "An empty batch."), Write),
        new("a refusal: ArgumentOutOfRangeException, from the host or its store", _ => new ReplicaCallRefused(ReplicaRefusal.ArgumentOutOfRange, "The slot is out of range."), Write),
        new("a refusal: an unknown protocol version", _ => new ReplicaCallRefused(ReplicaRefusal.UnsupportedVersion, "Version 2."), Write),
        new("a refusal: the callee's node stops", _ => new ReplicaCallRefused(ReplicaRefusal.Stopping, "Stopping."), Write),
        new("NotThisNode", _ => NotThisNode.Instance, Write),
        new("no registered Active silo: the director refuses", _ => new NodePlacementRefusedException(NodeRegistrySamples.Node(1), registered: null), Write),
        new("Orleans rejects the call as overloaded", _ => new LimitExceededException("Overloaded."), Write),
        new("a reply which fails a check: a recovery write answered Sealed", _ => ReplicaWriteOutcome.Sealed.Instance, (context, token) => Write(context, token, recovery: true)),
        new("a reply which fails a check: empty DATA", _ => WireBytes.Data(), Read),
        new("a reply which fails a check: DATA longer than the reply reserved", static request => WireBytes.Data(new byte[((LedgerReplicaReadRequest)request).MaxReplyBytes + 1]), Read),
        new("a reply which fails a check: a byte-range read's DATA of another length", _ => WireBytes.Data(1, 2, 3), Ranged),
        new("a reply which fails a check: TooLarge naming a length the read reserved", static request => new RemoteReplicaRead.TooLarge(((LedgerReplicaReadRequest)request).MaxReplyBytes), Read),
        new("a reply which fails a check: TooLarge for a byte-range read", _ => new RemoteReplicaRead.TooLarge(40), Ranged),
        new("a reply which fails a check: OutsideBatch for a whole read", _ => new ReplicaRead.OutsideBatch(2), Read),
        new("a reply which fails a check: a contiguous tail below the one sent", _ => new RemoteReplicaTailRead.Tail(1, IsSealed: false), (context, token) => Tail(context, token, sent: 2)),
        new("a reply which fails to decode", _ => new ReplicaReplyDecodingException("TooLarge of no length."), Read) { WireAnswer = _ => new RemoteReplicaRead.TooLarge(0) },
        new("IOException", _ => new IOException("Disk."), Write),
        new("UnauthorizedAccessException", _ => new UnauthorizedAccessException("Denied."), Write),
        new("the callee's ObjectDisposedException while it stops", _ => new ObjectDisposedException("store"), Write),
        new("the callee's OperationCanceledException while it stops", _ => new OperationCanceledException("Stopping."), Write),
        new("an ArgumentException the callee let escape", _ => new ArgumentException("Escaped."), Write),
        new("an InvalidDataException the callee let escape", _ => new InvalidDataException("Escaped."), Write),
        new("Orleans' TimeoutException", _ => new TimeoutException("No response."), Write),
        new("SiloUnavailableException", _ => new SiloUnavailableException("Dead."), Write),
        new("any other callee or Orleans exception", _ => new InvalidOperationException("Other."), Write),
    ];

    /// <summary>
    /// Reports the caller's cancellation of a call waiting in its node's queue behind <paramref name="lane"/>'s whole
    /// budget, which it holds meanwhile: the call is never sent.
    /// </summary>
    public static async Task<RowReport> CancelledWhileQueuedAsync(Context context, CallBudget lane)
    {
        using var cancel = new CancellationTokenSource();
        using (await lane.EnterAsync(lane.MaxBytes, lane.DeadlineAfter(TimeSpan.FromMinutes(1)), TestContext.Current.CancellationToken))
        {
            var call = Write(context, cancel.Token);
            await cancel.CancelAsync();
            return new("the caller's cancellation, while the call waits in its node's queue", await ReportAsync(() => call, cancel.Token));
        }
    }

    /// <summary>Reports an operation on a handle the caller disposed.</summary>
    public static async Task<RowReport> DisposedAsync(Context context)
    {
        await context.Replica.DisposeAsync();
        return new("an operation after the caller's replica is disposed", await ReportAsync(() => Write(context, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken));
    }

    /// <summary>Runs <paramref name="row"/>'s call and reports what reached the caller.</summary>
    public static async Task<RowReport> RunAsync(Row row, Context context) =>
        new(row.Name, await ReportAsync(() => row.Call(context, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken));

    /// <summary>Reports what a call returned or threw, naming the failure and what it wraps, and whether a cancellation carries the caller's token.</summary>
    public static async Task<string> ReportAsync(Func<Task<object?>> call, CancellationToken token)
    {
        try
        {
            return Describe(await call().WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken));
        }
        catch (OperationCanceledException canceled) when (token.IsCancellationRequested)
        {
            return canceled.CancellationToken == token ? "OperationCanceledException carrying the caller's token" : "OperationCanceledException without the caller's token";
        }
        catch (Exception failure)
        {
            var name = failure is OperationCanceledException ? nameof(OperationCanceledException) : failure.GetType().Name;
            return failure.InnerException is { } inner && failure is LedgerReplicaUnavailableException or InvalidDataException
                ? $"{name} wrapping {(inner is OperationCanceledException ? nameof(OperationCanceledException) : inner.GetType().Name)}"
                : failure is LedgerReplicaCorruptException corrupt ? $"{name} at slot {corrupt.Slot?.Value.ToString(CultureInfo.InvariantCulture) ?? "none"}: {corrupt.Message}" : name;
        }
    }

    public static Task<object?> Write(Context context, CancellationToken token) => Write(context, token, recovery: false);

    public static async Task<object?> Write(Context context, CancellationToken token, bool recovery, int length = 4)
    {
        using var payload = TransportScenario.Payload(length);
        return await context.Replica.WriteAsync(new Slot(0), payload, Slot.Zero, recovery, token);
    }

    private static string Describe(object? answer) => answer switch
    {
        ReplicaWriteOutcome outcome => outcome.Value?.GetType().Name,
        ReplicaRead read when read.TryGetValue(out ArcBuffer batch) => DescribeBatch(batch),
        ReplicaRead read => read.Value?.GetType().Name,
        ReplicaTailRead tail => tail.Value?.ToString(),
        string described => described,
        _ => answer?.ToString(),
    } ?? "no case";

    private static string DescribeBatch(ArcBuffer batch)
    {
        using (batch)
        {
            return "a batch of " + Convert.ToHexStringLower(batch.ToArray());
        }
    }


    private static async Task<object?> Read(Context context, CancellationToken token) => await context.Replica.ReadAsync(new Slot(0), Slot.Zero, token);

    private static async Task<object?> Ranged(Context context, CancellationToken token) => await context.Replica.ReadAsync(new Slot(0), 1, 2, Slot.Zero, token);

    private static async Task<object?> Tail(Context context, CancellationToken token, long sent = 0, bool seal = false) =>
        await context.Replica.ReadTailAsync(new Slot(sent), seal, token);

    private static async Task<object?> ReadPastTheReservedReply(Context context, CancellationToken token)
    {
        var read = await context.Replica.ReadAsync(new Slot(0), Slot.Zero, token);
        if (!read.TryGetValue(out ArcBuffer batch))
        {
            return read;
        }

        using (batch)
        {
            return batch.Length == context.ReadReply + 10 ? "a batch of the length TooLarge named" : $"a batch of {batch.Length} bytes";
        }
    }


    /// <summary>A replica handle on the callee's node and the reply a whole read reserves.</summary>
    public sealed record Context(ILedgerReplica Replica, int ReadReply);

    /// <summary>
    /// A row: its name, the callee's answer to the call's request, the answer a callee across the wire gives in its place
    /// when the two differ, and the call.
    /// </summary>
    public sealed record Row(string Name, Func<object, object> Answer, Func<Context, CancellationToken, Task<object?>> Call)
    {
        /// <summary>Gets the answer a callee across the wire gives, where serialization makes the row: by default <see cref="Answer"/>.</summary>
        public Func<object, object>? WireAnswer { get; init; }
    }

    /// <summary>A row and what the caller reported.</summary>
    public sealed record RowReport(string Row, string Reported);
}
