using System.Diagnostics;
using System.Globalization;
using Orleans.Testing.Simulation.Runtime.Execution;
using Orleans.Testing.Simulation.Runtime.Random;
using Orleans.Testing.Simulation.Runtime.Scheduling;
using Orleans.Testing.Simulation.Runtime.Shims;
using Microsoft.Extensions.Logging;
using Orleans.Testing.Simulation.Runtime.Tasks;

namespace Orleans.Testing.Simulation;

/// <summary>
/// A directly constructible deterministic simulation cluster. The cluster owns the scheduler,
/// runtime, network, drive loop, and every node attached through <see cref="AddNode(string)"/>,
/// or <see cref="AddCustomNode{TNode}(string, Func{SimulationNodeContext, TNode})"/>.
/// </summary>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public sealed partial class SimulationCluster : IAsyncDisposable
{
    private const string TeardownApi = "Orleans.Testing.Simulation.SimulationCluster.TeardownCancellationToken";

    private const int DisposalMaxIterations = 1_000_000;

    private readonly SortedDictionary<string, SimulationNode> _nodes = new(StringComparer.Ordinal);
    private readonly List<NodeRegistration> _nodeRegistrations = [];
    private readonly Dictionary<string, SimulationNodeContext> _attachments = new(StringComparer.Ordinal);
    private readonly SimulationTimeProvider _timeProvider;
    private readonly CancellationTokenSource _teardownCts;
    private readonly CancellationToken _callerCancellation;
    private readonly SimulationDriveLoop _driveLoop;
    private readonly int _simulationThreadId;
    private List<Exception>? _disposalFailures;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="SimulationCluster"/> class.
    /// Initializes a new simulation cluster with the specified seed.
    /// </summary>
    /// <param name="seed">The seed for deterministic random number generation.</param>
    /// <param name="startDateTime">Optional starting date/time for the simulation. Defaults to UTC now.</param>
    /// <param name="simulationTimeZone">
    /// Optional local time zone the deterministic <c>DateTime.Now</c>/<c>Today</c> shims observe.
    /// Defaults to <see cref="TimeZoneInfo.Utc"/> so local and UTC time coincide deterministically.
    /// </param>
    /// <param name="processorCount">
    /// Optional simulated processor count every node observes as <see cref="Environment.ProcessorCount"/>, and which
    /// sizes the work the BCL sizes by it. Defaults to 4; see <see cref="ProcessorCount"/>.
    /// </param>
    /// <param name="cancellationToken">
    /// Optional cancellation token which tears the cluster down: once it is cancelled, the next step of a cluster drive
    /// method (<c>RunUntil</c>, <c>RunUntilIdle</c>, <c>RunFor</c>, <c>RunToCompletion</c>) cancels
    /// <see cref="TeardownCancellationToken"/>.
    /// </param>
    /// <exception cref="Orleans.Testing.Simulation.Runtime.UnsupportedRuntimeException">
    /// A loaded assembly rewritten by Clockwork does not support the runtime this process runs.
    /// </exception>
    public SimulationCluster(
        int seed,
        DateTimeOffset? startDateTime = null,
        TimeZoneInfo? simulationTimeZone = null,
        int? processorCount = null,
        CancellationToken cancellationToken = default)
        : this(seed, startDateTime, simulationTimeZone, processorCount, operationListener: null, cancellationToken)
    {
    }

    internal SimulationCluster(
        int seed,
        DateTimeOffset? startDateTime,
        TimeZoneInfo? simulationTimeZone,
        int? processorCount,
        ISimulationOperationListener? operationListener,
        CancellationToken cancellationToken)
    {
        ProcessorCount = processorCount ?? SimulationRuntimeEnvironment.DefaultProcessorCount;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ProcessorCount, nameof(processorCount));
        _teardownCts = new CancellationTokenSource();
        _callerCancellation = cancellationToken;
        _simulationThreadId = Environment.CurrentManagedThreadId;
        TeardownCancellationToken = _teardownCts.Token;
        Seed = seed;
        var simulationStartDateTime = startDateTime ?? DateTimeOffset.UtcNow;
        SimulationTimeZone = simulationTimeZone ?? TimeZoneInfo.Utc;

        Random = new SimulationRandom(seed);

        SeedAuthority = new SimulationSeedAuthority(seed);

        // The scheduler is the runtime's single authority for work and virtual time.
        RuntimeIdentity = new SimulationRuntimeIdentity(Guid.NewGuid(), seed, GetType().Name);
        Scheduler = new SimulationScheduler(
            RuntimeIdentity,
            SeedAuthority,
            simulationStartDateTime,
            SimulationTimeZone,
            operationListener,
            ProcessorCount);
        Guard = new SingleThreadedGuard(
            () => Scheduler.IsSimulationThread || Environment.CurrentManagedThreadId == _simulationThreadId
                ? SimulationScheduler.SimulationLogicalThreadOwnerId
                : Environment.CurrentManagedThreadId);

        // Create shared cluster-level scheduling surfaces.
        SchedulerLane = new SimulationSchedulerLane(Scheduler, Guard);
        TaskScheduler = new SimulationTaskScheduler(SchedulerLane);

        // Create time provider using the cluster lane.
        _timeProvider = new SimulationTimeProvider(SchedulerLane);

        // The single engine that drives RunUntil/RunUntilIdle/RunFor.
        _driveLoop = new SimulationDriveLoop(
            () => _timeProvider.GetUtcNow(),
            RunOneTaskRoundRobin,
            GetNextWaitingDueTime,
            AdvanceVirtualTime,
            CapturePendingWorkSummary,
            () => Scheduler.PendingOperationCount > 0,
            IsTeardownRequested);
        Network = new SimulationNetwork(
            () => Nodes,
            new SimulationRandom(SeedAuthority.GetDomainSeed(SimulationSeedDomain.Network)));
    }

    /// <summary>
    /// Gets the seed used for this cluster.
    /// </summary>
    public int Seed { get; }

    /// <summary>
    /// <para>
    /// Gets this cluster's runtime identity - a process-unique id (plus the seed and this
    /// cluster's concrete type name, for diagnostics) that becomes ambient (see
    /// <see cref="Orleans.Testing.Simulation.Runtime.Execution.SimulationExecutionContext"/>) around every piece of
    /// work this cluster or its nodes execute through an ambient-integrated queue.
    /// </para>
    /// <para>
    /// Runtime services and diagnostics use this identity as the stable "which simulation is this?"
    /// answer wherever ambient context is installed.
    /// </para>
    /// </summary>
    public SimulationRuntimeIdentity RuntimeIdentity { get; }

    /// <summary>
    /// Gets the root deterministic seed/decision authority for this cluster, exposing independent
    /// named seed domains (scheduler, network, application, identity, Buggify, model - see
    /// <see cref="Orleans.Testing.Simulation.Runtime.Random.SimulationSeedDomain"/>) plus stable per-node/per-site
    /// child derivation, all as pure functions of <see cref="Seed"/> - never of registration or
    /// fork order.
    /// </summary>
    public SimulationSeedAuthority SeedAuthority { get; }

    /// <summary>
    /// Gets the local time zone the deterministic <c>DateTime.Now</c>/<c>DateTime.Today</c> shims
    /// observe for nodes in this cluster. Defaults to <see cref="TimeZoneInfo.Utc"/> so local and UTC
    /// time coincide deterministically regardless of the host machine's zone.
    /// </summary>
    public TimeZoneInfo SimulationTimeZone { get; }

    /// <summary>
    /// Gets the simulated processor count every node in this cluster observes as <see cref="Environment.ProcessorCount"/>.
    /// It sizes the work the BCL sizes by the processor count: a <c>Parallel</c> loop without a degree-of-parallelism
    /// limit runs this many workers, and <c>Partitioner.Create(fromInclusive, toExclusive)</c> sizes its ranges from it.
    /// Defaults to 4: big enough that such work runs on several workers whose interleavings the scheduler explores,
    /// small enough to keep the schedule space and the cost of each run bounded, and the same on every host, so a seed
    /// replays on any machine. Set it to model a production machine's processor count.
    /// </summary>
    public int ProcessorCount { get; }

    /// <summary>
    /// Gets the deterministic runtime environment the BCL shims dispatch to while this cluster's
    /// ambient runtime is active. Backed by this cluster's virtual clock and seed authority.
    /// </summary>
    public ISimulationRuntimeEnvironment RuntimeEnvironment => RuntimeIdentity.Environment;

    /// <summary>
    /// Gets a cancellation token used to signal when the simulation is being torn down. The cluster cancels it on the
    /// simulation's logical thread - from a step of a cluster drive method once the token passed to the constructor is
    /// cancelled, or when the cluster is disposed - so callbacks registered on it from simulation work may call
    /// controlled APIs.
    /// </summary>
    public CancellationToken TeardownCancellationToken { get; }

    /// <summary>
    /// Gets or sets maximum simulated time to advance before considering the simulation stuck.
    /// Default is 10 minutes of simulated time.
    /// </summary>
    public TimeSpan MaxSimulatedTimeAdvance { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Gets or sets the maximum number of consecutive time advances allowed without executing any
    /// work in between, before considering the simulation stuck. Default is 10,000.
    /// </summary>
    public int MaxConsecutiveTimeAdvances { get; set; } = 10_000;

    /// <summary>
    /// Gets the starting date/time for the simulation.
    /// </summary>
    public DateTimeOffset StartDateTime => Scheduler.StartDateTime;

    /// <summary>
    /// Gets the simulation random instance.
    /// </summary>
    public SimulationRandom Random { get; }

    /// <summary>
    /// Gets the scheduler which owns this cluster's work and virtual time.
    /// </summary>
    public SimulationScheduler Scheduler { get; }

    /// <summary>
    /// Gets the simulation time provider.
    /// </summary>
    public TimeProvider TimeProvider => _timeProvider;

    /// <summary>
    /// Gets all nodes in the simulation, including suspended nodes (snapshot).
    /// Consider using <see cref="ActiveNodes"/> for most operations.
    /// </summary>
    public IReadOnlyList<SimulationNode> Nodes => [.. _nodes.Values];

    /// <summary>
    /// Gets all running nodes in the simulation (snapshot).
    /// Suspended and discarded nodes cannot process messages and are excluded from convergence checks.
    /// </summary>
    public IReadOnlyList<SimulationNode> ActiveNodes =>
        [.. _nodes.Values.Where(static node => node.Context.State == SimulationNodeState.Running)];

    /// <summary>
    /// Gets the simulated network for this cluster. The network is available immediately, including
    /// when the cluster has no nodes, and observes nodes as they are attached.
    /// </summary>
    public SimulationNetwork Network { get; }

    /// <summary>
    /// Gets the cluster-level scheduler lane for general simulation work.
    /// For node-specific work, use the node context's scheduler lane.
    /// </summary>
    public SimulationSchedulerLane SchedulerLane { get; }

    /// <summary>
    /// Gets the cluster-level task scheduler for scheduling general simulation work.
    /// For node-specific work, use the node's context to get the node's scheduler.
    /// </summary>
    public SimulationTaskScheduler TaskScheduler { get; }

    /// <summary>
    /// Gets the cluster-level synchronization context.
    /// Install this on the test thread to capture async continuations in the simulation.
    /// </summary>
    public SimulationSynchronizationContext SynchronizationContext => SchedulerLane.SynchronizationContext;

    /// <summary>
    /// Gets the single-threaded guard used to detect accidental concurrent access.
    /// This guard should be shared with all simulation components to ensure single-threaded execution.
    /// </summary>
    public SingleThreadedGuard Guard { get; }

    /// <summary>
    /// Attaches a node with no application state and returns it ready for immediate use.
    /// </summary>
    public SimulationNode AddNode(string address) =>
        AddCustomNode(
            address,
            context => new AttachedSimulationNode(address, context));

    /// <summary>
    /// Attaches a custom node created from its fully initialized context and returns the concrete
    /// node ready for immediate use. The returned node must expose the supplied context instance,
    /// and its address must exactly match <paramref name="address"/>.
    /// </summary>
    public TNode AddCustomNode<TNode>(
        string address,
        Func<SimulationNodeContext, TNode> factory)
        where TNode : SimulationNode
    {
        ArgumentNullException.ThrowIfNull(factory);
        SimulationNodeContext context = BeginAttachment(address);

        TNode? node = null;
        try
        {
            node = factory(context);
            if (node is null)
            {
                throw new InvalidOperationException(
                    $"The factory for node '{address}' returned null.");
            }

            ThrowIfDisposed();
            ValidateCustomNode(address, context, node);
            RegisterNode(context, node);
            return node;
        }
        catch (Exception attachmentException)
        {
            List<Exception> cleanupFailures = [];
            if (TryBeginAttachmentCleanup(context, cleanupFailures))
            {
                DrainAttachmentWorkToQuiescence([context], cleanupFailures);
            }

            if (node is not null)
            {
                DisposeFailedAttachmentTarget(node, cleanupFailures);
            }

            try
            {
                CompleteFailedAttachment(address, context);
            }
            catch (Exception exception)
            {
                AddDisposalFailure(cleanupFailures, exception);
            }

            if (cleanupFailures.Count > 0)
            {
                throw new AggregateException(
                    "Node attachment and cleanup both failed.",
                    [attachmentException, .. cleanupFailures]);
            }

            throw;
        }
    }

    /// <summary>Gets the node attached at <paramref name="address"/>, or <see langword="null"/>.</summary>
    public SimulationNode? FindNode(string address)
    {
        ArgumentException.ThrowIfNullOrEmpty(address);
        using var _ = Guard.Enter();
        _nodes.TryGetValue(address, out var node);
        return node;
    }

    /// <summary>Gets the node attached at <paramref name="address"/> with the requested type.</summary>
    public TNode? FindNode<TNode>(string address)
        where TNode : SimulationNode =>
        FindNode(address) as TNode;

    /// <summary>
    /// Creates a new deterministic random instance derived from the cluster's random.
    /// </summary>
    public SimulationRandom ForkRandom()
    {
        using var _ = Guard.Enter();
#pragma warning disable CA5394 // Do not use insecure randomness
        return new SimulationRandom(Random.Next());
#pragma warning restore CA5394 // Do not use insecure randomness
    }

    /// <summary>Creates a node context bound to this cluster's scheduler and shared services.</summary>
    private SimulationNodeContext CreateNodeContext(string networkAddress, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(networkAddress);
        return new SimulationNodeContext(
            Scheduler,
            Guard,
            new SimulationRandom(
                SeedAuthority.GetSiteSeed(SimulationSeedDomain.Application, networkAddress)),
            SchedulerLane,
            logger,
            new SimulationNodeIdentity(networkAddress));
    }

    /// <summary>
    /// Runs the simulation until the specified condition is met.
    /// </summary>
    /// <param name="condition">The condition that ends the run when it becomes true.</param>
    /// <param name="maxIterations">The maximum number of loop iterations to execute.</param>
    /// <param name="cancellationToken">A token that can cancel the run between simulation dispatches.</param>
    /// <returns>A detailed result describing the execution.</returns>
#pragma warning disable CA1068 // Cancellation is required while the execution limit retains its established default.
    public SimulationExecutionResult RunUntil(
        Func<bool> condition,
        CancellationToken cancellationToken,
        int maxIterations = 100_000)
    {
        ArgumentNullException.ThrowIfNull(condition);
        return ExecuteDriveLoop(
            condition,
            MaxSimulatedTimeAdvance,
            maxIterations,
            observeTeardownCancellation: false,
            initialConsecutiveTimeAdvances: 0,
            absoluteEndTime: null,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Attempts to execute one eligible operation on the unified scheduler.
    /// </summary>
    private bool RunOneTaskRoundRobin(CancellationToken cancellationToken)
    {
        using var control = Scheduler.EnterControlScope();
        using var _ = Guard.Enter();
        ObserveCallerCancellation();
        if (_disposalFailures is not { } failures)
        {
            return Scheduler.RunStepForPump(cancellationToken);
        }

        bool result = Scheduler.RunStepCapturingFailure(cancellationToken, out Exception? failure);
        if (failure is not null)
        {
            AddDisposalFailure(failures, failure);
        }

        return result;
    }

    /// <summary>
    /// Gets the earliest deadline from the scheduler's unified timer queue.
    /// </summary>
    private DateTimeOffset? GetNextWaitingDueTime()
    {
        using var _ = Guard.Enter();
        var schedulerDue = Scheduler.NextTimerDue;
        return schedulerDue is null ? null : StartDateTime + schedulerDue.Value;
    }

    /// <summary>
    /// Advances the scheduler's modeled time and fires any virtual-time deadlines that are now due
    /// (finite <c>Monitor</c>/<c>SemaphoreSlim</c> waits).
    /// Forward-only and null-safe: with no pending deadlines the loop step is a cheap no-op.
    /// </summary>
    /// <param name="delta">The non-negative amount to advance.</param>
    private void AdvanceVirtualTime(TimeSpan delta)
    {
        Scheduler.AdvanceVirtualTimeTo(Scheduler.VirtualTime + delta);
    }

    /// <summary>
    /// Runs the simulation until it becomes idle.
    /// </summary>
    /// <param name="cancellationToken">A token that can cancel the run between simulation dispatches.</param>
    /// <param name="maxTimeAdvance">The maximum simulated-time gap to jump in a single advance, or <see langword="null"/> to use <see cref="MaxSimulatedTimeAdvance"/>.</param>
    /// <param name="maxIterations">The maximum number of loop iterations to execute.</param>
    /// <returns>A detailed result describing the execution.</returns>
    public SimulationExecutionResult RunUntilIdle(
        CancellationToken cancellationToken,
        TimeSpan? maxTimeAdvance = null,
        int maxIterations = 100_000) =>
        ExecuteDriveLoop(
            condition: null,
            maxTimeAdvance ?? MaxSimulatedTimeAdvance,
            maxIterations,
            observeTeardownCancellation: true,
            initialConsecutiveTimeAdvances: 0,
            absoluteEndTime: null,
            cancellationToken: cancellationToken);

    /// <summary>
    /// Drives a task to completion by running the simulation.
    /// The task factory is invoked with the cluster's synchronization context installed,
    /// ensuring async continuations are captured on the simulation scheduler. The root task and
    /// its continuations are scheduler-owned work and can execute on different
    /// <c>SimulationOperationWorker-*</c> threads while
    /// <see cref="Runtime.Scheduling.SimulationScheduler.IsSimulationThread"/> remains
    /// <see langword="true"/>. Managed thread id equality is not part of the contract.
    /// </summary>
    /// <param name="taskFactory">The asynchronous work to execute.</param>
    /// <param name="maxIterations">The maximum number of loop iterations to execute.</param>
    /// <param name="cancellationToken">A token that can cancel the run between simulation dispatches.</param>
    public void RunToCompletion(
        Func<Task> taskFactory,
        CancellationToken cancellationToken,
        int maxIterations = 1_000_000)
    {
        ArgumentNullException.ThrowIfNull(taskFactory);
        cancellationToken.ThrowIfCancellationRequested();
        using var control = Scheduler.EnterControlScope();
        using var lockScope = Guard.Enter();

        Task task = StartTask(taskFactory);
        SimulationExecutionResult result = RunUntil(() => task.IsCompleted, cancellationToken, maxIterations);
        EnsureTaskCompleted(task, result);
        task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// Drives a task to completion using an adaptive execution budget. The root task and its
    /// continuations are scheduler-owned work and can execute on different
    /// <c>SimulationOperationWorker-*</c> threads while
    /// <see cref="Runtime.Scheduling.SimulationScheduler.IsSimulationThread"/> remains
    /// <see langword="true"/>.
    /// </summary>
    /// <param name="taskFactory">The asynchronous work to execute.</param>
    /// <param name="budget">The adaptive execution budget.</param>
    /// <param name="cancellationToken">A token that can cancel the run between simulation dispatches.</param>
    public void RunToCompletion(
        Func<Task> taskFactory,
        AdaptiveExecutionBudget budget,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(taskFactory);
        ArgumentNullException.ThrowIfNull(budget);
        cancellationToken.ThrowIfCancellationRequested();
        using var control = Scheduler.EnterControlScope();
        using var lockScope = Guard.Enter();

        Task task = StartTask(taskFactory);
        SimulationExecutionResult result = RunUntil(() => task.IsCompleted, budget, cancellationToken);
        EnsureTaskCompleted(task, result);
        task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// Drives a task to completion and returns its result. The root task and its continuations are
    /// scheduler-owned work and can execute on different <c>SimulationOperationWorker-*</c>
    /// threads while <see cref="Runtime.Scheduling.SimulationScheduler.IsSimulationThread"/>
    /// remains <see langword="true"/>.
    /// </summary>
    /// <param name="taskFactory">The asynchronous work to execute.</param>
    /// <param name="maxIterations">The maximum number of loop iterations to execute.</param>
    /// <param name="cancellationToken">A token that can cancel the run between simulation dispatches.</param>
    public T RunToCompletion<T>(
        Func<Task<T>> taskFactory,
        CancellationToken cancellationToken,
        int maxIterations = 1_000_000)
    {
        ArgumentNullException.ThrowIfNull(taskFactory);
        cancellationToken.ThrowIfCancellationRequested();
        using var control = Scheduler.EnterControlScope();
        using var lockScope = Guard.Enter();

        Task<T> task = StartTask(taskFactory);
        SimulationExecutionResult result = RunUntil(() => task.IsCompleted, cancellationToken, maxIterations);
        EnsureTaskCompleted(task, result);
        return task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// Drives a task to completion using an adaptive execution budget and returns its result. The
    /// root task and its continuations are scheduler-owned work and can execute on different
    /// <c>SimulationOperationWorker-*</c> threads while
    /// <see cref="Runtime.Scheduling.SimulationScheduler.IsSimulationThread"/> remains
    /// <see langword="true"/>.
    /// </summary>
    /// <param name="taskFactory">The asynchronous work to execute.</param>
    /// <param name="budget">The adaptive execution budget.</param>
    /// <param name="cancellationToken">A token that can cancel the run between simulation dispatches.</param>
    public T RunToCompletion<T>(
        Func<Task<T>> taskFactory,
        AdaptiveExecutionBudget budget,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(taskFactory);
        ArgumentNullException.ThrowIfNull(budget);
        cancellationToken.ThrowIfCancellationRequested();
        using var control = Scheduler.EnterControlScope();
        using var lockScope = Guard.Enter();

        Task<T> task = StartTask(taskFactory);
        SimulationExecutionResult result = RunUntil(() => task.IsCompleted, budget, cancellationToken);
        EnsureTaskCompleted(task, result);
        return task.GetAwaiter().GetResult();
    }

    internal Task StartTask(Func<Task> taskFactory)
    {
        Task<Task> outer;
        using (ExecutionContext.SuppressFlow())
        {
            outer = new Task<Task>(taskFactory);
        }

        outer.Start(TaskScheduler);
        return outer.Unwrap();
    }

    private Task<T> StartTask<T>(Func<Task<T>> taskFactory)
    {
        Task<Task<T>> outer;
        using (ExecutionContext.SuppressFlow())
        {
            outer = new Task<Task<T>>(taskFactory);
        }

        outer.Start(TaskScheduler);
        return outer.Unwrap();
    }

    private static void EnsureTaskCompleted(Task task, SimulationExecutionResult result)
    {
        if (!task.IsCompleted)
        {
            throw new TimeoutException(string.Create(
                CultureInfo.InvariantCulture,
                $"Task did not complete.{Environment.NewLine}{result.ToDetailedString()}"));
        }
    }

    /// <summary>
    /// Runs the simulation for the specified duration or until the maximum iterations are
    /// exceeded, returning a detailed result describing exactly why the run stopped, how much work
    /// it did, and what (if anything) is still pending.
    /// </summary>
    /// <param name="duration">The amount of time to advance.</param>
    /// <param name="maxIterations">Maximum iterations to run while processing tasks.</param>
    /// <returns>A detailed result describing the execution.</returns>
    /// <param name="cancellationToken">A token that can cancel the run between simulation dispatches.</param>
    /// <returns>A detailed result describing the execution.</returns>
    public SimulationExecutionResult RunFor(
        TimeSpan duration,
        CancellationToken cancellationToken,
        int maxIterations = 100_000)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero);
        cancellationToken.ThrowIfCancellationRequested();

        using var control = Scheduler.EnterControlScope();
        using var lockScope = Guard.Enter();
        var startTime = TimeProvider.GetUtcNow();

        if (duration == TimeSpan.Zero)
        {
            return new SimulationExecutionResult(
                SimulationExecutionReason.Idle,
                startTime,
                startTime,
                iterations: 0,
                stepsExecuted: 0,
                timeAdvanceCount: 0,
                consecutiveTimeAdvanceCount: 0,
                CapturePendingWorkSummary(),
                new SimulationExecutionLimits(maxIterations, MaxSimulatedTimeAdvance, MaxConsecutiveTimeAdvances),
                attemptedTimeAdvance: null);
        }

        var targetTime = Scheduler.UtcNow + duration;
        return ExecuteDriveLoop(
            condition: null,
            maxSimulatedTimeAdvance: duration,
            maxIterations,
            observeTeardownCancellation: true,
            initialConsecutiveTimeAdvances: 0,
            absoluteEndTime: targetTime,
            cancellationToken: cancellationToken);
    }
#pragma warning restore CA1068

    /// <summary>Runs the consolidated drive-loop engine for one logical operation.</summary>
    private SimulationExecutionResult ExecuteDriveLoop(
        Func<bool>? condition,
        TimeSpan maxSimulatedTimeAdvance,
        int maxIterations,
        bool observeTeardownCancellation,
        int initialConsecutiveTimeAdvances,
        DateTimeOffset? absoluteEndTime,
        CancellationToken cancellationToken,
        SimulationProgressReporter? progressReporter = null,
        bool enableProgress = true)
    {
        using var control = Scheduler.EnterControlScope();
        using var _ = Guard.Enter();
        if (enableProgress && _disposalFailures is null)
        {
            progressReporter ??= SimulationProgressReporter.CreateFromEnvironment(
                RuntimeIdentity,
                CapturePendingWorkSummary,
                () => Scheduler.PendingOperationCount);
        }

        var options = new SimulationDriveLoopOptions(
            condition,
            maxSimulatedTimeAdvance,
            maxIterations,
            MaxConsecutiveTimeAdvances,
            observeTeardownCancellation,
            initialConsecutiveTimeAdvances,
            absoluteEndTime,
            cancellationToken,
            progressReporter is null ? null : progressReporter.Report);
        SimulationExecutionResult result = _driveLoop.Execute(options);
        progressReporter?.CompleteBatch(result);
        return result;
    }

    /// <summary>
    /// Combines the two RunUntilIdle passes of <see cref="RunFor(TimeSpan, CancellationToken, int)"/> (plus the
    /// forced advance to the target time, if any) into a single result describing the whole operation.
    /// </summary>
    private static SimulationExecutionResult CombineExecutionResults(
        DateTimeOffset startTime,
        SimulationExecutionResult first,
        SimulationExecutionResult? second,
        int forcedTimeAdvanceCount)
    {
        var final = second ?? first;
        return new SimulationExecutionResult(
            final.Reason,
            startTime,
            final.EndTime,
            first.Iterations + (second?.Iterations ?? 0),
            first.StepsExecuted + (second?.StepsExecuted ?? 0),
            first.TimeAdvanceCount + (second?.TimeAdvanceCount ?? 0) + forcedTimeAdvanceCount,
            final.ConsecutiveTimeAdvanceCount,
            final.PendingWork,
            final.Limits,
            final.AttemptedTimeAdvance);
    }

    /// <summary>
    /// Captures a snapshot of runnable, waiting, and blocked work across the cluster queue and
    /// every node queue (including suspended nodes), with per-item diagnostics in stable order.
    /// </summary>
    private SimulationPendingWorkSummary CapturePendingWorkSummary()
    {
        using var _ = Guard.Enter();
        var now = Scheduler.UtcNow;
        var diagnostics = new List<SimulationScheduledItemDiagnostic>();
        var runnableCount = 0;
        var waitingCount = 0;
        var blockedCount = 0;

        CollectQueueDiagnostics("cluster", SchedulerLane, isSuspended: false);
        foreach (var node in Nodes)
        {
            CollectQueueDiagnostics(node.NetworkAddress, node.Context.SchedulerLane, node.Context.State == SimulationNodeState.Suspended);
        }

        foreach (var timer in Scheduler.CapturePendingTimers())
        {
            var dueTime = StartDateTime + timer.DueTime;
            diagnostics.Add(new SimulationScheduledItemDiagnostic(
                "simulation-scheduler",
                timer.Kind,
                "Simulation scheduler timer",
                dueTime,
                timer.Sequence,
                dueTime <= now,
                IsBlocked: false));
            if (dueTime <= now)
            {
                runnableCount++;
            }
            else
            {
                waitingCount++;
            }
        }

        var orderedDiagnostics = diagnostics
            .OrderBy(static d => d.DueTime)
            .ThenBy(static d => d.SequenceNumber)
            .ThenBy(static d => d.QueueIdentity, StringComparer.Ordinal)
            .ToArray();

        return new SimulationPendingWorkSummary(runnableCount, waitingCount, blockedCount, orderedDiagnostics);

        void CollectQueueDiagnostics(string queueIdentity, SimulationSchedulerLane queue, bool isSuspended)
        {
            foreach (var item in queue.CaptureScheduledItems())
            {
                var isReady = item.IsReady;
                var isBlocked = isReady && isSuspended;
                diagnostics.Add(item with
                {
                    QueueIdentity = queueIdentity,
                    IsBlocked = isBlocked,
                });

                if (isBlocked)
                {
                    blockedCount++;
                }
                else if (isReady)
                {
                    runnableCount++;
                }
                else
                {
                    waitingCount++;
                }
            }
        }
    }

    /// <summary>
    /// Cancels <see cref="TeardownCancellationToken"/> once the caller's token is cancelled. The caller's token may be
    /// cancelled on any thread, while the callbacks registered on the teardown token belong to the system under test,
    /// so the drive loop cancels it on the simulation's logical thread - before each step, and where it checks for
    /// teardown - instead of linking the two tokens. A callback's exception propagates from the drive.
    /// </summary>
    private void ObserveCallerCancellation()
    {
        if (_callerCancellation.IsCancellationRequested && !_teardownCts.IsCancellationRequested)
        {
            SimulationCompletion.Cancel(_teardownCts, TeardownApi);
        }
    }

    private bool IsTeardownRequested()
    {
        ObserveCallerCancellation();
        return _teardownCts.IsCancellationRequested;
    }

    /// <summary>
    /// Safely cancels a CancellationTokenSource, catching and optionally logging any exceptions.
    /// </summary>
    private static void SafeCancel(CancellationTokenSource cts, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(cts);
        try
        {
            SimulationCompletion.Cancel(cts, TeardownApi);
        }
        catch (ObjectDisposedException)
        {
            // CTS was already disposed - this is fine
        }
        catch (AggregateException ex)
        {
            // Log but don't throw - we're in cleanup
#pragma warning disable CA1848 // Use the LoggerMessage delegates - this is rarely called cleanup code
            logger?.LogWarning(ex, "Exception during cancellation");
#pragma warning restore CA1848
        }
    }

    /// <summary>
    /// Disposes the cluster after canceling <see cref="TeardownCancellationToken"/> and driving
    /// pending simulation work and node disposal work to quiescence. Driving may advance virtual
    /// time to scheduled deadlines, subject to the cluster's configured execution bounds. Pending
    /// lane work is completed rather than discarded.
    /// </summary>
    /// <returns>A completed value task when teardown succeeds.</returns>
    /// <exception cref="AggregateException">
    /// One or more pending callbacks or disposal steps failed, or teardown reached an execution
    /// bound. Cleanup continues before the failures are aggregated.
    /// </exception>
    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        // Disposal cancels the teardown token, whose callbacks the system under test registered from simulation work,
        // and drives the remaining work, so it runs on the simulation's logical thread.
        using IDisposable control = Scheduler.EnterControlScope();
        ThrowIfAttachmentInProgress();
        _disposed = true;
        List<Exception> failures = [];
        List<SimulationNodeContext> cleanupContexts = [];

        foreach (var registration in _nodeRegistrations)
        {
            if (TryBeginAttachmentCleanup(registration.AttachmentContext, failures))
            {
                cleanupContexts.Add(registration.AttachmentContext);
            }
        }

        SafeCancel(_teardownCts);
        DrainAttachmentWorkToQuiescence(cleanupContexts, failures);

        RunDisposalToCompletion(
            async () => await DisposeNodesAsync(),
            failures);

        try
        {
            Scheduler.Dispose();
        }
        catch (Exception exception)
        {
            AddDisposalFailure(failures, exception);
        }

        // Escaped work which reached a controlled API after the last disposal step still fails the run.
        if (Scheduler.TakeThreadAffinityViolation() is { } violation)
        {
            AddDisposalFailure(failures, violation.SourceException);
        }

        try
        {
            _teardownCts.Dispose();
        }
        catch (Exception exception)
        {
            AddDisposalFailure(failures, exception);
        }

        GC.SuppressFinalize(this);

        if (failures.Count > 0)
        {
            throw new AggregateException("One or more errors occurred while disposing the simulation cluster.", failures);
        }

        return ValueTask.CompletedTask;
    }

    private static void AddDisposalFailure(ref List<Exception>? failures, Exception exception)
    {
        failures ??= [];
        AddDisposalFailure(failures, exception);
    }

    private static void AddDisposalFailure(List<Exception> failures, Exception exception)
    {
        if (exception is AggregateException aggregate)
        {
            failures.AddRange(aggregate.Flatten().InnerExceptions);
        }
        else
        {
            failures.Add(exception);
        }
    }

    private static bool TryBeginAttachmentCleanup(
        SimulationNodeContext context,
        List<Exception> failures)
    {
        try
        {
            context.BeginAttachmentCleanup();
            return true;
        }
        catch (Exception exception)
        {
            AddDisposalFailure(failures, exception);
            return false;
        }
    }

    private void DrainAttachmentWorkToQuiescence(
        List<SimulationNodeContext> contexts,
        List<Exception> failures)
    {
        if (contexts.Count == 0)
        {
            return;
        }

        List<Exception>? previousFailures = _disposalFailures;
        _disposalFailures = failures;
        try
        {
            SimulationExecutionResult result = ExecuteDriveLoop(
                () => contexts.All(static context => !context.HasPendingAttachmentWork),
                MaxSimulatedTimeAdvance,
                DisposalMaxIterations,
                observeTeardownCancellation: false,
                initialConsecutiveTimeAdvances: 0,
                absoluteEndTime: null,
                cancellationToken: CancellationToken.None,
                enableProgress: false);
            if (contexts.Any(static context => context.HasPendingAttachmentWork))
            {
                AddDisposalFailure(
                    failures,
                    new TimeoutException(string.Create(
                        CultureInfo.InvariantCulture,
                        $"Node attachment cleanup did not reach quiescence.{Environment.NewLine}{result.ToDetailedString()}")));
            }
        }
        catch (Exception exception)
        {
            AddDisposalFailure(failures, exception);
        }
        finally
        {
            _disposalFailures = previousFailures;
        }
    }

    private SimulationNodeContext BeginAttachment(string address)
    {
        ArgumentException.ThrowIfNullOrEmpty(address);
        ThrowIfDisposed();
        using var _ = Guard.Enter();
        if (_attachments.ContainsKey(address))
        {
            throw new ArgumentException(
                $"A node with address '{address}' has already been attached to this cluster.",
                nameof(address));
        }

        SimulationNodeContext context = CreateNodeContext(address);
        _attachments.Add(address, context);
        return context;
    }

    private void ThrowIfAttachmentInProgress()
    {
        using var _ = Guard.Enter();
        if (_attachments.Values.Any(static context => context.IsAttachmentInProgress))
        {
            throw new InvalidOperationException(
                "Cannot dispose the simulation cluster while a node attachment factory is in progress.");
        }
    }

    private void CompleteFailedAttachment(string address, SimulationNodeContext context)
    {
        try
        {
            context.CompleteAttachmentCleanup();
        }
        finally
        {
            using var _ = Guard.Enter();
            if (_attachments.TryGetValue(address, out SimulationNodeContext? current)
                && ReferenceEquals(current, context))
            {
                _attachments.Remove(address);
            }
        }
    }

    private void RegisterNode(
        SimulationNodeContext attachmentContext,
        SimulationNode node)
    {
        using var _ = Guard.Enter();
        ThrowIfDisposed();
        if (_nodes.ContainsKey(node.NetworkAddress))
        {
            throw new InvalidOperationException(
                $"Node with address '{node.NetworkAddress}' already exists.");
        }

        attachmentContext.CompleteAttachment();
        _nodes.Add(node.NetworkAddress, node);
        _nodeRegistrations.Add(new NodeRegistration(node.NetworkAddress, node, attachmentContext));
    }

    private static void ValidateCustomNode(
        string requestedAddress,
        SimulationNodeContext attachmentContext,
        SimulationNode node)
    {
        if (!ReferenceEquals(node.Context, attachmentContext))
        {
            throw new InvalidOperationException(
                $"The factory for node '{requestedAddress}' returned a node whose context is not " +
                "the supplied attachment context.");
        }

        string actualAddress = node.NetworkAddress;
        if (string.IsNullOrEmpty(actualAddress))
        {
            throw new InvalidOperationException(
                $"The factory for node '{requestedAddress}' returned a node with a null or empty network address.");
        }

        if (!string.Equals(requestedAddress, actualAddress, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The factory for node '{requestedAddress}' returned a node with address '{actualAddress}'. " +
                "Custom node addresses must exactly match the requested address.");
        }
    }

    private async ValueTask DisposeNodesAsync()
    {
        List<Exception>? failures = null;
        foreach (var registration in _nodeRegistrations)
        {
            try
            {
                await DisposeIfDisposableAsync(registration.Node);
            }
            catch (Exception exception)
            {
                AddDisposalFailure(ref failures, exception);
            }

            try
            {
                registration.AttachmentContext.CompleteAttachmentCleanup();
            }
            catch (Exception exception)
            {
                AddDisposalFailure(ref failures, exception);
            }

            using (Guard.Enter())
            {
                _nodes.Remove(registration.Address);
                _attachments.Remove(registration.Address);
            }
        }

        _nodeRegistrations.Clear();
        _attachments.Clear();
        if (failures is not null)
        {
            throw new AggregateException(
                "One or more simulation nodes failed to dispose.",
                failures);
        }
    }

    private static async ValueTask DisposeIfDisposableAsync(object? target)
    {
        switch (target)
        {
            case IAsyncDisposable asyncDisposable:
                await asyncDisposable.DisposeAsync();
                break;
            case IDisposable disposable:
                disposable.Dispose();
                break;
        }
    }

    private void DisposeFailedAttachmentTarget(object? target, List<Exception> failures)
    {
        RunDisposalToCompletion(() => DisposeIfDisposableAsync(target).AsTask(), failures);
    }

    private void RunDisposalToCompletion(Func<Task> taskFactory, List<Exception> failures)
    {
        List<Exception>? previousFailures = _disposalFailures;
        _disposalFailures = failures;
        try
        {
            RunToCompletion(taskFactory, CancellationToken.None, DisposalMaxIterations);
        }
        catch (Exception exception)
        {
            AddDisposalFailure(failures, exception);
        }
        finally
        {
            _disposalFailures = previousFailures;
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed record NodeRegistration(
        string Address,
        SimulationNode Node,
        SimulationNodeContext AttachmentContext);

    private string DebuggerDisplay => string.Create(CultureInfo.InvariantCulture, $"SimulationCluster(Seed={Seed}, Nodes={Nodes.Count}, Time={Scheduler.VirtualTime:hh\\:mm\\:ss\\.fff})");
}
