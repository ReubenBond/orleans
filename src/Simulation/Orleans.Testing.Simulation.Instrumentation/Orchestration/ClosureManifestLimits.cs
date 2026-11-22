namespace Orleans.Testing.Simulation.Instrumentation.Orchestration;

/// <summary>Hard limits shared by closure-manifest readers, writers, and producers.</summary>
public static class ClosureManifestLimits
{
    /// <summary>Maximum encoded closure-manifest size: 16 MiB.</summary>
    public const int MaxDocumentBytes = 16 * 1024 * 1024;

    /// <summary>Maximum number of assembly entries.</summary>
    public const int MaxAssemblies = 4096;

    /// <summary>Maximum number of copied assets.</summary>
    public const int MaxCopiedAssets = 65_536;

    /// <summary>Maximum UTF-16 length of a manifest string.</summary>
    public const int MaxStringLength = 8192;

    /// <summary>Required lower-case hexadecimal SHA-256 length.</summary>
    public const int Sha256Length = 64;

    /// <summary>Maximum JSON nesting depth.</summary>
    public const int MaxJsonDepth = 32;
}
