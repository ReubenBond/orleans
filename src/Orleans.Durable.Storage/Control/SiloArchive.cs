using Microsoft.Extensions.Logging;
using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Archive.Logs;
using Orleans.Durable.Storage.Engine.Archive.Lsm;
using Orleans.Durable.Storage.Engine.MetaStore;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Polly;

namespace Orleans.Durable.Storage.Control;

/// <summary>
/// The silo's archive store as its configuration selects it (orleans.md §5.13, §6, OR-I20), one of two cases:
/// <see cref="Lsm"/>, over the object store the silo's services supply, or <see cref="Logs"/>, over the cluster's container,
/// whose two stores, the logs and durable storage's objects under <c>durable-storage/</c>, lie in one container. Each case makes its first
/// registration's record, checks the record, and builds its sweeps; consumers switch on the case exhaustively. The silo's
/// archive gate, which the background check of the store's record passes or refuses, holds the evidence by which archive
/// work chooses its kind. The memory store, which only tests run, is no silo's.
/// </summary>
/// <remarks>
/// A closed class hierarchy rather than a <c>union</c> declaration: the silo's services hold it as a singleton, which a
/// union's value is not, and its cases carry behaviour. The constructor is private, so the two nested cases are the only
/// ones.
/// </remarks>
internal abstract class SiloArchive
{
    private SiloArchive(ArchiveStorePrefix prefix)
    {
        Prefix = prefix.Value is null ? throw new ArgumentException("The default ArchiveStorePrefix is no store prefix.", nameof(prefix)) : prefix;
    }

    /// <summary>Gets the kind of store the silo is configured with.</summary>
    public abstract ArchiveStoreKind Kind { get; }

    /// <summary>
    /// Gets the cluster's store prefix, under which the silo's store lies in its bucket or container (orleans.md §5.13),
    /// which the node registry records with the store's kind and ID.
    /// </summary>
    public ArchiveStorePrefix Prefix { get; }

    /// <summary>
    /// Gets the objects a first registration's record call creates or adopts in the store, which stay there when the
    /// registration then loses to another store's (orleans.md §5.3, §5.13): named for an operator who cleans them up.
    /// </summary>
    public abstract string FirstRecordObjects { get; }

    /// <summary>Gets the silo's archive gate, which every service that reads or writes the archive waits for.</summary>
    public ArchiveGate Gate { get; } = new();

    private int _controllerRefusalReported;

    /// <summary>
    /// Returns whether this call is the silo's first report that a controller grain activation found its gate refused: the
    /// silo reports the refusal as critical once, however often the grain activates there and deactivates again, since the
    /// refusal holds for the runtime's life and the sweep host and the record check report it too.
    /// </summary>
    public bool ReportsControllerRefusal() => Interlocked.Exchange(ref _controllerRefusalReported, 1) == 0;

    /// <summary>
    /// Answers the identity of the store's record at a new cluster's first registration, whose registry records none,
    /// creating or adopting the record (orleans.md §5.3, §5.13).
    /// </summary>
    /// <param name="parameters">The caller parameters a created record holds.</param>
    /// <param name="cancellationToken">Cancels the calls.</param>
    /// <returns>The identity the registration records.</returns>
    public abstract ValueTask<ArchiveStoreIdentity> FirstRecordAsync(IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken);

    /// <summary>
    /// Checks the store's record against <paramref name="identity"/>, the one the node registry records, which the node
    /// registered with, and passes the gate once the store is the registry's (OR-I20). First the store's kind and its
    /// prefix, which need no request; then the case's own checks, each call of which <paramref name="retry"/> repeats while its outcome is
    /// unknown.
    /// </summary>
    /// <param name="identity">The archive store the node registry records.</param>
    /// <param name="parameters">The caller parameters of the process.</param>
    /// <param name="retry">Repeats a call whose outcome is unknown.</param>
    /// <param name="cancellationToken">Stops the check.</param>
    /// <exception cref="ArchiveStoreMismatchException">The store is another than the registry's, or its record holds other fields.</exception>
    /// <exception cref="ArchiveCorruptException">The record is corrupt.</exception>
    public async Task CheckAsync(ArchiveStoreIdentity identity, IReadOnlyDictionary<string, string> parameters, ResiliencePipeline retry, CancellationToken cancellationToken)
    {
        // Checked before any request: the registry names another kind of store, or a store under another prefix, which this
        // silo must not touch.
        identity.CheckKind(Kind);
        identity.CheckPrefix(Prefix);
        await CheckRecordAsync(identity, parameters, retry, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The case's check of the store's record, once its kind is the registry's.</summary>
    private protected abstract Task CheckRecordAsync(ArchiveStoreIdentity identity, IReadOnlyDictionary<string, string> parameters, ResiliencePipeline retry, CancellationToken cancellationToken);

    /// <summary>The LSM over its object store, under the cluster's store prefix.</summary>
    /// <param name="objects">The LSM's object store, under the prefix.</param>
    /// <param name="prefix">The cluster's store prefix.</param>
    public sealed class Lsm(IObjectStore objects, ArchiveStorePrefix prefix) : SiloArchive(prefix)
    {
        /// <summary>Gets the LSM's object store, under the cluster's store prefix.</summary>
        public IObjectStore Objects { get; } = objects ?? throw new ArgumentNullException(nameof(objects));

        /// <inheritdoc/>
        public override ArchiveStoreKind Kind => ArchiveStoreKind.Lsm;

        /// <inheritdoc/>
        public override string FirstRecordObjects => $"the store's record '{Prefix}/{ObjectName.Store}'";

        /// <inheritdoc/>
        public override async ValueTask<ArchiveStoreIdentity> FirstRecordAsync(IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken)
        {
            var record = await LsmArchiveStore.StoreRecordAsync(Objects, parameters, cancellationToken).ConfigureAwait(false);
            return new ArchiveStoreIdentity(ArchiveStoreKind.Lsm, Prefix, record.Id);
        }

        /// <summary>Returns the LSM's sweeps, which wait for the gate (archive-lsm.md §2.7).</summary>
        public LsmSweeps Sweeps(ObjectStoreMaintenance? maintenance, TimeProvider timeProvider, ILoggerFactory loggers) =>
            new(Gate.Passed, Objects, maintenance, timeProvider, loggers);

        /// <inheritdoc/>
        /// <remarks>
        /// Reads the record through the LSM's <c>StoreRecordAsync</c> and passes the gate once its ID and store fields hold,
        /// so the node may read and write the LSM, whatever its parameters; a parameter's refusal then refuses the Journals
        /// alone (OR-I8).
        /// </remarks>
        private protected override async Task CheckRecordAsync(ArchiveStoreIdentity identity, IReadOnlyDictionary<string, string> parameters, ResiliencePipeline retry, CancellationToken cancellationToken)
        {
            var record = await retry.ExecuteAsync(
                static async (state, token) => await LsmArchiveStore.StoreRecordAsync(state.Objects, state.parameters, token).ConfigureAwait(false),
                (Objects, parameters),
                cancellationToken).ConfigureAwait(false);
            identity.Check(Kind, record.Id);
            Gate.Pass(new ArchiveGateEvidence.Lsm(Objects));
            record.CheckParametersMatch(parameters);
        }
    }

    /// <summary>
    /// Archive logs over the keys' logs and durable storage's objects, which lie in one container
    /// (<see cref="LogArchiveStore"/>'s construction contract), naming the keys' logs by the composition's scheme,
    /// <see cref="OrleansJournalLogNames"/>.
    /// </summary>
    public sealed class Logs : SiloArchive
    {
        /// <summary>Creates the case over the keys' logs and durable storage's objects in one container, under the cluster's store prefix.</summary>
        /// <param name="logs">The keys' logs.</param>
        /// <param name="objects">durable storage's objects under <c>durable-storage/</c>, in the logs' container.</param>
        /// <param name="prefix">The cluster's store prefix, under which the container's <paramref name="logs"/> and <paramref name="objects"/> lie.</param>
        public Logs(IArchiveLogStore logs, IObjectStore objects, ArchiveStorePrefix prefix)
            : base(prefix)
        {
            ArgumentNullException.ThrowIfNull(logs);
            ArgumentNullException.ThrowIfNull(objects);
            Store = new LogArchiveStore(logs, objects, OrleansJournalLogNames.Names);
        }

        /// <summary>Gets the archive store over archive logs.</summary>
        public LogArchiveStore Store { get; }

        /// <inheritdoc/>
        public override ArchiveStoreKind Kind => ArchiveStoreKind.Logs;

        /// <inheritdoc/>
        public override string FirstRecordObjects => $"the store's record '{Prefix}/{ArchiveLogNames.StoreRecord}'";

        /// <inheritdoc/>
        /// <remarks>Creates or adopts the record, as on the LSM: the container's one cluster is the operator's precondition.</remarks>
        public override async ValueTask<ArchiveStoreIdentity> FirstRecordAsync(IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken)
        {
            var record = await Store.StoreRecordAsync(parameters, cancellationToken).ConfigureAwait(false);
            return new ArchiveStoreIdentity(ArchiveStoreKind.Logs, Prefix, record.Id);
        }

        /// <summary>Returns archive logs' sweeps over the gate's evidence (archive-logs.md §2.5).</summary>
        public ArchiveLogSweeps Sweeps(IMetaStore metaStore, Guid cluster, ILoggerFactory loggers) =>
            new(Gate.Passed, metaStore, cluster, loggers);

        /// <inheritdoc/>
        /// <remarks>
        /// Passes <see cref="ArchiveLogGate"/>, which reads the record without creating it, checks its store fields, and
        /// checks the parameters too, since the Azure layout writes the format into the logs: the gate opens the drains, the
        /// sweeps, and the Journals at once.
        /// </remarks>
        private protected override async Task CheckRecordAsync(ArchiveStoreIdentity identity, IReadOnlyDictionary<string, string> parameters, ResiliencePipeline retry, CancellationToken cancellationToken)
        {
            var gate = await retry.ExecuteAsync(
                static async (state, token) => await ArchiveLogGate.PassAsync(state.Store, state.identity, state.parameters, token).ConfigureAwait(false),
                (Store, identity, parameters),
                cancellationToken).ConfigureAwait(false);
            Gate.Pass(new ArchiveGateEvidence.Logs(gate));
        }
    }
}

/// <summary>
/// An object store's maintenance, which the sweep host runs after each sweep: a file-system object store's reclamation of
/// its temporary files (archive-lsm.md §9), which has no timer of its own. The silo's services supply it with such a store.
/// </summary>
/// <param name="Run">Runs the maintenance once.</param>
internal sealed record ObjectStoreMaintenance(Action Run);

/// <summary>
/// The evidence of a passed archive gate, by the kind of store its check found the registry's (orleans.md §5.13,
/// conventions.md §15): <see cref="Lsm"/>, the LSM's object store, or <see cref="Logs"/>, archive logs'
/// <see cref="ArchiveLogGate"/>. Archive work chooses its kind from it.
/// </summary>
internal readonly union ArchiveGateEvidence(ArchiveGateEvidence.Lsm, ArchiveGateEvidence.Logs)
{
    /// <summary>The LSM's gate passed: its record's identity and store fields hold.</summary>
    /// <param name="Objects">The LSM's object store.</param>
    public sealed record Lsm(IObjectStore Objects);

    /// <summary>Archive logs' gate passed: the record's identity, its store fields, and the parameters hold.</summary>
    /// <param name="Gate">The gate's evidence, which archive logs' drains and sweeps take.</param>
    public sealed record Logs(ArchiveLogGate Gate);
}

/// <summary>
/// The silo's archive gate (orleans.md §5.13, OR-I8, OR-I20), which holds one decision: pending; passed, with the evidence
/// of its kind, once the background check of the store's record found the store the registry's and the record's store
/// fields the store's own, and on archive logs its caller parameters the process's too; refused, for the runtime's life,
/// by another store, other store fields, a corrupt record, the store's definite refusal of the check's request, a defect,
/// each with its own reason, or on archive logs another parameter; or cancelled as the silo stops. The node's drains and compactions, the controller grain on this silo, and
/// the sweep host's sweeps wait for it, and take its evidence.
/// </summary>
internal sealed class ArchiveGate
{
    private readonly TaskCompletionSource<ArchiveGateEvidence> _decision = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Gets the gate's decision: a task which completes with the evidence once the gate passed, faults with its refusal,
    /// and is cancelled as the silo stops.
    /// </summary>
    public Task<ArchiveGateEvidence> Passed => _decision.Task;

    /// <summary>Gets a value indicating whether the gate passed.</summary>
    public bool IsPassed => _decision.Task.IsCompletedSuccessfully;

    /// <summary>
    /// Gets the kind of the store the gate passed, which its check found to be the one the node registry records for the
    /// cluster (OR-I20), or <see langword="null"/> until it passed: the evidence by which archive work chooses its kind.
    /// </summary>
    public ArchiveStoreKind? PassedKind => IsPassed
        ? _decision.Task.Result switch
        {
            ArchiveGateEvidence.Lsm => ArchiveStoreKind.Lsm,
            ArchiveGateEvidence.Logs => ArchiveStoreKind.Logs,
            null => throw new InvalidOperationException("A passed gate holds its evidence."),
        }
        : null;

    /// <summary>Passes the gate with <paramref name="evidence"/>.</summary>
    public void Pass(ArchiveGateEvidence evidence)
    {
        if (evidence.Value is null)
        {
            throw new ArgumentException("A gate passes with its kind's evidence.", nameof(evidence));
        }

        _ = _decision.TrySetResult(evidence);
    }

    /// <summary>Refuses the gate for the runtime's life with <paramref name="refusal"/>.</summary>
    public void Refuse(Exception refusal)
    {
        ArgumentNullException.ThrowIfNull(refusal);
        _ = _decision.TrySetException(refusal);

        // Each waiter reads the refusal from the task; none need observe it.
        _ = _decision.Task.Exception;
    }

    /// <summary>Cancels the gate as the silo stops: a gate not yet decided is never decided.</summary>
    public void Cancel() => _ = _decision.TrySetCanceled(CancellationToken.None);
}
