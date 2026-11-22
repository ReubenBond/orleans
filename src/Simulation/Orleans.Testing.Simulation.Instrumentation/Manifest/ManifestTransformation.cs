using Orleans.Testing.Simulation.Instrumentation.Rules;
using Orleans.Testing.Simulation.Runtime.Policy;

namespace Orleans.Testing.Simulation.Instrumentation.Manifest;

/// <summary>
/// A single entry in the instrumentation manifest describing one site the engine acted on: the rule
/// and operation, the outcome and its simulation API-policy classification, the matched target and its
/// replacement, and the precise location (method, IL offset, and source file/line when symbols are
/// available).
/// </summary>
/// <param name="RuleId">The id of the rule that matched.</param>
/// <param name="Operation">The operation the rule performs.</param>
/// <param name="Outcome">What the engine did at the site.</param>
/// <param name="Policy">The simulation API-policy classification of the target.</param>
/// <param name="Target">The canonical target signature that matched.</param>
/// <param name="Replacement">The canonical replacement signature.</param>
/// <param name="Method">The fully-qualified method containing the site.</param>
/// <param name="ILOffset">The IL offset of the site within <paramref name="Method"/>.</param>
/// <param name="SourceFile">The source file for the site, if symbols were available; else <see langword="null"/>.</param>
/// <param name="SourceLine">The source line for the site, or <c>-1</c> if unavailable.</param>
public readonly record struct ManifestTransformation(
    string RuleId,
    RewriteOperationKind Operation,
    TransformationOutcome Outcome,
    SimulationApiPolicy Policy,
    string Target,
    string Replacement,
    string Method,
    int ILOffset,
    string? SourceFile,
    int SourceLine);
