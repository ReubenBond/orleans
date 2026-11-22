namespace Orleans.Testing.Simulation.Instrumentation.Attributes;

/// <summary>
/// Marks a type that <see cref="Rewriting.RewriteEngine"/> must never rewrite. The engine skips the
/// whole type, including the compiler-generated types nested in it (async and iterator state machines,
/// lambda and local-function closures), transitively, and records each skip as an explicit exclusion in
/// the instrumentation manifest rather than silently ignoring it. An explicitly declared nested type keeps
/// its own annotation. A callback the type hands to simulated code - a lambda or local function - is
/// therefore not rewritten either and runs uncontrolled, with real time, real timers, and the thread pool.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false)]
public sealed class DoNotRewriteAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DoNotRewriteAttribute"/> class.
    /// </summary>
    /// <param name="reason">A human-readable reason recorded as the exclusion's justification.</param>
    public DoNotRewriteAttribute(string reason)
    {
        Reason = reason;
    }

    /// <summary>Gets the reason this type is excluded from rewriting.</summary>
    public string Reason { get; }
}
