namespace Orleans.Testing.Simulation.Runtime.Replay;

/// <summary>Hard parser and recorder limits for replay artifacts.</summary>
public static class ReplayArtifactLimits
{
    /// <summary>Maximum encoded artifact size: 16 MiB.</summary>
    public const int MaxDocumentBytes = 16 * 1024 * 1024;

    /// <summary>Maximum number of deterministic decisions.</summary>
    public const int MaxDecisions = 250_000;

    /// <summary>Maximum number of race scheduling points.</summary>
    public const int MaxRaceSchedulingPoints = 250_000;

    /// <summary>Maximum number of assemblies in an instrumentation closure.</summary>
    public const int MaxAssemblies = 4096;

    /// <summary>Maximum number of scheduler options.</summary>
    public const int MaxSchedulerOptions = 64;

    /// <summary>Maximum UTF-16 length of any single artifact string.</summary>
    public const int MaxStringLength = 8192;
}

/// <summary>Thrown when an artifact is malformed, corrupt, unsafe, or exceeds a hard limit.</summary>
public sealed class ReplayArtifactFormatException : Exception
{
    /// <summary>Initializes a replay artifact format exception.</summary>
    public ReplayArtifactFormatException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a replay artifact format exception with an inner parser exception.</summary>
    public ReplayArtifactFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
