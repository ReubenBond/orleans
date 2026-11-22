using Orleans.Testing.Simulation.Runtime.Decisions;
using Orleans.Testing.Simulation.Runtime.Random;
using Orleans.Testing.Simulation.Runtime.Racing;

using System.Text.Json.Serialization;

namespace Orleans.Testing.Simulation.Runtime.Replay;

/// <summary>
/// <para>
/// Classifies the terminal outcome of a recorded execution. The first applicable rule decides:
/// <see cref="Aborted"/>, <see cref="RaceDetected"/>, <see cref="Faulted"/> for an exception which
/// escaped an operation body, <see cref="Deadlocked"/> for a wait-for cycle while the entry is
/// incomplete, <see cref="BoundExceeded"/>, and otherwise the entry task's own terminal state:
/// <see cref="Completed"/>, <see cref="Canceled"/>, <see cref="Faulted"/>, or, when it never
/// completed, <see cref="Deadlocked"/>.
/// </para>
/// <para>
/// The entry task is the task returned by an entry-task scenario
/// (<see cref="ReplayRunner.RecordClusterEntry"/>); the run ends when it completes, as a process ends
/// when <c>Main</c> returns. A synchronous scenario callback is an entry which ran to completion when
/// it returned; its run continues until the work it registered quiesces. A canceled operation never
/// decides the outcome; canceled operations are reported in
/// <see cref="ReplayDiagnosticSnapshot.CanceledOperations"/>.
/// </para>
/// </summary>
public enum ReplayTerminationKind
{
    /// <summary>
    /// The entry task ran to completion and no other rule applies. Operations canceled during the run
    /// and work still pending when it ended, including a wait-for cycle left behind by an entry task
    /// which completed, are diagnostics and do not change this outcome.
    /// </summary>
    Completed,

    /// <summary>
    /// An exception escaped a controlled operation's body, or the entry task faulted. An exception
    /// escaping a body is unhandled wherever .NET would crash the process: a <c>Thread</c> body, a
    /// thread-pool work item, a <c>System.Threading.Timer</c> callback, a scheduler-lane or
    /// synchronization-context callback, or a raw
    /// <see cref="Scheduling.SimulationScheduler.Schedule(string, Action, Execution.SimulationNodeIdentity?, int)"/>
    /// body. This includes an <see cref="OperationCanceledException"/>. The failure identity is the
    /// concrete type of the first such exception, such as <c>System.OperationCanceledException</c> or
    /// <c>System.Threading.Tasks.TaskCanceledException</c>, or of the entry task's first inner exception.
    /// A task-backed operation (<c>Task.Run</c>, <c>TaskFactory.StartNew</c>, a continuation, or an
    /// async method) captures its delegate's exception, including cancellation, in its task, so it is
    /// not an operation fault.
    /// </summary>
    Faulted,

    /// <summary>
    /// The entry task ended canceled. The failure identity is <c>entry-task-canceled</c>.
    /// </summary>
    Canceled,

    /// <summary>
    /// The terminal wait-for graph contains a cycle while the entry is incomplete or is a synchronous
    /// entry, whose run continues until quiescence (the identity is <c>deadlock:</c> and a hash of the
    /// cycle), or the entry task had not completed when nothing was runnable and no timer was pending
    /// (the identity is <c>entry-task-incomplete</c>). A cycle left when an entry task completes is a
    /// diagnostic (<see cref="ReplayDiagnosticSnapshot.DeadlockCycles"/>), not this outcome.
    /// </summary>
    Deadlocked,

    /// <summary>Race exploration detected a data race.</summary>
    RaceDetected,

    /// <summary>
    /// The step bound was reached before the run's work was done: the entry task had not completed, or,
    /// for a synchronous entry, operations were still pending. The identity is <c>step-bound</c>.
    /// </summary>
    BoundExceeded,

    /// <summary>The recording was aborted before a terminal boundary.</summary>
    Aborted,
}

/// <summary>A stable assembly identity used for replay compatibility checks.</summary>
public sealed record ReplayAssemblyIdentity
{
    /// <summary>Gets the assembly simple name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the lower-case SHA-256 of the instrumented assembly bytes.</summary>
    public required string Sha256 { get; init; }

    /// <summary>Gets the target runtime compatibility identifier, when known.</summary>
    public string? RuntimeCompatibility { get; init; }
}

/// <summary>Identifies the instrumentation closure which produced a replayable execution.</summary>
public sealed record ReplayInstrumentationIdentity
{
    /// <summary>Gets a stable caller-supplied identity for the closure manifest.</summary>
    public required string ManifestId { get; init; }

    /// <summary>Gets the lower-case SHA-256 of the canonical instrumentation manifest.</summary>
    public required string ManifestSha256 { get; init; }

    /// <summary>Gets the instrumentation engine version.</summary>
    public required string EngineVersion { get; init; }

    /// <summary>Gets the applied rule-set identity.</summary>
    public required string RuleSetId { get; init; }

    /// <summary>Gets the applied rule-set version.</summary>
    public required string RuleSetVersion { get; init; }

    /// <summary>Gets the applied rule-set content signature.</summary>
    public required string RuleSetSignature { get; init; }

    /// <summary>Gets the instrumentation mode.</summary>
    public required string Mode { get; init; }

    /// <summary>Gets the instrumented assembly closure in stable name order.</summary>
    public IReadOnlyList<ReplayAssemblyIdentity> Assemblies { get; init; } = [];
}

/// <summary>Replay compatibility metadata captured without process arguments, environment variables, or user data.</summary>
public sealed record ReplayEnvironmentIdentity
{
    /// <summary>Gets the Clockwork assembly version.</summary>
    public required string ClockworkVersion { get; init; }

    /// <summary>Gets the runtime compatibility identifier used for pre-execution checks.</summary>
    public required string RuntimeCompatibility { get; init; }

    /// <summary>Gets the process architecture.</summary>
    public required string ProcessArchitecture { get; init; }

    /// <summary>Gets the operating-system platform family.</summary>
    public required string OperatingSystem { get; init; }
}

/// <summary>Configures the deterministic scheduler represented by an artifact.</summary>
public sealed record ReplaySchedulerConfiguration
{
    /// <summary>Gets the stable scheduling strategy name.</summary>
    public required string Strategy { get; init; }

    /// <summary>Gets the explicit schedule seed, when the strategy uses one.</summary>
    public int? ScheduleSeed { get; init; }

    /// <summary>Gets bounded, non-secret strategy options in stable ordinal key order.</summary>
    public IReadOnlyDictionary<string, string> Options { get; init; } =
        new SortedDictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>A replay-safe decision without process-unique runtime identity.</summary>
public sealed record ReplayDecision
{
    /// <summary>Gets the zero-based decision sequence.</summary>
    public required long Sequence { get; init; }

    /// <summary>Gets the independent deterministic decision domain.</summary>
    public required SimulationSeedDomain Domain { get; init; }

    /// <summary>Gets the decision category.</summary>
    public required SimulationDecisionKind Kind { get; init; }

    /// <summary>Gets the stable source/member/operation identifier, when available.</summary>
    public string? SourceId { get; init; }

    /// <summary>Gets bounded deterministic input metadata.</summary>
    public string? InputMetadata { get; init; }

    /// <summary>Gets the selected result.</summary>
    public required string SelectedResult { get; init; }

    /// <summary>Gets the stable node identity, when node-scoped.</summary>
    public string? NodeId { get; init; }

    /// <summary>Gets the scheduler-local logical execution identity.</summary>
    public required long LogicalExecutionId { get; init; }

    /// <summary>Creates an artifact decision from a live decision record.</summary>
    public static ReplayDecision FromRecord(SimulationDecisionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new ReplayDecision
        {
            Sequence = record.Id.Sequence,
            Domain = record.Domain,
            Kind = record.Kind,
            SourceId = record.SourceId,
            InputMetadata = record.InputMetadata,
            SelectedResult = record.SelectedResult,
            NodeId = record.NodeId,
            LogicalExecutionId = record.LogicalExecutionId.Value,
        };
    }

    /// <summary>Creates the runtime decision shape used by the replay scheduler and validator.</summary>
    public SimulationDecisionRecord ToRecord() => new(
        new SimulationDecisionId(Sequence),
        Domain,
        Kind,
        SourceId,
        InputMetadata,
        SelectedResult,
        Guid.Empty,
        NodeId,
        new Execution.SimulationLogicalExecutionId(LogicalExecutionId));
}

/// <summary>An injected race scheduling point represented without runtime object references.</summary>
public sealed record ReplayRaceSchedulingPoint
{
    /// <summary>Gets the scheduling-point sequence.</summary>
    public required long Sequence { get; init; }

    /// <summary>Gets the controlled operation identity.</summary>
    public required long OperationId { get; init; }

    /// <summary>Gets the access or scheduling-point kind.</summary>
    public required RaceAccessKind Kind { get; init; }

    /// <summary>Gets the stable member, array, or control-flow location.</summary>
    public required string Location { get; init; }

    /// <summary>Gets the containing member identity.</summary>
    public required string Member { get; init; }

    /// <summary>Gets the original IL offset.</summary>
    public required int ILOffset { get; init; }

    /// <summary>Gets the source document path, when symbols were available.</summary>
    public string? SourceFile { get; init; }

    /// <summary>Gets the source line, or -1 when unavailable.</summary>
    public required int SourceLine { get; init; }

    /// <summary>Creates an artifact scheduling point from a live race trace.</summary>
    public static ReplayRaceSchedulingPoint FromPoint(RaceSchedulingPoint point) => new()
    {
        Sequence = point.Sequence,
        OperationId = point.OperationId.Value,
        Kind = point.Kind,
        Location = point.Location,
        Member = point.Source.Method,
        ILOffset = point.Source.ILOffset,
        SourceFile = point.Source.SourceFile,
        SourceLine = point.Source.SourceLine,
    };
}

/// <summary>A stable terminal outcome and failure identity.</summary>
public sealed record ReplayOutcome
{
    /// <summary>Gets the terminal category.</summary>
    public required ReplayTerminationKind Kind { get; init; }

    /// <summary>Gets a stable failure/race/deadlock identity, when applicable.</summary>
    public string? FailureIdentity { get; init; }

    /// <summary>Gets a bounded deterministic diagnostic message without exception stack data.</summary>
    public string? Diagnostic { get; init; }
}

/// <summary>
/// A complete, versioned deterministic execution artifact. The format intentionally excludes process
/// arguments, environment variables, exception stack traces, object values, and arbitrary user metadata.
/// </summary>
public sealed record ReplayArtifact
{
    /// <summary>The current replay artifact schema version.</summary>
    public const int CurrentSchemaVersion = 2;

    /// <summary>The stable schema name.</summary>
    public const string SchemaName = "clockwork.replay";

    /// <summary>Gets the schema name.</summary>
    public string Format { get; init; } = SchemaName;

    /// <summary>Gets the schema version.</summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Gets the simulation seed.</summary>
    [JsonPropertyName("rootSeed")]
    public required int SimulationSeed { get; init; }

    /// <summary>Gets the scheduler strategy and options.</summary>
    public required ReplaySchedulerConfiguration Scheduler { get; init; }

    /// <summary>Gets the instrumentation closure identity, when instrumented execution was used.</summary>
    public ReplayInstrumentationIdentity? Instrumentation { get; init; }

    /// <summary>Gets the runtime and platform compatibility identity.</summary>
    public required ReplayEnvironmentIdentity Environment { get; init; }

    /// <summary>Gets the ordered deterministic decision stream.</summary>
    public IReadOnlyList<ReplayDecision> Decisions { get; init; } = [];

    /// <summary>Gets injected race scheduling points in encounter order.</summary>
    public IReadOnlyList<ReplayRaceSchedulingPoint> RaceSchedulingPoints { get; init; } = [];

    /// <summary>Gets the terminal outcome.</summary>
    public required ReplayOutcome Outcome { get; init; }

    /// <summary>Gets stable operation, resource, timer, race, and deadlock diagnostics.</summary>
    public ReplayDiagnosticSnapshot Diagnostics { get; init; } = new();
}
