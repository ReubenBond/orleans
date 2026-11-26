using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage;

/// <summary>Why a replica host refused a call before its store, with no effect (orleans.md §5.12).</summary>
internal enum ReplicaRefusal : byte
{
    /// <summary>An argument the host or its store rejected; the caller reports <see cref="ArgumentException"/>.</summary>
    Argument = 1,

    /// <summary>An argument out of its range; the caller reports <see cref="ArgumentOutOfRangeException"/>.</summary>
    ArgumentOutOfRange = 2,

    /// <summary>A protocol version the callee does not speak; the caller reports <see cref="NotSupportedException"/>.</summary>
    UnsupportedVersion = 3,

    /// <summary>The callee's node stops and admits no new call; the caller reports the node unavailable.</summary>
    Stopping = 4,
}

/// <summary>
/// A replica host's definite refusal of a call before its store, with no effect (orleans.md §5.12, OR-I15, OR-I16): the
/// refusal's kind and its message, which the caller reports as the kind's exception.
/// </summary>
/// <param name="Kind">The refusal's kind.</param>
/// <param name="Message">The refusal's message.</param>
internal sealed record ReplicaCallRefused(ReplicaRefusal Kind, string Message);

/// <summary>
/// The callee's store found the slot's value failing its integrity check (LR-I2), a definite answer the caller reports as
/// <see cref="LedgerReplicaCorruptException"/> with the same slot and message.
/// </summary>
/// <param name="Slot">The slot, when the store named it.</param>
/// <param name="Message">The store's message.</param>
internal sealed record ReplicaCorrupt(long? Slot, string Message);

/// <summary>
/// The answer of <see cref="ILedgerStoreGrain.WriteAsync"/> (orleans.md §5.12, conventions.md §15): the store's
/// <see cref="ReplicaWriteOutcome"/> cases, <see cref="NotThisNode"/>, the host's refusal, or the store's corruption.
/// </summary>
internal readonly union RemoteReplicaWriteOutcome(
    ReplicaWriteOutcome.Written,
    ReplicaWriteOutcome.Sealed,
    ReplicaWriteOutcome.Dropped,
    ReplicaWriteOutcome.Backpressure,
    NotThisNode,
    ReplicaCallRefused,
    ReplicaCorrupt) : IEquatable<RemoteReplicaWriteOutcome>
{
    /// <inheritdoc/>
    public bool Equals(RemoteReplicaWriteOutcome other) => Equals(Value, other.Value);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is RemoteReplicaWriteOutcome other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => Value?.GetHashCode() ?? 0;

    /// <inheritdoc/>
    public override string ToString() => Value?.ToString() ?? string.Empty;
}

/// <summary>
/// The answer of <see cref="ILedgerStoreGrain.ReadAsync"/> (orleans.md §5.12, conventions.md §15): the slot's DATA in an
/// Orleans buffer; <see cref="TooLarge"/>, when the batch is longer than the reply the read reserved;
/// <see cref="ReplicaRead.OutsideBatch"/>, when a byte-range read's range extends past the batch; <see cref="ReplicaRead.Missing"/>, <see cref="ReplicaWriteOutcome.Dropped"/>, <see cref="ReplicaWriteOutcome.Backpressure"/>,
/// <see cref="NotThisNode"/>, the host's refusal, or the store's corruption.
/// </summary>
internal readonly union RemoteReplicaRead(
    RemoteReplicaRead.Data,
    RemoteReplicaRead.TooLarge,
    ReplicaRead.OutsideBatch,
    ReplicaRead.Missing,
    ReplicaWriteOutcome.Dropped,
    ReplicaWriteOutcome.Backpressure,
    NotThisNode,
    ReplicaCallRefused,
    ReplicaCorrupt) : IEquatable<RemoteReplicaRead>
{
    /// <inheritdoc/>
    public bool Equals(RemoteReplicaRead other) => Equals(Value, other.Value);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is RemoteReplicaRead other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => Value?.GetHashCode() ?? 0;

    /// <inheritdoc/>
    public override string ToString() => Value?.ToString() ?? string.Empty;

    /// <summary>
    /// The slot's DATA, or the range of it a ranged read asked for, in a zero-copy consume-on-serialize
    /// <see cref="WireArcBuffer"/> (OR-I18). At the callee it takes ownership of the store's pooled buffer directly: across
    /// silos <see cref="WireArcBufferCodec"/> writes it to the socket and immediately releases the pages; on a call within
    /// one silo <see cref="LedgerReplicaCallResults.Read"/> takes ownership of the buffer directly with zero copy. Two are
    /// equal when their bytes are, which tests compare. At the callee it holds the read's host admission, which Orleans'
    /// first conversion of the reply to its surrogate releases (OR-I17); it is no part of the answer.
    /// </summary>
    public sealed record Data(WireArcBuffer Bytes)
    {
        /// <summary>Gets the host admission the reply holds until it is first converted or its deadline passes; <see langword="null"/> at the caller.</summary>
        internal LedgerReplicaHostAdmission? Admission { get; init; }

        /// <inheritdoc/>
        public bool Equals(Data? other) => other is not null && (Bytes ?? WireArcBuffer.Empty).ToArray().AsSpan().SequenceEqual((other.Bytes ?? WireArcBuffer.Empty).ToArray());

        /// <inheritdoc/>
        public override int GetHashCode() => Bytes?.Length ?? 0;

        /// <inheritdoc/>
        public override string ToString() => $"Data[{Bytes?.Length ?? 0} bytes]";
    }

    /// <summary>
    /// The slot's batch is <paramref name="Length"/> bytes, longer than the reply the read reserved, so the host returned
    /// none of it; the caller reads again, reserving exactly that (OR-I17).
    /// </summary>
    /// <param name="Length">The batch's length.</param>
    public sealed record TooLarge(int Length);
}

/// <summary>
/// The answer of <see cref="ILedgerStoreGrain.ReadRangeAsync"/> (orleans.md §5.12, conventions.md §15): a packed
/// <see cref="Batch"/> of consecutive slots (or empty when the first slot is missing), <see cref="TooLarge"/> when the
/// first slot alone exceeded the caller's <c>MaxBytes</c>, <see cref="ReplicaWriteOutcome.Dropped"/>,
/// <see cref="ReplicaWriteOutcome.Backpressure"/>, <see cref="NotThisNode"/>, the host's refusal, or the store's
/// corruption.
/// </summary>
internal readonly union RemoteReplicaRangeRead(
    RemoteReplicaRangeRead.Batch,
    RemoteReplicaRangeRead.TooLarge,
    ReplicaWriteOutcome.Dropped,
    ReplicaWriteOutcome.Backpressure,
    NotThisNode,
    ReplicaCallRefused,
    ReplicaCorrupt) : IEquatable<RemoteReplicaRangeRead>
{
    /// <inheritdoc/>
    public bool Equals(RemoteReplicaRangeRead other) => Equals(Value, other.Value);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is RemoteReplicaRangeRead other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => Value?.GetHashCode() ?? 0;

    /// <inheritdoc/>
    public override string ToString() => Value?.ToString() ?? string.Empty;

    /// <summary>A contiguous range of <paramref name="Count"/> slots starting at <paramref name="FirstSlot"/> packed in <paramref name="Bytes"/>.</summary>
    public sealed record Batch(long FirstSlot, int Count, WireArcBuffer Bytes)
    {
        /// <summary>Gets the host admission held until first conversion or deadline; <see langword="null"/> at the caller.</summary>
        internal LedgerReplicaHostAdmission? Admission { get; init; }

        /// <inheritdoc/>
        public bool Equals(Batch? other) =>
            other is not null && FirstSlot == other.FirstSlot && Count == other.Count && (Bytes ?? WireArcBuffer.Empty).ToArray().AsSpan().SequenceEqual((other.Bytes ?? WireArcBuffer.Empty).ToArray());

        /// <inheritdoc/>
        public override int GetHashCode() => HashCode.Combine(FirstSlot, Count, Bytes?.Length ?? 0);

        /// <inheritdoc/>
        public override string ToString() => $"Batch(FirstSlot={FirstSlot}, Count={Count}, Bytes={Bytes?.Length ?? 0})";
    }

    /// <summary>The first requested slot's single batch of <paramref name="Length"/> bytes exceeded the range read's byte budget.</summary>
    public sealed record TooLarge(int Length);
}

/// <summary>
/// The answer of <see cref="ILedgerStoreGrain.ReadSlotsAsync"/> (orleans.md §5.12, conventions.md §15): a packed
/// <see cref="Batch"/> of the requested ascending slots present on the replica, <see cref="ReplicaWriteOutcome.Dropped"/>,
/// <see cref="ReplicaWriteOutcome.Backpressure"/>, <see cref="NotThisNode"/>, the host's refusal, or the store's
/// corruption.
/// </summary>
internal readonly union RemoteReplicaSlotsRead(
    RemoteReplicaSlotsRead.Batch,
    ReplicaWriteOutcome.Dropped,
    ReplicaWriteOutcome.Backpressure,
    NotThisNode,
    ReplicaCallRefused,
    ReplicaCorrupt) : IEquatable<RemoteReplicaSlotsRead>
{
    /// <inheritdoc/>
    public bool Equals(RemoteReplicaSlotsRead other) => Equals(Value, other.Value);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is RemoteReplicaSlotsRead other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => Value?.GetHashCode() ?? 0;

    /// <inheritdoc/>
    public override string ToString() => Value?.ToString() ?? string.Empty;

    /// <summary>A packed subset of <paramref name="Count"/> requested slots in <paramref name="Bytes"/>.</summary>
    public sealed record Batch(int Count, WireArcBuffer Bytes)
    {
        /// <summary>Gets the host admission held until first conversion or deadline; <see langword="null"/> at the caller.</summary>
        internal LedgerReplicaHostAdmission? Admission { get; init; }

        /// <inheritdoc/>
        public bool Equals(Batch? other) =>
            other is not null && Count == other.Count && (Bytes ?? WireArcBuffer.Empty).ToArray().AsSpan().SequenceEqual((other.Bytes ?? WireArcBuffer.Empty).ToArray());

        /// <inheritdoc/>
        public override int GetHashCode() => HashCode.Combine(Count, Bytes?.Length ?? 0);

        /// <inheritdoc/>
        public override string ToString() => $"SlotsBatch(Count={Count}, Bytes={Bytes?.Length ?? 0})";
    }
}

/// <summary>
/// The answer of <see cref="ILedgerStoreGrain.ReadTailAsync"/> (orleans.md §5.12, conventions.md §15): the replica's
/// contiguous tail and seal as the callee reported them, <see cref="ReplicaWriteOutcome.Dropped"/>,
/// <see cref="ReplicaWriteOutcome.Backpressure"/>, <see cref="NotThisNode"/>, the host's refusal, or the store's corruption.
/// </summary>
internal readonly union RemoteReplicaTailRead(
    RemoteReplicaTailRead.Tail,
    ReplicaWriteOutcome.Dropped,
    ReplicaWriteOutcome.Backpressure,
    NotThisNode,
    ReplicaCallRefused,
    ReplicaCorrupt) : IEquatable<RemoteReplicaTailRead>
{
    /// <inheritdoc/>
    public bool Equals(RemoteReplicaTailRead other) => Equals(Value, other.Value);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is RemoteReplicaTailRead other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => Value?.GetHashCode() ?? 0;

    /// <inheritdoc/>
    public override string ToString() => Value?.ToString() ?? string.Empty;

    /// <summary>
    /// The replica's retained contiguous tail, whether it is sealed, and its remaining RAM-only lease duration in ticks
    /// when sealed (LR-I10), as the callee reported them; the caller's reply checks accept it only at or above the
    /// contiguous tail it sent (OR-I15).
    /// </summary>
    /// <param name="ContiguousTail">The contiguous tail.</param>
    /// <param name="IsSealed">Whether the replica is sealed.</param>
    /// <param name="LeaseRemainingTicks">Remaining read-lease ticks when sealed; zero when unsealed or expired.</param>
    public sealed record Tail(long ContiguousTail, bool IsSealed, long LeaseRemainingTicks = 0);
}

/// <summary>
/// The answer of <see cref="ILedgerStoreGrain.HeartbeatAsync"/> (orleans.md §5.12, LR-I10, conventions.md §15): the
/// node's lease grant and per-ledger outcomes, <see cref="ReplicaWriteOutcome.Backpressure"/>, <see cref="NotThisNode"/>,
/// or the host's refusal.
/// </summary>
internal readonly union RemoteReplicaHeartbeatRead(
    RemoteReplicaHeartbeatRead.Reply,
    ReplicaWriteOutcome.Backpressure,
    NotThisNode,
    ReplicaCallRefused) : IEquatable<RemoteReplicaHeartbeatRead>
{
    /// <inheritdoc/>
    public bool Equals(RemoteReplicaHeartbeatRead other) => Equals(Value, other.Value);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is RemoteReplicaHeartbeatRead other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => Value?.GetHashCode() ?? 0;

    /// <inheritdoc/>
    public override string ToString() => Value?.ToString() ?? string.Empty;

    /// <summary>
    /// The node's batched heartbeat reply: its configured lease grant duration in ticks and one
    /// <see cref="ReplicaHeartbeatStatus"/> byte per requested ledger, in request order.
    /// </summary>
    /// <param name="LeaseGrantTicks">The node's lease grant duration in ticks (zero when leases are disabled).</param>
    /// <param name="Outcomes">One status byte per requested ledger, matching <see cref="ReplicaHeartbeatStatus"/>.</param>
    public sealed record Reply(long LeaseGrantTicks, byte[] Outcomes)
    {
        /// <inheritdoc/>
        public bool Equals(Reply? other) =>
            other is not null && LeaseGrantTicks == other.LeaseGrantTicks && Outcomes.AsSpan().SequenceEqual(other.Outcomes);

        /// <inheritdoc/>
        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(LeaseGrantTicks);
            foreach (var status in Outcomes)
            {
                hash.Add(status);
            }

            return hash.ToHashCode();
        }

        /// <inheritdoc/>
        public override string ToString() => $"Reply(LeaseGrantTicks = {LeaseGrantTicks}, Outcomes = [{Outcomes.Length}])";
    }
}
