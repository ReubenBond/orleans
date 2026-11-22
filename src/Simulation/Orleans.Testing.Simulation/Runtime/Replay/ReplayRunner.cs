using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Orleans.Testing.Simulation.Runtime.Decisions;
using Orleans.Testing.Simulation.Runtime.Execution;
using Orleans.Testing.Simulation.Runtime.Random;
using Orleans.Testing.Simulation.Runtime.Racing;
using Orleans.Testing.Simulation.Runtime.Scheduling;
using Orleans.Testing.Simulation.Runtime.Scheduling.Resources;
using Orleans.Testing.Simulation.Runtime.Scheduling.Strategies;
using Orleans.Testing.Simulation.Runtime.Shims;

namespace Orleans.Testing.Simulation.Runtime.Replay;

/// <summary>Supported deterministic scheduling policies for recorded execution.</summary>
public enum ReplaySchedulingPolicy
{
    /// <summary>Select the earliest registered runnable operation and waiter.</summary>
    Fifo,

    /// <summary>Rotate fairly among runnable operations and use FIFO waiter order.</summary>
    RoundRobin,

    /// <summary>Prefer higher-priority operations with deterministic round-robin tie breaking.</summary>
    Priority,

    /// <summary>Choose runnable operations and resource waiters from one explicit seeded stream.</summary>
    SeededRandom,
}

/// <summary>Configures one recorded execution.</summary>
public sealed record ReplayRecordingOptions
{
    /// <summary>Gets the seed for simulated application, network, identity, and fault decisions.</summary>
    public required int SimulationSeed { get; init; }

    /// <summary>Gets the scheduler policy.</summary>
    public ReplaySchedulingPolicy SchedulingPolicy { get; init; } = ReplaySchedulingPolicy.RoundRobin;

    /// <summary>
    /// Gets the schedule seed. When omitted for seeded-random scheduling, it is derived from
    /// <see cref="SimulationSeed"/> in the scheduler seed domain and recorded explicitly.
    /// </summary>
    public int? ScheduleSeed { get; init; }

    /// <summary>Gets the maximum number of controlled operation steps.</summary>
    public int MaxSteps { get; init; } = 1_000_000;

    /// <summary>
    /// Gets the simulated processor count the run's runtime uses (see <see cref="SimulationCluster.ProcessorCount"/>).
    /// Defaults to 4. The artifact records it, and replay runs with the recorded value.
    /// </summary>
    public int ProcessorCount { get; init; } = SimulationRuntimeEnvironment.DefaultProcessorCount;

    /// <summary>Gets the instrumentation closure identity, when applicable.</summary>
    public ReplayInstrumentationIdentity? Instrumentation { get; init; }

    /// <summary>Gets whether bounded exception messages may be included in artifact diagnostics.</summary>
    public bool IncludeDiagnosticMessages { get; init; }

    /// <summary>Gets whether source document paths may be included in race scheduling points.</summary>
    public bool IncludeSourcePaths { get; init; }
}

/// <summary>Results from one record or replay execution.</summary>
public sealed record ReplayExecutionResult
{
    /// <summary>Gets the deterministic replay artifact.</summary>
    public required ReplayArtifact Artifact { get; init; }

    /// <summary>Gets the SHA-256 artifact identity.</summary>
    public required string ArtifactId { get; init; }

    /// <summary>Gets the number of controlled operation steps executed.</summary>
    public required int Steps { get; init; }

    /// <summary>Gets the original exception which aborted orchestration, when present.</summary>
    public Exception? ExecutionException { get; init; }

    /// <summary>Gets whether a replay reproduced the recorded terminal outcome.</summary>
    public bool Reproduced { get; init; }

}

/// <summary>Thrown when replay reaches a different terminal outcome than the artifact.</summary>
public sealed class ReplayOutcomeMismatchException : InvalidOperationException
{
    /// <summary>Initializes a terminal outcome mismatch.</summary>
    public ReplayOutcomeMismatchException(ReplayOutcome expected, ReplayOutcome actual)
        : base(
            $"Replay terminal outcome diverged: expected {Describe(expected)}, observed {Describe(actual)}.")
    {
        Expected = expected;
        Actual = actual;
    }

    /// <summary>Gets the recorded outcome.</summary>
    public ReplayOutcome Expected { get; }

    /// <summary>Gets the replay outcome.</summary>
    public ReplayOutcome Actual { get; }

    private static string Describe(ReplayOutcome outcome) =>
        $"{outcome.Kind} identity='{outcome.FailureIdentity ?? "<none>"}'";
}

/// <summary>
/// Records and replays controlled scheduler scenarios. A scenario is the run's entry: a synchronous
/// callback (<see cref="Record"/>, <see cref="RecordCluster"/>) registers controlled work, and an
/// entry-task scenario (<see cref="RecordClusterEntry"/>) returns the entry task. Artifacts record the
/// entry form, and replay rejects the other form. This runner owns scheduler creation, driving, terminal
/// classification (see <see cref="ReplayTerminationKind"/>), and replay validation.
/// </summary>
public static class ReplayRunner
{
    private const string ExecutionHostOption = "executionHost";
    private const string ClusterExecutionHost = "cluster";
    private const string EntryOption = "entry";
    private const string CallbackEntry = "callback";
    private const string TaskEntry = "task";
    private const string EntryTaskCanceledIdentity = "entry-task-canceled";
    private const string EntryTaskIncompleteIdentity = "entry-task-incomplete";

    /// <summary>
    /// Records one controlled scenario with explicit orchestration cancellation. The callback is a
    /// synchronous entry: it registers controlled work, and the runner drives that work until it
    /// quiesces or the run ends otherwise.
    /// </summary>
    public static ReplayExecutionResult Record(
        ReplayRecordingOptions configuration,
        Action<SimulationScheduler> scenario,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(scenario);
        ValidateConfiguration(configuration);

        int scheduleSeed = GetScheduleSeed(configuration);
        var decisionLog = new SimulationDecisionLog();
        var listener = new ReplayOperationListener(configuration.IncludeDiagnosticMessages);
        using SimulationScheduler scheduler = CreateScheduler(configuration.SimulationSeed, configuration.ProcessorCount, listener);
        scheduler.SchedulingStrategy = CreateStrategy(configuration.SchedulingPolicy, scheduleSeed);
        scheduler.DecisionLog = decisionLog;

        DriveResult drive = Drive(
            scheduler,
            listener,
            () =>
            {
                scenario(scheduler);
                return null;
            },
            configuration.MaxSteps,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        ReplayArtifact artifact = CreateArtifact(
            configuration,
            scheduleSeed,
            decisionLog.Records,
            scheduler,
            listener,
            drive);
        return new ReplayExecutionResult
        {
            Artifact = artifact,
            ArtifactId = ReplayArtifactSerializer.ComputeId(artifact),
            Steps = drive.Steps,
            ExecutionException = drive.Exception,
            Reproduced = false,
        };
    }

    /// <summary>
    /// Records one fresh <see cref="SimulationCluster"/> scenario with explicit orchestration
    /// cancellation. The runner owns cluster creation, driving, artifact capture, and disposal.
    /// The callback is a synchronous entry: it registers work and returns, and the runner drives that
    /// work until it quiesces or the run ends otherwise.
    /// </summary>
    public static ReplayExecutionResult RecordCluster(
        ReplayRecordingOptions configuration,
        Action<SimulationCluster> scenario,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(scenario);
        return RecordClusterCore(configuration, StartSynchronousEntry(scenario), isEntryTask: false, cancellationToken);
    }

    /// <summary>
    /// Records one fresh <see cref="SimulationCluster"/> scenario whose returned task is the run's entry
    /// task. The runner starts the scenario on the cluster's task scheduler, as
    /// <see cref="SimulationCluster.RunToCompletion(Func{Task}, CancellationToken, int)"/> does, drives
    /// until the entry task completes or the run ends otherwise, and classifies the run from the entry
    /// task's terminal state (see <see cref="ReplayTerminationKind"/>). The artifact records the entry
    /// form, and only <see cref="ReplayClusterEntry"/> replays it.
    /// </summary>
    public static ReplayExecutionResult RecordClusterEntry(
        ReplayRecordingOptions configuration,
        Func<SimulationCluster, Task> scenario,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(scenario);
        return RecordClusterCore(configuration, StartEntryTask(scenario), isEntryTask: true, cancellationToken);
    }

    private static ReplayExecutionResult RecordClusterCore(
        ReplayRecordingOptions configuration,
        Func<SimulationCluster, Task?> startEntry,
        bool isEntryTask,
        CancellationToken cancellationToken)
    {
        ValidateConfiguration(configuration);

        int scheduleSeed = GetScheduleSeed(configuration);
        var decisionLog = new SimulationDecisionLog();
        var listener = new ReplayOperationListener(configuration.IncludeDiagnosticMessages);
        SimulationCluster cluster = CreateCluster(configuration.SimulationSeed, configuration.ProcessorCount, listener);
        try
        {
            SimulationScheduler scheduler = cluster.Scheduler;
            scheduler.SchedulingStrategy = CreateStrategy(configuration.SchedulingPolicy, scheduleSeed);
            scheduler.DecisionLog = decisionLog;

            DriveResult drive = Drive(
                scheduler,
                listener,
                () => startEntry(cluster),
                configuration.MaxSteps,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            ReplayArtifact artifact = CreateArtifact(
                configuration,
                scheduleSeed,
                decisionLog.Records,
                scheduler,
                listener,
                drive,
                ClusterExecutionHost,
                isEntryTask);
            return new ReplayExecutionResult
            {
                Artifact = artifact,
                ArtifactId = ReplayArtifactSerializer.ComputeId(artifact),
                Steps = drive.Steps,
                ExecutionException = drive.Exception,
                Reproduced = false,
            };
        }
        finally
        {
            DetachReplayState(cluster.Scheduler);
            cluster.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    /// <summary>Replays a complete artifact exactly with explicit orchestration cancellation.</summary>
    public static ReplayExecutionResult Replay(
        ReplayArtifact artifact,
        ReplayCompatibilityRequirements requirements,
        Action<SimulationScheduler> scenario,
        int maxSteps,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(requirements);
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSteps);
        ReplayCompatibility.Validate(artifact, requirements);
        ValidateExecutionHost(artifact, expectCluster: false, expectEntryTask: false);
        int processorCount = ReplayCompatibility.GetProcessorCount(artifact);

        var records = artifact.Decisions.Select(static decision => decision.ToRecord()).ToArray();
        var listener = new ReplayOperationListener(includeDiagnosticMessages: false);
        using SimulationScheduler scheduler = CreateScheduler(artifact.SimulationSeed, processorCount, listener);
        scheduler.SchedulingStrategy = SimulationSchedulingStrategies.Replay(records);
        scheduler.ReplayValidator = new SimulationDecisionReplayValidator(
            new SimulationInMemoryDecisionReplayReader(records));

        DriveResult drive = Drive(
            scheduler,
            listener,
            () =>
            {
                scenario(scheduler);
                return null;
            },
            maxSteps,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!drive.IsAborted)
        {
            scheduler.ValidateReplayDecisionStreamsComplete();
        }

        ReplayRecordingOptions replayConfiguration = new()
        {
            SimulationSeed = artifact.SimulationSeed,
            SchedulingPolicy = ParsePolicy(artifact.Scheduler.Strategy),
            ScheduleSeed = artifact.Scheduler.ScheduleSeed,
            MaxSteps = maxSteps,
            ProcessorCount = processorCount,
            Instrumentation = artifact.Instrumentation,
        };
        ReplayArtifact replayArtifact = CreateArtifact(
            replayConfiguration,
            artifact.Scheduler.ScheduleSeed ?? 0,
            records,
            scheduler,
            listener,
            drive);
        if (!OutcomesMatch(artifact.Outcome, replayArtifact.Outcome))
        {
            throw new ReplayOutcomeMismatchException(artifact.Outcome, replayArtifact.Outcome);
        }

        return new ReplayExecutionResult
        {
            Artifact = artifact,
            ArtifactId = ReplayArtifactSerializer.ComputeId(artifact),
            Steps = drive.Steps,
            ExecutionException = drive.Exception,
            Reproduced = true,
        };
    }

    /// <summary>
    /// Replays a cluster artifact exactly against one fresh <see cref="SimulationCluster"/> and
    /// disposes the cluster after replay validation. The callback is a synchronous entry, as for
    /// <see cref="RecordCluster(ReplayRecordingOptions, Action{SimulationCluster}, CancellationToken)"/>;
    /// an artifact recorded with an entry task is rejected.
    /// </summary>
    public static ReplayExecutionResult ReplayCluster(
        ReplayArtifact artifact,
        ReplayCompatibilityRequirements requirements,
        Action<SimulationCluster> scenario,
        int maxSteps,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(requirements);
        ArgumentNullException.ThrowIfNull(scenario);
        return ReplayClusterCore(artifact, requirements, StartSynchronousEntry(scenario), isEntryTask: false, maxSteps, cancellationToken);
    }

    /// <summary>
    /// Replays an artifact recorded by <see cref="RecordClusterEntry"/> exactly against one fresh
    /// <see cref="SimulationCluster"/> whose scenario returns the run's entry task, and disposes the
    /// cluster after replay validation. An artifact recorded with a synchronous entry is rejected.
    /// </summary>
    public static ReplayExecutionResult ReplayClusterEntry(
        ReplayArtifact artifact,
        ReplayCompatibilityRequirements requirements,
        Func<SimulationCluster, Task> scenario,
        int maxSteps,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(requirements);
        ArgumentNullException.ThrowIfNull(scenario);
        return ReplayClusterCore(artifact, requirements, StartEntryTask(scenario), isEntryTask: true, maxSteps, cancellationToken);
    }

    private static ReplayExecutionResult ReplayClusterCore(
        ReplayArtifact artifact,
        ReplayCompatibilityRequirements requirements,
        Func<SimulationCluster, Task?> startEntry,
        bool isEntryTask,
        int maxSteps,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSteps);
        ReplayCompatibility.Validate(artifact, requirements);
        ValidateExecutionHost(artifact, expectCluster: true, expectEntryTask: isEntryTask);
        int processorCount = ReplayCompatibility.GetProcessorCount(artifact);

        var records = artifact.Decisions.Select(static decision => decision.ToRecord()).ToArray();
        var listener = new ReplayOperationListener(includeDiagnosticMessages: false);
        SimulationCluster cluster = CreateCluster(artifact.SimulationSeed, processorCount, listener);
        try
        {
            SimulationScheduler scheduler = cluster.Scheduler;
            scheduler.SchedulingStrategy = SimulationSchedulingStrategies.Replay(records);
            scheduler.ReplayValidator = new SimulationDecisionReplayValidator(
                new SimulationInMemoryDecisionReplayReader(records));

            DriveResult drive = Drive(
                scheduler,
                listener,
                () => startEntry(cluster),
                maxSteps,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!drive.IsAborted)
            {
                scheduler.ValidateReplayDecisionStreamsComplete();
            }

            ReplayRecordingOptions replayConfiguration = new()
            {
                SimulationSeed = artifact.SimulationSeed,
                SchedulingPolicy = ParsePolicy(artifact.Scheduler.Strategy),
                ScheduleSeed = artifact.Scheduler.ScheduleSeed,
                MaxSteps = maxSteps,
                ProcessorCount = processorCount,
                Instrumentation = artifact.Instrumentation,
            };
            ReplayArtifact replayArtifact = CreateArtifact(
                replayConfiguration,
                artifact.Scheduler.ScheduleSeed ?? 0,
                records,
                scheduler,
                listener,
                drive,
                ClusterExecutionHost,
                isEntryTask);
            if (!OutcomesMatch(artifact.Outcome, replayArtifact.Outcome))
            {
                throw new ReplayOutcomeMismatchException(artifact.Outcome, replayArtifact.Outcome);
            }

            return new ReplayExecutionResult
            {
                Artifact = artifact,
                ArtifactId = ReplayArtifactSerializer.ComputeId(artifact),
                Steps = drive.Steps,
                ExecutionException = drive.Exception,
                Reproduced = true,
            };
        }
        finally
        {
            DetachReplayState(cluster.Scheduler);
            cluster.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static SimulationScheduler CreateScheduler(int seed, int processorCount, ISimulationOperationListener listener)
    {
        var runtime = new SimulationRuntimeIdentity(Guid.NewGuid(), seed, "replay-run");
        return new SimulationScheduler(
            runtime,
            new SimulationSeedAuthority(seed),
            DateTimeOffset.UnixEpoch,
            TimeZoneInfo.Utc,
            listener,
            processorCount);
    }

    private static SimulationCluster CreateCluster(int seed, int processorCount, ISimulationOperationListener listener) =>
        new(
            seed,
            DateTimeOffset.UnixEpoch,
            TimeZoneInfo.Utc,
            processorCount,
            listener,
            CancellationToken.None);

    private static void DetachReplayState(SimulationScheduler scheduler)
    {
        scheduler.DecisionLog = null;
        scheduler.ReplayValidator = null;
        scheduler.SchedulingStrategy = SimulationSchedulingStrategies.RoundRobin();
    }

    private static ISimulationSchedulingStrategy CreateStrategy(ReplaySchedulingPolicy policy, int scheduleSeed) =>
        policy switch
        {
            ReplaySchedulingPolicy.Fifo => SimulationSchedulingStrategies.Fifo(),
            ReplaySchedulingPolicy.RoundRobin => SimulationSchedulingStrategies.RoundRobin(),
            ReplaySchedulingPolicy.Priority => SimulationSchedulingStrategies.Priority(),
            ReplaySchedulingPolicy.SeededRandom => SimulationSchedulingStrategies.SeededRandom(scheduleSeed),
            _ => throw new ArgumentOutOfRangeException(nameof(policy)),
        };

    // A synchronous scenario callback is an entry which ran to completion when it returned; the
    // runner represents it with no entry task.
    private static Func<SimulationCluster, Task?> StartSynchronousEntry(Action<SimulationCluster> scenario) =>
        cluster =>
        {
            scenario(cluster);
            return null;
        };

    private static Func<SimulationCluster, Task?> StartEntryTask(Func<SimulationCluster, Task> scenario) =>
        cluster => cluster.StartTask(
            () => scenario(cluster) ?? throw new InvalidOperationException("The replay scenario returned a null entry task."));

    /// <summary>
    /// Starts the entry and drives the scheduler until a race or an operation fault (including a lane
    /// callback's unhandled exception, which the lane also reports) stops the run; the entry task completes; nothing is runnable and no timer is
    /// pending; or the step bound is reached. Without an entry task, the run is the work the
    /// synchronous entry registered, so the runner drives it until it quiesces. A controlled API called
    /// off the simulation's logical thread aborts the run.
    /// </summary>
    private static DriveResult Drive(
        SimulationScheduler scheduler,
        ReplayOperationListener listener,
        Func<Task?> startEntry,
        int maxSteps,
        CancellationToken cancellationToken)
    {
        var steps = 0;
        Task? entry = null;
        using IDisposable control = scheduler.EnterControlScope();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            entry = startEntry();
            while (steps < maxSteps && entry is not { IsCompleted: true })
            {
                bool ran = scheduler.RunStepCapturingFailure(cancellationToken, out Exception? failure);
                if (ran)
                {
                    steps++;
                }

                if (failure is SimulationThreadAffinityException)
                {
                    return Aborted(failure);
                }

                if (failure is not null)
                {
                    return new DriveResult(
                        steps,
                        BoundExceeded: false,
                        Exception: null,
                        IsAborted: false,
                        entry);
                }

                if (ran)
                {
                    if (scheduler.FirstRace is not null || listener.FirstFailure is not null)
                    {
                        break;
                    }

                    continue;
                }

                if (scheduler.NextTimerDue is null)
                {
                    break;
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (!scheduler.TryAdvanceVirtualTime())
                {
                    break;
                }
            }

            if (scheduler.TakeThreadAffinityViolation() is { } violation)
            {
                return Aborted(violation.SourceException);
            }

            bool workRemains = entry is null
                ? scheduler.PendingOperationCount > 0
                : !entry.IsCompleted;
            return new DriveResult(
                steps,
                BoundExceeded: steps >= maxSteps && workRemains,
                Exception: null,
                IsAborted: false,
                entry);
        }
        catch (Exception exception) when (
            exception is not SimulationDecisionReplayMismatchException &&
            exception is not ReplayOutcomeMismatchException)
        {
            return Aborted(exception);
        }

        // Work which escaped the simulation onto a real thread ran outside the recorded schedule, so a thread-affinity
        // violation aborts the run, as an exception from the runner does.
        DriveResult Aborted(Exception exception) =>
            new(steps, BoundExceeded: false, exception, IsAborted: true, entry);
    }

    private static ReplayArtifact CreateArtifact(
        ReplayRecordingOptions configuration,
        int scheduleSeed,
        IReadOnlyList<SimulationDecisionRecord> decisions,
        SimulationScheduler scheduler,
        ReplayOperationListener listener,
        DriveResult drive,
        string? executionHost = null,
        bool isEntryTask = false)
    {
        ReplayOutcome outcome = ClassifyOutcome(configuration, scheduler, listener, drive);
        IReadOnlyList<ReplayRaceSchedulingPoint> points = scheduler.CaptureRaceSchedulingPoints()
            .Select(point =>
            {
                ReplayRaceSchedulingPoint replayPoint = ReplayRaceSchedulingPoint.FromPoint(point);
                return configuration.IncludeSourcePaths
                    ? replayPoint
                    : replayPoint with { SourceFile = null };
            })
            .ToArray();

        var schedulerOptions = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["maxSteps"] = configuration.MaxSteps.ToString(CultureInfo.InvariantCulture),
            [ReplayCompatibility.ProcessorCountOption] = configuration.ProcessorCount.ToString(CultureInfo.InvariantCulture),
            ["resourceWaiters"] = configuration.SchedulingPolicy == ReplaySchedulingPolicy.SeededRandom
                ? "seeded-random"
                : "fifo",
        };
        if (executionHost is not null)
        {
            schedulerOptions[ExecutionHostOption] = executionHost;
        }

        // Only an entry-task run records its form; an absent option is a synchronous callback entry,
        // so callback artifacts are unchanged.
        if (isEntryTask)
        {
            schedulerOptions[EntryOption] = TaskEntry;
        }

        return new ReplayArtifact
        {
            SimulationSeed = configuration.SimulationSeed,
            Scheduler = new ReplaySchedulerConfiguration
            {
                Strategy = GetStrategyName(configuration.SchedulingPolicy),
                ScheduleSeed = configuration.SchedulingPolicy == ReplaySchedulingPolicy.SeededRandom
                    ? scheduleSeed
                    : null,
                Options = schedulerOptions,
            },
            Instrumentation = configuration.Instrumentation,
            Environment = ReplayCompatibility.CaptureEnvironment(),
            Decisions = decisions.Select(static decision => ReplayDecision.FromRecord(decision)).ToArray(),
            RaceSchedulingPoints = points,
            Outcome = outcome,
            Diagnostics = CaptureDiagnostics(
                scheduler,
                configuration.IncludeDiagnosticMessages,
                configuration.IncludeSourcePaths),
        };
    }

    private static ReplayDiagnosticSnapshot CaptureDiagnostics(
        SimulationScheduler scheduler,
        bool includeUserMetadata,
        bool includeSourcePaths)
    {
        SimulationDeadlockReport deadlock = scheduler.DetectDeadlock();
        ReplayOperationDiagnostic[] operations = scheduler.CaptureStatus()
            .Select(status => new ReplayOperationDiagnostic
            {
                Id = status.Id.Value,
                ParentId = status.ParentId.Value,
                State = status.State.ToString(),
                Node = includeUserMetadata ? status.NodeAddress : null,
                Description = includeUserMetadata ? status.WorkDescription : null,
                WaitReason = includeUserMetadata ? status.PauseReason?.ToString() : null,
            })
            .ToArray();
        ReplayResourceDiagnostic[] resources = scheduler.CaptureResources()
            .Select(resource => new ReplayResourceDiagnostic
            {
                Id = resource.Id.Value,
                Kind = resource.Kind.ToString(),
                Name = includeUserMetadata ? resource.Name : null,
                OwnerId = resource.Owner?.Id.Value,
                Waiters = resource.SnapshotWaiters()
                    .Where(static waiter => waiter.Resolution is null)
                    .Select(waiter => new ReplayWaiterDiagnostic
                    {
                        OperationId = waiter.OperationId.Value,
                        EnqueueSequence = waiter.EnqueueSequence,
                        TimeoutTicks = waiter.TimeoutDueTime?.Ticks,
                        Reason = includeUserMetadata ? waiter.Reason : null,
                    })
                    .ToArray(),
            })
            .ToArray();
        ReplayTimerDiagnostic[] timers = scheduler.CapturePendingTimeouts()
            .Select(static timer => new ReplayTimerDiagnostic
            {
                DueTicks = timer.DueTime.Ticks,
                Sequence = timer.Sequence,
                OperationId = timer.OperationId.Value,
                ResourceId = timer.ResourceId.Value,
            })
            .ToArray();
        ReplayDeadlockCycleDiagnostic[] cycles = deadlock.Cycles
            .Select(static cycle => new ReplayDeadlockCycleDiagnostic
            {
                Edges = cycle.Entries.Select(static entry => new ReplayDeadlockEdgeDiagnostic
                {
                    OperationId = entry.OperationId.Value,
                    ResourceId = entry.ResourceId.Value,
                    OwnerId = entry.OwnerId.Value,
                    EnqueueSequence = entry.EnqueueSequence,
                }).ToArray(),
            })
            .ToArray();

        return new ReplayDiagnosticSnapshot
        {
            Liveness = deadlock.Liveness.ToString(),
            VirtualTimeTicks = scheduler.VirtualTime.Ticks,
            Operations = operations,
            Resources = resources,
            PendingTimers = timers,
            DeadlockCycles = cycles,
            Race = scheduler.FirstRace is { } race
                ? new ReplayRacePairDiagnostic
                {
                    First = CaptureRaceAccess(race.FirstAccess, includeSourcePaths),
                    Second = CaptureRaceAccess(race.SecondAccess, includeSourcePaths),
                }
                : null,
        };
    }

    private static ReplayRaceAccessDiagnostic CaptureRaceAccess(
        RaceAccessRecord access,
        bool includeSourcePaths) => new()
        {
            OperationId = access.OperationId.Value,
            Kind = access.Kind.ToString(),
            Location = access.Location.ToString(),
            Member = access.Source.Method,
            ILOffset = access.Source.ILOffset,
            SourceFile = includeSourcePaths ? access.Source.SourceFile : null,
            SourceLine = access.Source.SourceLine,
        };

    private static ReplayOutcome ClassifyOutcome(
        ReplayRecordingOptions configuration,
        SimulationScheduler scheduler,
        ReplayOperationListener listener,
        DriveResult drive)
    {
        if (drive.IsAborted)
        {
            return new ReplayOutcome
            {
                Kind = ReplayTerminationKind.Aborted,
                FailureIdentity = drive.Exception?.GetType().FullName,
                Diagnostic = configuration.IncludeDiagnosticMessages
                    ? Bound(drive.Exception?.Message)
                    : null,
            };
        }

        if (scheduler.FirstRace is { } race)
        {
            string identity = ComputeRaceIdentity(race);
            return new ReplayOutcome
            {
                Kind = ReplayTerminationKind.RaceDetected,
                FailureIdentity = identity,
                Diagnostic = configuration.IncludeDiagnosticMessages ? Bound(race.ToString()) : null,
            };
        }

        if (listener.FirstFailure is { } failure)
        {
            return new ReplayOutcome
            {
                Kind = ReplayTerminationKind.Faulted,
                FailureIdentity = failure.ExceptionType,
                Diagnostic = configuration.IncludeDiagnosticMessages ? Bound(failure.Message) : null,
            };
        }

        // A completed entry task ends the run, as a process ends when Main returns, so a wait-for cycle
        // left behind is reported in the diagnostics (DeadlockCycles) with the other leftover work rather
        // than deciding the outcome. A synchronous entry's run continues until quiescence, so its cycles
        // decide the run, as do cycles found while an entry task is still incomplete.
        SimulationDeadlockReport deadlock = scheduler.DetectDeadlock();
        if (drive.Entry is not { IsCompleted: true } && deadlock.IsDeadlocked)
        {
            return new ReplayOutcome
            {
                Kind = ReplayTerminationKind.Deadlocked,
                FailureIdentity = ComputeDeadlockIdentity(deadlock),
                Diagnostic = configuration.IncludeDiagnosticMessages ? Bound(scheduler.DescribeLiveness()) : null,
            };
        }

        if (drive.BoundExceeded)
        {
            return new ReplayOutcome
            {
                Kind = ReplayTerminationKind.BoundExceeded,
                FailureIdentity = "step-bound",
                Diagnostic = configuration.IncludeDiagnosticMessages
                    ? $"Controlled execution reached the {configuration.MaxSteps.ToString(CultureInfo.InvariantCulture)} step bound."
                    : null,
            };
        }

        // Otherwise the entry task's own terminal state decides the run. Canceled operations are
        // diagnostics (ReplayDiagnosticSnapshot.CanceledOperations), never the run's outcome.
        return drive.Entry switch
        {
            null or { IsCompletedSuccessfully: true } => new ReplayOutcome { Kind = ReplayTerminationKind.Completed },
            { IsCanceled: true } => new ReplayOutcome
            {
                Kind = ReplayTerminationKind.Canceled,
                FailureIdentity = EntryTaskCanceledIdentity,
            },
            { Exception.InnerExceptions: [var exception, ..] } => Faulted(configuration, exception),
            _ => new ReplayOutcome
            {
                Kind = ReplayTerminationKind.Deadlocked,
                FailureIdentity = EntryTaskIncompleteIdentity,
                Diagnostic = configuration.IncludeDiagnosticMessages ? Bound(scheduler.DescribeLiveness()) : null,
            },
        };
    }

    private static ReplayOutcome Faulted(ReplayRecordingOptions configuration, Exception exception) => new()
    {
        Kind = ReplayTerminationKind.Faulted,
        FailureIdentity = GetExceptionIdentity(exception),
        Diagnostic = configuration.IncludeDiagnosticMessages ? Bound(exception.Message) : null,
    };

    private static string GetExceptionIdentity(Exception exception) =>
        exception.GetType().FullName ?? exception.GetType().Name;

    private static bool OutcomesMatch(ReplayOutcome expected, ReplayOutcome actual) =>
        expected.Kind == actual.Kind &&
        string.Equals(expected.FailureIdentity, actual.FailureIdentity, StringComparison.Ordinal);

    private static string ComputeRaceIdentity(RaceReport race)
    {
        string first = string.Create(
            CultureInfo.InvariantCulture,
            $"{race.FirstAccess.Kind}|{race.FirstAccess.Source.Method}");
        string second = string.Create(
            CultureInfo.InvariantCulture,
            $"{race.SecondAccess.Kind}|{race.SecondAccess.Source.Method}");
        if (StringComparer.Ordinal.Compare(first, second) > 0)
        {
            (first, second) = (second, first);
        }

        string input = string.Create(
            CultureInfo.InvariantCulture,
            $"{race.FirstAccess.Location}|{first}|{second}");
        return "race:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input)))[..16];
    }

    private static string ComputeDeadlockIdentity(SimulationDeadlockReport report)
    {
        string input = string.Join(
            "|",
            report.Cycles.SelectMany(static cycle => cycle.Entries)
                .Select(static entry => string.Create(
                    CultureInfo.InvariantCulture,
                    $"{entry.OperationId.Value}>{entry.ResourceId.Value}>{entry.OwnerId.Value}")));
        return "deadlock:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input)))[..16];
    }

    private static string? Bound(string? value) =>
        value is null || value.Length <= ReplayArtifactLimits.MaxStringLength
            ? value
            : value[..ReplayArtifactLimits.MaxStringLength];

    private static int GetScheduleSeed(ReplayRecordingOptions configuration) =>
        configuration.SchedulingPolicy == ReplaySchedulingPolicy.SeededRandom
            ? configuration.ScheduleSeed ??
              new SimulationSeedAuthority(configuration.SimulationSeed).GetDomainSeed(SimulationSeedDomain.Scheduler)
            : 0;

    private static string GetStrategyName(ReplaySchedulingPolicy policy) =>
        policy switch
        {
            ReplaySchedulingPolicy.Fifo => "fifo",
            ReplaySchedulingPolicy.RoundRobin => "round-robin",
            ReplaySchedulingPolicy.Priority => "priority",
            ReplaySchedulingPolicy.SeededRandom => "seeded-random",
            _ => throw new ArgumentOutOfRangeException(nameof(policy)),
        };

    private static ReplaySchedulingPolicy ParsePolicy(string strategy) =>
        strategy switch
        {
            "fifo" => ReplaySchedulingPolicy.Fifo,
            "round-robin" => ReplaySchedulingPolicy.RoundRobin,
            "priority" => ReplaySchedulingPolicy.Priority,
            "seeded-random" => ReplaySchedulingPolicy.SeededRandom,
            _ => throw new ReplayCompatibilityException($"Unsupported recorded scheduler strategy '{strategy}'."),
        };

    private static void ValidateExecutionHost(ReplayArtifact artifact, bool expectCluster, bool expectEntryTask)
    {
        artifact.Scheduler.Options.TryGetValue(ExecutionHostOption, out string? executionHost);
        bool isCluster;
        if (executionHost is null || string.Equals(executionHost, "scheduler", StringComparison.Ordinal))
        {
            isCluster = false;
        }
        else if (string.Equals(executionHost, ClusterExecutionHost, StringComparison.Ordinal))
        {
            isCluster = true;
        }
        else
        {
            throw new ReplayCompatibilityException(
                $"Unsupported replay execution host '{executionHost}'.");
        }

        if (expectCluster != isCluster)
        {
            string expected = expectCluster ? "cluster" : "scheduler";
            string actual = isCluster ? "cluster" : "scheduler";
            throw new ReplayCompatibilityException(
                $"Replay execution host mismatch: artifact='{actual}', requested='{expected}'.");
        }

        // A run's entry form decides when it ends and how it is classified, so replaying it in the
        // other form would diverge; reject the mismatch before any scenario code runs.
        artifact.Scheduler.Options.TryGetValue(EntryOption, out string? entry);
        bool isEntryTask;
        if (entry is null || string.Equals(entry, CallbackEntry, StringComparison.Ordinal))
        {
            isEntryTask = false;
        }
        else if (string.Equals(entry, TaskEntry, StringComparison.Ordinal))
        {
            isEntryTask = true;
        }
        else
        {
            throw new ReplayCompatibilityException($"Unsupported replay entry form '{entry}'.");
        }

        if (expectEntryTask != isEntryTask)
        {
            string expected = expectEntryTask ? TaskEntry : CallbackEntry;
            string actual = isEntryTask ? TaskEntry : CallbackEntry;
            throw new ReplayCompatibilityException(
                $"Replay entry form mismatch: artifact='{actual}', requested='{expected}'. " +
                $"Replay an '{TaskEntry}' artifact with {nameof(ReplayClusterEntry)} and a '{CallbackEntry}' artifact with {nameof(ReplayCluster)} or {nameof(Replay)}.");
        }
    }

    private static void ValidateConfiguration(ReplayRecordingOptions configuration)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(configuration.MaxSteps);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(configuration.ProcessorCount);
        if (configuration.SchedulingPolicy != ReplaySchedulingPolicy.SeededRandom &&
            configuration.ScheduleSeed is not null)
        {
            throw new ArgumentException(
                "ScheduleSeed is only valid with SeededRandom scheduling.",
                nameof(configuration));
        }
    }

    private sealed record DriveResult(
        int Steps,
        bool BoundExceeded,
        Exception? Exception,
        bool IsAborted,
        Task? Entry);

    private sealed class ReplayOperationListener(bool includeDiagnosticMessages) : ISimulationOperationListener
    {
        private readonly bool _includeDiagnosticMessages = includeDiagnosticMessages;

        public OperationFailure? FirstFailure { get; private set; }

        public void OnStateChanged(SimulationOperation operation, SimulationOperationState newState)
        {
            if (newState != SimulationOperationState.Faulted ||
                FirstFailure is not null ||
                operation.TerminalException is not { } exception)
            {
                return;
            }

            FirstFailure = new OperationFailure(
                GetExceptionIdentity(exception),
                _includeDiagnosticMessages ? exception.Message : null);
        }
    }

    private sealed record OperationFailure(string ExceptionType, string? Message);
}
