namespace Orleans.Testing.Simulation.Instrumentation.Rules;

/// <summary>
/// The kind of IL transformation a <see cref="RewriteRule"/> performs at a matched site. Each kind
/// maps to a specific, verifiable edit the engine's passes know how to apply; the set is
/// deliberately small and shape-driven rather than tied to any particular BCL API.
/// </summary>
public enum RewriteOperationKind
{
    /// <summary>
    /// Redirect a <c>call</c>/<c>callvirt</c> to a static replacement method whose parameters match
    /// the original stack shape (for an instance target, the receiver is the replacement's first
    /// parameter). The replacement's return type must match the original's.
    /// </summary>
    RedirectCall,

    /// <summary>
    /// Redirect a <c>newobj</c> to a static factory method that takes the same constructor arguments
    /// and returns an instance assignable to the constructed type.
    /// </summary>
    RedirectNewObj,

    /// <summary>
    /// Rewrite references to a type (in method bodies and member signatures) to a substitute type of
    /// identical shape.
    /// </summary>
    SubstituteType,

    /// <summary>
    /// Insert a post-call wrapper: after the matched call returns a value, invoke a static
    /// interception method that takes that value and returns a (possibly wrapped) value of the same
    /// type.
    /// </summary>
    WrapAfterCall,

    /// <summary>
    /// Inject a deterministic rejection call immediately before an unsupported invocation. The
    /// injected static method throws, so the original invocation never executes at runtime; it is
    /// left in place so the IL stack stays balanced and verifiable.
    /// </summary>
    InjectRejection,

    /// <summary>
    /// Inject a race-exploration scheduling point while preserving the original memory or
    /// control-flow instruction and its stack behavior.
    /// </summary>
    InjectSchedulingPoint,
}
