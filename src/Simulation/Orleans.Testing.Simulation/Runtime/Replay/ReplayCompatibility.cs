using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Orleans.Testing.Simulation.Runtime.Replay;

/// <summary>Describes the expected runtime and instrumentation identity for a replay.</summary>
public sealed record ReplayCompatibilityRequirements
{
    /// <summary>Gets the expected runtime compatibility identifier.</summary>
    public required string RuntimeCompatibility { get; init; }

    /// <summary>Gets the expected Clockwork assembly version.</summary>
    public required string ClockworkVersion { get; init; }

    /// <summary>Gets the expected instrumentation identity, when instrumented execution is required.</summary>
    public ReplayInstrumentationIdentity? Instrumentation { get; init; }

    /// <summary>
    /// Gets the simulated processor count the replay expects the artifact to have recorded, or <see langword="null"/>
    /// to accept the recorded value. Replay always runs with the recorded value.
    /// </summary>
    public int? ProcessorCount { get; init; }

    /// <summary>Captures requirements for the current Clockwork runtime without instrumentation.</summary>
    public static ReplayCompatibilityRequirements Current() => new()
    {
        RuntimeCompatibility = ReplayCompatibility.CurrentRuntimeCompatibility,
        ClockworkVersion = ReplayCompatibility.CurrentClockworkVersion,
    };
}

/// <summary>Thrown before execution when an artifact cannot be replayed by the requested runtime or closure.</summary>
public sealed class ReplayCompatibilityException : InvalidOperationException
{
    /// <summary>Initializes a replay compatibility exception.</summary>
    public ReplayCompatibilityException(string message)
        : base(message)
    {
    }
}

/// <summary>Captures and validates deterministic replay compatibility metadata.</summary>
public static class ReplayCompatibility
{
    /// <summary>The scheduler option which records the run's simulated processor count.</summary>
    internal const string ProcessorCountOption = "processorCount";

    /// <summary>Gets the current major/minor runtime compatibility identifier.</summary>
    public static string CurrentRuntimeCompatibility =>
        FormattableString.Invariant($".NETCoreApp,Version=v{Environment.Version.Major}.{Environment.Version.Minor}");

    /// <summary>Gets the current Clockwork informational or assembly version.</summary>
    public static string CurrentClockworkVersion
    {
        get
        {
            Assembly assembly = typeof(ReplayArtifact).Assembly;
            return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? assembly.GetName().Version?.ToString()
                ?? "0.0.0.0";
        }
    }

    /// <summary>Captures a non-secret environment identity for an artifact.</summary>
    public static ReplayEnvironmentIdentity CaptureEnvironment() => new()
    {
        ClockworkVersion = CurrentClockworkVersion,
        RuntimeCompatibility = CurrentRuntimeCompatibility,
        ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
        OperatingSystem = GetOperatingSystem(),
    };

    /// <summary>Validates all compatibility requirements before replay execution begins.</summary>
    public static void Validate(ReplayArtifact artifact, ReplayCompatibilityRequirements requirements)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(requirements);

        if (artifact.Outcome.Kind == ReplayTerminationKind.Aborted)
        {
            throw new ReplayCompatibilityException(
                "The artifact has an Aborted outcome and cannot be used for exact replay.");
        }

        RequireEqual(
            "runtime compatibility",
            artifact.Environment.RuntimeCompatibility,
            requirements.RuntimeCompatibility);
        RequireEqual(
            "Clockwork runtime version",
            artifact.Environment.ClockworkVersion,
            requirements.ClockworkVersion);
        if (requirements.ProcessorCount is { } processorCount)
        {
            artifact.Scheduler.Options.TryGetValue(ProcessorCountOption, out string? recordedProcessorCount);
            RequireEqual(
                "simulated processor count",
                recordedProcessorCount,
                processorCount.ToString(CultureInfo.InvariantCulture));
        }

        if (artifact.Instrumentation is null && requirements.Instrumentation is null)
        {
            return;
        }

        if (artifact.Instrumentation is null || requirements.Instrumentation is null)
        {
            throw new ReplayCompatibilityException(
                "Instrumentation compatibility mismatch: one execution uses an instrumentation manifest and the other does not.");
        }

        ReplayInstrumentationIdentity recorded = artifact.Instrumentation;
        ReplayInstrumentationIdentity expected = requirements.Instrumentation;
        RequireEqual("instrumentation manifest id", recorded.ManifestId, expected.ManifestId);
        RequireEqual("instrumentation manifest hash", recorded.ManifestSha256, expected.ManifestSha256);
        RequireEqual("instrumentation engine version", recorded.EngineVersion, expected.EngineVersion);
        RequireEqual("rule-set id", recorded.RuleSetId, expected.RuleSetId);
        RequireEqual("rule-set version", recorded.RuleSetVersion, expected.RuleSetVersion);
        RequireEqual("rule-set signature", recorded.RuleSetSignature, expected.RuleSetSignature);
        RequireEqual("instrumentation mode", recorded.Mode, expected.Mode);

        if (recorded.Assemblies.Count != expected.Assemblies.Count)
        {
            throw new ReplayCompatibilityException(
                $"Instrumentation assembly count mismatch: artifact={recorded.Assemblies.Count}, current={expected.Assemblies.Count}.");
        }

        for (var index = 0; index < recorded.Assemblies.Count; index++)
        {
            ReplayAssemblyIdentity recordedAssembly = recorded.Assemblies[index];
            ReplayAssemblyIdentity expectedAssembly = expected.Assemblies[index];
            RequireEqual($"assembly[{index}] name", recordedAssembly.Name, expectedAssembly.Name);
            RequireEqual($"assembly '{recordedAssembly.Name}' hash", recordedAssembly.Sha256, expectedAssembly.Sha256);
            RequireEqual(
                $"assembly '{recordedAssembly.Name}' runtime compatibility",
                recordedAssembly.RuntimeCompatibility,
                expectedAssembly.RuntimeCompatibility);
        }
    }

    /// <summary>
    /// Gets the simulated processor count an artifact recorded, which its replay runs with: the program observes it as
    /// <see cref="Environment.ProcessorCount"/> and sizes parallel work by it, so a replay with another value diverges.
    /// </summary>
    /// <param name="artifact">The artifact.</param>
    /// <returns>The recorded simulated processor count.</returns>
    /// <exception cref="ReplayCompatibilityException">The artifact records no valid processor count.</exception>
    public static int GetProcessorCount(ReplayArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (!artifact.Scheduler.Options.TryGetValue(ProcessorCountOption, out string? recorded) ||
            !int.TryParse(recorded, NumberStyles.None, CultureInfo.InvariantCulture, out int processorCount) ||
            processorCount <= 0)
        {
            throw new ReplayCompatibilityException(
                $"The artifact records no valid simulated processor count: '{ProcessorCountOption}' is '{recorded ?? "<none>"}'.");
        }

        return processorCount;
    }

    private static string GetOperatingSystem()
    {
        if (OperatingSystem.IsWindows())
        {
            return "windows";
        }

        if (OperatingSystem.IsLinux())
        {
            return "linux";
        }

        if (OperatingSystem.IsMacOS())
        {
            return "macos";
        }

        return "unknown";
    }

    private static void RequireEqual(string field, string? recorded, string? expected)
    {
        if (!string.Equals(recorded, expected, StringComparison.Ordinal))
        {
            throw new ReplayCompatibilityException(
                $"Replay {field} mismatch: artifact='{recorded ?? "<none>"}', current='{expected ?? "<none>"}'.");
        }
    }
}
