using System.Net;
using Microsoft.Extensions.Options;

namespace Orleans.Durable.Storage.CasPaxos.Hosting;

/// <summary>
/// The native Fast CASPaxos MetaStore of a silo: the cluster's fixed voters, each served by the silo at its endpoint, and
/// this silo's proposer (caspaxos.md).
/// </summary>
/// <remarks>
/// Every silo configures the same <see cref="Voters"/>. The silo whose advertised silo endpoint is a voter's
/// <see cref="CasPaxosVoterOptions.Endpoint"/> hosts that voter over its <see cref="CasPaxosVoterOptions.Database"/>.
/// Every silo runs a proposer, whose <see cref="ProposerId"/> is unique in the cluster for the lifetime of its
/// <see cref="ProposerDatabase"/>.
/// </remarks>
public sealed class CasPaxosMetaStoreOptions
{
    /// <summary>Gets the cluster's fixed voters, identical on every silo.</summary>
    public IList<CasPaxosVoterOptions> Voters { get; } = [];

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

/// <summary>One fixed voter of the native Fast CASPaxos MetaStore.</summary>
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

/// <summary>Validates <see cref="CasPaxosMetaStoreOptions"/> as the silo starts.</summary>
internal sealed class CasPaxosMetaStoreOptionsValidator : IValidateOptions<CasPaxosMetaStoreOptions>
{
    /// <inheritdoc/>
    public ValidateOptionsResult Validate(string? name, CasPaxosMetaStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
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
