namespace Orleans.Testing.Simulation.Tool;

/// <summary>
/// Process exit codes, grouped by failure class so that callers and CI can branch on the kind of
/// failure. Every command returns exactly one of these.
/// </summary>
internal enum ExitCode
{
    /// <summary>The command completed successfully.</summary>
    Success = 0,

    /// <summary>The command line was malformed (unknown command, missing or invalid option).</summary>
    UsageError = 1,

    /// <summary>The configuration or a rule-set document was invalid.</summary>
    ConfigurationError = 2,

    /// <summary>The application closure could not be discovered (missing directory, unresolved entry).</summary>
    ClosureError = 3,

    /// <summary>Instrumentation ran but a targeted failure occurred (unresolved call, mixed-mode, R2R, strong-name).</summary>
    InstrumentationError = 4,

    /// <summary>An I/O or otherwise unexpected error occurred.</summary>
    IoError = 5,

    /// <summary>The scenario executed and produced a fault, cancellation, race, deadlock, or bound failure.</summary>
    ExecutionFailure = 6,

    /// <summary>The artifact was invalid, incompatible, or diverged during replay.</summary>
    ReplayError = 7,

    /// <summary>Trace minimization could not establish or preserve the recorded failure.</summary>
    MinimizationError = 8,
}
