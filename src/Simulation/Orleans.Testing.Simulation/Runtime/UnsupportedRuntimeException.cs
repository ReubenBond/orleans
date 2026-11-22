namespace Orleans.Testing.Simulation.Runtime;

/// <summary>
/// Thrown when a simulation runs in a process whose .NET runtime is outside the range an assembly rewritten by
/// Clockwork supports. The rules that rewrote it were classified against the surface of the runtimes in that range,
/// and the controlled shims depend on their internals. A process usually ends up on another runtime through a
/// roll-forward policy (<c>rollForward</c> in <c>runtimeconfig.json</c>, or <c>DOTNET_ROLL_FORWARD</c>) or because the
/// runtime it targets is not installed.
/// </summary>
public sealed class UnsupportedRuntimeException : InvalidOperationException
{
    /// <summary>Initializes a new instance of the <see cref="UnsupportedRuntimeException"/> class.</summary>
    /// <param name="assemblyName">The rewritten assembly's simple name.</param>
    /// <param name="minimumRuntime">The earliest feature version the assembly supports, or <see langword="null"/> for no limit.</param>
    /// <param name="maximumRuntime">The latest feature version the assembly supports, or <see langword="null"/> for no limit.</param>
    /// <param name="runtime">The feature version (major.minor) of the runtime the process runs.</param>
    public UnsupportedRuntimeException(string assemblyName, Version? minimumRuntime, Version? maximumRuntime, Version runtime)
        : base(
            $"Assembly '{assemblyName}' was rewritten by Clockwork for .NET " +
            $"{SupportedRuntimesMarker.DescribeRange(minimumRuntime, maximumRuntime)}, but this process runs .NET {runtime}. " +
            "Run it on a supported runtime (check the app's runtimeconfig.json rollForward setting and " +
            "DOTNET_ROLL_FORWARD), or instrument it for this runtime with rules that support it.")
    {
        ArgumentException.ThrowIfNullOrEmpty(assemblyName);
        ArgumentNullException.ThrowIfNull(runtime);
        AssemblyName = assemblyName;
        MinimumRuntime = minimumRuntime;
        MaximumRuntime = maximumRuntime;
        Runtime = runtime;
    }

    /// <summary>Gets the rewritten assembly's simple name.</summary>
    public string AssemblyName { get; }

    /// <summary>Gets the earliest feature version the assembly supports, or <see langword="null"/> for no limit.</summary>
    public Version? MinimumRuntime { get; }

    /// <summary>Gets the latest feature version the assembly supports, or <see langword="null"/> for no limit.</summary>
    public Version? MaximumRuntime { get; }

    /// <summary>Gets the feature version (major.minor) of the runtime the process runs.</summary>
    public Version Runtime { get; }
}
