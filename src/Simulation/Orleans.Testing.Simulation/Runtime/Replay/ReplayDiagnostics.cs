using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using Orleans.Testing.Simulation.Runtime.Racing;
using Orleans.Testing.Simulation.Runtime.Scheduling;

namespace Orleans.Testing.Simulation.Runtime.Replay;

/// <summary>A stable operation status in replay diagnostics.</summary>
public sealed record ReplayOperationDiagnostic
{
    /// <summary>Gets the operation identity.</summary>
    public required long Id { get; init; }

    /// <summary>Gets the parent operation identity, or zero for a root.</summary>
    public required long ParentId { get; init; }

    /// <summary>Gets the terminal or pending state.</summary>
    public required string State { get; init; }

    /// <summary>Gets the node address, when node-scoped and explicitly retained.</summary>
    public string? Node { get; init; }

    /// <summary>Gets the work description when user metadata retention was enabled.</summary>
    public string? Description { get; init; }

    /// <summary>Gets the wait reason when the operation is paused and metadata retention was enabled.</summary>
    public string? WaitReason { get; init; }
}

/// <summary>A stable pending resource waiter.</summary>
public sealed record ReplayWaiterDiagnostic
{
    /// <summary>Gets the waiting operation identity.</summary>
    public required long OperationId { get; init; }

    /// <summary>Gets deterministic queue order.</summary>
    public required long EnqueueSequence { get; init; }

    /// <summary>Gets the virtual timeout in ticks, when finite.</summary>
    public long? TimeoutTicks { get; init; }

    /// <summary>Gets the wait reason when user metadata retention was enabled.</summary>
    public string? Reason { get; init; }
}

/// <summary>A stable resource and wait-queue snapshot.</summary>
public sealed record ReplayResourceDiagnostic
{
    /// <summary>Gets the resource identity.</summary>
    public required long Id { get; init; }

    /// <summary>Gets the resource kind.</summary>
    public required string Kind { get; init; }

    /// <summary>Gets the resource name when user metadata retention was enabled.</summary>
    public string? Name { get; init; }

    /// <summary>Gets the owning operation identity, when present.</summary>
    public long? OwnerId { get; init; }

    /// <summary>Gets pending waiters in enqueue order.</summary>
    public IReadOnlyList<ReplayWaiterDiagnostic> Waiters { get; init; } = [];
}

/// <summary>A pending virtual timer/deadline.</summary>
public sealed record ReplayTimerDiagnostic
{
    /// <summary>Gets the due time in virtual ticks.</summary>
    public required long DueTicks { get; init; }

    /// <summary>Gets deterministic registration order.</summary>
    public required long Sequence { get; init; }

    /// <summary>Gets the waiting operation identity.</summary>
    public required long OperationId { get; init; }

    /// <summary>Gets the resource identity.</summary>
    public required long ResourceId { get; init; }
}

/// <summary>One edge in a deadlock cycle.</summary>
public sealed record ReplayDeadlockEdgeDiagnostic
{
    /// <summary>Gets the waiting operation.</summary>
    public required long OperationId { get; init; }

    /// <summary>Gets the awaited resource.</summary>
    public required long ResourceId { get; init; }

    /// <summary>Gets the owning operation.</summary>
    public required long OwnerId { get; init; }

    /// <summary>Gets deterministic waiter order.</summary>
    public required long EnqueueSequence { get; init; }
}

/// <summary>A deterministic deadlock cycle.</summary>
public sealed record ReplayDeadlockCycleDiagnostic
{
    /// <summary>Gets cycle edges in owner-follows order.</summary>
    public IReadOnlyList<ReplayDeadlockEdgeDiagnostic> Edges { get; init; } = [];
}

/// <summary>One access in a detected race pair.</summary>
public sealed record ReplayRaceAccessDiagnostic
{
    /// <summary>Gets the operation identity.</summary>
    public required long OperationId { get; init; }

    /// <summary>Gets the read/write kind.</summary>
    public required string Kind { get; init; }

    /// <summary>Gets the logical location.</summary>
    public required string Location { get; init; }

    /// <summary>Gets the containing member.</summary>
    public required string Member { get; init; }

    /// <summary>Gets the IL offset.</summary>
    public required int ILOffset { get; init; }

    /// <summary>Gets the source file when explicit source-path retention was enabled.</summary>
    public string? SourceFile { get; init; }

    /// <summary>Gets the source line, or -1 when unavailable.</summary>
    public required int SourceLine { get; init; }
}

/// <summary>The conflicting pair for a detected race.</summary>
public sealed record ReplayRacePairDiagnostic
{
    /// <summary>Gets the first conflicting access.</summary>
    public required ReplayRaceAccessDiagnostic First { get; init; }

    /// <summary>Gets the second conflicting access.</summary>
    public required ReplayRaceAccessDiagnostic Second { get; init; }
}

/// <summary>Structured operation/resource/timer/race/deadlock diagnostics for a replay artifact.</summary>
public sealed record ReplayDiagnosticSnapshot
{
    /// <summary>Gets the scheduler liveness category.</summary>
    public string Liveness { get; init; } = "Quiescent";

    /// <summary>Gets virtual time in ticks.</summary>
    public long VirtualTimeTicks { get; init; }

    /// <summary>Gets operation statuses in operation-id order.</summary>
    public IReadOnlyList<ReplayOperationDiagnostic> Operations { get; init; } = [];

    /// <summary>
    /// Gets the operations in <see cref="Operations"/> that ended canceled, in operation-id order: for
    /// example, the awaited-task continuation a <c>Task.WaitAsync</c> withdraws when its timeout or token
    /// wins, a lane callback whose timer was changed or disposed before it ran, an operation canceled
    /// through the scheduler, or work a node detachment canceled. A canceled operation is a diagnostic;
    /// it never decides the run's <see cref="ReplayTerminationKind"/>. An operation whose body lets an
    /// <see cref="OperationCanceledException"/> escape is faulted, not canceled. Derived from
    /// <see cref="Operations"/>; not persisted separately.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<ReplayOperationDiagnostic> CanceledOperations =>
        [.. Operations.Where(static operation => operation.State == nameof(SimulationOperationState.Canceled))];

    /// <summary>Gets resources in resource-id order.</summary>
    public IReadOnlyList<ReplayResourceDiagnostic> Resources { get; init; } = [];

    /// <summary>Gets pending timers in due-time/sequence order.</summary>
    public IReadOnlyList<ReplayTimerDiagnostic> PendingTimers { get; init; } = [];

    /// <summary>
    /// Gets the wait-for cycles at the terminal boundary. A cycle left when an entry task completed is
    /// reported here without deciding the outcome; see <see cref="ReplayTerminationKind.Deadlocked"/>.
    /// </summary>
    public IReadOnlyList<ReplayDeadlockCycleDiagnostic> DeadlockCycles { get; init; } = [];

    /// <summary>Gets the detected race pair, when present.</summary>
    public ReplayRacePairDiagnostic? Race { get; init; }
}

/// <summary>Renders stable text diagnostics from a replay artifact.</summary>
public static class ReplayTraceRenderer
{
    /// <summary>Renders a deterministic, newline-normalized trace summary.</summary>
    public static string RenderText(ReplayArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        var builder = new StringBuilder();
        builder.Append("Orleans simulation replay ").Append(artifact.Format).Append("/v")
            .Append(artifact.SchemaVersion).Append('\n');
        builder.Append("Outcome: ").Append(artifact.Outcome.Kind);
        if (artifact.Outcome.FailureIdentity is { } identity)
        {
            builder.Append(" (").Append(identity).Append(')');
        }

        builder.Append('\n');
        builder.Append("Seeds: simulation=").Append(artifact.SimulationSeed)
            .Append(" schedule=").Append(artifact.Scheduler.ScheduleSeed?.ToString(CultureInfo.InvariantCulture) ?? "n/a")
            .Append('\n');
        builder.Append("Strategy: ").Append(artifact.Scheduler.Strategy).Append('\n');
        builder.Append("Decisions: ").Append(artifact.Decisions.Count).Append('\n');
        foreach (ReplayDecision decision in artifact.Decisions)
        {
            builder.Append("  #").Append(decision.Sequence)
                .Append(' ').Append(decision.Domain).Append('/').Append(decision.Kind)
                .Append(" source=").Append(decision.SourceId ?? "n/a")
                .Append(" selected=").Append(decision.SelectedResult)
                .Append(" candidates=").Append(decision.InputMetadata ?? "n/a")
                .Append('\n');
        }

        ReplayDiagnosticSnapshot diagnostics = artifact.Diagnostics;
        builder.Append("Liveness: ").Append(diagnostics.Liveness)
            .Append(" virtualTicks=").Append(diagnostics.VirtualTimeTicks).Append('\n');
        builder.Append("Operations:\n");
        foreach (ReplayOperationDiagnostic operation in diagnostics.Operations)
        {
            builder.Append("  op").Append(operation.Id)
                .Append(" parent=").Append(operation.ParentId)
                .Append(" state=").Append(operation.State);
            if (operation.Description is { } description)
            {
                builder.Append(" description=").Append(description);
            }

            if (operation.WaitReason is { } waitReason)
            {
                builder.Append(" wait=").Append(waitReason);
            }

            builder.Append('\n');
        }

        IReadOnlyList<ReplayOperationDiagnostic> canceled = diagnostics.CanceledOperations;
        builder.Append("Canceled operations: ").Append(canceled.Count);
        for (var index = 0; index < canceled.Count; index++)
        {
            builder.Append(index == 0 ? " (" : ", ").Append("op").Append(canceled[index].Id);
        }

        builder.Append(canceled.Count == 0 ? "\n" : ")\n");

        builder.Append("Resources:\n");
        foreach (ReplayResourceDiagnostic resource in diagnostics.Resources)
        {
            builder.Append("  res").Append(resource.Id)
                .Append(" kind=").Append(resource.Kind)
                .Append(" owner=").Append(resource.OwnerId?.ToString(CultureInfo.InvariantCulture) ?? "none");
            if (resource.Name is { } name)
            {
                builder.Append(" name=").Append(name);
            }

            builder.Append('\n');
            foreach (ReplayWaiterDiagnostic waiter in resource.Waiters)
            {
                builder.Append("    op").Append(waiter.OperationId)
                    .Append(" queue=").Append(waiter.EnqueueSequence)
                    .Append(" timeoutTicks=").Append(waiter.TimeoutTicks?.ToString(CultureInfo.InvariantCulture) ?? "infinite")
                    .Append('\n');
            }
        }

        builder.Append("Pending timers:\n");
        foreach (ReplayTimerDiagnostic timer in diagnostics.PendingTimers)
        {
            builder.Append("  dueTicks=").Append(timer.DueTicks)
                .Append(" sequence=").Append(timer.Sequence)
                .Append(" op=").Append(timer.OperationId)
                .Append(" resource=").Append(timer.ResourceId)
                .Append('\n');
        }

        for (var cycleIndex = 0; cycleIndex < diagnostics.DeadlockCycles.Count; cycleIndex++)
        {
            builder.Append("Deadlock cycle ").Append(cycleIndex + 1).Append(':').Append('\n');
            foreach (ReplayDeadlockEdgeDiagnostic edge in diagnostics.DeadlockCycles[cycleIndex].Edges)
            {
                builder.Append("  op").Append(edge.OperationId)
                    .Append(" -> res").Append(edge.ResourceId)
                    .Append(" -> op").Append(edge.OwnerId)
                    .Append(" queue=").Append(edge.EnqueueSequence)
                    .Append('\n');
            }
        }

        if (diagnostics.Race is { } race)
        {
            builder.Append("Race pair:\n");
            AppendRaceAccess(builder, "first", race.First);
            AppendRaceAccess(builder, "second", race.Second);
        }

        builder.Append("Race scheduling points: ").Append(artifact.RaceSchedulingPoints.Count).Append('\n');
        foreach (ReplayRaceSchedulingPoint point in artifact.RaceSchedulingPoints)
        {
            builder.Append("  #").Append(point.Sequence)
                .Append(" op=").Append(point.OperationId)
                .Append(' ').Append(point.Kind)
                .Append(' ').Append(point.Location)
                .Append(" at ").Append(point.Member)
                .Append(" IL_").Append(point.ILOffset.ToString("x4", CultureInfo.InvariantCulture))
                .Append('\n');
        }

        return builder.ToString().TrimEnd('\n');
    }

    private static void AppendRaceAccess(
        StringBuilder builder,
        string label,
        ReplayRaceAccessDiagnostic access)
    {
        builder.Append("  ").Append(label)
            .Append(": op").Append(access.OperationId)
            .Append(' ').Append(access.Kind)
            .Append(' ').Append(access.Location)
            .Append(" at ").Append(access.Member)
            .Append(" IL_").Append(access.ILOffset.ToString("x4", CultureInfo.InvariantCulture))
            .Append('\n');
    }
}
