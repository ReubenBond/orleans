using System.Diagnostics;
using Orleans.Durable.Storage.Engine.Journals;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage;

/// <summary>
/// The gate through which the silo's data plane calls its Journals (orleans.md §5.2, §5.5): the node's
/// <see cref="JournalStore"/>, which the Ledgers runtime owns, serves once the archive store's record holds the silo's
/// journal format and <c>MaxBodyBytes</c> (OR-I8), until the runtime's stop, which closes the gate before it disposes the
/// store and then the writer registry the store admits through. Its state, <see cref="JournalsState.Checking"/>,
/// <see cref="JournalsState.Serving"/>, <see cref="JournalsState.Refused"/>, and <see cref="JournalsState.Stopped"/>, only
/// advances.
/// </summary>
/// <remarks>
/// A call reaches the store only while the gate serves, through <see cref="WriteAsync"/> and <see cref="ReadAsync"/>;
/// otherwise it is answered <c>Moved</c> naming no owner at once, which has no effect. A call which reaches the store as the
/// runtime disposes it gets the store's <see cref="ObjectDisposedException"/>, which the store throws only for a call
/// without effect (journals.md §2.2), so it too is answered <c>Moved</c> naming no owner. A write to a key whose open
/// ledger the node is primary of is answered <c>Throttled</c> before the store while that ledger is throttled
/// (<see cref="NodePressure"/>, OR-I10): the router's local path and a forwarded write both come through here.
/// </remarks>
internal sealed class LocalJournals(NodePressure pressure)
{
    /// <summary>The answer of a call the node's Journals do not serve: <c>Moved</c> naming no owner, without effect.</summary>
    internal static readonly JournalMoved Stopped = new(Owner: null);

    private Served? _served;
    private int _state;

    /// <summary>The Journals' states, in the order they advance.</summary>
    public enum JournalsState
    {
        /// <summary>The runtime has not started them, or checks the archive store's record: calls get the stopped answer.</summary>
        Checking,

        /// <summary>The store takes calls.</summary>
        Serving,

        /// <summary>
        /// The archive store's record holds another journal format or <c>MaxBodyBytes</c> than the silo's, names another
        /// store, or breaks its format, or its check met a defect: the Journals never serve (OR-I8).
        /// </summary>
        Refused,

        /// <summary>The runtime stops them, or never started them.</summary>
        Stopped,
    }

    /// <summary>Gets or sets a step a call the gate admitted awaits before it reaches the store, which tests hold; none by default.</summary>
    internal Func<Task>? BeforeCall { get; set; }

    /// <summary>Gets the Journals' state.</summary>
    public JournalsState State => (JournalsState)Volatile.Read(ref _state);

    /// <summary>
    /// Serves <paramref name="store"/>, the Journals of <paramref name="node"/> over the node's map view
    /// <paramref name="view"/>, which calls reach from now on; not once refused or stopped.
    /// </summary>
    /// <returns>Whether the Journals serve: <see langword="false"/> when they were already refused or stopped.</returns>
    /// <exception cref="InvalidOperationException">The gate was already handed a store.</exception>
    public bool Serve(JournalStore store, LedgerMapView view, NodeId node)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(view);
        if (Interlocked.CompareExchange(ref _served, new Served(store, view, node), null) is not null)
        {
            throw new InvalidOperationException("The node's Journals serve one store.");
        }

        return Advance(JournalsState.Checking, JournalsState.Serving);
    }

    /// <summary>Refuses to serve the Journals ever: the check of the archive store's record failed definitely.</summary>
    public void Refuse() => _ = Advance(JournalsState.Checking, JournalsState.Refused);

    /// <summary>Stops the Journals, from any state: every later call gets its stopped answer.</summary>
    public void Stop() => Volatile.Write(ref _state, (int)JournalsState.Stopped);

    /// <summary>
    /// Writes <paramref name="record"/> through the Journals while they serve (journals.md §2.2); answers <c>Moved</c>
    /// naming no owner, without effect, otherwise, and <c>Throttled</c>, without effect, before the store while the open
    /// ledger over the record's key is one the node is primary of and is throttled (orleans.md §5.9, OR-I10); and
    /// <c>Unavailable</c>, without effect, when the store fails the write with <see cref="JournalStoreUnavailableException"/>,
    /// which it throws only for an entry it never proposed (journals.md §6). The caller keeps the record until the call
    /// completes, and disposes it.
    /// </summary>
    public async ValueTask<LocalJournalWriteOutcome> WriteAsync(JournalEntryRecord record, CancellationToken cancellationToken)
    {
        if (State != JournalsState.Serving || Volatile.Read(ref _served) is not { } served)
        {
            return Stopped;
        }

        if (served.View.FindOpen(record.Key.Span) is { } open && open.Primary.Node == served.Node && pressure.IsThrottled(open))
        {
            pressure.CountThrottledWrite(open.Id);
            return LocalJournalWriteOutcome.Throttled.Instance;
        }

        JournalWriteOutcome written;
        try
        {
            if (BeforeCall is { } before)
            {
                await before().ConfigureAwait(false);
            }

            written = await served.Store.WriteAsync(record, cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException) when (State == JournalsState.Stopped)
        {
            // The store refused the call without effect: the runtime disposed it after closing the gate.
            return Stopped;
        }
        catch (JournalStoreUnavailableException)
        {
            return LocalJournalWriteOutcome.Unavailable.Instance;
        }

        return written switch
        {
            JournalWriteOutcome.Accepted accepted => accepted,
            JournalWriteOutcome.Rejected rejected => rejected,
            JournalWriteOutcome.ReplaceRequired replaceRequired => replaceRequired,
            JournalMoved moved => moved,
            JournalWriteOutcome.Fenced fenced => fenced,
            JournalWriteOutcome.Ambiguous ambiguous => ambiguous,
            null => throw new UnreachableException(),
        };
    }

    /// <summary>
    /// Reads the journal <paramref name="key"/> through the Journals while they serve (journals.md §2.3); answers
    /// <c>Moved</c> naming no owner otherwise. The caller disposes the read.
    /// </summary>
    public Task<JournalReadOutcome> ReadAsync(JournalRowKey key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        return RunAsync(static (store, key, cancellationToken) => store.ReadAsync(key, cancellationToken), key, new JournalReadOutcome(Stopped), cancellationToken);
    }

    private async Task<T> RunAsync<TArgument, T>(Func<JournalStore, TArgument, CancellationToken, Task<T>> work, TArgument argument, T stopped, CancellationToken cancellationToken)
    {
        if (State != JournalsState.Serving || Volatile.Read(ref _served) is not { } served)
        {
            return stopped;
        }

        try
        {
            if (BeforeCall is { } before)
            {
                await before().ConfigureAwait(false);
            }

            return await work(served.Store, argument, cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException) when (State == JournalsState.Stopped)
        {
            // The store refused the call without effect: the runtime disposed it after closing the gate.
            return stopped;
        }
    }

    private bool Advance(JournalsState from, JournalsState to) => Interlocked.CompareExchange(ref _state, (int)to, (int)from) == (int)from;

    /// <summary>The store the Journals serve, the map view it serves over, and the node it serves.</summary>
    private sealed record Served(JournalStore Store, LedgerMapView View, NodeId Node);
}
