namespace Orleans.Testing.Simulation.Instrumentation.Rules;

/// <summary>The kinds of site a <see cref="RewriteRule"/> rewrites.</summary>
[Flags]
internal enum RewriteSites
{
    /// <summary>No site.</summary>
    None = 0,

    /// <summary>A <c>call</c> or <c>callvirt</c> naming the target member.</summary>
    Call = 1,

    /// <summary>A <c>newobj</c> naming the target constructor.</summary>
    NewObj = 2,

    /// <summary>A reference to the target type.</summary>
    Type = 4,
}
