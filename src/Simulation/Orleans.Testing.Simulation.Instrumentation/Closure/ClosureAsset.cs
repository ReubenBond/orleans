namespace Orleans.Testing.Simulation.Instrumentation.Closure;

/// <summary>
/// A single file discovered in an application closure, classified by <see cref="Kind"/> and marked
/// either as a rewrite candidate (<see cref="Rewrite"/> is <see langword="true"/>) or as an asset
/// copied verbatim into the staged output. When a managed assembly is not rewritten,
/// <see cref="SkipReason"/> records why (framework assembly, satellite, excluded by pattern, or
/// framework boundary), which keeps the closure both runnable and auditable.
/// </summary>
/// <param name="SourcePath">The absolute path of the file in the source directory.</param>
/// <param name="RelativePath">The path of the file relative to the closure root (using <c>/</c> separators).</param>
/// <param name="Kind">The asset classification.</param>
/// <param name="Rewrite">Whether the file is a managed assembly selected for IL rewriting.</param>
/// <param name="SkipReason">The reason a file is copied rather than rewritten, or <see langword="null"/>.</param>
public sealed record ClosureAsset(
    string SourcePath,
    string RelativePath,
    AssetKind Kind,
    bool Rewrite,
    string? SkipReason);
