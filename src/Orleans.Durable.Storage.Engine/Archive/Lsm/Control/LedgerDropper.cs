using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Control;

namespace Orleans.Durable.Storage.Engine.Archive.Lsm.Control;

/// <summary>
/// Submits the drops of complete ledgers (archive §2.6, §8.4): <c>Drop(S)</c> for each closed ledger which a manifest
/// value this process read, or whose replace returned applied, shows complete, oldest first along each key's lineage.
/// </summary>
/// <remarks>
/// <para>
/// The evidence is the process's <see cref="ManifestView.Current"/>, which holds only manifest values the process read or
/// whose replace by its committer returned applied, never one whose replace has an unknown outcome, so a drop is
/// submitted only once the LSM holds every row of its ledger below <c>T</c> (LSM-I3, HI-I2).
/// </para>
/// <para>
/// Each drop derives from the map view's current value, which lists its ledger closed and no ledger with a lower ID whose
/// range overlaps it (LG-I10, ledgers.md §3). Once drops apply, the dropper refreshes the map view to their epoch and drops
/// the ledgers they unblocked, until none is left. A drop the map committer rejects is a defect: it fails a debug
/// assertion, is logged as an error, and is never submitted again.
/// </para>
/// </remarks>
public sealed partial class LedgerDropper
{
    private readonly LedgerMapView _map;
    private readonly ManifestView _manifest;
    private readonly ILogger _logger;
    private readonly Lock _sync = new();
    private readonly HashSet<LedgerId> _rejected = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="LedgerDropper"/> class.
    /// </summary>
    /// <param name="map">The process's ledger map view, from whose values drops derive.</param>
    /// <param name="manifest">The process's manifest view, whose values are the evidence of coverage.</param>
    /// <param name="logger">Logs a rejected drop.</param>
    public LedgerDropper(LedgerMapView map, ManifestView manifest, ILogger<LedgerDropper> logger)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(logger);
        _map = map;
        _manifest = manifest;
        _logger = logger;
    }

    /// <summary>
    /// Submits <c>Drop(S)</c> for every closed ledger the manifest view's current value shows complete and which no listed
    /// ledger with a lower ID overlaps, in passes: each pass submits every such ledger at once, decides every answer,
    /// refreshes the map view once to the epoch of the drops which applied, and the next pass submits those they unblocked.
    /// </summary>
    /// <param name="commit">Submits a change once to the ledger map committer, as its <c>CommitAsync</c> does.</param>
    /// <param name="cancellationToken">Stops the caller's wait; a submitted drop may still apply.</param>
    /// <returns>The ledgers whose drops applied, in the order they were submitted.</returns>
    /// <remarks>
    /// An exception from <paramref name="commit"/> leaves its drop's outcome unknown: the pass still decides every other
    /// drop and refreshes the map view, then throws the first such exception, and a later call submits the drop again.
    /// </remarks>
    public async Task<ImmutableArray<LedgerId>> DropCompleteAsync(
        Func<LedgerMapChange, CancellationToken, Task<LedgerMapCommitOutcome>> commit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commit);
        var dropped = ImmutableArray.CreateBuilder<LedgerId>();
        while (_manifest.Current is { } manifest)
        {
            var droppable = Candidates(_map.Current, manifest.Value);
            if (droppable.IsEmpty)
            {
                break;
            }

            var submissions = droppable.Select(ledger => commit(new LedgerMapChange.Drop(ledger), cancellationToken)).ToArray();
            ulong? epoch = null;
            ExceptionDispatchInfo? failure = null;
            for (var index = 0; index < submissions.Length; index++)
            {
                LedgerMapCommitOutcome outcome;
                try
                {
                    outcome = await submissions[index].ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    failure ??= ExceptionDispatchInfo.Capture(exception);
                    continue;
                }

                if (Decide(droppable[index], outcome) is { } applied)
                {
                    dropped.Add(droppable[index]);
                    epoch = Math.Max(epoch ?? 0, applied);
                }
            }

            if (epoch is { } reached)
            {
                _ = await _map.RefreshAsync(reached, cancellationToken).ConfigureAwait(false);
            }

            failure?.Throw();
            if (epoch is null)
            {
                break;
            }
        }

        return dropped.DrainToImmutable();
    }

    /// <summary>
    /// Returns the ledgers <paramref name="map"/> lists closed, which <paramref name="manifest"/> shows complete, and which
    /// no ledger <paramref name="map"/> lists with a lower ID overlaps (LG-I10), in ID order, leaving out those
    /// <paramref name="excluded"/> holds.
    /// </summary>
    private static ImmutableArray<LedgerId> Droppable(LedgerMapValue map, ManifestValue manifest, HashSet<LedgerId> excluded)
    {
        var droppable = ImmutableArray.CreateBuilder<LedgerId>();
        foreach (var ledger in map.Ledgers)
        {
            if (!ledger.IsOpen && manifest.IsComplete(ledger.Id) && !excluded.Contains(ledger.Id) && map.OlderOverlapping(ledger.Id) is null)
            {
                droppable.Add(ledger.Id);
            }
        }

        return droppable.DrainToImmutable();
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The ledger map committer rejected the drop of ledger {Ledger} as a defect: {Reason}.")]
    private static partial void LogRejected(ILogger logger, LedgerId ledger, LedgerMapRejection reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "The ledger map committer answered the drop of ledger {Ledger} {Outcome}, which no drop derived from the map view earns.")]
    private static partial void LogUnexpected(ILogger logger, LedgerId ledger, object outcome);

    /// <summary>
    /// Decides a drop's answer: the epoch of the map which holds it when it applied, and otherwise none. A drop derived from
    /// the map view's value, which lists its ledger closed and none older overlapping it, is never superseded, and shrinks
    /// the map, so is never refused; either answer, like a rejection, is a defect.
    /// </summary>
    private ulong? Decide(LedgerId ledger, LedgerMapCommitOutcome outcome) => outcome switch
    {
        LedgerMapCommitOutcome.Applied applied => applied.Epoch,
        LedgerMapCommitOutcome.Rejected rejected => Reject(ledger, rejected.Reason),
        LedgerMapCommitOutcome.Superseded or LedgerMapCommitOutcome.Refused => Unexpected(ledger, outcome),
        null => throw new UnreachableException("A map committer's answer holds its case."),
    };

    private ulong? Unexpected(LedgerId ledger, LedgerMapCommitOutcome outcome)
    {
        LogUnexpected(_logger, ledger, outcome.Value);
        Debug.Fail($"The ledger map committer answered the drop of ledger {ledger} {outcome.Value}.");
        return null;
    }
    private ImmutableArray<LedgerId> Candidates(LedgerMapValue map, ManifestValue manifest)
    {
        lock (_sync)
        {
            return Droppable(map, manifest, _rejected);
        }
    }

    private ulong? Reject(LedgerId ledger, LedgerMapRejection reason)
    {
        lock (_sync)
        {
            _ = _rejected.Add(ledger);
        }

        LogRejected(_logger, ledger, reason);
        Debug.Fail($"The ledger map committer rejected the drop of ledger {ledger} as a defect: {reason}.");
        return null;
    }
}
