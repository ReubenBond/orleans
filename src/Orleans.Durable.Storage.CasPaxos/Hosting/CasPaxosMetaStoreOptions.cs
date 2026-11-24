using System.Net;
using Microsoft.Extensions.Options;

namespace Orleans.Durable.Storage.CasPaxos.Hosting;

/// <summary>
/// The native Fast CASPaxos MetaStore of a silo: the bootstrap (or seed) voter configuration, an optional voter joining
/// in a later configuration, and this silo's proposer (caspaxos.md).
/// </summary>
/// <remarks>
/// <para>
/// At bootstrap (<see cref="InitialConfiguration"/> equal to <c>1</c>), every silo configures the identical
/// <see cref="Voters"/> set, which defines configuration <c>1</c>; bootstrap voters validate agreement on its canonical
/// digest during admission before serving. After the first reconfiguration, the reserved configuration register
/// (<c>__caspaxos/config</c>) is authoritative and proposers discover subsequent configurations automatically.
/// </para>
/// <para>
/// When a silo introduces a brand-new voter in configuration <c>c_join &gt; 1</c> (replacing a lost voter or expanding
/// the voter set), configure <see cref="JoiningVoter"/> on that silo while keeping <see cref="Voters"/> at the active
/// bootstrap or seed configuration. The silo initializes the new voter's durable floor to <c>(c_join, 0, 0)</c> and
/// obtains majority admission from <see cref="CasPaxosJoiningVoterOptions.AdmissionPeers"/> before answering protocol
/// requests.
/// </para>
/// <para>
/// Every silo runs a proposer whose <see cref="ProposerId"/> is unique in the cluster for the lifetime of its
/// <see cref="ProposerDatabase"/>.
/// </para>
/// </remarks>
public sealed class CasPaxosMetaStoreOptions
{
    /// <summary>
    /// Gets the cluster's bootstrap voters (for <see cref="InitialConfiguration"/> equal to <c>1</c>), or discovery seed
    /// voters for <see cref="InitialConfiguration"/> greater than <c>1</c> after bootstrap voters have been retired.
    /// </summary>
    public IList<CasPaxosVoterOptions> Voters { get; } = [];

    /// <summary>
    /// Gets or sets the 1-based configuration number of <see cref="Voters"/>. Defaults to <c>1</c> (bootstrap).
    /// Set greater than <c>1</c> only after earlier bootstrap voters have been retired and <see cref="Voters"/> names an
    /// already-active configuration's discovery seeds; startup verifies that configuration is active in <c>__caspaxos/config</c>.
    /// </summary>
    public long InitialConfiguration { get; set; } = 1;

    /// <summary>
    /// Gets or sets the configuration of a voter first introduced on this silo in configuration <c>c_join &gt; 1</c>
    /// (for replacing a lost voter or adding a voter). Leave <see langword="null"/> for bootstrap voters and non-voter silos.
    /// </summary>
    public CasPaxosJoiningVoterOptions? JoiningVoter { get; set; }

    /// <summary>Gets or sets this silo's proposer identity: positive, and unique in the cluster.</summary>
    public int ProposerId { get; set; }

    /// <summary>Gets or sets the SQLite database holding this proposer's durable ballot reservations.</summary>
    public string ProposerDatabase { get; set; } = string.Empty;

    /// <summary>Gets or sets the deadline of one register operation, retries included.</summary>
    public TimeSpan OperationTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Gets or sets the deadline of one protocol phase's round of voter calls.</summary>
    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Gets or sets the delay before a contended operation's next phase.</summary>
    public TimeSpan RetryBackoff { get; set; } = TimeSpan.FromMilliseconds(10);

    /// <summary>Gets or sets the most register operations the proposer admits at once; one more fails as busy.</summary>
    public int Capacity { get; set; } = 256;

    /// <summary>Gets or sets the most requests the hosted voter's SQLite writer queues.</summary>
    public int VoterQueueCapacity { get; set; } = 256;

    /// <summary>Gets or sets the most requests the hosted voter commits in one transaction.</summary>
    public int VoterMaxBatchSize { get; set; } = 64;

    /// <summary>Gets or sets the longest delay between a fresh voter's admission attempts.</summary>
    public TimeSpan AdmissionRetryDelay { get; set; } = TimeSpan.FromSeconds(1);
}

/// <summary>One voter entry in <see cref="CasPaxosMetaStoreOptions.Voters"/>.</summary>
public sealed class CasPaxosVoterOptions
{
    /// <summary>Gets or sets the voter's durable identity, which its database is bound to.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the advertised silo endpoint, <c>address:port</c>, of the silo which serves the voter; a restart at
    /// the same endpoint serves the same voter with a new silo generation.
    /// </summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>Gets or sets the voter's SQLite acceptor database, on the silo which serves it.</summary>
    public string Database { get; set; } = string.Empty;
}

/// <summary>A remote voter identity and endpoint, used as an admission peer for a joining voter.</summary>
public sealed class CasPaxosVoterPeerOptions
{
    /// <summary>Gets or sets the voter's durable identity.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the advertised silo endpoint, <c>address:port</c>, of the silo which serves the voter.</summary>
    public string Endpoint { get; set; } = string.Empty;
}

/// <summary>
/// Configures a voter first introduced on this silo in configuration <see cref="JoinConfiguration"/> (<c>&gt;= 2</c>).
/// </summary>
public sealed class CasPaxosJoiningVoterOptions
{
    /// <summary>Gets or sets the new voter's fresh, never-previously-used durable identity.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the new voter's SQLite acceptor database path.</summary>
    public string Database { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the 1-based configuration number (<c>&gt;= 2</c>) in which this voter joins; its acceptor floor is
    /// durably initialized to <c>(JoinConfiguration, 0, 0)</c> before answering any protocol request.
    /// </summary>
    public long JoinConfiguration { get; set; } = 2;

    /// <summary>
    /// Gets the voters of the target configuration <c>V(JoinConfiguration)</c> (excluding or including this voter) from
    /// which a majority admits this voter's fresh incarnation before it answers protocol requests.
    /// </summary>
    public IList<CasPaxosVoterPeerOptions> AdmissionPeers { get; } = [];
}

/// <summary>Validates <see cref="CasPaxosMetaStoreOptions"/> as the silo starts.</summary>
internal sealed class CasPaxosMetaStoreOptionsValidator : IValidateOptions<CasPaxosMetaStoreOptions>
{
    /// <inheritdoc/>
    public ValidateOptionsResult Validate(string? name, CasPaxosMetaStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        if (options.InitialConfiguration < 1)
        {
            failures.Add("InitialConfiguration must be at least 1.");
        }

        if (options.Voters.Count == 0)
        {
            failures.Add("Voters must name at least one voter.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var endpoints = new HashSet<IPEndPoint>();
        foreach (var voter in options.Voters)
        {
            if (string.IsNullOrEmpty(voter.Id) || !ids.Add(voter.Id))
            {
                failures.Add($"Voter identity '{voter.Id}' must be nonempty and unique.");
            }

            if (!IPEndPoint.TryParse(voter.Endpoint, out var endpoint) || endpoint.Port == 0)
            {
                failures.Add($"Voter '{voter.Id}' needs an endpoint of the form address:port, not '{voter.Endpoint}'.");
            }
            else if (!endpoints.Add(endpoint))
            {
                failures.Add($"Voter '{voter.Id}' shares its endpoint {endpoint} with another voter.");
            }

            if (string.IsNullOrWhiteSpace(voter.Database))
            {
                failures.Add($"Voter '{voter.Id}' needs a database.");
            }
        }

        if (options.JoiningVoter is { } joining)
        {
            if (string.IsNullOrEmpty(joining.Id))
            {
                failures.Add("JoiningVoter.Id must be nonempty.");
            }
            else if (options.InitialConfiguration == 1 && ids.Contains(joining.Id))
            {
                failures.Add($"JoiningVoter.Id '{joining.Id}' must be a fresh identity distinct from bootstrap Voters.");
            }

            if (string.IsNullOrWhiteSpace(joining.Database))
            {
                failures.Add($"JoiningVoter '{joining.Id}' needs a database.");
            }

            if (joining.JoinConfiguration < 2)
            {
                failures.Add("JoiningVoter.JoinConfiguration must be at least 2.");
            }

            var peerIds = new HashSet<string>(StringComparer.Ordinal);
            var peerEndpoints = new HashSet<IPEndPoint>();
            foreach (var peer in joining.AdmissionPeers)
            {
                if (string.IsNullOrEmpty(peer.Id) || !peerIds.Add(peer.Id))
                {
                    failures.Add($"JoiningVoter admission peer identity '{peer.Id}' must be nonempty and unique.");
                }

                if (!IPEndPoint.TryParse(peer.Endpoint, out var peerEndpoint) || peerEndpoint.Port == 0)
                {
                    failures.Add($"JoiningVoter admission peer '{peer.Id}' needs an endpoint of the form address:port, not '{peer.Endpoint}'.");
                }
                else if (!peerEndpoints.Add(peerEndpoint))
                {
                    failures.Add($"JoiningVoter admission peer '{peer.Id}' shares its endpoint {peerEndpoint} with another admission peer.");
                }
            }
        }

        if (options.ProposerId <= 0)
        {
            failures.Add("ProposerId must be positive.");
        }

        if (string.IsNullOrWhiteSpace(options.ProposerDatabase))
        {
            failures.Add("ProposerDatabase must name the proposer's ballot database.");
        }

        Positive(failures, options.OperationTimeout, nameof(options.OperationTimeout));
        Positive(failures, options.AttemptTimeout, nameof(options.AttemptTimeout));
        Positive(failures, options.RetryBackoff, nameof(options.RetryBackoff));
        Positive(failures, options.AdmissionRetryDelay, nameof(options.AdmissionRetryDelay));
        if (options.Capacity <= 0 || options.VoterQueueCapacity <= 0 || options.VoterMaxBatchSize <= 0)
        {
            failures.Add("Capacity, VoterQueueCapacity, and VoterMaxBatchSize must be positive.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void Positive(List<string> failures, TimeSpan value, string name)
    {
        if (value <= TimeSpan.Zero)
        {
            failures.Add($"{name} must be positive.");
        }
    }
}
