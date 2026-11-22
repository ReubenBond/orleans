namespace Orleans.Testing.Simulation.Instrumentation.Closure;

/// <summary>The exception thrown when an application closure cannot be discovered or is invalid.</summary>
public sealed class ClosureException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="ClosureException"/> class.</summary>
    /// <param name="message">The error message.</param>
    public ClosureException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ClosureException"/> class.</summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The underlying cause.</param>
    public ClosureException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
