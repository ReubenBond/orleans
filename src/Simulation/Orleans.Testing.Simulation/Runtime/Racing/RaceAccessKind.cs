namespace Orleans.Testing.Simulation.Runtime.Racing;

/// <summary>Classifies an instrumented race-exploration scheduling point.</summary>
public enum RaceAccessKind
{
    /// <summary>A shared location is read.</summary>
    Read,

    /// <summary>A shared location is written.</summary>
    Write,

    /// <summary>A control-flow branch can change which operation runs next.</summary>
    ControlFlow,

    /// <summary>An access is schedulable but its logical memory location cannot be identified safely.</summary>
    UntrackedMemory,
}
