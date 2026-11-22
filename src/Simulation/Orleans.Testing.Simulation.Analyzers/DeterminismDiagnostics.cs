using Microsoft.CodeAnalysis;

namespace Orleans.Testing.Simulation.Analyzers;

/// <summary>
/// The diagnostic descriptors raised by <see cref="NondeterministicApiAnalyzer"/>. They deliberately
/// mirror the built-in <c>clockwork.bcl.deterministic</c> rewrite rule set:
/// <list type="bullet">
///   <item><see cref="ControlledApi"/> (<c>CW1001</c>) - time / identity / random members that the
///   rewriter redirects to a deterministic shim. The call only becomes deterministic once the
///   assembly is instrumented, so this is an informational nudge rather than a defect.</item>
/// </list>
/// The ids intentionally sit alongside the runtime/rewrite <c>CWR####</c> ids (see
/// <c>RewriteDiagnosticIds</c>) so tooling messages stay coherent across compile time and rewrite time.
/// </summary>
public static class DeterminismDiagnostics
{
    /// <summary>The diagnostic category shared by every Clockwork determinism diagnostic.</summary>
    public const string Category = "Clockwork.Determinism";

    /// <summary>
    /// <c>CW1001</c>: a BCL member which must be rewritten to remain under Clockwork's simulation
    /// control when the containing assembly is instrumented.
    /// </summary>
    public static readonly DiagnosticDescriptor ControlledApi = new(
        id: "CW1001",
        title: "Nondeterministic BCL member requires Clockwork instrumentation",
        messageFormat: "'{0}' requires Clockwork's built-in '{1}' instrumentation to remain under simulation control",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Nondeterministic BCL, task, thread, and synchronization members are controlled or rejected after the containing assembly is instrumented. Instrumented closure binaries are simulation/test artifacts whose Controlled entry points require an active Clockwork simulation; uninstrumented production binaries retain ordinary BCL behaviour.");

    /// <summary>
    /// <c>CW1003</c>: unordered dictionary or hash-set enumeration is consumed by an operation whose
    /// result depends on element position.
    /// </summary>
    public static readonly DiagnosticDescriptor UnstableCollectionOrdering = new(
        id: "CW1003",
        title: "Unordered collection enumeration influences deterministic logic",
        messageFormat: "'{0}' consumes Dictionary/HashSet enumeration order; apply an explicit OrderBy with a stable comparer before this operation",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Dictionary and HashSet enumeration order is not a deterministic ordering contract. Clockwork reports only position-sensitive consumers such as First, Last, ElementAt, SequenceEqual, and string.Join; ordinary membership and keyed access are not reported.");
}
