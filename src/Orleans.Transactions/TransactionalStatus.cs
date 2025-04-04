
using System;

namespace Orleans.Transactions;

/// <summary>
/// Used to propagate information about the status of a transaction. Used for transaction orchestration, for diagnostics, 
/// and for generating informative user exceptions
/// </summary>
public enum TransactionalStatus
{
    Ok,

    PrepareTimeout,    // TM could not finish prepare in time
    CascadingAbort,    // a transaction this transaction depends on aborted
    BrokenLock,        // a lock was lost due to timeout, wait-die, or failures
    LockValidationFailed,  // during prepare, recorded accesses did not match
    ParticipantResponseTimeout, // TA timed out waiting for response from participants of read-only transaction
    TMResponseTimeout,  // TA timed out waiting for response from TM

    StorageConflict,   // storage was modified by duplicate grain activation

    PresumedAbort,     // TM never heard of this transaction

    UnknownException,  // an unknown exception was caught
    AssertionFailed,   // an internal assertion was violated
    CommitFailure,     // Unable to commit transaction
}

public static class TransactionalStatusExtensions
{
    public static bool DefinitelyAborted(this TransactionalStatus status)
    {
        return status switch
        {
            TransactionalStatus.PrepareTimeout or TransactionalStatus.CascadingAbort or TransactionalStatus.BrokenLock or TransactionalStatus.LockValidationFailed or TransactionalStatus.ParticipantResponseTimeout or TransactionalStatus.CommitFailure => true,
            _ => false,
        };
    }

    public static OrleansTransactionException ConvertToUserException(this TransactionalStatus status, string transactionId, Exception exception)
    {
        return status switch
        {
            TransactionalStatus.PrepareTimeout => new OrleansTransactionPrepareTimeoutException(transactionId, exception),
            TransactionalStatus.CascadingAbort => new OrleansCascadingAbortException(transactionId, exception),
            TransactionalStatus.BrokenLock => new OrleansBrokenTransactionLockException(transactionId, "before prepare", exception),
            TransactionalStatus.LockValidationFailed => new OrleansBrokenTransactionLockException(transactionId, "when validating accesses during prepare", exception),
            TransactionalStatus.ParticipantResponseTimeout => new OrleansTransactionTransientFailureException(transactionId, $"transaction agent timed out waiting for read-only transaction participant responses ({status})", exception),
            TransactionalStatus.TMResponseTimeout => new OrleansTransactionInDoubtException(transactionId, $"transaction agent timed out waiting for read-only transaction participant responses ({status})", exception),
            TransactionalStatus.CommitFailure => new OrleansTransactionAbortedException(transactionId, $"Unable to commit transaction ({status})", exception),
            _ => new OrleansTransactionInDoubtException(transactionId, $"failure during transaction commit, status={status}", exception),
        };
    }
}