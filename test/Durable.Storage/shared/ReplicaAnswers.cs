using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Testing;

/// <summary>
/// The case a <see cref="ReplicaRead"/> holds, which tests compare and list.
/// </summary>
public enum ReplicaReadCase
{
    /// <summary>The slot holds a batch.</summary>
    Batch,

    /// <summary><see cref="ReplicaRead.Missing"/>.</summary>
    Missing,

    /// <summary><see cref="ReplicaWriteOutcome.Dropped"/>.</summary>
    Dropped,

    /// <summary><see cref="ReplicaWriteOutcome.Backpressure"/>.</summary>
    Backpressure,

    /// <summary><see cref="ReplicaRead.OutsideBatch"/>.</summary>
    OutsideBatch,
}

/// <summary>
/// The case a <see cref="ReplicaWriteOutcome"/> holds, which a theory's data names.
/// </summary>
public enum ReplicaWriteCase
{
    /// <summary><see cref="ReplicaWriteOutcome.Written"/>.</summary>
    Written,

    /// <summary><see cref="ReplicaWriteOutcome.Sealed"/>.</summary>
    Sealed,

    /// <summary><see cref="ReplicaWriteOutcome.Dropped"/>.</summary>
    Dropped,

    /// <summary><see cref="ReplicaWriteOutcome.Backpressure"/>.</summary>
    Backpressure,
}

/// <summary>
/// Reads the replica answers (ledger-replicas.md §2.1) as tests assert on them, and names each write answer as a
/// <see cref="ReplicaWriteOutcome"/>.
/// </summary>
internal static class ReplicaAnswers
{
    /// <summary>Gets <see cref="ReplicaWriteOutcome.Written"/> as a <see cref="ReplicaWriteOutcome"/>.</summary>
    public static ReplicaWriteOutcome Written => ReplicaWriteOutcome.Written.Instance;

    /// <summary>Gets <see cref="ReplicaWriteOutcome.Sealed"/> as a <see cref="ReplicaWriteOutcome"/>.</summary>
    public static ReplicaWriteOutcome Sealed => ReplicaWriteOutcome.Sealed.Instance;

    /// <summary>Gets <see cref="ReplicaWriteOutcome.Dropped"/> as a <see cref="ReplicaWriteOutcome"/>.</summary>
    public static ReplicaWriteOutcome Dropped => ReplicaWriteOutcome.Dropped.Instance;

    /// <summary>Gets <see cref="ReplicaWriteOutcome.Backpressure"/> as a <see cref="ReplicaWriteOutcome"/>.</summary>
    public static ReplicaWriteOutcome Backpressure => ReplicaWriteOutcome.Backpressure.Instance;

    /// <summary>Gets <see cref="ReplicaRead.Missing"/> as a <see cref="ReplicaRead"/>.</summary>
    public static ReplicaRead Missing => ReplicaRead.Missing.Instance;

    /// <summary>
    /// Returns the write answer a case names.
    /// </summary>
    public static ReplicaWriteOutcome ToOutcome(this ReplicaWriteCase answer) => answer switch
    {
        ReplicaWriteCase.Written => Written,
        ReplicaWriteCase.Sealed => Sealed,
        ReplicaWriteCase.Dropped => Dropped,
        ReplicaWriteCase.Backpressure => Backpressure,
        _ => throw new ArgumentOutOfRangeException(nameof(answer), answer, "Not a write answer."),
    };

    extension(ReplicaWriteOutcome answer)
    {
        /// <summary>
        /// Gets the case the answer holds.
        /// </summary>
        public ReplicaWriteCase Case => answer switch
        {
            ReplicaWriteOutcome.Written => ReplicaWriteCase.Written,
            ReplicaWriteOutcome.Sealed => ReplicaWriteCase.Sealed,
            ReplicaWriteOutcome.Dropped => ReplicaWriteCase.Dropped,
            ReplicaWriteOutcome.Backpressure => ReplicaWriteCase.Backpressure,
            null => throw new InvalidOperationException("A replica write answer holds no case."),
        };
    }

    extension(ReplicaRead read)
    {
        /// <summary>
        /// Gets the case the read holds.
        /// </summary>
        public ReplicaReadCase Case => read switch
        {
            ArcBuffer => ReplicaReadCase.Batch,
            ReplicaRead.Missing => ReplicaReadCase.Missing,
            ReplicaRead.OutsideBatch => ReplicaReadCase.OutsideBatch,
            ReplicaWriteOutcome.Dropped => ReplicaReadCase.Dropped,
            ReplicaWriteOutcome.Backpressure => ReplicaReadCase.Backpressure,
            null => throw new InvalidOperationException("A replica read holds no case."),
        };

        /// <summary>
        /// Gets the batch the read holds, which the caller owns, or throws when it holds another case.
        /// </summary>
        public ArcBuffer Batch => read.TryGetValue(out ArcBuffer batch) ? batch : throw new InvalidOperationException($"The read answered {read}, not a batch.");

        /// <summary>
        /// Releases the batch the read holds, if any.
        /// </summary>
        public void Release()
        {
            if (read.TryGetValue(out ArcBuffer batch))
            {
                batch.Dispose();
            }
        }
    }

    extension(ReplicaTailRead answer)
    {
        /// <summary>
        /// Gets the tail the answer holds, or throws when it holds <see cref="ReplicaWriteOutcome.Dropped"/> or
        /// <see cref="ReplicaWriteOutcome.Backpressure"/>.
        /// </summary>
        public LedgerReplicaTail Tail =>
            answer.TryGetValue(out LedgerReplicaTail tail) ? tail : throw new InvalidOperationException($"The tail read answered {answer}, not a tail.");

        /// <summary>
        /// Gets the case's name: <c>Tail</c>, <c>Dropped</c>, or <c>Backpressure</c>.
        /// </summary>
        public string Name => answer switch
        {
            LedgerReplicaTail => "Tail",
            ReplicaWriteOutcome.Dropped => nameof(ReplicaWriteOutcome.Dropped),
            ReplicaWriteOutcome.Backpressure => nameof(ReplicaWriteOutcome.Backpressure),
            null => throw new InvalidOperationException("A tail read answer holds no case."),
        };
    }

    extension(LedgerTailRead answer)
    {
        /// <summary>
        /// Gets the tail a <c>Ledger</c>'s seal check answered, or throws when it answered
        /// <see cref="ReplicaWriteOutcome.Backpressure"/>.
        /// </summary>
        public LedgerTail Tail =>
            answer.Value is LedgerTail tail ? tail : throw new InvalidOperationException($"The seal check answered {answer}, not a tail.");
    }

    extension(LedgerWriteOutcome answer)
    {
        /// <summary>
        /// Gets the case a <c>Ledger</c>'s write answer holds.
        /// </summary>
        public ReplicaWriteCase Case => answer switch
        {
            ReplicaWriteOutcome.Written => ReplicaWriteCase.Written,
            ReplicaWriteOutcome.Sealed => ReplicaWriteCase.Sealed,
            ReplicaWriteOutcome.Backpressure => ReplicaWriteCase.Backpressure,
            null => throw new InvalidOperationException("A ledger write answer holds no case."),
        };
    }

    extension(RecoveryOutcome outcome)
    {
        /// <summary>
        /// Gets the candidate end of a recovery which recovered, or throws when it answered
        /// <see cref="ReplicaWriteOutcome.Dropped"/>.
        /// </summary>
        public Slot End => outcome.Value is RecoveryOutcome.Recovered recovered
            ? recovered.End
            : throw new InvalidOperationException($"The recovery answered {outcome}, not an end.");
    }
}
