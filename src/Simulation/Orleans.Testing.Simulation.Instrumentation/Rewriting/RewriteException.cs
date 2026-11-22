using System.Text;

namespace Orleans.Testing.Simulation.Instrumentation.Rewriting;

/// <summary>
/// Thrown by <see cref="RewriteResult.EnsureSuccess"/> when a rewrite produced error diagnostics. The
/// message lists the error diagnostics, and <see cref="Result"/> exposes the full outcome.
/// </summary>
public sealed class RewriteException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="RewriteException"/> class.</summary>
    /// <param name="result">The failed rewrite result.</param>
    public RewriteException(RewriteResult result)
        : base(BuildMessage(result))
    {
        Result = result;
    }

    /// <summary>Gets the failed rewrite result.</summary>
    public RewriteResult Result { get; }

    private static string BuildMessage(RewriteResult result)
    {
        var builder = new StringBuilder("The rewrite failed with the following error diagnostics:");
        foreach (Diagnostics.RewriteDiagnostic error in result.Errors)
        {
            builder.Append('\n').Append(" - ").Append(error);
        }

        return builder.ToString();
    }
}
