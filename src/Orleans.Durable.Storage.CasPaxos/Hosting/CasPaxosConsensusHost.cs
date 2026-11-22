using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Consensus.CasPaxos.Server;
using Orleans.Consensus.CasPaxos.Sqlite;
using Orleans.Runtime;

namespace Orleans.Durable.Storage.CasPaxos.Hosting;

/// <summary>
/// Hosts a silo's part of the native Fast CASPaxos MetaStore (caspaxos.md): its proposer, a
/// <see cref="ReplicatedRegisterService"/> over the fixed voters, and, on a voter's silo, the voter and the system target
/// which serves it.
/// </summary>
/// <remarks>
/// <para>
/// At <see cref="OpenStage"/>, before the runtime initializes, the host opens the proposer's ballot database and the
/// voter's acceptor database, recovering its committed state and validating its identity binding, then registers the
/// voter's system target and builds the register service. A voter answers admissions from then on, and protocol
/// requests once its incarnation is admitted. At <see cref="ReadyStage"/>, once the silo has joined membership as
/// joining, the host waits for the admission, so a silo whose voter is refused, a voter whose state was lost, does not
/// start; Storage's node registers at <see cref="ServiceLifecycleStage.Active"/>, after its consensus is ready. Neither
/// stage reads a register: addressing uses Orleans membership, never the registers consensus serves.
/// </para>
/// <para>
/// Stopping the silo stops admission, drains the register service's operations, closes the proposer's allocator, and
/// stops the voter, which drains its admitted requests before its database closes.
/// </para>
/// </remarks>
internal sealed partial class CasPaxosConsensusHost : IRegisterService, ILifecycleParticipant<ISiloLifecycle>, IDisposable
{
    /// <summary>The stage at which the host opens its stores and registers its system target.</summary>
    public const int OpenStage = ServiceLifecycleStage.RuntimeInitialize - 1;

    /// <summary>The stage at which the silo waits for its voter's admission: after it joined membership, before it is active.</summary>
    public const int ReadyStage = ServiceLifecycleStage.AfterRuntimeGrainServices + 1;

    private readonly CasPaxosMetaStoreOptions _options;
    private readonly ILocalSiloDetails _silo;
    private readonly IClusterMembershipService _membership;
    private readonly IServiceProvider _services;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stopping = new();
    private SqliteProposerBallotSource? _ballots;
    private LocalVoter? _voter;
    private CasPaxosVoterSystemTarget? _target;
    private List<RemoteVoter> _remotes = [];
#pragma warning disable CA2213 // CloseAsync drains and disposes the asynchronous register service at its lifecycle stage.
    private ReplicatedRegisterService? _registers;
#pragma warning restore CA2213
    private Task _admission = Task.CompletedTask;

    public CasPaxosConsensusHost(
        IOptions<CasPaxosMetaStoreOptions> options,
        ILocalSiloDetails silo,
        IClusterMembershipService membership,
        IServiceProvider services,
        TimeProvider timeProvider,
        ILogger<CasPaxosConsensusHost> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _silo = silo;
        _membership = membership;
        _services = services;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>Gets the voter this silo hosts, or <see langword="null"/> when it hosts none or has not opened it.</summary>
    internal LocalVoter? Voter => _voter;

    /// <summary>Gets the register service, once the host opened.</summary>
    internal ReplicatedRegisterService Registers => _registers ?? throw new InvalidOperationException("The silo's consensus host has not opened.");

    /// <inheritdoc/>
    public void Participate(ISiloLifecycle observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        _ = observer.Subscribe(nameof(CasPaxosConsensusHost), OpenStage, OpenAsync, CloseAsync);
        _ = observer.Subscribe(nameof(CasPaxosConsensusHost), ReadyStage, WaitUntilAdmittedAsync, static _ => Task.CompletedTask);
    }

    /// <inheritdoc/>
    public ValueTask<RegisterValue> ReadAsync(string key, CancellationToken cancellationToken = default) =>
        Registers.ReadAsync(key, cancellationToken);

    /// <inheritdoc/>
    public ValueTask<CompareExchangeResult> CompareExchangeAsync(string key, ByteString? expectedVersion, ByteString value, CancellationToken cancellationToken = default) =>
        Registers.CompareExchangeAsync(key, expectedVersion, value, cancellationToken);

    /// <inheritdoc/>
    public ValueTask<RegisterKeyPage> ListKeysAsync(string prefix = "", string? afterKey = null, int limit = 100, CancellationToken cancellationToken = default) =>
        Registers.ListKeysAsync(prefix, afterKey, limit, cancellationToken);

    /// <inheritdoc/>
    public void Dispose()
    {
        // CloseAsync drained and closed the stores at their lifecycle stage; disposing them again is a no-op, and closes
        // them when the silo stopped before that stage ran.
        _ballots?.Dispose();
        _target?.Dispose();
        _stopping.Dispose();
    }

    private async Task OpenAsync(CancellationToken cancellationToken)
    {
        var failures = ConsensusStorageFailures.Instance;
        var proposerPath = Path.GetFullPath(_options.ProposerDatabase);
        DurableDirectory.Create(Path.GetDirectoryName(proposerPath)!, failures);
        _ballots = new SqliteProposerBallotSource(proposerPath, _options.ProposerId, failures);

        var local = _options.Voters.FirstOrDefault(voter => IPEndPoint.Parse(voter.Endpoint).Equals(_silo.SiloAddress.Endpoint));
        if (local is not null)
        {
            _voter = await LocalVoter.OpenAsync(
                local.Id,
                local.Database,
                _options.Voters.Select(static voter => voter.Id),
                _options.VoterQueueCapacity,
                _options.VoterMaxBatchSize,
                failures,
                cancellationToken).ConfigureAwait(false);
            _target = new CasPaxosVoterSystemTarget(new CasPaxosVoterService(_voter), _services.GetRequiredService<SystemTargetShared>());
            LogVoterOpened(_logger, local.Id, local.Database, _voter.Incarnation, _voter.IsServing);
        }

        var grains = _services.GetRequiredService<IInternalGrainFactory>();
        _remotes = [.. _options.Voters.Where(voter => voter != local).Select(voter =>
        {
            var endpoint = IPEndPoint.Parse(voter.Endpoint);
            return new RemoteVoter(voter.Id, () => grains.GetSystemTarget<ICasPaxosVoter>(CasPaxosVoterSystemTarget.VoterType, Resolve(voter.Id, endpoint)));
        })];
        _registers = new ReplicatedRegisterService(
            [.. _options.Voters.Select(voter => voter == local
                ? new AcceptorPeer(voter.Id, _voter!)
                : new AcceptorPeer(voter.Id, _remotes.Single(remote => remote.Id == voter.Id)))],
            _ballots,
            new RegisterServiceOptions
            {
                TimeProvider = _timeProvider,
                OperationTimeout = _options.OperationTimeout,
                AttemptTimeout = _options.AttemptTimeout,
                RetryBackoff = _options.RetryBackoff,
                Capacity = _options.Capacity,
            },
            _voter?.Inspection);

        if (_voter is { IsServing: false } voter)
        {
            using (ExecutionContext.SuppressFlow())
            {
                _admission = Task.Run(
                    () => VoterAdmission.AdmitAsync(voter, _remotes, _options.AttemptTimeout, _options.AdmissionRetryDelay, _timeProvider, _logger, _stopping.Token),
                    CancellationToken.None);
            }
        }
    }

    private Task WaitUntilAdmittedAsync(CancellationToken cancellationToken) => _admission.WaitAsync(cancellationToken);

    private async Task CloseAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        try
        {
            await _admission.ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is OperationCanceledException || _admission.IsFaulted)
        {
            // The admission's own failure stopped the silo's start, which reported it.
        }

        try
        {
            if (_registers is { } registers)
            {
                await registers.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _ballots?.Dispose();
            if (_voter is { } voter)
            {
                await voter.DisposeAsync().ConfigureAwait(false);
            }

            _target?.Dispose();
        }
    }

    /// <summary>
    /// Returns the current silo address of the voter at <paramref name="endpoint"/>: of the silos membership holds at that
    /// endpoint and has not declared dead, the newest generation.
    /// </summary>
    private SiloAddress Resolve(string voter, IPEndPoint endpoint)
    {
        SiloAddress? current = null;
        foreach (var member in _membership.CurrentSnapshot.Members.Values)
        {
            if (member.Status is not (SiloStatus.None or SiloStatus.Dead)
                && member.SiloAddress.Endpoint.Equals(endpoint)
                && (current is null || member.SiloAddress.Generation > current.Generation))
            {
                current = member.SiloAddress;
            }
        }

        return current ?? throw new CasPaxosVoterUnavailableException($"Voter '{voter}' at {endpoint} has no live silo in the cluster's membership.");
    }

    [LoggerMessage(1, LogLevel.Information, "Opened consensus voter {Voter} over {Database}, incarnation {Incarnation}; serving: {Serving}.")]
    private static partial void LogVoterOpened(ILogger logger, string voter, string database, Guid incarnation, bool serving);
}
