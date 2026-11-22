namespace Orleans.Durable.Storage.Engine.Ledgers;

/// <summary>
/// Classifies the failure of a ledger replica operation by whether repeating the operation may change it (ledger-replicas.md §6.4).
/// </summary>
/// <remarks>
/// A recovery repeats a replica's operation, and a ledger writer its write, only while this classifier calls the
/// failure transient, so the two agree on which failures a repetition waits out (ledger-protocol.md §6.5, LP-I12).
/// </remarks>
public static class LedgerReplicaFailures
{
    /// <summary>
    /// Returns whether repeating the operation which failed with <paramref name="exception"/> may succeed.
    /// </summary>
    /// <remarks>
    /// A rejected argument, a disposed component, an unsupported request, a corrupt value, a root
    /// another cluster claims, an <see cref="InvalidDataException"/> from any source, the quorum's that a caller broke its
    /// window or replicas lost a decided address among them, running out of memory, and the caller's own cancellation do
    /// not change on repetition. Every other failure, an I/O error, a timeout, and a cancellation the caller
    /// did not request among them, leaves the outcome unknown, so a repetition may succeed. An
    /// <see cref="AggregateException"/> is transient when any of its failures is, as a quorum may form once one replica
    /// recovers.
    /// </remarks>
    /// <param name="exception">The failure.</param>
    /// <param name="stop">
    /// The repeating caller's cancellation: a cancellation once it is requested is the caller's own, which ends the
    /// repetition; one raised while it is not, such as a transport's timeout, is an unknown outcome.
    /// </param>
    /// <returns>Whether the operation may succeed when repeated.</returns>
    public static bool IsTransient(Exception exception, CancellationToken stop)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            AggregateException aggregate => aggregate.Flatten().InnerExceptions.Any(inner => IsTransient(inner, stop)),
            OperationCanceledException => !stop.IsCancellationRequested,
            ArgumentException
                or ObjectDisposedException
                or NotSupportedException
                or InvalidDataException
                or LedgerReplicaCorruptException
                or LedgerStorageCorruptException
                or LedgerStoreClusterMismatchException
                or OutOfMemoryException => false,
            _ => true,
        };
    }
}
