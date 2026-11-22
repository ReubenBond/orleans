namespace Orleans.Testing.Simulation.Instrumentation.Configuration;

/// <summary>
/// Thrown when <see cref="InstrumentationConfiguration"/> or rule-set content is malformed or fails
/// strict validation (invalid JSON, missing/invalid fields, an unknown enum value, an invalid member
/// signature, or a referenced path that cannot be resolved). The message identifies the offending
/// element.
/// </summary>
public sealed class ConfigurationException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="ConfigurationException"/> class.</summary>
    /// <param name="message">A description of the configuration error.</param>
    public ConfigurationException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ConfigurationException"/> class.</summary>
    /// <param name="message">A description of the configuration error.</param>
    /// <param name="innerException">The underlying error, if any.</param>
    public ConfigurationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
