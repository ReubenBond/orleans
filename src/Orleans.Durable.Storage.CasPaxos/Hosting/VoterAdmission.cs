using Microsoft.Extensions.Logging;

namespace Orleans.Durable.Storage.CasPaxos.Hosting;

/// <summary>
/// A fresh voter's admission (<see cref="Consensus.CasPaxos.Sqlite.SqliteVoterRegistry"/>): before its first protocol
/// answer, a majority of the other voters durably admit the incarnation of its database's binding.
/// </summary>
/// <remarks>
/// Each other voter records the first incarnation it admits for this voter and answers that one ever after. Any two
/// majorities of the other voters share a voter, so once one majority admitted an incarnation, a later fresh database
/// under the same identity, a voter whose state was lost, meets a voter which answers the earlier incarnation, and is
/// refused. A single voter has no other voter to remember it, so it admits itself.
/// </remarks>
internal static partial class VoterAdmission
{
    private static readonly TimeSpan FirstRetry = TimeSpan.FromMilliseconds(50);

    /// <summary>Returns the number of other voters which admit a voter: a majority of <paramref name="others"/>.</summary>
    public static int Needed(int others) => others == 0 ? 0 : others / 2 + 1;

    /// <summary>
    /// Asks <paramref name="others"/> to admit <paramref name="voter"/>'s incarnation, asking unanswered voters again with
    /// a doubling delay of at most <paramref name="longestRetry"/>, until a majority of them has; then records the
    /// admission, after which the voter serves.
    /// </summary>
    /// <exception cref="InvalidOperationException">Another voter admitted another incarnation of this voter.</exception>
    public static async Task AdmitAsync(
        LocalVoter voter,
        IReadOnlyList<RemoteVoter> others,
        TimeSpan attemptTimeout,
        TimeSpan longestRetry,
        TimeProvider timeProvider,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var needed = Needed(others.Count);
        var admitted = new HashSet<string>(StringComparer.Ordinal);
        var delay = FirstRetry;
        while (admitted.Count < needed)
        {
            var answers = await Task.WhenAll(others
                .Where(remote => !admitted.Contains(remote.Id))
                .Select(remote => AskAsync(remote, voter, attemptTimeout, timeProvider, logger, cancellationToken))).ConfigureAwait(false);
            foreach (var (remote, reply) in answers)
            {
                if (reply is null)
                {
                    continue;
                }

                if (reply.Status == VoterAdmitStatus.BootstrapDigestMismatch)
                {
                    throw new InvalidOperationException(
                        $"Voter '{remote}' rejected admission of voter '{voter.Id}' because their bootstrap voter configurations disagree. "
                        + "Every bootstrap silo must configure the identical V(1) voter set.");
                }

                if (reply.Admitted != voter.Incarnation)
                {
                    throw new InvalidOperationException(
                        $"Voter '{remote}' admitted incarnation {reply.Admitted} of voter '{voter.Id}', not this database's {voter.Incarnation}: this voter's state was lost or replaced, "
                        + "and a fresh database cannot keep the promises the lost one gave. Restore the voter's database, or replace the lost voter with a new identity through CasPaxosReconfigurationService.");
                }

                _ = admitted.Add(remote);
            }

            if (admitted.Count < needed)
            {
                LogAwaitingAdmission(logger, voter.Id, admitted.Count, needed);
                await Task.Delay(delay, timeProvider, cancellationToken).ConfigureAwait(false);
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, longestRetry.Ticks));
            }
        }

        await voter.AdmittedAsync(cancellationToken).ConfigureAwait(false);
        LogAdmitted(logger, voter.Id, voter.Incarnation, needed);
    }

    private static async Task<(string Remote, VoterAdmitReply? Reply)> AskAsync(
        RemoteVoter remote, LocalVoter voter, TimeSpan attemptTimeout, TimeProvider timeProvider, ILogger logger, CancellationToken cancellationToken)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var deadline = new CancellationTokenSource(attemptTimeout, timeProvider);
        using var registration = deadline.Token.UnsafeRegister(static state => ((CancellationTokenSource)state!).Cancel(), attempt);
        try
        {
            return (remote.Id, await remote.AdmitAsync(voter.Id, voter.Incarnation, voter.BootstrapDigest, attempt.Token).ConfigureAwait(false));
        }
        catch (Exception failure) when (!cancellationToken.IsCancellationRequested)
        {
            // An unreachable or not yet started voter answers later; the loop asks it again.
            LogUnanswered(logger, failure, voter.Id, remote.Id);
            return (remote.Id, null);
        }
    }

    [LoggerMessage(1, LogLevel.Information, "Consensus voter {Voter} incarnation {Incarnation} was admitted by {Needed} other voters and serves.")]
    private static partial void LogAdmitted(ILogger logger, string voter, Guid incarnation, int needed);

    [LoggerMessage(2, LogLevel.Information, "Consensus voter {Voter} awaits admission: {Admitted} of the {Needed} other voters it needs have admitted it.")]
    private static partial void LogAwaitingAdmission(ILogger logger, string voter, int admitted, int needed);

    [LoggerMessage(3, LogLevel.Debug, "Consensus voter {Voter}'s admission request to voter {Remote} was not answered.")]
    private static partial void LogUnanswered(ILogger logger, Exception failure, string voter, string remote);
}
