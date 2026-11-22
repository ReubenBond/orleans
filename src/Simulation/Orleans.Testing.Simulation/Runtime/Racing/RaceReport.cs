using System.Collections.Immutable;
using System.Text;

namespace Orleans.Testing.Simulation.Runtime.Racing;

/// <summary>A deterministic report of the first conflicting unordered access in a run.</summary>
public sealed record RaceReport
{
    /// <summary>Gets the earlier conflicting access.</summary>
    public required RaceAccessRecord FirstAccess { get; init; }

    /// <summary>Gets the later conflicting access that exposed the race.</summary>
    public required RaceAccessRecord SecondAccess { get; init; }

    /// <summary>
    /// Gets the most recent scheduling points through the second access, up to the scheduler's
    /// <see cref="Scheduling.SimulationScheduler.RaceTraceCapacity"/>.
    /// </summary>
    public ImmutableArray<RaceSchedulingPoint> ScheduleTrace { get; init; } = [];

    /// <summary>Formats a deterministic multi-line race diagnostic suitable for replay artifacts.</summary>
    public string ToDetailedString()
    {
        var builder = new StringBuilder();
        builder.Append("Data race: ").Append(FirstAccess.Location).Append('\n');
        AppendAccess(builder, "First", FirstAccess);
        AppendAccess(builder, "Second", SecondAccess);
        builder.AppendLine("Schedule trace:");
        foreach (RaceSchedulingPoint point in ScheduleTrace)
        {
            builder.Append("  #").Append(point.Sequence)
                .Append(' ').Append(point.OperationId)
                .Append(' ').Append(point.Kind)
                .Append(' ').Append(point.Location)
                .Append(" at ").Append(point.Source)
                .Append('\n');
        }

        return builder.ToString().TrimEnd();
    }

    /// <inheritdoc />
    public override string ToString() =>
        $"Data race on {FirstAccess.Location}: {FirstAccess.OperationId} {FirstAccess.Kind} at " +
        $"{FirstAccess.Source} conflicts with {SecondAccess.OperationId} {SecondAccess.Kind} at {SecondAccess.Source}.";

    private static void AppendAccess(StringBuilder builder, string label, RaceAccessRecord access)
    {
        builder.Append(label).Append(": ")
            .Append(access.OperationId).Append(' ')
            .Append(access.Kind).Append(" at ")
            .Append(access.Source).Append('\n');
        builder.Append("  synchronization: ")
            .Append(access.SynchronizationContext.IsEmpty
                ? "none"
                : string.Join(", ", access.SynchronizationContext))
            .Append('\n');
    }
}
