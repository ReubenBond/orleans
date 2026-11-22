using System.ComponentModel;
using Orleans.Testing.Simulation.Runtime.Scheduling;

namespace Orleans.Testing.Simulation.Runtime.Racing;

/// <summary>
/// Runtime targets for race-exploration IL instrumentation. Calls are no-ops unless they execute
/// inside a <see cref="SimulationScheduler"/> operation.
/// </summary>
/// <remarks>These methods are intended for generated IL rather than direct application use.</remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class RaceInstrumentation
{
    [ThreadStatic]
    private static bool t_isProcessingPoint;

    /// <summary>Records and schedules an instance-field read.</summary>
    public static void ReadInstance(
        object? target,
        string member,
        string method,
        int ilOffset,
        string? sourceFile,
        int sourceLine) =>
        Interleave(
            RaceAccessKind.Read,
            RaceMemoryLocationKind.InstanceField,
            target,
            member,
            elementIndex: null,
            method,
            ilOffset,
            sourceFile,
            sourceLine);

    /// <summary>Records and schedules an instance-field write.</summary>
    public static void WriteInstance(
        object? target,
        string member,
        string method,
        int ilOffset,
        string? sourceFile,
        int sourceLine) =>
        Interleave(
            RaceAccessKind.Write,
            RaceMemoryLocationKind.InstanceField,
            target,
            member,
            elementIndex: null,
            method,
            ilOffset,
            sourceFile,
            sourceLine);

    /// <summary>Records and schedules a static-field read.</summary>
    public static void ReadStatic(
        string member,
        string method,
        int ilOffset,
        string? sourceFile,
        int sourceLine) =>
        Interleave(
            RaceAccessKind.Read,
            RaceMemoryLocationKind.StaticField,
            target: null,
            member,
            elementIndex: null,
            method,
            ilOffset,
            sourceFile,
            sourceLine);

    /// <summary>Records and schedules a static-field write.</summary>
    public static void WriteStatic(
        string member,
        string method,
        int ilOffset,
        string? sourceFile,
        int sourceLine) =>
        Interleave(
            RaceAccessKind.Write,
            RaceMemoryLocationKind.StaticField,
            target: null,
            member,
            elementIndex: null,
            method,
            ilOffset,
            sourceFile,
            sourceLine);

    /// <summary>Records and schedules a static-field read with closed declaring-type identity.</summary>
    public static void ReadStaticField(
        RuntimeTypeHandle declaringType,
        string member,
        string method,
        int ilOffset,
        string? sourceFile,
        int sourceLine) =>
        Interleave(
            RaceAccessKind.Read,
            RaceMemoryLocationKind.StaticField,
            Type.GetTypeFromHandle(declaringType),
            member,
            elementIndex: null,
            method,
            ilOffset,
            sourceFile,
            sourceLine);

    /// <summary>Records and schedules a static-field write with closed declaring-type identity.</summary>
    public static void WriteStaticField(
        RuntimeTypeHandle declaringType,
        string member,
        string method,
        int ilOffset,
        string? sourceFile,
        int sourceLine) =>
        Interleave(
            RaceAccessKind.Write,
            RaceMemoryLocationKind.StaticField,
            Type.GetTypeFromHandle(declaringType),
            member,
            elementIndex: null,
            method,
            ilOffset,
            sourceFile,
            sourceLine);

    /// <summary>Records and schedules an array-element read.</summary>
    public static void ReadArray(
        Array? target,
        long index,
        string method,
        int ilOffset,
        string? sourceFile,
        int sourceLine) =>
        Interleave(
            RaceAccessKind.Read,
            RaceMemoryLocationKind.ArrayElement,
            target,
            "element",
            index,
            method,
            ilOffset,
            sourceFile,
            sourceLine);

    /// <summary>Records and schedules an array-element write.</summary>
    public static void WriteArray(
        Array? target,
        long index,
        string method,
        int ilOffset,
        string? sourceFile,
        int sourceLine) =>
        Interleave(
            RaceAccessKind.Write,
            RaceMemoryLocationKind.ArrayElement,
            target,
            "element",
            index,
            method,
            ilOffset,
            sourceFile,
            sourceLine);

    /// <summary>Records and schedules a mutable collection read or iteration.</summary>
    public static void ReadCollection(
        object? target,
        string member,
        string method,
        int ilOffset,
        string? sourceFile,
        int sourceLine) =>
        Interleave(
            RaceAccessKind.Read,
            RaceMemoryLocationKind.Collection,
            target,
            member,
            elementIndex: null,
            method,
            ilOffset,
            sourceFile,
            sourceLine);

    /// <summary>Records and schedules a mutable collection write.</summary>
    public static void WriteCollection(
        object? target,
        string member,
        string method,
        int ilOffset,
        string? sourceFile,
        int sourceLine) =>
        Interleave(
            RaceAccessKind.Write,
            RaceMemoryLocationKind.Collection,
            target,
            member,
            elementIndex: null,
            method,
            ilOffset,
            sourceFile,
            sourceLine);

    /// <summary>Schedules an access to a thread-safe concurrent collection without reporting a race.</summary>
    public static void InterleaveConcurrentCollection(
        object? target,
        string member,
        string method,
        int ilOffset,
        string? sourceFile,
        int sourceLine) =>
        Interleave(
            RaceAccessKind.UntrackedMemory,
            locationKind: null,
            target,
            member,
            elementIndex: null,
            method,
            ilOffset,
            sourceFile,
            sourceLine);

    /// <summary>Schedules a memory access whose stable logical location cannot be recovered from IL.</summary>
    public static void InterleaveUntrackedMemory(
        string description,
        string method,
        int ilOffset,
        string? sourceFile,
        int sourceLine) =>
        Interleave(
            RaceAccessKind.UntrackedMemory,
            locationKind: null,
            target: null,
            description,
            elementIndex: null,
            method,
            ilOffset,
            sourceFile,
            sourceLine);

    /// <summary>Schedules a conditional control-flow branch.</summary>
    public static void InterleaveControlFlow(
        string method,
        int ilOffset,
        string? sourceFile,
        int sourceLine) =>
        Interleave(
            RaceAccessKind.ControlFlow,
            locationKind: null,
            target: null,
            "conditional branch",
            elementIndex: null,
            method,
            ilOffset,
            sourceFile,
            sourceLine);

    private static void Interleave(
        RaceAccessKind kind,
        RaceMemoryLocationKind? locationKind,
        object? target,
        string member,
        long? elementIndex,
        string method,
        int ilOffset,
        string? sourceFile,
        int sourceLine)
    {
        if (t_isProcessingPoint ||
            !SimulationScheduler.TryGetExecutingOperation(out SimulationScheduler? scheduler, out _))
        {
            return;
        }

        t_isProcessingPoint = true;
        try
        {
            scheduler.ReachRaceSchedulingPoint(
                kind,
                locationKind,
                target,
                member,
                elementIndex,
                new RaceSourceLocation(method, ilOffset, sourceFile, sourceLine));
        }
        finally
        {
            t_isProcessingPoint = false;
        }
    }
}
