using System.Collections.Immutable;

namespace Orleans.Testing.Simulation.Instrumentation.Closure;

/// <summary>
/// The deterministic result of discovering an application closure: the root directory that was
/// scanned, the detected entry assembly (if any), and every classified <see cref="ClosureAsset"/>.
/// Assets are ordered by their relative path so the plan is stable across runs and machines.
/// </summary>
/// <param name="RootDirectory">The absolute root directory that was scanned.</param>
/// <param name="EntryAssemblyRelativePath">The relative path of the detected entry assembly, or <see langword="null"/>.</param>
/// <param name="Assets">Every discovered asset, ordered by relative path.</param>
public sealed record ClosurePlan(
    string RootDirectory,
    string? EntryAssemblyRelativePath,
    ImmutableArray<ClosureAsset> Assets)
{
    /// <summary>Gets the managed assemblies selected for rewriting, in stable order.</summary>
    public IEnumerable<ClosureAsset> AssembliesToRewrite => Assets.Where(a => a.Rewrite);

    /// <summary>Gets the assets copied verbatim into the staged closure, in stable order.</summary>
    public IEnumerable<ClosureAsset> AssetsToCopy => Assets.Where(a => !a.Rewrite);
}
