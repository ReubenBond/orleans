using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Consensus.CasPaxos.Model;
using Orleans.Consensus.CasPaxos.Server;
using Orleans.Consensus.CasPaxos.Sqlite;
using Orleans.Consensus.CasPaxos.Testing;
using Orleans.Durable.Storage.CasPaxos.Hosting;

namespace Orleans.Durable.Storage.CasPaxos.Tests;

/// <summary>
/// The native consensus's voters and proposers in one process, without the Orleans runtime: three voters over real
/// SQLite databases, each behind its <see cref="CasPaxosVoterService"/>, and two proposers whose
/// <see cref="RemoteVoter"/>s reach each voter through a <see cref="VoterLink"/>, which stands in for Orleans RPC and
/// injects partitions, lost and late replies, held requests, misrouting, and forged replies.
/// </summary>
/// <remarks>
/// Databases are named after the voters and proposers within the directory, so reopening a cluster over the same
/// directory restores every identity, as a silo's restart at the same endpoint does.
/// </remarks>
internal sealed class NativeVoterCluster : IAsyncDisposable
{
    public static readonly string[] Ids = ["a", "b", "c"];

    private readonly List<SqliteProposerBallotSource> _ballots = [];
    private readonly List<ReplicatedRegisterService> _registers = [];

    private NativeVoterCluster(string directory, TimeSpan attemptTimeout)
    {
        Directory = directory;
        AttemptTimeout = attemptTimeout;
    }

    public string Directory { get; }

    public TimeSpan AttemptTimeout { get; }

    public RecordingFatalErrorHandler FatalErrors { get; } = new();

    /// <summary>Gets each voter, by its index in <see cref="Ids"/>; null once it is closed.</summary>
    public LocalVoter?[] Voters { get; } = new LocalVoter?[Ids.Length];

    /// <summary>Gets each voter's service, by its index in <see cref="Ids"/>.</summary>
    public CasPaxosVoterService?[] Services { get; } = new CasPaxosVoterService?[Ids.Length];

    /// <summary>Gets the link from each proposer, or from admission (the last row), to each voter.</summary>
    public VoterLink[,] Links { get; private set; } = new VoterLink[0, 0];

    public IReadOnlyList<ReplicatedRegisterService> Registers => _registers;

    public static string DatabaseOf(string directory, int voter) => Path.Combine(directory, string.Create(CultureInfo.InvariantCulture, $"voter-{Ids[voter]}.db"));

    /// <summary>Opens the voters, admits each fresh one, and builds two proposers.</summary>
    public static async Task<NativeVoterCluster> OpenAsync(string directory, CancellationToken cancellationToken, TimeSpan? attemptTimeout = null, bool admit = true, TimeSpan? operationTimeout = null)
    {
        var cluster = new NativeVoterCluster(directory, attemptTimeout ?? TimeSpan.FromMilliseconds(500));
        try
        {
            const int Proposers = 2;
            cluster.Links = new VoterLink[Proposers + 1, Ids.Length];
            for (var voter = 0; voter < Ids.Length; voter++)
            {
                await cluster.OpenVoterAsync(voter, cancellationToken);
            }

            for (var row = 0; row <= Proposers; row++)
            {
                for (var voter = 0; voter < Ids.Length; voter++)
                {
                    var index = voter;
                    cluster.Links[row, voter] = new VoterLink(() => cluster.Services[index] ?? throw new CasPaxosVoterUnavailableException($"Voter '{Ids[index]}' is closed."));
                }
            }

            if (admit)
            {
                await Task.WhenAll(Enumerable.Range(0, Ids.Length).Select(voter => cluster.AdmitAsync(voter, cancellationToken)));
            }

            for (var proposer = 0; proposer < Proposers; proposer++)
            {
                var ballots = new SqliteProposerBallotSource(
                    Path.Combine(directory, string.Create(CultureInfo.InvariantCulture, $"proposer-{proposer}.db")), proposer + 1, cluster.FatalErrors);
                cluster._ballots.Add(ballots);
                cluster._registers.Add(new ReplicatedRegisterService(
                    [.. Ids.Select((id, voter) => new AcceptorPeer(id, cluster.Remote(proposer, voter)))],
                    ballots,
                    new RegisterServiceOptions
                    {
                        OperationTimeout = operationTimeout ?? TimeSpan.FromSeconds(5),
                        AttemptTimeout = cluster.AttemptTimeout,
                        RetryBackoff = TimeSpan.FromMilliseconds(5),
                    }));
            }

            return cluster;
        }
        catch
        {
            await cluster.DisposeAsync();
            throw;
        }
    }

    /// <summary>Returns the proposer's (or, for row 2, admission's) <see cref="RemoteVoter"/> of a voter.</summary>
    public RemoteVoter Remote(int row, int voter) => new(Ids[voter], () => Links[row, voter]);

    /// <summary>Opens a voter's database, recovering its state and validating its binding.</summary>
    public async Task OpenVoterAsync(int voter, CancellationToken cancellationToken)
    {
        var local = await LocalVoter.OpenAsync(Ids[voter], DatabaseOf(Directory, voter), Ids, 256, 64, FatalErrors, cancellationToken);
        Voters[voter] = local;
        Services[voter] = new CasPaxosVoterService(local);
    }

    /// <summary>Admits a voter through the admission row of links, unless it already serves.</summary>
    public Task AdmitAsync(int voter, CancellationToken cancellationToken)
    {
        var local = Voters[voter]!;
        if (local.IsServing)
        {
            return Task.CompletedTask;
        }

        var others = Enumerable.Range(0, Ids.Length).Where(other => other != voter).Select(other => Remote(Links.GetLength(0) - 1, other)).ToList();
        return VoterAdmission.AdmitAsync(local, others, AttemptTimeout, TimeSpan.FromMilliseconds(100), TimeProvider.System, NullLogger.Instance, cancellationToken);
    }

    /// <summary>Stops a voter, draining its admitted requests and closing its database.</summary>
    public async Task CloseVoterAsync(int voter)
    {
        Services[voter] = null;
        if (Voters[voter] is { } local)
        {
            Voters[voter] = null;
            await local.DisposeAsync();
        }
    }

    /// <summary>Applies <paramref name="change"/> to every link from <paramref name="row"/>.</summary>
    public void Each(int row, Action<VoterLink> change)
    {
        for (var voter = 0; voter < Ids.Length; voter++)
        {
            change(Links[row, voter]);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Task.WhenAll(_registers.Select(static register => register.DisposeAsync().AsTask()));
        }
        finally
        {
            foreach (var ballots in _ballots)
            {
                ballots.Dispose();
            }

            for (var voter = 0; voter < Ids.Length; voter++)
            {
                await CloseVoterAsync(voter);
            }
        }
    }
}

/// <summary>
/// One caller's path to one voter, standing in for Orleans RPC. A partition fails a request before it reaches the voter;
/// a lost reply fails it after the voter answered; a late reply arrives after its delay, whatever the caller's
/// cancellation; a held request waits before it reaches the voter; a misroute delivers it to another voter's service;
/// a forged reply names another voter.
/// </summary>
internal sealed class VoterLink(Func<ICasPaxosVoter> voter) : ICasPaxosVoter
{
    private int _delivered;

    public bool Partitioned { get; set; }

    public bool LoseAcceptReplies { get; set; }

    public TimeSpan ReplyDelay { get; set; }

    public Task? Hold { get; set; }

    public Func<ICasPaxosVoter>? MisrouteTo { get; set; }

    public string? ForgedVoter { get; set; }

    /// <summary>Gets the number of requests which reached a voter.</summary>
    public int Delivered => Volatile.Read(ref _delivered);

    public void Heal()
    {
        Partitioned = false;
        LoseAcceptReplies = false;
        ReplyDelay = TimeSpan.Zero;
        Hold = null;
        MisrouteTo = null;
        ForgedVoter = null;
    }

    public async ValueTask<VoterReadReply> ReadAsync(string addressed, string key, Ballot? knownBallot, ByteString? knownHash, CancellationToken cancellationToken)
    {
        var target = await ReachAsync(cancellationToken);
        var reply = await target.ReadAsync(addressed, key, knownBallot, knownHash, cancellationToken);
        await ReplyAsync();
        return ForgedVoter is { } forged ? reply with { Voter = forged } : reply;
    }

    public async ValueTask<VoterPrepareReply> PrepareAsync(string addressed, string key, Ballot ballot, CancellationToken cancellationToken)
    {
        var target = await ReachAsync(cancellationToken);
        var reply = await target.PrepareAsync(addressed, key, ballot, cancellationToken);
        await ReplyAsync();
        return ForgedVoter is { } forged ? reply with { Voter = forged } : reply;
    }

    public async ValueTask<VoterAcceptReply> AcceptAsync(string addressed, string key, Ballot ballot, ByteString value, Ballot nextBallotToPrepare, CancellationToken cancellationToken)
    {
        var target = await ReachAsync(cancellationToken);
        var reply = await target.AcceptAsync(addressed, key, ballot, value, nextBallotToPrepare, cancellationToken);
        if (LoseAcceptReplies)
        {
            throw new TimeoutException("The accept's reply was lost.");
        }

        await ReplyAsync();
        return ForgedVoter is { } forged ? reply with { Voter = forged } : reply;
    }

    public async ValueTask<VoterKeyPageReply> ListKeysAsync(string addressed, string prefix, string? afterKey, int limit, CancellationToken cancellationToken)
    {
        var target = await ReachAsync(cancellationToken);
        var reply = await target.ListKeysAsync(addressed, prefix, afterKey, limit, cancellationToken);
        await ReplyAsync();
        return ForgedVoter is { } forged ? reply with { Voter = forged } : reply;
    }

    public async ValueTask<VoterAdmitReply> AdmitAsync(string addressed, string candidate, Guid incarnation, CancellationToken cancellationToken)
    {
        var target = await ReachAsync(cancellationToken);
        var reply = await target.AdmitAsync(addressed, candidate, incarnation, cancellationToken);
        await ReplyAsync();
        return ForgedVoter is { } forged ? reply with { Voter = forged } : reply;
    }

    private async Task<ICasPaxosVoter> ReachAsync(CancellationToken cancellationToken)
    {
        if (Hold is { } hold)
        {
            await hold.WaitAsync(cancellationToken);
        }

        if (Partitioned)
        {
            throw new CasPaxosVoterUnavailableException("The link is partitioned.");
        }

        _ = Interlocked.Increment(ref _delivered);
        return MisrouteTo?.Invoke() ?? voter();
    }

    private Task ReplyAsync() => ReplyDelay > TimeSpan.Zero ? Task.Delay(ReplyDelay, TimeProvider.System, CancellationToken.None) : Task.CompletedTask;
}
