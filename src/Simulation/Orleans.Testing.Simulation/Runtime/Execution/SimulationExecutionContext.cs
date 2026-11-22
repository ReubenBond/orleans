namespace Orleans.Testing.Simulation.Runtime.Execution;

/// <summary>
/// <para>
/// Provides ambient access to the currently-active simulation execution identity: which
/// simulation runtime, which node (if any), and which logical execution (if any) the calling code
/// is conceptually running under. This deliberately mirrors <see cref="System.Threading.ExecutionContext"/>'s
/// flow model - it is built directly on <see cref="AsyncLocal{T}"/> - so it participates correctly
/// in <c>async</c>/<c>await</c> flow, <see cref="Task"/> continuations, and per-logical-call-context
/// isolation across parallel operations, with no additional plumbing required from callers.
/// </para>
/// <para>
/// <b>Nesting.</b> Each <c>Enter*</c> method pushes an immutable frame onto a linked stack held in
/// a single <see cref="AsyncLocal{T}"/> slot and returns an <see cref="IDisposable"/> scope that
/// restores the exact previous frame on <see cref="IDisposable.Dispose"/> - not "whatever is
/// current at Dispose time" - so scopes nest correctly and deterministically even if disposal
/// happens out of the order a caller might naively expect (though callers should still use
/// <c>using</c>/structured nesting; this is a safety net, not a license for arbitrary ordering).
/// </para>
/// <para>
/// <b>Exception safety.</b> Because restoration is driven by <see cref="IDisposable.Dispose"/>,
/// wrapping an <c>Enter*</c> call in a <c>using</c> statement (as every method here is designed to
/// be used) restores the previous frame even if the guarded code throws - the C# compiler emits
/// the equivalent of a <c>finally</c> block for <c>using</c>.
/// </para>
/// <para>
/// <b>Cheap when inactive.</b> Outside any simulation, <see cref="Current"/> is a single
/// <see cref="AsyncLocal{T}"/> read that returns <see langword="null"/> - no allocation, no
/// dictionary lookup, no environment/global-state check. This is intentionally cheap and
/// branch-predictable so that inlineable simulation checks and fault-injection hooks can call it on
/// every production code path without meaningful overhead.
/// </para>
/// </summary>
public static class SimulationExecutionContext
{
    private static readonly AsyncLocal<Frame?> AmbientFrame = new();

    /// <summary>
    /// Gets a value indicating whether ambient simulation execution context is currently active
    /// on the calling logical call context. Equivalent to <c>Current is not null</c>, but avoids
    /// constructing a <see cref="SimulationExecutionSnapshot"/> when the caller only needs the
    /// active/inactive fact - this is the recommended check for a cheap, inlineable "am I
    /// simulating?" fast path.
    /// </summary>
    public static bool IsActive => AmbientFrame.Value is not null;

    /// <summary>
    /// Gets a snapshot of the current ambient simulation execution context, or
    /// <see langword="null"/> if no simulation runtime is currently active on the calling logical
    /// call context (the common case in production - see <see cref="IsActive"/> for a cheaper
    /// active/inactive check).
    /// </summary>
    public static SimulationExecutionSnapshot? Current
    {
        get
        {
            var frame = AmbientFrame.Value;
            if (frame is null)
            {
                return null;
            }

            return new SimulationExecutionSnapshot(frame.Runtime, frame.Node, frame.LogicalExecutionId);
        }
    }

    /// <summary>
    /// Attempts to get the current ambient simulation runtime identity without allocating a
    /// snapshot record.
    /// </summary>
    /// <param name="runtime">The active runtime identity, if any.</param>
    /// <returns><see langword="true"/> if a simulation runtime is currently active.</returns>
    public static bool TryGetCurrentRuntime(out SimulationRuntimeIdentity? runtime)
    {
        var frame = AmbientFrame.Value;
        runtime = frame?.Runtime;
        return frame is not null;
    }

    /// <summary>
    /// Enters a new ambient scope for a fully configured simulation runtime. This is the root scope:
    /// <see cref="EnterNode"/> and <see cref="EnterLogicalExecution"/> both require a runtime scope
    /// to already be active.
    /// </summary>
    /// <param name="runtime">The runtime identity to make ambient for the duration of the scope.</param>
    /// <returns>A disposable scope that restores the previous ambient frame when disposed.</returns>
    internal static IDisposable EnterRuntime(SimulationRuntimeIdentity runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        runtime.EnsureConfigured();

        var previous = AmbientFrame.Value;
        var frame = new Frame(previous, runtime, node: null, SimulationLogicalExecutionId.None);
        AmbientFrame.Value = frame;
        return new Scope(previous);
    }

    /// <summary>
    /// Enters a new ambient scope that additionally narrows execution to a specific simulated
    /// node, inheriting the currently-active runtime.
    /// </summary>
    /// <param name="node">The node identity to make ambient for the duration of the scope.</param>
    /// <returns>A disposable scope that restores the previous ambient frame when disposed.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown if no runtime scope (<see cref="EnterRuntime"/>) is currently active - a node cannot
    /// exist without a runtime.
    /// </exception>
    public static IDisposable EnterNode(SimulationNodeIdentity node)
    {
        ArgumentNullException.ThrowIfNull(node);

        var previous = AmbientFrame.Value ?? throw new InvalidOperationException(
            "Cannot enter a node scope: no simulation runtime scope is currently active. " +
            "EnterNode requires an enclosing EnterRuntime scope.");

        var frame = new Frame(previous, previous.Runtime, node, previous.LogicalExecutionId);
        AmbientFrame.Value = frame;
        return new Scope(previous);
    }

    /// <summary>
    /// Enters a new ambient scope that narrows execution to a specific logical execution identity
    /// (see <see cref="SimulationLogicalExecutionId"/>), inheriting the currently-active runtime
    /// and node (if any).
    /// </summary>
    /// <param name="logicalExecutionId">The logical execution identity to make ambient.</param>
    /// <returns>A disposable scope that restores the previous ambient frame when disposed.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown if no runtime scope (<see cref="EnterRuntime"/>) is currently active.
    /// </exception>
    public static IDisposable EnterLogicalExecution(SimulationLogicalExecutionId logicalExecutionId)
    {
        var previous = AmbientFrame.Value ?? throw new InvalidOperationException(
            "Cannot enter a logical execution scope: no simulation runtime scope is currently active. " +
            "EnterLogicalExecution requires an enclosing EnterRuntime scope.");

        var frame = new Frame(previous, previous.Runtime, previous.Node, logicalExecutionId);
        AmbientFrame.Value = frame;
        return new Scope(previous);
    }

    /// <summary>
    /// Re-establishes an ambient simulation snapshot after a BCL execution-context operation replaced the
    /// AsyncLocal-backed ambient frame.
    /// </summary>
    internal static void RestoreSnapshot(SimulationExecutionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        AmbientFrame.Value = new Frame(
            parent: null,
            snapshot.Runtime,
            snapshot.Node,
            snapshot.LogicalExecutionId);
    }

    /// <summary>
    /// <para>
    /// Deliberately suppresses <see cref="ExecutionContext"/> flow (via
    /// <see cref="System.Threading.ExecutionContext.SuppressFlow"/>) for the calling thread until
    /// the returned scope is disposed, and records a diagnostic entry
    /// (see <see cref="SimulationFlowSuppressionDiagnostics"/>) describing why. Because ambient
    /// simulation context rides on <see cref="AsyncLocal{T}"/>/<see cref="ExecutionContext"/> flow,
    /// suppressing flow means any new asynchronous operation started inside the returned scope
    /// (e.g. an un-flowed <see cref="Task.Run(Action)"/>, a raw <see cref="Thread"/>) will not
    /// observe the ambient simulation context that is active on the calling thread right now.
    /// </para>
    /// <para>
    /// This exists so that deliberate flow suppression is discoverable and distinguishable from a
    /// genuine bug: <see cref="SimulationExternalEntryGuard"/> consults
    /// <see cref="SimulationFlowSuppressionDiagnostics"/> when it reports a missing/foreign ambient
    /// context, so a diagnostic can note "a flow suppression was recorded nearby" instead of
    /// leaving the caller to guess why the context disappeared.
    /// </para>
    /// </summary>
    /// <param name="reason">
    /// A short, human-readable reason for the suppression, included in the recorded diagnostic
    /// entry and in any guard message that references it.
    /// </param>
    /// <returns>
    /// A disposable scope; disposing it restores flow (<see cref="AsyncFlowControl.Undo"/>) if this
    /// call is what suppressed it.
    /// </returns>
    public static IDisposable SuppressFlow(string reason)
    {
        ArgumentException.ThrowIfNullOrEmpty(reason);

        var snapshot = Current;
        var flowControl = System.Threading.ExecutionContext.SuppressFlow();
        var entry = new SimulationFlowSuppressionEvent(reason, snapshot, DateTimeOffset.UtcNow);
        SimulationFlowSuppressionDiagnostics.Record(entry);
        return new FlowSuppressionScope(flowControl);
    }

    /// <summary>
    /// The immutable, linked ambient frame. Each <c>Enter*</c> call allocates exactly one new
    /// frame pointing at its parent; nothing here is ever mutated after construction, which is what
    /// makes concurrent reads from multiple logical call contexts (parallel simulations, parallel
    /// tests) safe without any locking.
    /// </summary>
    private sealed class Frame(
        Frame? parent,
        SimulationRuntimeIdentity runtime,
        SimulationNodeIdentity? node,
        SimulationLogicalExecutionId logicalExecutionId)
    {
        public Frame? Parent { get; } = parent;

        public SimulationRuntimeIdentity Runtime { get; } = runtime;

        public SimulationNodeIdentity? Node { get; } = node;

        public SimulationLogicalExecutionId LogicalExecutionId { get; } = logicalExecutionId;
    }

    /// <summary>
    /// Restores a captured previous frame on disposal. Deliberately restores exactly the frame
    /// captured at construction time (not "whatever <see cref="AmbientFrame"/> currently holds"),
    /// so disposal is correct regardless of what else has mutated the ambient slot in the interim.
    /// </summary>
    private sealed class Scope(Frame? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            AmbientFrame.Value = previous;
        }
    }

    private sealed class FlowSuppressionScope(AsyncFlowControl flowControl) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            flowControl.Undo();
        }
    }
}
