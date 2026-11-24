using System.Collections.Concurrent;
using System.Collections.Immutable;
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
/// <see cref="ReplicatedRegisterService"/> over the active voters, and, on a voter's silo, the voter and the system target
/// which serves it.
/// </summary>
/// <remarks>
/// <para>
/// At <see cref="OpenStage"/>, before the runtime initializes, the host opens the proposer's ballot database and the
/// voter's acceptor database, recovering its committed state, initializing its join floor when joining in configuration
/// <c>c_join &gt; 1</c>, and validating its identity and bootstrap digest bindings, then registers the voter's system
/// target and builds the register service. A voter answers admissions from then on, and protocol requests once its
/// incarnation is admitted. At <see cref="ReadyStage"/>, once the silo has joined membership as joining, the host waits
/// for the admission, so a silo whose voter is refused (lost state or bootstrap configuration mismatch) does not start;
/// when <see cref="CasPaxosMetaStoreOptions.InitialConfiguration"/> is greater than <c>1</c>, it also verifies that an
/// active configuration at or above that number is established in <c>__caspaxos/config</c>. Storage's node registers at
/// <see cref="ServiceLifecycleStage.Active"/>, after its consensus is ready.
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
    private readonly ConcurrentDictionary<string, string> _knownEndpoints = new(StringComparer.Ordinal);
    private SqliteProposerBallotSource? _ballots;
    private LocalVoter? _voter;
    private CasPaxosVoterSystemTarget? _target;
    private IInternalGrainFactory? _grains;
    private ImmutableArray<VoterDescriptor> _seedVoters;
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

    /// <summary>Creates a <see cref="VoterReconfigurationDriver"/> over this silo's proposer ballot source and Orleans RPC resolver.</summary>
    internal VoterReconfigurationDriver CreateReconfigurator()
    {
        var ballots = _ballots ?? throw new InvalidOperationException("The silo's consensus host has not opened.");
        return new VoterReconfigurationDriver(ballots, ResolvePeer, CreateRegisterServiceOptions());
    }

    /// <summary>Returns the highest locally known configuration number and its voter descriptors with endpoints.</summary>
    internal (long Configuration, ImmutableArray<VoterDescriptor> Voters) GetSeedConfiguration()
    {
        var registers = Registers;
        var activePeers = registers.ActivePeers;
        var builder = ImmutableArray.CreateBuilder<VoterDescriptor>(activePeers.Length);
        foreach (var peer in activePeers)
        {
            if (!_knownEndpoints.TryGetValue(peer.Id, out var endpoint) || string.IsNullOrEmpty(endpoint))
            {
                return (_options.InitialConfiguration, _seedVoters);
            }

            builder.Add(new VoterDescriptor(peer.Id, endpoint));
        }

        return (registers.ActiveConfiguration, builder.MoveToImmutable());
    }

    /// <summary>Records endpoints from a discovered or activated configuration for subsequent resolution.</summary>
    internal void RememberEndpoints(IEnumerable<VoterDescriptor> voters)
    {
        foreach (var voter in voters)
        {
            if (!string.IsNullOrEmpty(voter.Endpoint))
            {
                _knownEndpoints[voter.Id] = voter.Endpoint;
            }
        }
    }

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

        _grains = _services.GetRequiredService<IInternalGrainFactory>();
        _seedVoters = [.. _options.Voters.Select(static voter => new VoterDescriptor(voter.Id, voter.Endpoint))];
        RememberEndpoints(_seedVoters);

        List<RemoteVoter> admissionPeers = [];
        if (_options.JoiningVoter is { } joining)
        {
            _voter = await LocalVoter.OpenAsync(
                joining.Id,
                joining.Database,
                _options.VoterQueueCapacity,
                _options.VoterMaxBatchSize,
                failures,
                joinConfiguration: joining.JoinConfiguration,
                bootstrapDigest: null,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            _knownEndpoints[joining.Id] = _silo.SiloAddress.Endpoint.ToString();
            foreach (var peer in joining.AdmissionPeers)
            {
                _knownEndpoints[peer.Id] = peer.Endpoint;
                if (!string.Equals(peer.Id, joining.Id, StringComparison.Ordinal))
                {
                    admissionPeers.Add(CreateRemoteVoter(peer.Id, peer.Endpoint));
                }
            }

            _target = new CasPaxosVoterSystemTarget(new CasPaxosVoterService(_voter), _services.GetRequiredService<SystemTargetShared>());
            LogVoterOpened(_logger, joining.Id, joining.Database, _voter.Incarnation, _voter.IsServing);
        }
        else if (_options.Voters.FirstOrDefault(voter => IPEndPoint.Parse(voter.Endpoint).Equals(_silo.SiloAddress.Endpoint)) is { } local)
        {
            ByteString? bootstrapDigest = _options.InitialConfiguration == 1
                ? VoterConfiguration.ComputeBootstrapDigest(_options.Voters.Select(static voter => voter.Id))
                : null;
            _voter = await LocalVoter.OpenAsync(
                local.Id,
                local.Database,
                _options.VoterQueueCapacity,
                _options.VoterMaxBatchSize,
                failures,
                joinConfiguration: _options.InitialConfiguration,
                bootstrapDigest: bootstrapDigest,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            foreach (var peer in _options.Voters)
            {
                if (!string.Equals(peer.Id, local.Id, StringComparison.Ordinal))
                {
                    admissionPeers.Add(CreateRemoteVoter(peer.Id, peer.Endpoint));
                }
            }

            _target = new CasPaxosVoterSystemTarget(new CasPaxosVoterService(_voter), _services.GetRequiredService<SystemTargetShared>());
            LogVoterOpened(_logger, local.Id, local.Database, _voter.Incarnation, _voter.IsServing);
        }

        _registers = new ReplicatedRegisterService(
            [.. _seedVoters.Select(voter => new AcceptorPeer(voter.Id, ResolvePeer(voter)))],
            _ballots,
            CreateRegisterServiceOptions(),
            _voter is null ? null : new ActiveVoterInspection(this, _voter));

        if (_voter is { IsServing: false } voter)
        {
            using (ExecutionContext.SuppressFlow())
            {
                _admission = Task.Run(
                    () => VoterAdmission.AdmitAsync(voter, admissionPeers, _options.AttemptTimeout, _options.AdmissionRetryDelay, _timeProvider, _logger, _stopping.Token),
                    CancellationToken.None);
            }
        }
    }

    private async Task WaitUntilAdmittedAsync(CancellationToken cancellationToken)
    {
        await _admission.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (_options.InitialConfiguration > 1)
        {
            await VerifyNonBootstrapSeedAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task VerifyNonBootstrapSeedAsync(CancellationToken cancellationToken)
    {
        var reconfigurator = CreateReconfigurator();
        var delay = TimeSpan.FromMilliseconds(50);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var discovered = await reconfigurator.DiscoverConfigurationAsync(_seedVoters, _options.InitialConfiguration, cancellationToken).ConfigureAwait(false);
                if (discovered.State != VoterConfigurationState.Active)
                {
                    throw new InvalidOperationException(
                        $"Configuration {discovered.Number} is {discovered.State}, not Active; complete or resume reconfiguration before starting silos with InitialConfiguration={_options.InitialConfiguration}.");
                }

                RememberEndpoints(discovered.Voters);
                return;
            }
            catch (RegisterUnavailableException) when (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, _options.AdmissionRetryDelay.Ticks));
            }
        }
    }

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

    private RegisterServiceOptions CreateRegisterServiceOptions() => new()
    {
        TimeProvider = _timeProvider,
        OperationTimeout = _options.OperationTimeout,
        AttemptTimeout = _options.AttemptTimeout,
        RetryBackoff = _options.RetryBackoff,
        Capacity = _options.Capacity,
        InitialConfiguration = _options.InitialConfiguration,
        PeerResolver = ResolvePeer,
    };

    private IKeyedAcceptor ResolvePeer(VoterDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (!string.IsNullOrEmpty(descriptor.Endpoint))
        {
            _knownEndpoints[descriptor.Id] = descriptor.Endpoint;
        }

        if (_voter is { } local && string.Equals(descriptor.Id, local.Id, StringComparison.Ordinal))
        {
            return local;
        }

        var endpointText = !string.IsNullOrEmpty(descriptor.Endpoint)
            ? descriptor.Endpoint
            : _knownEndpoints.TryGetValue(descriptor.Id, out var known)
                ? known
                : throw new CasPaxosVoterUnavailableException($"Voter '{descriptor.Id}' has no known silo endpoint.");
        return CreateRemoteVoter(descriptor.Id, endpointText);
    }

    private RemoteVoter CreateRemoteVoter(string voterId, string endpointText)
    {
        var grains = _grains ?? throw new InvalidOperationException("The silo's consensus host has not opened.");
        var endpoint = IPEndPoint.Parse(endpointText);
        return new RemoteVoter(voterId, () => grains.GetSystemTarget<ICasPaxosVoter>(CasPaxosVoterSystemTarget.VoterType, Resolve(voterId, endpoint)));
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

    /// <summary>
    /// Exposes the hosted voter's local inspection to the silo's proposer only while that voter is a member of the
    /// proposer's active configuration, so a joining voter's higher floor or a retired voter's stale state does not
    /// interfere with active proposals.
    /// </summary>
    private sealed class ActiveVoterInspection(CasPaxosConsensusHost host, LocalVoter voter) : ILocalAcceptorInspection
    {
        public ValueTask<LocalAcceptorState?> InspectAsync(string key, CancellationToken cancellationToken = default) =>
            IsActiveMember() ? voter.Inspection.InspectAsync(key, cancellationToken) : ValueTask.FromResult<LocalAcceptorState?>(null);

        public ValueTask<LocalAcceptorBallots?> InspectBallotsAsync(string key, CancellationToken cancellationToken = default) =>
            IsActiveMember() ? voter.Inspection.InspectBallotsAsync(key, cancellationToken) : ValueTask.FromResult<LocalAcceptorBallots?>(null);

        public ValueTask<LocalAcceptorPage> InspectKeysAsync(string prefix = "", string? afterKey = null, int limit = 100, CancellationToken cancellationToken = default) =>
            IsActiveMember() ? voter.Inspection.InspectKeysAsync(prefix, afterKey, limit, cancellationToken) : ValueTask.FromResult(new LocalAcceptorPage([], null));

        private bool IsActiveMember() =>
            host._registers is { } registers
            && registers.ActivePeers.Any(peer => string.Equals(peer.Id, voter.Id, StringComparison.Ordinal));
    }

    [LoggerMessage(1, LogLevel.Information, "Opened consensus voter {Voter} over {Database}, incarnation {Incarnation}; serving: {Serving}.")]
    private static partial void LogVoterOpened(ILogger logger, string voter, string database, Guid incarnation, bool serving);
}
