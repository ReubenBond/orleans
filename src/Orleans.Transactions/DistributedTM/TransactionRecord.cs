using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Orleans.Transactions;

/// <summary>
/// Each participant plays a particular role in the commit protocol
/// </summary>
internal enum CommitRole
{
    NotYetDetermined,  // role is known only when prepare message is received from TA
    ReadOnly,          // this participant has not written
    RemoteCommit,      // this participant has written, but is not the TM
    LocalCommit,       // this participant has written, and is the TM
}

/// <summary>
/// Record that is kept for each transaction at each participant
/// </summary>
/// <typeparam name="TState">The type of state</typeparam>
internal sealed class TransactionRecord<TState>
{
    // a unique identifier for this transaction
    public Guid TransactionId;

    // the time at which this transaction was started on the TA
    public DateTime Priority;

    // a deadline for the transaction to complete successfully, set by the TA
    public DateTime Deadline;

    // the transaction timestamp as computed by the algorithm
    public DateTime Timestamp;

    // the number of reads and writes that this transaction has performed on this transactional participant
    public int NumberReads;
    public int NumberWrites;

    // the state for this transaction, and the sequence number of this state
    public TState State;
    public long SequenceNumber;
    public bool HasCopiedState;

    public void AddRead() => ++NumberReads;
    public void AddWrite() => ++NumberWrites;

    public CommitRole Role;

    // used for readonly and local commit
    public TaskCompletionSource<TransactionalStatus> PromiseForTA;

    // used for local and remote commit
    public ParticipantId TransactionManager;

    // used for local commit
    public List<ParticipantId> WriteParticipants;
    public int WaitCount;
    public DateTime WaitingSince;

    // used for remote commit
    public DateTime? LastSent;
    public bool PrepareIsPersisted;
    public TaskCompletionSource<bool> ConfirmationResponsePromise;


    /// <summary>
    /// Indicates whether a transaction record is ready to commit
    /// </summary>
    public bool ReadyToCommit => Role switch
    {
        CommitRole.ReadOnly => true,
        CommitRole.LocalCommit => WaitCount == 0,// received all "Prepared" messages
        CommitRole.RemoteCommit => ConfirmationResponsePromise != null  // TM has sent confirm and is waiting for response
                                 || NumberWrites == 0 && LastSent.HasValue,// this participant did not write and finished prepare
        _ => throw new NotSupportedException($"{Role} is not a supported CommitRole."),
    };

    public bool IsReadOnly => Role switch
    {
        CommitRole.ReadOnly => true,
        CommitRole.LocalCommit => false,
        CommitRole.RemoteCommit => NumberWrites == 0,
        _ => throw new NotSupportedException($"{Role} is not a supported CommitRole."),
    };

    public bool Batchable => Role switch
    {
        CommitRole.ReadOnly or CommitRole.LocalCommit => true,
        CommitRole.RemoteCommit => NumberWrites == 0,
        _ => throw new NotImplementedException(),
    };

    // formatted for debugging commit queue contents
    public override string ToString() => Role switch
    {
        CommitRole.NotYetDetermined => $"ND tid={TransactionId} v{SequenceNumber}",
        CommitRole.ReadOnly => $"RE tid={TransactionId} v{SequenceNumber}",
        CommitRole.LocalCommit => $"LCE tid={TransactionId} v{SequenceNumber} wc={WaitCount} rtb={ReadyToCommit}",
        CommitRole.RemoteCommit => $"RCE tid={TransactionId} v{SequenceNumber} pip={PrepareIsPersisted} ls={LastSent.HasValue} ro={IsReadOnly} rtb={ReadyToCommit} tm={TransactionManager}",
        _ => throw new NotSupportedException($"{Role} is not a supported CommitRole."),
    };
}