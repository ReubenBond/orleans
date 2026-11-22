using Orleans.Testing.Simulation.Instrumentation.Configuration;
using Orleans.Testing.Simulation.Instrumentation.Rules;

namespace Orleans.Testing.Simulation.Instrumentation.Orchestration;

/// <summary>
/// A request to instrument an application closure: the source directory to read from, the staging
/// directory to write the instrumented closure to (never the source), the effective configuration
/// and merged rule set, and the predictable manifest path. Callers merge rule-set documents (via
/// <see cref="RuleSetMerge"/>) and supply the result here, so the runner is decoupled from file
/// loading and is directly testable.
/// </summary>
public sealed record InstrumentationRequest
{
    /// <summary>Gets the source application output/publish directory to read (never modified).</summary>
    public required string SourceDirectory { get; init; }

    /// <summary>
    /// Gets the staging directory the instrumented closure is written to. It is fully owned by the
    /// runner: it is cleared and recreated on a non-incremental run. It must never be the source
    /// directory or a bin/source input.
    /// </summary>
    public required string StagingDirectory { get; init; }

    /// <summary>Gets the effective instrumentation configuration.</summary>
    public required InstrumentationConfiguration Configuration { get; init; }

    /// <summary>Gets the merged rule set applied to every rewritten assembly.</summary>
    public required RewriteRuleSet RuleSet { get; init; }

    /// <summary>
    /// Gets the entry assembly's simple name or file name, or <see langword="null"/> to auto-detect it
    /// from a <c>*.runtimeconfig.json</c> in the source directory.
    /// </summary>
    public string? EntryAssemblyName { get; init; }

    /// <summary>
    /// Gets the path the closure manifest is written to. Defaults to
    /// <c>&lt;StagingDirectory&gt;.manifest.json</c> (a sibling of the staging directory, so it never
    /// ships inside the instrumented closure). A path within the staging directory is supported as a
    /// reserved metadata file only when it does not collide with a staged closure asset. It must
    /// always be outside the source directory.
    /// </summary>
    public string ManifestPath
    {
        get => _manifestPath ?? InstrumentationPath.GetFullPath(
            StagingDirectory,
            nameof(StagingDirectory)).TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + ".manifest.json";
        init => _manifestPath = value;
    }

    /// <summary>
    /// Gets the path the authenticated incremental cache record is written to. The bounded record
    /// binds the incremental key to the exact closure-manifest bytes. Defaults to
    /// <c>&lt;StagingDirectory&gt;.cache</c> (a sibling, so it survives clearing the staging directory).
    /// It must be outside both the source and staging directories and distinct from
    /// <see cref="ManifestPath"/>.
    /// </summary>
    public string CachePath
    {
        get => _cachePath ?? InstrumentationPath.GetFullPath(
            StagingDirectory,
            nameof(StagingDirectory)).TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + ".cache";
        init => _cachePath = value;
    }

    internal string? ManifestPathOverride => _manifestPath;
    internal string? CachePathOverride => _cachePath;

    private readonly string? _manifestPath;
    private readonly string? _cachePath;
}
