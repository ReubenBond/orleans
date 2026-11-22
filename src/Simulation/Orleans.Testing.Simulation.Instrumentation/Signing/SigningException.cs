namespace Orleans.Testing.Simulation.Instrumentation.Signing;

/// <summary>
/// The exception thrown when a strong-name key cannot be loaded or is unusable for the requested
/// operation (for example, a public-only key supplied where signing requires a private key).
/// </summary>
public sealed class SigningException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="SigningException"/> class.</summary>
    /// <param name="message">The error message.</param>
    public SigningException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="SigningException"/> class.</summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The underlying cause.</param>
    public SigningException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
