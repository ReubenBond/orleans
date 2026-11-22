namespace Orleans.Durable.Storage.Engine.Cluster.Control;

/// <summary>
/// The controllers' options (orleans.md §6): placement, balancing, budgets, timeouts, and the control plane's
/// intervals. The silo's root options hold them beside the node's own.
/// </summary>
public sealed class ControlOptions
{
    /// <summary>Gets or sets <c>N</c>, the replicas of each new ledger, an odd number (orleans.md §6).</summary>
    public int Replicas { get; set; } = 3;

    /// <summary>Gets or sets <c>K</c>, the sequencer window of each new ledger.</summary>
    public int SequencerWindow { get; set; } = 64;

    /// <summary>
    /// Gets or sets where newly opened ledgers are placed (orleans.md §5.14, §6): on nodes or in the cluster's object-ledger
    /// store. Changing it rolls open ledgers to the new location within the balancing budget. Default: <see cref="LedgerPlacement.Nodes"/>.
    /// </summary>
    public LedgerPlacement LedgerPlacement { get; set; } = LedgerPlacement.Nodes;

    /// <summary>
    /// Gets or sets <c>K</c>, the sequencer window of each new object-store ledger, or <see langword="null"/> for the
    /// layout's default (2 for per-slot, 4 for append; orleans.md §5.14, §6). Default: <see langword="null"/>.
    /// </summary>
    public int? ObjectLedgerWindow { get; set; }

    /// <summary>
    /// Gets or sets the cluster's object-store request budget, in requests per second, shared among the nodes by the
    /// controller grain for object-ledger writes, checks, recoveries, reads, drains, and archive logs when they share the
    /// account (orleans.md §5.14, §6). Default: 10,000.
    /// </summary>
    public double ObjectStoreRequestsPerSecond { get; set; } = 10_000;

    /// <summary>Gets or sets <c>G</c>, how long a <c>Dead</c> replica node keeps its place before the controller rolls it out.</summary>
    public TimeSpan ReplicaGrace { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Gets or sets the most balancing transitions in progress at once; 0 pauses balancing.</summary>
    public int MaxConcurrentTransitions { get; set; } = 8;

    /// <summary>
    /// Gets or sets the most departures in progress at once, hand-offs of a stopping or departing node's ledgers, apart from
    /// balancing so a stop never waits behind it; 0 pauses them.
    /// </summary>
    public int MaxConcurrentHandOffs { get; set; } = 32;

    /// <summary>
    /// Gets or sets the most repair rolls in progress at once, rolls out of a replica dead past <c>G</c> or retired, which
    /// carry several to a map write; 0 pauses them.
    /// </summary>
    public int MaxConcurrentRepairs { get; set; } = 64;

    /// <summary>Gets or sets how long the controller counts a sent transition in progress without its reply or a map which shows its outcome.</summary>
    public TimeSpan TransitionTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets the drain deadline sent on cooperative <c>PrepareRelease</c> requests before a planned replacement
    /// handoff, and how long the controller waits for a <c>PrepareRelease</c> reply before falling back to a
    /// non-cooperative takeover (orleans.md §5.6, §6, OR-I24). Default: 5 seconds.
    /// </summary>
    public TimeSpan PrepareReleaseTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Gets or sets the target number of open ledgers per live active node.</summary>
    public int LedgersPerNode { get; set; } = 8;

    /// <summary>Gets or sets the bytes proposed per second to a ledger above which it splits; siblings both below a quarter of it merge.</summary>
    public long SplitBytesPerSecond { get; set; } = 16L << 20;

    /// <summary>
    /// Gets or sets the window over which a node measures the bytes proposed per second to each ledger it is primary of,
    /// which it reports once the ledger's writer has been measured for a whole window (orleans.md §5.6).
    /// </summary>
    public TimeSpan LoadWindow { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Gets or sets how long a closed ledger's drain may make no progress before the next live node in its replica order
    /// drains it too.
    /// </summary>
    public TimeSpan ClosedDrainTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Gets or sets how long a compaction assignment counts as in progress before it is sent again.</summary>
    public TimeSpan CompactionTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Gets or sets the drains a node has in flight at once, over its control intervals, each scanning its ledger with the
    /// reader's read-ahead and holding its rows within the archive's drain memory, <c>ArchiveOptions.DrainMemoryBytes</c>,
    /// which the drain worker enforces; at least one. Each interval starts the due ledgers whose drains are not in flight
    /// while a slot is free: those a dead replica exposes first, closed ledgers naming a dead node, the failure-triggered
    /// drains, and on archive logs the older ledger one of them waits for, then the others, least recently started first
    /// within each. The exposed ledgers also have one slot reserved beyond these, so a node holds at most
    /// <c>MaxConcurrentDrains + 1</c> drains (orleans.md §5.6, OR-I23).
    /// </summary>
    public int MaxConcurrentDrains { get; set; } = 8;

    /// <summary>
    /// Gets or sets how often a node reconciles its replicas, reports to the controller, and drains what is due
    /// (orleans.md §5.6).
    /// </summary>
    public TimeSpan ControlInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Gets or sets the longest interval between a node's reads of the ledger map, which it skips while its reports'
    /// replies confirm its view's epoch (orleans.md §5.4).
    /// </summary>
    public TimeSpan MapRefreshInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Gets or sets how often a node sweeps its stray replicas (ledgers.md §8.5).</summary>
    public TimeSpan ReplicaSweepInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Gets or sets how often the controller runs a timed pass (orleans.md §5.6).</summary>
    public TimeSpan BalanceInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Gets or sets the shortest interval between the starts of two sweeps of the archive store, the LSM's object store or
    /// archive logs, which the sweep host on the controller grain's silo runs when the grain's passes ask (orleans.md §5.6).
    /// </summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Gets or sets the cluster's archive-log request budget, the requests a second every node's drains make together on
    /// archive logs, which the controller shares among the nodes by their undrained keys, each share fitting what the
    /// others' reservations leave of it (orleans.md §5.6, OR-I21); at least one a second, and half an Azure storage account's 20,000 a second by default.
    /// Sweeps and the Journals' own requests are outside it. Ignored on the LSM.
    /// </summary>
    public double ArchiveLogRequestsPerSecond { get; set; } = 10_000;

    /// <summary>
    /// Gets or sets the shortest interval from one manifest replace's completion to the next one's start: at least the
    /// object store's per-object write interval (archive-lsm.md §8.8).
    /// </summary>
    public TimeSpan ManifestCommitInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Throws unless the options are in range.</summary>
    /// <exception cref="ArgumentException">An option is out of range.</exception>
    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(Replicas, nameof(Replicas));
        if (Replicas % 2 == 0)
        {
            throw new ArgumentException("A ledger's replica count is odd.", nameof(Replicas));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(SequencerWindow, nameof(SequencerWindow));
        if (LedgerPlacement is not (LedgerPlacement.Nodes or LedgerPlacement.ObjectStore))
        {
            throw new ArgumentOutOfRangeException(nameof(LedgerPlacement), LedgerPlacement, "A ledger placement is Nodes or ObjectStore.");
        }

        if (ObjectLedgerWindow is { } objectWindow)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(objectWindow, nameof(ObjectLedgerWindow));
        }

        ArchiveLogRequestShare.CheckBudget(ObjectStoreRequestsPerSecond, nameof(ObjectStoreRequestsPerSecond));
        ArgumentOutOfRangeException.ThrowIfLessThan(ReplicaGrace, TimeSpan.Zero, nameof(ReplicaGrace));
        ArgumentOutOfRangeException.ThrowIfNegative(MaxConcurrentTransitions, nameof(MaxConcurrentTransitions));
        ArgumentOutOfRangeException.ThrowIfNegative(MaxConcurrentRepairs, nameof(MaxConcurrentRepairs));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(TransitionTimeout, TimeSpan.Zero, nameof(TransitionTimeout));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(PrepareReleaseTimeout, TimeSpan.Zero, nameof(PrepareReleaseTimeout));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(LedgersPerNode, nameof(LedgersPerNode));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(SplitBytesPerSecond, nameof(SplitBytesPerSecond));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(LoadWindow, TimeSpan.Zero, nameof(LoadWindow));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ClosedDrainTimeout, TimeSpan.Zero, nameof(ClosedDrainTimeout));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(CompactionTimeout, TimeSpan.Zero, nameof(CompactionTimeout));
        ArgumentOutOfRangeException.ThrowIfNegative(MaxConcurrentHandOffs, nameof(MaxConcurrentHandOffs));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxConcurrentDrains, nameof(MaxConcurrentDrains));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ControlInterval, TimeSpan.Zero, nameof(ControlInterval));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(MapRefreshInterval, TimeSpan.Zero, nameof(MapRefreshInterval));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ReplicaSweepInterval, TimeSpan.Zero, nameof(ReplicaSweepInterval));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(BalanceInterval, TimeSpan.Zero, nameof(BalanceInterval));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(SweepInterval, TimeSpan.Zero, nameof(SweepInterval));
        ArgumentOutOfRangeException.ThrowIfLessThan(ManifestCommitInterval, TimeSpan.Zero, nameof(ManifestCommitInterval));
        ArchiveLogRequestShare.CheckBudget(ArchiveLogRequestsPerSecond, nameof(ArchiveLogRequestsPerSecond));
    }

    /// <summary>Returns the configured <c>K</c> for object-store ledgers of <paramref name="layout"/>: <see cref="ObjectLedgerWindow"/> or the layout's default (2 for per-slot, 4 for append).</summary>
    internal int EffectiveObjectLedgerWindow(Ledgers.Objects.ObjectLedgerLayout layout) =>
        ObjectLedgerWindow ?? (layout == Ledgers.Objects.ObjectLedgerLayout.Append ? 4 : 2);
}
