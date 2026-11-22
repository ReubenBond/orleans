using System.Globalization;
using Orleans.Consensus.CasPaxos.Protocol;
using Orleans.Consensus.CasPaxos.Server;
using Orleans.Consensus.CasPaxos.Sqlite;
using Orleans.Consensus.CasPaxos.Testing;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.CasPaxos.Tests;

/// <summary>
/// Composes five SQLite acceptors and three Fast proposers, each an in-process register service.
/// There is no simulated transport or scheduler; member names, ballot databases, and proposer IDs
/// are derived from the directory and stable ordinals so reopening restores those identities.
/// </summary>
/// <remarks>
/// A failed directory flush is recorded where a host would stop the process, and fails the open which reported it.
/// </remarks>
internal sealed class SqliteFastRegisterCluster : IAsyncDisposable
{
    private readonly RecordingFatalErrorHandler _fatalErrors = new();
    private readonly List<SqliteAcceptorServer> _acceptors = [];
    private readonly List<SqliteProposerBallotSource> _ballots = [];
    private readonly List<ReplicatedRegisterService> _registers = [];

    internal IReadOnlyList<SqliteAcceptorServer> Acceptors => _acceptors;

    internal IReadOnlyList<ReplicatedRegisterService> Registers => _registers;

    internal static async Task<SqliteFastRegisterCluster> OpenAsync(string directory, CancellationToken cancellationToken)
    {
        var cluster = new SqliteFastRegisterCluster();
        try
        {
            for (var index = 0; index < 5; index++)
            {
                cluster._acceptors.Add(await SqliteAcceptorServer.OpenAsync(
                    Path.Combine(directory, string.Create(CultureInfo.InvariantCulture, $"member-{index}.sqlite")),
                    cluster._fatalErrors,
                    cancellationToken: cancellationToken).ConfigureAwait(false));
            }

            for (var index = 0; index < 3; index++)
            {
                var ballots = new SqliteProposerBallotSource(
                    Path.Combine(directory, string.Create(CultureInfo.InvariantCulture, $"proposer-{index}.sqlite")),
                    proposerId: index + 1,
                    cluster._fatalErrors);
                cluster._ballots.Add(ballots);
                var service = new ReplicatedRegisterService(
                    [
                        .. cluster._acceptors.Select((acceptor, ordinal) =>
                            new AcceptorPeer(string.Create(CultureInfo.InvariantCulture, $"member-{ordinal}"), acceptor)),
                    ],
                    ballots,
                    new RegisterServiceOptions
                    {
                        Mode = ProposerModePolicy.StaticFast,
                        TimeProvider = TimeProvider.System,
                        OperationTimeout = TimeSpan.FromSeconds(15),
                        AttemptTimeout = TimeSpan.FromSeconds(5),
                    });
                cluster._registers.Add(service);
            }

            return cluster;
        }
        catch
        {
            await cluster.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Drain register operations, then close allocators and SQLite writers.
        // Each group starts all disposals so a failed service cannot strand a sibling.
        try
        {
            await Task.WhenAll(_registers.Select(static service => service.DisposeAsync().AsTask())).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                foreach (var ballots in _ballots)
                {
                    ballots.Dispose();
                }
            }
            finally
            {
                await Task.WhenAll(_acceptors.Select(static acceptor => acceptor.DisposeAsync().AsTask())).ConfigureAwait(false);
            }
        }
    }
}
