using Orleans.Testing.Simulation.Instrumentation.Diagnostics;
using Mono.Cecil;

namespace Orleans.Testing.Simulation.Instrumentation.Rewriting;

/// <summary>
/// Read-only pass that fails the rewrite at every method compiled with runtime async. Such a method carries the
/// <c>MethodImplOptions.Async</c> implementation flag instead of an async state machine: the runtime suspends it at each
/// await and resumes it through its own continuation machinery, so the controlled builders and awaiters a rule set
/// substitutes never see it, and its continuations would bypass the simulation coordinator. The pass runs when the rule
/// set substitutes the async method builders, and reports <see cref="RewriteDiagnosticIds.RuntimeAsyncMethod"/> naming
/// the method and the remedy.
/// </summary>
internal sealed class RuntimeAsyncDetectionPass : RewritePass
{
    private const MethodImplAttributes Async = (MethodImplAttributes)global::System.Reflection.MethodImplAttributes.Async;

    private static readonly HashSet<string> AsyncMethodBuilderTypes = new(StringComparer.Ordinal)
    {
        "System.Runtime.CompilerServices.AsyncTaskMethodBuilder",
        "System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1",
        "System.Runtime.CompilerServices.AsyncValueTaskMethodBuilder",
        "System.Runtime.CompilerServices.AsyncValueTaskMethodBuilder`1",
    };

    public RuntimeAsyncDetectionPass(RewriteSession session)
        : base(session)
    {
    }

    /// <summary>Returns whether <paramref name="matcher"/> controls async methods by substituting their builders.</summary>
    /// <param name="matcher">The matcher of the rule set the rewrite applies.</param>
    /// <returns>Whether a runtime-async method would escape the rule set.</returns>
    public static bool Applies(RewriteRuleMatcher matcher) =>
        matcher.TypeSubstitutionRules.Any(entry => AsyncMethodBuilderTypes.Contains(entry.Key));

    internal override void VisitMethod(MethodDefinition method)
    {
        if ((method.ImplAttributes & Async) != 0)
        {
            string name = CecilNames.FullyQualifiedMethodName(method);
            Session.AddDiagnostic(RewriteDiagnostic.Error(
                RewriteDiagnosticIds.RuntimeAsyncMethod,
                $"Method '{name}' in '{Module!.Assembly?.Name.Name ?? Module.Name}' is compiled with runtime async (MethodImplOptions.Async), " +
                "so the runtime resumes it through its own continuation machinery, which bypasses the simulation " +
                "coordinator. Compile it without runtime async (remove the 'runtime-async=on' compiler feature) so the C# " +
                "compiler emits an async state machine, which Clockwork controls, or exclude its assembly from rewriting " +
                "to leave it uncontrolled.",
                name));
        }
    }
}
