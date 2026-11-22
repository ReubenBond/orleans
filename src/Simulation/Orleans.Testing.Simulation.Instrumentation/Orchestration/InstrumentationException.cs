using System.Text;

namespace Orleans.Testing.Simulation.Instrumentation.Orchestration;

/// <summary>
/// Thrown when an <see cref="InstrumentationRunner"/> run fails and the caller opted into fail-fast
/// behaviour via <see cref="InstrumentationResult.EnsureSuccess"/>. The message aggregates the
/// error-severity diagnostics so the failure is explicit rather than silent.
/// </summary>
public sealed class InstrumentationException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="InstrumentationException"/> class.</summary>
    /// <param name="result">The failed instrumentation result.</param>
    public InstrumentationException(InstrumentationResult result)
        : base(BuildMessage(result))
    {
        Result = result;
    }

    /// <summary>Gets the failed instrumentation result.</summary>
    public InstrumentationResult Result { get; }

    private static string BuildMessage(InstrumentationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var builder = new StringBuilder("Instrumentation failed:");
        foreach (var diagnostic in result.Errors)
        {
            builder.Append('\n').Append("  ").Append(diagnostic.ToString());
        }

        return builder.ToString();
    }
}
