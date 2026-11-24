using Orleans.Consensus.CasPaxos.Messages;
using Orleans.Consensus.CasPaxos.Model;
using Orleans.Consensus.CasPaxos.Protocol;
using Orleans.Consensus.CasPaxos.Server;
using Orleans.Consensus.CasPaxos.Sqlite;

namespace Orleans.Durable.Storage.CasPaxos.Hosting;

/// <summary>
/// The voter this silo hosts: its durable SQLite acceptor, bound to the voter's identity, which answers protocol
/// requests once its incarnation is admitted (<see cref="SqliteVoterRegistry"/>), and the admissions it grants other
/// voters, which it answers from its open.
/// </summary>
/// <remarks>
/// Opening recovers the acceptor's committed state, initializes the join floor for voters introduced in configuration
/// <c>c_join &gt; 1</c>, and validates its identity and bootstrap digest binding before anything is answered: a database
/// bound to another voter, holding acceptor state without a binding, or disagreeing on its bootstrap digest fails the
/// open. Until its incarnation is admitted, and after it stops, every protocol request fails with
/// <see cref="CasPaxosVoterUnavailableException"/>, which a proposer counts as a missing reply.
/// </remarks>
internal sealed class LocalVoter : IKeyedAcceptor, IAsyncDisposable
{
    private readonly SqliteAcceptorServer _acceptor;
    private readonly SqliteVoterRegistry _registry;
    private volatile bool _serving;

    private LocalVoter(string id, SqliteAcceptorServer acceptor, SqliteVoterRegistry registry)
    {
        Id = id;
        _acceptor = acceptor;
        _registry = registry;
        _serving = registry.IsAdmitted;
    }

    /// <summary>Gets the voter's identity.</summary>
    public string Id { get; }

    /// <summary>Gets the incarnation of the voter's database binding, which other voters admit.</summary>
    public Guid Incarnation => _registry.Incarnation;

    /// <summary>Gets the 1-based configuration number in which this voter was first introduced.</summary>
    public long JoinConfiguration => _registry.JoinConfiguration;

    /// <summary>Gets the optional SHA-256 digest of the canonical bootstrap configuration <c>V(1)</c>.</summary>
    public ByteString? BootstrapDigest => _registry.BootstrapDigest;

    /// <summary>Gets a value indicating whether the voter answers protocol requests.</summary>
    public bool IsServing => _serving;

    /// <summary>Gets the acceptor's local inspection, which the silo's proposer reads its proposal floors from.</summary>
    public ILocalAcceptorInspection Inspection => _acceptor;

    /// <summary>
    /// Opens the voter <paramref name="id"/> over <paramref name="database"/>: creates the database's directory under its
    /// existing parent, recovers the acceptor, initializes its join floor when <paramref name="joinConfiguration"/> &gt; 1,
    /// and validates its binding.
    /// </summary>
    public static async Task<LocalVoter> OpenAsync(
        string id,
        string database,
        int queueCapacity,
        int maxBatchSize,
        IFatalErrorHandler fatalErrors,
        long joinConfiguration = 1,
        ByteString? bootstrapDigest = null,
        CancellationToken cancellationToken = default)
    {
        var path = Path.GetFullPath(database);
        DurableDirectory.Create(Path.GetDirectoryName(path)!, fatalErrors);
        var acceptor = await SqliteAcceptorServer.OpenAsync(path, fatalErrors, queueCapacity, maxBatchSize, cancellationToken: cancellationToken).ConfigureAwait(false);
        try
        {
            var registry = await SqliteVoterRegistry.OpenAsync(acceptor, id, joinConfiguration, bootstrapDigest, cancellationToken).ConfigureAwait(false);
            return new LocalVoter(id, acceptor, registry);
        }
        catch
        {
            await acceptor.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Durably records the voter's admission, after which it answers protocol requests.</summary>
    public async Task AdmittedAsync(CancellationToken cancellationToken)
    {
        await _registry.MarkAdmittedAsync(cancellationToken).ConfigureAwait(false);
        _serving = true;
    }

    /// <summary>
    /// Admits <paramref name="incarnation"/> of the other voter <paramref name="candidate"/> unless this voter admitted
    /// another incarnation of it, validating <paramref name="bootstrapDigest"/> when both sides supply one and answering
    /// with the incarnation it admits.
    /// </summary>
    public ValueTask<Guid> AdmitAsync(string candidate, Guid incarnation, ByteString? bootstrapDigest, CancellationToken cancellationToken) =>
        _registry.AdmitAsync(candidate, incarnation, bootstrapDigest, cancellationToken);

    /// <inheritdoc/>
    public ValueTask<AcceptorReadResult> ReadAsync(string key, AcceptedDigest? known = null, CancellationToken cancellationToken = default)
    {
        ThrowIfNotServing();
        return _acceptor.ReadAsync(key, known, cancellationToken);
    }

    /// <inheritdoc/>
    public ValueTask<AcceptorPrepareResult<ByteString?>> PrepareAsync(string key, PrepareRequest request, CancellationToken cancellationToken = default)
    {
        ThrowIfNotServing();
        return _acceptor.PrepareAsync(key, request, cancellationToken);
    }

    /// <inheritdoc/>
    public ValueTask<AcceptorAcceptResult> AcceptAsync(string key, AcceptRequest<ByteString> request, CancellationToken cancellationToken = default)
    {
        ThrowIfNotServing();
        return _acceptor.AcceptAsync(key, request, cancellationToken);
    }

    /// <inheritdoc/>
    public ValueTask<Ballot> SetFloorAsync(Ballot targetFloor, CancellationToken cancellationToken = default)
    {
        ThrowIfNotServing();
        return _acceptor.SetFloorAsync(targetFloor, cancellationToken);
    }

    /// <inheritdoc/>
    public ValueTask<KeyPage> ListKeysAsync(string prefix = "", string? afterKey = null, int limit = 100, CancellationToken cancellationToken = default)
    {
        ThrowIfNotServing();
        return _acceptor.ListKeysAsync(prefix, afterKey, limit, cancellationToken);
    }

    /// <summary>Stops answering, drains the acceptor's admitted requests, and closes its database.</summary>
    public ValueTask DisposeAsync()
    {
        _serving = false;
        return _acceptor.DisposeAsync();
    }

    private void ThrowIfNotServing()
    {
        if (!_serving)
        {
            throw new CasPaxosVoterUnavailableException($"Voter '{Id}' is not serving: its incarnation is not admitted, or it stopped.");
        }
    }
}

/// <summary>A voter could not be reached or does not serve, so its reply does not count toward a quorum.</summary>
internal sealed class CasPaxosVoterUnavailableException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="CasPaxosVoterUnavailableException"/> class.</summary>
    public CasPaxosVoterUnavailableException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="CasPaxosVoterUnavailableException"/> class.</summary>
    /// <param name="message">The message.</param>
    public CasPaxosVoterUnavailableException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="CasPaxosVoterUnavailableException"/> class.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public CasPaxosVoterUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
