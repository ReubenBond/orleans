using Orleans.Testing.Simulation.Instrumentation.Rules;

namespace Orleans.Testing.Simulation.Instrumentation.Rewriting;

/// <summary>
/// A single request to the <see cref="RewriteEngine"/>: the assembly to read, where to write the
/// rewritten result, the rule set to apply, and the <see cref="RewriteOptions"/> controlling
/// resolution and symbols.
/// </summary>
/// <param name="InputPath">The path of the assembly to rewrite.</param>
/// <param name="OutputPath">The path to write the rewritten assembly to (may equal <paramref name="InputPath"/>).</param>
/// <param name="RuleSet">The versioned rule set to apply.</param>
/// <param name="Options">The rewrite options, including the target runtime the rules are evaluated against.</param>
public sealed record RewriteRequest(
    string InputPath,
    string OutputPath,
    RewriteRuleSet RuleSet,
    RewriteOptions Options);
