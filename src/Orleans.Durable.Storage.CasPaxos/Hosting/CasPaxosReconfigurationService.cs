using System.Collections.Immutable;
using System.Net;
using Orleans.Consensus.CasPaxos.Server;

namespace Orleans.Durable.Storage.CasPaxos.Hosting;

/// <summary>
/// Operator-driven reconfiguration service for the native Fast CASPaxos MetaStore voter set (caspaxos.md).
/// </summary>
/// <remarks>
/// <para>
/// Executes the four-step idempotent, resumable reconfiguration procedure (Choose → Fence <c>c</c> → Install → Activate)
/// over Orleans RPC using <see cref="VoterReconfigurationDriver"/>. Replacing a lost voter, adding a voter, removing a
/// voter, or migrating to a new voter set advances the authoritative configuration register (<c>__caspaxos/config</c>)
/// from configuration <c>c</c> to <c>c + 1</c>.
/// </para>
/// <para>
/// Any silo registered with <see cref="CasPaxosSiloBuilderExtensions.AddCasPaxosMetaStore(Orleans.Hosting.ISiloBuilder, Action{Microsoft.Extensions.Options.OptionsBuilder{CasPaxosMetaStoreOptions}})"/>
/// resolves this service from dependency injection.
/// </para>
/// </remarks>
public sealed class CasPaxosReconfigurationService
{
    private readonly CasPaxosConsensusHost _host;

    internal CasPaxosReconfigurationService(CasPaxosConsensusHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
    }

    /// <summary>
    /// Discovers and returns the latest reachable voter configuration from the cluster via ballot-free
    /// <c>Read(__caspaxos/config)</c>.
    /// </summary>
    public async Task<VoterConfiguration> GetCurrentConfigurationAsync(CancellationToken cancellationToken = default)
    {
        var (seedConfig, seedVoters) = _host.GetSeedConfiguration();
        var reconfigurator = _host.CreateReconfigurator();
        var discovered = await reconfigurator.DiscoverConfigurationAsync(seedVoters, seedConfig, cancellationToken).ConfigureAwait(false);
        _host.RememberEndpoints(discovered.Voters);
        return discovered;
    }

    /// <summary>
    /// Reconfigures the voter set to <paramref name="nextVoters"/> (Choose → Fence <c>c</c> → Install → Activate).
    /// Idempotent and resumable: if a pending reconfiguration with the same target voters is already in progress,
    /// resumes and completes it; if a pending reconfiguration targets a different voter set, throws
    /// <see cref="ReconfigurationConflictException"/>.
    /// </summary>
    public async Task<VoterConfiguration> ReconfigureAsync(IReadOnlyList<VoterDescriptor> nextVoters, CancellationToken cancellationToken = default)
    {
        var desired = ValidateNextVoters(nextVoters);
        var (seedConfig, seedVoters) = _host.GetSeedConfiguration();
        var reconfigurator = _host.CreateReconfigurator();
        var current = await reconfigurator.DiscoverConfigurationAsync(seedVoters, seedConfig, cancellationToken).ConfigureAwait(false);
        _host.RememberEndpoints(current.Voters);

        if (current.State == VoterConfigurationState.Pending)
        {
            if (!current.HasSameVoters(desired))
            {
                throw new ReconfigurationConflictException(current);
            }

            var resumed = await reconfigurator.ResumeAsync(seedVoters, seedConfig, cancellationToken).ConfigureAwait(false);
            _host.RememberEndpoints(resumed.Voters);
            return resumed;
        }

        _host.RememberEndpoints(desired);
        var active = await reconfigurator.ReconfigureAsync(current.CanonicalVoters, current.Number, desired, cancellationToken).ConfigureAwait(false);
        _host.RememberEndpoints(active.Voters);
        return active;
    }

    /// <summary>
    /// Replaces <paramref name="lostVoterId"/> with <paramref name="replacement"/> (which must carry a fresh,
    /// never-previously-used voter identity) and activates configuration <c>c + 1</c>.
    /// </summary>
    /// <remarks>
    /// The retired <paramref name="lostVoterId"/> is permanently removed from <c>c + 1</c> and refused by surviving
    /// voters' admission records if revived on a fresh database.
    /// </remarks>
    public async Task<VoterConfiguration> ReplaceVoterAsync(
        string lostVoterId,
        VoterDescriptor replacement,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(lostVoterId);
        ValidateVoterDescriptor(replacement, nameof(replacement));
        if (string.Equals(lostVoterId, replacement.Id, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Replacement voter identity '{replacement.Id}' must differ from retired voter identity '{lostVoterId}'.",
                nameof(replacement));
        }

        var (seedConfig, seedVoters) = _host.GetSeedConfiguration();
        var reconfigurator = _host.CreateReconfigurator();
        var current = await reconfigurator.DiscoverConfigurationAsync(seedVoters, seedConfig, cancellationToken).ConfigureAwait(false);
        _host.RememberEndpoints(current.Voters);

        if (current.State == VoterConfigurationState.Pending)
        {
            var matchesReplacement = current.Voters.Any(v =>
                    string.Equals(v.Id, replacement.Id, StringComparison.Ordinal)
                    && string.Equals(v.Endpoint, replacement.Endpoint, StringComparison.Ordinal))
                && !current.Voters.Any(v => string.Equals(v.Id, lostVoterId, StringComparison.Ordinal));
            if (!matchesReplacement)
            {
                throw new ReconfigurationConflictException(current);
            }

            var resumed = await reconfigurator.ResumeAsync(seedVoters, seedConfig, cancellationToken).ConfigureAwait(false);
            _host.RememberEndpoints(resumed.Voters);
            return resumed;
        }

        if (!current.Voters.Any(v => string.Equals(v.Id, lostVoterId, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"Voter '{lostVoterId}' is not a member of active configuration {current.Number}.");
        }

        if (current.Voters.Any(v => string.Equals(v.Id, replacement.Id, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"Replacement voter '{replacement.Id}' is already a member of active configuration {current.Number}.");
        }

        ImmutableArray<VoterDescriptor> nextVoters =
        [
            .. current.Voters.Where(v => !string.Equals(v.Id, lostVoterId, StringComparison.Ordinal)),
            replacement,
        ];
        _ = ValidateNextVoters(nextVoters);
        _host.RememberEndpoints(nextVoters);
        var active = await reconfigurator.ReconfigureAsync(current.CanonicalVoters, current.Number, nextVoters, cancellationToken).ConfigureAwait(false);
        _host.RememberEndpoints(active.Voters);
        return active;
    }

    /// <summary>Adds <paramref name="voter"/> to the current voter set and activates configuration <c>c + 1</c>.</summary>
    public async Task<VoterConfiguration> AddVoterAsync(VoterDescriptor voter, CancellationToken cancellationToken = default)
    {
        ValidateVoterDescriptor(voter, nameof(voter));
        var (seedConfig, seedVoters) = _host.GetSeedConfiguration();
        var reconfigurator = _host.CreateReconfigurator();
        var current = await reconfigurator.DiscoverConfigurationAsync(seedVoters, seedConfig, cancellationToken).ConfigureAwait(false);
        _host.RememberEndpoints(current.Voters);

        if (current.State == VoterConfigurationState.Pending)
        {
            var matchesAddition = current.Voters.Any(v =>
                string.Equals(v.Id, voter.Id, StringComparison.Ordinal)
                && string.Equals(v.Endpoint, voter.Endpoint, StringComparison.Ordinal));
            if (!matchesAddition)
            {
                throw new ReconfigurationConflictException(current);
            }

            var resumed = await reconfigurator.ResumeAsync(seedVoters, seedConfig, cancellationToken).ConfigureAwait(false);
            _host.RememberEndpoints(resumed.Voters);
            return resumed;
        }

        if (current.Voters.Any(v => string.Equals(v.Id, voter.Id, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"Voter '{voter.Id}' is already a member of active configuration {current.Number}.");
        }

        ImmutableArray<VoterDescriptor> nextVoters = [.. current.Voters, voter];
        _ = ValidateNextVoters(nextVoters);
        _host.RememberEndpoints(nextVoters);
        var active = await reconfigurator.ReconfigureAsync(current.CanonicalVoters, current.Number, nextVoters, cancellationToken).ConfigureAwait(false);
        _host.RememberEndpoints(active.Voters);
        return active;
    }

    /// <summary>Removes <paramref name="voterId"/> from the current voter set and activates configuration <c>c + 1</c>.</summary>
    public async Task<VoterConfiguration> RemoveVoterAsync(string voterId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(voterId);
        var (seedConfig, seedVoters) = _host.GetSeedConfiguration();
        var reconfigurator = _host.CreateReconfigurator();
        var current = await reconfigurator.DiscoverConfigurationAsync(seedVoters, seedConfig, cancellationToken).ConfigureAwait(false);
        _host.RememberEndpoints(current.Voters);

        if (current.State == VoterConfigurationState.Pending)
        {
            if (current.Voters.Any(v => string.Equals(v.Id, voterId, StringComparison.Ordinal)))
            {
                throw new ReconfigurationConflictException(current);
            }

            var resumed = await reconfigurator.ResumeAsync(seedVoters, seedConfig, cancellationToken).ConfigureAwait(false);
            _host.RememberEndpoints(resumed.Voters);
            return resumed;
        }

        if (!current.Voters.Any(v => string.Equals(v.Id, voterId, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"Voter '{voterId}' is not a member of active configuration {current.Number}.");
        }

        if (current.Voters.Length <= 1)
        {
            throw new InvalidOperationException($"Cannot remove the last voter '{voterId}' from active configuration {current.Number}.");
        }

        ImmutableArray<VoterDescriptor> nextVoters = [.. current.Voters.Where(v => !string.Equals(v.Id, voterId, StringComparison.Ordinal))];
        var active = await reconfigurator.ReconfigureAsync(current.CanonicalVoters, current.Number, nextVoters, cancellationToken).ConfigureAwait(false);
        _host.RememberEndpoints(active.Voters);
        return active;
    }

    /// <summary>
    /// Resumes any interrupted pending reconfiguration from Step 2 (Fence <c>c</c>) through Step 3 (Install) and
    /// Step 4 (Activate), or returns the current active configuration when no reconfiguration is pending.
    /// </summary>
    public async Task<VoterConfiguration> ResumeAsync(CancellationToken cancellationToken = default)
    {
        var (seedConfig, seedVoters) = _host.GetSeedConfiguration();
        var reconfigurator = _host.CreateReconfigurator();
        var active = await reconfigurator.ResumeAsync(seedVoters, seedConfig, cancellationToken).ConfigureAwait(false);
        _host.RememberEndpoints(active.Voters);
        return active;
    }

    private static ImmutableArray<VoterDescriptor> ValidateNextVoters(IReadOnlyList<VoterDescriptor> nextVoters)
    {
        ArgumentNullException.ThrowIfNull(nextVoters);
        if (nextVoters.Count == 0)
        {
            throw new ArgumentException("Next voter set must contain at least one voter.", nameof(nextVoters));
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var endpoints = new HashSet<IPEndPoint>();
        var builder = ImmutableArray.CreateBuilder<VoterDescriptor>(nextVoters.Count);
        foreach (var voter in nextVoters)
        {
            ValidateVoterDescriptor(voter, nameof(nextVoters));
            if (!ids.Add(voter.Id))
            {
                throw new ArgumentException($"Duplicate voter identity '{voter.Id}' in target voter set.", nameof(nextVoters));
            }

            var endpoint = IPEndPoint.Parse(voter.Endpoint);
            if (!endpoints.Add(endpoint))
            {
                throw new ArgumentException($"Voter '{voter.Id}' shares its endpoint {endpoint} with another voter.", nameof(nextVoters));
            }

            builder.Add(voter);
        }

        return builder.MoveToImmutable();
    }

    private static void ValidateVoterDescriptor(VoterDescriptor voter, string paramName)
    {
        ArgumentNullException.ThrowIfNull(voter, paramName);
        if (string.IsNullOrEmpty(voter.Id))
        {
            throw new ArgumentException("Voter identity must be nonempty.", paramName);
        }

        if (!IPEndPoint.TryParse(voter.Endpoint, out var endpoint) || endpoint.Port == 0)
        {
            throw new ArgumentException($"Voter '{voter.Id}' needs an endpoint of the form address:port, not '{voter.Endpoint}'.", paramName);
        }
    }
}
