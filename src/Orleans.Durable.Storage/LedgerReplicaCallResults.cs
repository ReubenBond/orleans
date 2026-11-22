using System.Diagnostics;
using System.Globalization;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Serialization;

namespace Orleans.Durable.Storage;

/// <summary>
/// How a replica call's result reaches its caller (orleans.md §5.12's result table, OR-I15, OR-I16), classified by where
/// it comes from: the callee's answers, after the caller's reply checks, each as the table maps it; a reply the caller
/// could not decode, or which fails a reply check, as <see cref="InvalidDataException"/>; the caller's own cancellation as
/// itself; and every other failure of the call as an unknown outcome, <see cref="LedgerReplicaUnavailableException"/>.
/// </summary>
internal static class LedgerReplicaCallResults
{
    /// <summary>
    /// Returns the write's answer: the store's outcome; a refusal or corruption as its exception; <c>NotThisNode</c> as
    /// <see cref="LedgerReplicaUnavailableException"/>; and a reply which fails its check, a recovery write answered
    /// <c>Sealed</c> or no case, as <see cref="InvalidDataException"/>.
    /// </summary>
    public static ReplicaWriteOutcome Write(NodeId node, RemoteReplicaWriteOutcome answer, bool recovery) => answer switch
    {
        ReplicaWriteOutcome.Written written => written,
        ReplicaWriteOutcome.Sealed when recovery => throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture, $"Node {node} answered a recovery write Sealed, which a recovery write never answers.")),
        ReplicaWriteOutcome.Sealed @sealed => @sealed,
        ReplicaWriteOutcome.Dropped dropped => dropped,
        ReplicaWriteOutcome.Backpressure backpressure => backpressure,
        NotThisNode => throw NotThisNodeAnswered(node),
        ReplicaCallRefused refused => throw Refusal(node, refused),
        ReplicaCorrupt corrupt => throw Corruption(corrupt),
        null => throw NoCase(node),
    };

    /// <summary>
    /// Returns the read's answer, its DATA copied into a buffer from Durable Storage's page pool (OR-I18): the store's answer; a
    /// refusal or corruption as its exception; <c>NotThisNode</c> as <see cref="LedgerReplicaUnavailableException"/>; and
    /// a reply which fails its check, empty DATA, DATA longer than the <paramref name="reserved"/> reply, a ranged read's
    /// DATA not of its length, <c>TooLarge</c> for a byte-range read or naming a length the read reserved, <c>OutsideBatch</c> for a
    /// whole read, or no case, as
    /// <see cref="InvalidDataException"/>. It releases the DATA's received bytes on every path.
    /// </summary>
    /// <param name="node">The callee's node.</param>
    /// <param name="answer">The callee's answer.</param>
    /// <param name="reserved">The reply the read reserved.</param>
    /// <param name="ranged">Whether the read is ranged, so its DATA is exactly <paramref name="reserved"/> bytes.</param>
    public static ReplicaRead Read(NodeId node, RemoteReplicaRead answer, int reserved, bool ranged) => answer switch
    {
        RemoteReplicaRead.Data data => Received(node, data.Bytes, reserved, ranged),
        RemoteReplicaRead.TooLarge tooLarge =>
            throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture, $"Node {node} answered a {(ranged ? "ranged " : string.Empty)}read which reserved {reserved} bytes TooLarge with a batch of {tooLarge.Length}.")),
        ReplicaRead.OutsideBatch outside when ranged => outside,
        ReplicaRead.OutsideBatch outside =>
            throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture, $"Node {node} answered a whole read with a range outside its {outside.Length}-byte batch.")),
        ReplicaRead.Missing missing => missing,
        ReplicaWriteOutcome.Dropped dropped => dropped,
        ReplicaWriteOutcome.Backpressure backpressure => backpressure,
        NotThisNode => throw NotThisNodeAnswered(node),
        ReplicaCallRefused refused => throw Refusal(node, refused),
        ReplicaCorrupt corrupt => throw Corruption(corrupt),
        null => throw NoCase(node),
    };

    /// <summary>
    /// Returns the contiguous-tail read's answer: the store's answer; a refusal or corruption as its exception;
    /// <c>NotThisNode</c> as <see cref="LedgerReplicaUnavailableException"/>; and a reply which fails its check, a
    /// contiguous tail below <paramref name="sent"/>, a negative remaining lease, a non-zero lease on an unsealed tail, or
    /// no case, as <see cref="InvalidDataException"/>.
    /// </summary>
    public static ReplicaTailRead Tail(NodeId node, RemoteReplicaTailRead answer, Slot sent) => answer switch
    {
        RemoteReplicaTailRead.Tail tail when tail.ContiguousTail < sent.Value =>
            throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture, $"Node {node} answered a contiguous tail of {tail.ContiguousTail}, below the {sent.Value} the call carried.")),
        RemoteReplicaTailRead.Tail tail when tail.LeaseRemainingTicks < 0 =>
            throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture, $"Node {node} answered a tail with negative lease remaining ticks {tail.LeaseRemainingTicks}.")),
        RemoteReplicaTailRead.Tail tail when !tail.IsSealed && tail.LeaseRemainingTicks != 0 =>
            throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture, $"Node {node} answered an unsealed tail with non-zero lease remaining ticks {tail.LeaseRemainingTicks}.")),
        RemoteReplicaTailRead.Tail tail => new ReplicaTailRead(new LedgerReplicaTail(new Slot(tail.ContiguousTail), tail.IsSealed, TimeSpan.FromTicks(tail.LeaseRemainingTicks))),
        ReplicaWriteOutcome.Dropped dropped => new ReplicaTailRead(dropped),
        ReplicaWriteOutcome.Backpressure backpressure => new ReplicaTailRead(backpressure),
        NotThisNode => throw NotThisNodeAnswered(node),
        ReplicaCallRefused refused => throw Refusal(node, refused),
        ReplicaCorrupt corrupt => throw Corruption(corrupt),
        null => throw NoCase(node),
    };

    /// <summary>
    /// Returns the batched heartbeat's answer: the store's reply; a refusal as its exception; <c>NotThisNode</c> as
    /// <see cref="LedgerReplicaUnavailableException"/>; and a reply which fails its check (negative lease grant, mismatched
    /// outcome count, unknown status byte, or no case) as <see cref="InvalidDataException"/>.
    /// </summary>
    public static ReplicaHeartbeatRead Heartbeat(NodeId node, RemoteReplicaHeartbeatRead answer, int expectedCount) => answer switch
    {
        RemoteReplicaHeartbeatRead.Reply reply => ConvertHeartbeatReply(node, reply, expectedCount),
        ReplicaWriteOutcome.Backpressure backpressure => new ReplicaHeartbeatRead(backpressure),
        NotThisNode => throw NotThisNodeAnswered(node),
        ReplicaCallRefused refused => throw Refusal(node, refused),
        null => throw NoCase(node),
    };

    private static ReplicaHeartbeatRead ConvertHeartbeatReply(NodeId node, RemoteReplicaHeartbeatRead.Reply reply, int expectedCount)
    {
        if (reply.LeaseGrantTicks < 0)
        {
            throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture, $"Node {node} answered a heartbeat with negative lease grant ticks {reply.LeaseGrantTicks}."));
        }

        if (reply.Outcomes is null || reply.Outcomes.Length != expectedCount)
        {
            throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture, $"Node {node} answered a heartbeat for {expectedCount} ledgers with {reply.Outcomes?.Length ?? 0} outcomes."));
        }

        var statuses = new ReplicaHeartbeatStatus[expectedCount];
        for (var i = 0; i < expectedCount; i++)
        {
            var status = (ReplicaHeartbeatStatus)reply.Outcomes[i];
            if (!Enum.IsDefined(status))
            {
                throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture, $"Node {node} answered a heartbeat with unknown status {reply.Outcomes[i]}."));
            }

            statuses[i] = status;
        }

        return new ReplicaHeartbeatRead(new ReplicaHeartbeatReply(TimeSpan.FromTicks(reply.LeaseGrantTicks), statuses));
    }

    /// <summary>
    /// Returns the failure the caller reports for a call to <paramref name="node"/> which failed with
    /// <paramref name="failure"/>: the caller's own cancellation as itself; a reply the caller could not decode, its own
    /// surrogate's refusal or Orleans' codec's (such as a version-1 reply's null DATA field), however Orleans wraps it, as
    /// <see cref="InvalidDataException"/>; and every other failure, the placement's refusal, an
    /// Orleans timeout, and any exception the callee let escape among them, as an unknown outcome,
    /// <see cref="LedgerReplicaUnavailableException"/>, wrapping it.
    /// </summary>
    public static Exception Failure(NodeId node, Exception failure, CancellationToken cancellationToken)
    {
        if (failure is OperationCanceledException canceled && cancellationToken.IsCancellationRequested)
        {
            return canceled.CancellationToken == cancellationToken ? canceled : new OperationCanceledException(canceled.Message, canceled, cancellationToken);
        }

        if (Decoding(failure) is { } decoding)
        {
            return new InvalidDataException(string.Create(CultureInfo.InvariantCulture, $"Node {node}'s reply failed to decode: {decoding.Message}"), decoding);
        }

        return failure is NodePlacementRefusedException
            ? new LedgerReplicaUnavailableException(node, "the placement found no registered active silo for the node.", failure)
            : new LedgerReplicaUnavailableException(node, failure.Message, failure);
    }

    /// <summary>
    /// Checks a read's DATA against the reply the read reserved and moves it into Durable Storage's page pool; releases
    /// <paramref name="received"/>, which the caller owns, on every path.
    /// </summary>
    private static ReplicaRead Received(NodeId node, OrleansBuffer received, int reserved, bool ranged)
    {
        if (DataRejection(node, received.Length, reserved, ranged) is { } rejection)
        {
            received.Dispose();
            throw rejection;
        }

        return new ReplicaRead(WireBuffers.MoveToArcBuffer(ref received));
    }

    /// <summary>Returns the reply check a read's DATA of <paramref name="length"/> bytes fails, or null when it passes.</summary>
    private static InvalidDataException? DataRejection(NodeId node, int length, int reserved, bool ranged) => length switch
    {
        0 => new(string.Create(CultureInfo.InvariantCulture, $"Node {node} answered a read with empty DATA.")),
        _ when length > reserved => new(string.Create(CultureInfo.InvariantCulture, $"Node {node} answered a read with {length} bytes of DATA, more than the {reserved} it reserved.")),
        _ when ranged && length != reserved => new(string.Create(CultureInfo.InvariantCulture, $"Node {node} answered a ranged read of {reserved} bytes with {length}.")),
        _ => null,
    };
    /// <summary>Returns the exception the caller reports for the callee's definite refusal, which had no effect.</summary>
    public static Exception Refusal(NodeId node, ReplicaCallRefused refused) => refused.Kind switch
    {
        ReplicaRefusal.Argument => new ArgumentException(refused.Message),
        ReplicaRefusal.ArgumentOutOfRange => new ArgumentOutOfRangeException(paramName: null, refused.Message),
        ReplicaRefusal.UnsupportedVersion => new NotSupportedException(refused.Message),
        ReplicaRefusal.Stopping => new LedgerReplicaUnavailableException(node, refused.Message),
        _ => throw new UnreachableException("A refusal's kind is checked when its reply is decoded."),
    };

    private static LedgerReplicaCorruptException Corruption(ReplicaCorrupt corrupt) =>
        corrupt.Slot is { } slot && slot >= 0 ? new LedgerReplicaCorruptException(new Slot(slot), corrupt.Message) : new LedgerReplicaCorruptException(corrupt.Message);

    /// <summary>Returns the decoding failure among <paramref name="failure"/> and the exceptions it wraps, if any.</summary>
    private static Exception? Decoding(Exception failure)
    {
        for (Exception? next = failure; next is not null; next = next.InnerException)
        {
            if (next is ReplicaReplyDecodingException or SerializerException)
            {
                return next;
            }

            if (next is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    if (Decoding(inner) is { } found)
                    {
                        return found;
                    }
                }

                return null;
            }
        }

        return null;
    }

    private static LedgerReplicaUnavailableException NotThisNodeAnswered(NodeId node) =>
        new(node, "the activation runs beside another node's store (NotThisNode), and had no effect.");

    private static InvalidDataException NoCase(NodeId node) =>
        new(string.Create(CultureInfo.InvariantCulture, $"Node {node} answered with no case."));
}
