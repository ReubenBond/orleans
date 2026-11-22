using System.Collections.Immutable;
using Orleans.Durable.Storage.Engine.Archive.Logs;
using Orleans.Durable.Storage.Engine.Archive.Lsm.Control;
using Orleans.Durable.Storage.Engine.Cluster.Control;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Control;
using Orleans.Durable.Storage.Engine.Threading;

namespace Orleans.Durable.Storage.Control;

/// <summary>
/// The node's control side as the silo's Ledgers runtime runs it (orleans.md §5.6), and the one state the node control
/// grain and the runtime's loop share: <see cref="ControlState.Starting"/>, <see cref="ControlState.Serving"/>,
/// <see cref="ControlState.HandingOff"/>, and <see cref="ControlState.Stopped"/>, which only advance.
/// </summary>
/// <remarks>
/// <para>
/// The runtime publishes its <see cref="NodeController"/> here once it starts, with the exclusive scheduler it owns and its
/// stopping token. Every call of the node controller runs on that scheduler, one task at a time, interleaving at awaits, so
/// its state changes only in synchronous segments; the grain's work and the node's drains run under the stopping token,
/// which the runtime cancels before it disposes what the controller uses.
/// </para>
/// <para>
/// The grain's calls and the drain starts enter through one admission gate, which the runtime's stop closes and drains
/// before it cancels, and again after, waiting then also for every drain the node started to end: a transition answers
/// <c>Declined</c> once the node hands off or stops, or before it ever served; a compaction and a drain start are refused
/// once the gate closes or the node stopped. The loop's reports run on the scheduler outside the gate until the runtime
/// stops.
/// </para>
/// </remarks>
internal sealed class LocalNodeControl
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly AdmissionGate _admission = new();
    private Runtime? _runtime;
    private int _state;

    /// <summary>The node's control states, in the order they advance.</summary>
    public enum ControlState
    {
        /// <summary>The runtime has not started: a call waits for it.</summary>
        Starting,

        /// <summary>The node executes transitions, compactions, and drains.</summary>
        Serving,

        /// <summary>The node stops and hands its ledgers off: it executes no transition, and still compacts and drains.</summary>
        HandingOff,

        /// <summary>The runtime stopped, or never started: every call is declined or refused.</summary>
        Stopped,
    }

    /// <summary>Gets the node's control state.</summary>
    public ControlState State => (ControlState)Volatile.Read(ref _state);

    /// <summary>
    /// Publishes the started runtime's node controller, the exclusive scheduler it runs on, and the runtime's stopping
    /// token, and starts serving.
    /// </summary>
    public void Serve(NodeController controller, TaskScheduler scheduler, CancellationToken stopping)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(scheduler);
        Volatile.Write(ref _runtime, new Runtime(controller, scheduler, stopping));
        Advance(ControlState.Serving);
        _ = _ready.TrySetResult();
    }

    /// <summary>Starts the node's hand-off: from now on it executes no transition.</summary>
    public void BeginHandOff() => Advance(ControlState.HandingOff);

    /// <summary>
    /// Closes the admission of grain calls and drain starts, and completes once every admitted one has ended and then every
    /// drain the node started has ended (<see cref="NodeController.DrainsEnded"/>), so no drain outlives the runtime.
    /// </summary>
    public async Task CloseAdmissionAsync()
    {
        await _admission.CloseAsync().ConfigureAwait(false);
        if (Volatile.Read(ref _runtime) is { } runtime)
        {
            await runtime.Controller.DrainsEnded.ConfigureAwait(false);
        }
    }

    /// <summary>Marks the node stopped, after its runtime cancelled its stopping token, or when it never started.</summary>
    public void Stop()
    {
        Advance(ControlState.Stopped);
        _ = _admission.CloseAsync();
        _ = _ready.TrySetResult();
    }

    /// <summary>
    /// Executes <paramref name="transition"/> through the node controller, keyed by <paramref name="node"/>, and answers its
    /// verdict; <see cref="TransitionReply.Declined"/> once the node hands off or stops, or when it never served.
    /// </summary>
    /// <param name="node">The node the grain's key names, which the published controller's node equals.</param>
    /// <param name="transition">The transition.</param>
    /// <param name="commit">Submits the change once to the controller's ledger map committer.</param>
    public async Task<TransitionReply> ExecuteAsync(NodeId node, Transition transition, Func<LedgerMapChange, CancellationToken, Task<LedgerMapCommitOutcome>> commit)
    {
        await _ready.Task.ConfigureAwait(false);
        using var admission = _admission.TryEnter();
        if (!admission.Entered || State != ControlState.Serving || Published(node) is not { } runtime)
        {
            return TransitionReply.Declined.Instance;
        }

        return await runtime.RunAsync(controller => controller.ExecuteAsync(transition, commit, runtime.Stopping)).ConfigureAwait(false);
    }

    /// <summary>
    /// Cooperatively drains and stops admitting writes on <paramref name="request"/>'s open ledger through the node
    /// controller, keyed by <paramref name="node"/>; answers <see cref="PrepareReleaseReply.Declined"/> once the node
    /// stopped or never served, while still serving during <see cref="ControlState.HandingOff"/> so a departing primary
    /// drains cleanly before hand-off (orleans.md §5.6, OR-I24).
    /// </summary>
    public async Task<PrepareReleaseReply> PrepareReleaseAsync(NodeId node, PrepareReleaseRequest request)
    {
        await _ready.Task.ConfigureAwait(false);
        using var admission = _admission.TryEnter();
        if (!admission.Entered || State == ControlState.Stopped || Published(node) is not { } runtime)
        {
            return PrepareReleaseReply.Declined.Instance;
        }

        return await runtime.RunAsync(controller => controller.PrepareReleaseAsync(request, runtime.Stopping)).ConfigureAwait(false);
    }

    /// <summary>
    /// Compacts <paramref name="assignment"/>'s partition through the node controller, keyed by <paramref name="node"/>.
    /// </summary>
    /// <exception cref="NodeControlStoppedException">The node stopped, or never served: the compaction did not start.</exception>
    public async Task<CompactionReply> CompactAsync(NodeId node, CompactionAssignment assignment, Func<ManifestChange, CancellationToken, Task<ManifestCommitOutcome>> commit)
    {
        await _ready.Task.ConfigureAwait(false);
        using var admission = _admission.TryEnter();
        if (!admission.Entered || State == ControlState.Stopped || Published(node) is not { } runtime)
        {
            throw new NodeControlStoppedException($"Node {node} stops; the compaction of {assignment.Partition} did not start.");
        }

        return await runtime.RunAsync(controller => controller.CompactAsync(assignment, commit, runtime.Stopping)).ConfigureAwait(false);
    }

    /// <summary>
    /// Starts the node's drains due which are not in flight (orleans.md §5.6) while the gate admits it, and answers the
    /// ledgers it started; <see langword="null"/> once the gate closed, when none starts. Each drain ends on its own,
    /// telling <paramref name="failed"/> of its failure, and the gate's closing waits for it.
    /// </summary>
    public async Task<ImmutableArray<LedgerId>?> StartDrainsAsync(
        ImmutableArray<LedgerId> drainToo,
        Func<ManifestChange, CancellationToken, Task<ManifestCommitOutcome>> commit,
        Action<LedgerId, Exception> failed)
    {
        using var admission = _admission.TryEnter();
        if (!admission.Entered || State == ControlState.Stopped || Volatile.Read(ref _runtime) is not { } runtime)
        {
            return null;
        }

        return await runtime.RunAsync<ImmutableArray<LedgerId>?>(controller => Task.FromResult<ImmutableArray<LedgerId>?>(controller.StartDrains(drainToo, commit, failed, runtime.Stopping))).ConfigureAwait(false);
    }

    /// <summary>
    /// On archive logs, starts the node's drains due as <see cref="StartDrainsAsync"/> does, over the silo's archive gate,
    /// which passed, submitting each completed ledger's drop through <paramref name="commit"/>, the controller grain's
    /// <c>CommitLedgerMapAsync</c> (orleans.md §5.6).
    /// </summary>
    public async Task<ImmutableArray<LedgerId>?> StartLogDrainsAsync(
        ArchiveLogGate gate,
        ImmutableArray<LedgerId> drainToo,
        Func<LedgerMapChange, CancellationToken, Task<LedgerMapCommitOutcome>> commit,
        Action<LedgerId, Exception> failed)
    {
        using var admission = _admission.TryEnter();
        if (!admission.Entered || State == ControlState.Stopped || Volatile.Read(ref _runtime) is not { } runtime)
        {
            return null;
        }

        return await runtime.RunAsync<ImmutableArray<LedgerId>?>(controller => Task.FromResult<ImmutableArray<LedgerId>?>(controller.StartLogDrains(gate, drainToo, commit, failed, runtime.Stopping))).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs <paramref name="work"/> on the node controller's scheduler, outside the admission gate, for the runtime's own
    /// loop: its reports and its following of their replies.
    /// </summary>
    /// <exception cref="NodeControlStoppedException">The runtime has not started, or stopped.</exception>
    public Task<T> RunAsync<T>(Func<NodeController, Task<T>> work) =>
        State is ControlState.Serving or ControlState.HandingOff && Volatile.Read(ref _runtime) is { } runtime
            ? runtime.RunAsync(work)
            : Task.FromException<T>(new NodeControlStoppedException("The node's control runtime does not run."));

    private Runtime? Published(NodeId node)
    {
        var runtime = Volatile.Read(ref _runtime);
        if (runtime is not null && runtime.Controller.Node != node)
        {
            // The grain admitted the call through the node's serving gate, which answers NotThisNode for another node.
            throw new InvalidOperationException($"The runtime's node controller is node {runtime.Controller.Node}'s, not node {node}'s.");
        }

        return runtime;
    }

    private void Advance(ControlState state)
    {
        int current;
        do
        {
            current = Volatile.Read(ref _state);
            if (current >= (int)state)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref _state, (int)state, current) != current);
    }

    /// <summary>The published runtime: its node controller, the exclusive scheduler the controller runs on, and its stopping token.</summary>
    private sealed class Runtime(NodeController controller, TaskScheduler scheduler, CancellationToken stopping)
    {
        public NodeController Controller { get; } = controller;

        public CancellationToken Stopping { get; } = stopping;

        public Task<T> RunAsync<T>(Func<NodeController, Task<T>> work) =>
            Task.Factory.StartNew(() => work(Controller), CancellationToken.None, TaskCreationOptions.DenyChildAttach, scheduler).Unwrap();
    }
}
