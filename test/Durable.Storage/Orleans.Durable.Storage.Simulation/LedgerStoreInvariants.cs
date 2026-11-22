using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Store;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Simulation;

/// <summary>
/// Checks simulated LedgerStore nodes after every step against the invariants of ledgerstore.md §5 which their
/// catalogs and ledgers show (docs/spec/verification.md §3.2).
/// </summary>
/// <remarks>
/// <para>
/// LS-I1: a replica exists on a node exactly while its catalog lists it. Every replica whose creation the store
/// acknowledged stays listed until a client requests its drop, every listed replica was requested, no replica whose
/// drop the store acknowledged is listed, and the next handle never decreases, so no handle is reused. LS-I2: the catalog
/// holds the tombstone of every drop the store acknowledged, and never loses one, across restarts. LS-I6: the node
/// identity survives restarts. LS-I5 and LR-I2: after every restart, each DATA entry the node acknowledged reads back
/// from the recovered store as itself, or as corrupt, and never as missing. LS-I11 and LR-I8: once a replica answered
/// sealed, a seal, a sealed contiguous-tail read, or a writer write answered <see cref="ReplicaAnswers.Sealed"/>, every
/// later answer which reports its seal reports it sealed, and every restart recovers it sealed, until its drop.
/// </para>
/// <para>
/// Whenever a node publishes its store, the checks project a <see cref="LedgerStoreCatalog"/> from it; a check reads
/// the latest projection and what the node's <see cref="ReplicaAudit"/> recorded. What needs every publication, such as
/// the next handle's and the tombstones' history, or the store itself, such as the reads after a restart, is observed as it
/// happens and recorded for the check to report.
/// </para>
/// <para>
/// These checks and the projection follow ledgerstore.md §5 and §7.2. A change to the replica lifecycle or the catalog
/// there changes this file, and leaves <see cref="LedgerStoreNode"/> unchanged.
/// </para>
/// </remarks>
public sealed class LedgerStoreInvariants
{
    private readonly SimulationEnvironment _environment;
    private readonly SimulatedLedgerReplicaTransport _replicas;

    private LedgerStoreInvariants(SimulationEnvironment environment, SimulatedLedgerReplicaTransport replicas)
    {
        _environment = environment;
        _replicas = replicas;
    }

    /// <summary>
    /// Gets the number of acknowledged entries read back after restarts.
    /// </summary>
    public int VerifiedEntries { get; private set; }

    /// <summary>
    /// Gets the number of acknowledged entries which read back as corrupt after restarts.
    /// </summary>
    public int CorruptEntries { get; private set; }

    /// <summary>
    /// Gets the number of restarts whose recovered stores were checked.
    /// </summary>
    public int Recoveries { get; private set; }

    /// <summary>
    /// Gets or sets a transformation of each catalog the checks project, so a self-test can break a projection and see
    /// the check which reads it fire.
    /// </summary>
    public Func<LedgerStoreCatalog, LedgerStoreCatalog>? CatalogFilter { get; set; }

    /// <summary>
    /// Adds the checks of every node <paramref name="replicas"/> reaches to <paramref name="environment"/>.
    /// </summary>
    /// <param name="environment">The simulation.</param>
    /// <param name="replicas">The transport whose ledgers record what each node acknowledged.</param>
    /// <returns>The invariants, whose counts show that the checks ran.</returns>
    public static LedgerStoreInvariants Register(SimulationEnvironment environment, SimulatedLedgerReplicaTransport replicas)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(replicas);
        var invariants = new LedgerStoreInvariants(environment, replicas);
        foreach (var node in replicas.Nodes)
        {
            var audit = replicas.Audit(node);
            var observation = new Observation();
            if (node.IsUp)
            {
                observation.Observe(invariants.Project(node, node.Store));
            }

            node.OnPublished(store => observation.Observe(invariants.Project(node, store)));
            node.OnRecovered(store => invariants.VerifyAsync(node, observation, store));
            var invariantChecks = environment.Invariants;
            invariantChecks.Add(
                Invariant($"{node.Address}: every acknowledged replica stays listed until its drop (LS-I1)"),
                () => AcknowledgedReplicasListed(observation.Catalog, audit));
            invariantChecks.Add(
                Invariant($"{node.Address}: every listed replica was requested and not dropped (LS-I1)"),
                () => ListedReplicasRequested(observation.Catalog, audit));
            invariantChecks.Add(
                Invariant($"{node.Address}: the next handle never decreases (LS-I1)"),
                () => observation.HandleViolation);
            invariantChecks.Add(
                Invariant($"{node.Address}: every acknowledged drop leaves a tombstone (LS-I2)"),
                () => AcknowledgedDropsTombstoned(observation.Catalog, audit));
            invariantChecks.Add(
                Invariant($"{node.Address}: no tombstone is ever lost (LS-I2)"),
                () => observation.TombstoneViolation);
            invariantChecks.Add(
                Invariant($"{node.Address}: the node identity survives restarts (LS-I6)"),
                () => observation.IdentityViolation);
            invariantChecks.Add(
                Invariant($"{node.Address}: every acknowledged entry survives restarts, as itself or corrupt (LS-I5, LR-I2)"),
                () => observation.DurabilityViolation);
            invariantChecks.Add(
                Invariant($"{node.Address}: a replica which answered sealed answers sealed until its drop, across restarts (LS-I11, LR-I8)"),
                () => observation.SealViolation ?? observation.ScanSealAnswers(audit));
        }

        return invariants;
    }

    private static string Invariant(FormattableString value) => FormattableString.Invariant(value);

    private static string? AcknowledgedReplicasListed(LedgerStoreCatalog? catalog, ReplicaAudit audit)
    {
        if (catalog is null)
        {
            return null;
        }

        var missing = audit.AcknowledgedCreates
            .Where(ledger => !audit.RequestedDrops.Contains(ledger) && !catalog.Replicas.Contains(ledger))
            .Order()
            .ToArray();
        return missing.Length == 0
            ? null
            : Invariant($"incarnation {catalog.Incarnation} lists [{string.Join(", ", catalog.Replicas)}], without acknowledged replicas [{string.Join(", ", missing)}]");
    }

    private static string? AcknowledgedDropsTombstoned(LedgerStoreCatalog? catalog, ReplicaAudit audit)
    {
        if (catalog is null)
        {
            return null;
        }

        var missing = audit.AcknowledgedDrops.Where(ledger => !catalog.Tombstones.Contains(ledger)).Order().ToArray();
        return missing.Length == 0
            ? null
            : Invariant($"incarnation {catalog.Incarnation} holds no tombstones of acknowledged drops [{string.Join(", ", missing)}]");
    }

    private static string? ListedReplicasRequested(LedgerStoreCatalog? catalog, ReplicaAudit audit)
    {
        if (catalog is null)
        {
            return null;
        }

        var unrequested = catalog.Replicas.Where(ledger => !audit.RequestedCreates.Contains(ledger)).ToArray();
        var dropped = catalog.Replicas.Where(audit.AcknowledgedDrops.Contains).ToArray();
        return (unrequested.Length, dropped.Length) switch
        {
            (0, 0) => null,
            (_, 0) => Invariant($"incarnation {catalog.Incarnation} lists replicas [{string.Join(", ", unrequested)}] which no client requested"),
            _ => Invariant($"incarnation {catalog.Incarnation} lists replicas [{string.Join(", ", dropped)}] whose drops it acknowledged"),
        };
    }

    /// <summary>
    /// Projects what <paramref name="store"/> shows of its catalog as <paramref name="node"/> publishes it.
    /// </summary>
    private LedgerStoreCatalog Project(LedgerStoreNode node, LedgerStore store)
    {
        var catalog = new LedgerStoreCatalog(node.Incarnation, store.NodeId, store.Replicas, [.. store.Catalog.Tombstones], store.Catalog.NextHandle);
        return CatalogFilter?.Invoke(catalog) ?? catalog;
    }

    /// <summary>
    /// Reads every DATA entry the node acknowledged from its recovered store, and the seal of every replica which answered
    /// sealed, before it serves.
    /// </summary>
    private async ValueTask VerifyAsync(LedgerStoreNode node, Observation observation, LedgerStore store)
    {
        var audit = _replicas.Audit(node);
        var listed = store.Replicas;
        var verified = 0;
        var corrupt = 0;
        foreach (var (ledger, address) in audit.AcknowledgedEntries())
        {
            // A replica whose drop may have landed reads as dropped, and one missing otherwise is LS-I1's to report.
            if (audit.RequestedDrops.Contains(ledger) || !listed.Contains(ledger))
            {
                continue;
            }

            verified++;
            await using var replica = store.OpenReplica(ledger, audit.Options(ledger));
            try
            {
                var result = await replica.ReadAsync(address, Slot.Zero, CancellationToken.None);
                result.Release();
                if (result.Case != ReplicaReadCase.Batch)
                {
                    observation.DurabilityViolation ??= Invariant(
                        $"incarnation {node.Incarnation} reads acknowledged DATA at ledger {ledger} address {address} as {result.Case}");
                }
            }
            catch (LedgerReplicaCorruptException)
            {
                // Damage faults the slots it reaches (LS-I5).
                corrupt++;
            }
        }

        _ = observation.ScanSealAnswers(audit);
        foreach (var ledger in observation.Sealed.Where(ledger => !audit.RequestedDrops.Contains(ledger) && listed.Contains(ledger)).Order())
        {
            // LS-I11: the seal is a record in the replica's log, which the recovery replays.
            await using var replica = store.OpenReplica(ledger, audit.Options(ledger));
            var tail = await replica.ReadTailAsync(Slot.Zero, seal: false, CancellationToken.None);
            if (!(tail.TryGetValue(out LedgerReplicaTail held) && held.IsSealed))
            {
                observation.SealViolation ??= Invariant($"incarnation {node.Incarnation} recovered the sealed replica of ledger {ledger} unsealed");
            }
        }

        VerifiedEntries += verified;
        CorruptEntries += corrupt;
        Recoveries++;
        _environment.Trace.Record(Invariant($"verify {node.Address}: {verified} acknowledged entries, {corrupt} corrupt, {observation.Sealed.Count} sealed replicas"));
    }

    /// <summary>
    /// What a node's publications and recoveries showed, as they happened.
    /// </summary>
    private sealed class Observation
    {
        private readonly HashSet<LedgerId> _tombstones = [];
        private readonly HashSet<LedgerId> _sealed = [];
        private NodeId? _identity;
        private uint _highestNextHandle;
        private int _sealAnswersScanned;

        public string? IdentityViolation { get; private set; }

        public string? HandleViolation { get; private set; }

        public string? TombstoneViolation { get; private set; }

        public string? DurabilityViolation { get; set; }

        public string? SealViolation { get; set; }

        public LedgerStoreCatalog? Catalog { get; private set; }

        /// <summary>
        /// Gets the ledgers whose replica answered sealed.
        /// </summary>
        public IReadOnlySet<LedgerId> Sealed => _sealed;

        /// <summary>
        /// Reads the seal answers the store gave since the last scan, in order: once a replica answered sealed, an answer
        /// which reports it unsealed violates LS-I11 (LR-I8).
        /// </summary>
        /// <returns>The violation, or <see langword="null"/>.</returns>
        public string? ScanSealAnswers(ReplicaAudit audit)
        {
            var answers = audit.SealAnswers;
            for (; _sealAnswersScanned < answers.Count; _sealAnswersScanned++)
            {
                var (ledger, isSealed, answer) = answers[_sealAnswersScanned];
                if (isSealed)
                {
                    _ = _sealed.Add(ledger);
                }
                else if (_sealed.Contains(ledger))
                {
                    SealViolation ??= Invariant($"ledger {ledger}'s replica answered its {answer} unsealed after it answered sealed");
                }
            }

            return SealViolation;
        }

        public void Observe(LedgerStoreCatalog catalog)
        {
            Catalog = catalog;
            if (_identity is null)
            {
                _identity = catalog.NodeId;
            }
            else if (catalog.NodeId != _identity)
            {
                IdentityViolation ??= Invariant($"incarnation {catalog.Incarnation} has identity {catalog.NodeId} instead of {_identity}");
            }

            if (catalog.NextHandle < _highestNextHandle)
            {
                HandleViolation ??= Invariant($"incarnation {catalog.Incarnation} reports next handle {catalog.NextHandle} after next handle {_highestNextHandle}");
            }

            _highestNextHandle = Math.Max(_highestNextHandle, catalog.NextHandle);
            var lost = _tombstones.Where(ledger => !catalog.Tombstones.Contains(ledger)).Order().ToArray();
            if (lost.Length != 0)
            {
                TombstoneViolation ??= Invariant($"incarnation {catalog.Incarnation} lost tombstones [{string.Join(", ", lost)}]");
            }

            _tombstones.UnionWith(catalog.Tombstones);
        }
    }
}

/// <summary>
/// What a node's store shows of its catalog when the node publishes the store (ledgerstore.md §6.1).
/// </summary>
/// <param name="Incarnation">The node's process, counted from its first start.</param>
/// <param name="NodeId">The node identity the catalog holds.</param>
/// <param name="Replicas">The live replicas, ascending.</param>
/// <param name="Tombstones">The dropped ledgers, ascending.</param>
/// <param name="NextHandle">The handle the next created replica takes.</param>
public sealed record LedgerStoreCatalog(
    int Incarnation, NodeId NodeId, IReadOnlyList<LedgerId> Replicas, IReadOnlyList<LedgerId> Tombstones, uint NextHandle);
