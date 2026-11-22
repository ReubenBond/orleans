using System.Diagnostics;
using System.Runtime.ExceptionServices;

namespace Orleans.Durable.Storage.Engine.Journals;

/// <summary>
/// The primary's expiry scan (journals.md §8.6), the only writer of <c>Expired</c> (JS-I7): it proposes, through
/// <see cref="JournalStore.ExpireAsync"/> (warm candidates) and <see cref="JournalStore.ExpireColdAsync"/> (cold
/// candidates from key indexes), an <c>Expired</c> of each deleted key whose tombstone retention has passed, in bounded
/// rounds each awaited before the next.
/// </summary>
internal static class JournalExpiryScan
{
    /// <summary>
    /// Runs one scan of <paramref name="store"/>'s warm state, answering each proposal's outcome by kind. A proposal which
    /// fails counts as <see cref="JournalExpiryScanResult.Failed"/>, and the scan goes on with the rest; only the cancellation
    /// of <paramref name="cancellationToken"/> or the store's disposal ends it early, after the round it met them in ends.
    /// </summary>
    /// <param name="store">The store whose admissions' warm state gave the candidates.</param>
    /// <param name="candidates">The candidates, each a key and its deleted state's version.</param>
    /// <param name="round">The most <c>Expired</c>s proposed at once, at least 1.</param>
    /// <param name="cancellationToken">Stops the scan between rounds and its wait for a round, which asserts nothing.</param>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <exception cref="ObjectDisposedException">The store was disposed.</exception>
    public static Task<JournalExpiryScanResult> RunAsync(JournalStore store, List<(JournalRowKey Key, JournalVersion Version)> candidates, int round, CancellationToken cancellationToken) =>
        RunRoundsAsync(
            candidates,
            round,
            (candidate, ct) => ProposeWarmAsync(store, candidate.Key, candidate.Version, ct),
            cancellationToken);

    /// <summary>
    /// Runs cold tombstone expiry candidates discovered from key indexes (journals.md §8.6) through
    /// <see cref="JournalStore.ExpireColdAsync"/> in rounds of at most <paramref name="round"/>, tallying every submitted
    /// proposal's outcome by kind.
    /// </summary>
    public static Task<JournalExpiryScanResult> RunColdAsync(
        JournalStore store,
        List<JournalRowKey> coldKeys,
        int round,
        CancellationToken cancellationToken) =>
        RunRoundsAsync(
            coldKeys,
            round,
            store.ExpireColdAsync,
            cancellationToken);

    private static async Task<JournalExpiryScanResult> RunRoundsAsync<TCandidate>(
        List<TCandidate> candidates,
        int round,
        Func<TCandidate, CancellationToken, Task<JournalWriteOutcome?>> propose,
        CancellationToken cancellationToken)
    {
        Debug.Assert(round >= 1, "A round proposes at least one Expired.");
        var result = default(JournalExpiryScanResult);
        for (var start = 0; start < candidates.Count; start += round)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var proposals = new Task<JournalWriteOutcome?>[Math.Min(round, candidates.Count - start)];
            for (var index = 0; index < proposals.Length; index++)
            {
                proposals[index] = propose(candidates[start + index], cancellationToken);
            }

            await ((Task)Task.WhenAll(proposals)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            Exception? ends = null;
            foreach (var proposal in proposals)
            {
                if (proposal.IsCompletedSuccessfully)
                {
                    if (proposal.Result is { } outcome)
                    {
                        result = Count(result, outcome);
                    }
                }
                else if (proposal.Exception?.InnerException is ObjectDisposedException disposed)
                {
                    ends ??= disposed;
                }
                else if (proposal.IsCanceled && cancellationToken.IsCancellationRequested)
                {
                    ends ??= new OperationCanceledException(cancellationToken);
                }
                else
                {
                    result = result with { Failed = result.Failed + 1 };
                }
            }

            if (ends is not null)
            {
                ExceptionDispatchInfo.Throw(ends);
            }
        }

        return result;
    }

    /// <summary>
    /// Proposes one warm <c>Expired</c>, so a failure the store raises as it routes the key fails the proposal alone, and
    /// tells the store's observer, if a test set one, of the proposal before it is made and of its outcome, none when it
    /// failed.
    /// </summary>
    private static async Task<JournalWriteOutcome?> ProposeWarmAsync(JournalStore store, JournalRowKey key, JournalVersion version, CancellationToken cancellationToken)
    {
        var observed = store.OnExpiryProposed?.Invoke(key, version);
        JournalWriteOutcome? outcome = null;
        try
        {
            outcome = await store.ExpireAsync(key, version, cancellationToken).ConfigureAwait(false);
            return outcome;
        }
        finally
        {
            observed?.Invoke(outcome);
        }
    }

    private static JournalExpiryScanResult Count(JournalExpiryScanResult result, JournalWriteOutcome outcome) => outcome switch
    {
        JournalWriteOutcome.Accepted => result with { Expired = result.Expired + 1 },
        JournalWriteOutcome.Rejected => result with { Rejected = result.Rejected + 1 },
        JournalMoved => result with { Moved = result.Moved + 1 },
        JournalWriteOutcome.Fenced => result with { Fenced = result.Fenced + 1 },
        JournalWriteOutcome.Ambiguous => result with { Ambiguous = result.Ambiguous + 1 },
        JournalWriteOutcome.ReplaceRequired => throw new UnreachableException("Only an Append is answered ReplaceRequired."),
        null => throw new UnreachableException("A write's outcome holds one of its cases."),
    };
}
