namespace Orleans.Testing.Simulation.Instrumentation.Closure;

/// <summary>
/// The classification of a file discovered in an application output or publish directory. It
/// determines whether the file is a candidate for IL rewriting or must be copied verbatim so the
/// staged closure remains runnable.
/// </summary>
public enum AssetKind
{
    /// <summary>A managed assembly containing IL (a rewrite candidate).</summary>
    ManagedAssembly,

    /// <summary>A managed satellite resource assembly (<c>*.resources.dll</c>); copied, never rewritten.</summary>
    SatelliteAssembly,

    /// <summary>A native (unmanaged) library; copied verbatim.</summary>
    NativeLibrary,

    /// <summary>A debug-symbol file (<c>*.pdb</c>).</summary>
    DebugSymbols,

    /// <summary>An application dependency manifest (<c>*.deps.json</c>).</summary>
    DepsJson,

    /// <summary>A runtime configuration file (<c>*.runtimeconfig.json</c>).</summary>
    RuntimeConfig,

    /// <summary>Any other content asset (configuration, data, resources); copied verbatim.</summary>
    Other,
}
