namespace Orleans.Testing.Simulation.Runtime.Shims;

/// <summary>Thrown when a controlled rewrite target is invoked without an active simulation.</summary>
public sealed class SimulationNotActiveException : InvalidOperationException
{
    /// <summary>The stable diagnostic emitted for every controlled API invoked outside simulation.</summary>
    public const string DiagnosticMessage =
        "Controlled APIs may only be invoked while a Clockwork simulation is active.";

    /// <summary>Initializes a new instance for the controlled API that was invoked.</summary>
    /// <param name="apiName">The rewritten BCL API name.</param>
    public SimulationNotActiveException(string apiName)
        : base(DiagnosticMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiName);
        ApiName = apiName;
    }

    /// <summary>Gets the rewritten BCL API name.</summary>
    public string ApiName { get; }
}
