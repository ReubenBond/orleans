using System.Globalization;

namespace Orleans.Testing.Simulation.Instrumentation.Diagnostics;

/// <summary>
/// A single deterministic diagnostic produced while rewriting an assembly. Diagnostics carry a
/// stable <see cref="Id"/> (see <see cref="RewriteDiagnosticIds"/>), a <see cref="Severity"/>, a
/// human-readable <see cref="Message"/>, and optional location context (the fully-qualified method
/// and IL offset the diagnostic relates to). They are surfaced both on the
/// <see cref="Rewriting.RewriteResult"/> and, in a stable order, in the instrumentation manifest.
/// </summary>
/// <param name="Id">The stable diagnostic identifier.</param>
/// <param name="Severity">The diagnostic severity.</param>
/// <param name="Message">A human-readable description.</param>
/// <param name="Method">The fully-qualified method the diagnostic relates to, if any.</param>
/// <param name="ILOffset">The IL offset within <paramref name="Method"/>, if applicable (else <c>-1</c>).</param>
public readonly record struct RewriteDiagnostic(
    string Id,
    RewriteDiagnosticSeverity Severity,
    string Message,
    string? Method = null,
    int ILOffset = -1)
{
    /// <summary>Gets a value indicating whether this diagnostic is an <see cref="RewriteDiagnosticSeverity.Error"/>.</summary>
    public bool IsError => Severity == RewriteDiagnosticSeverity.Error;

    /// <summary>Creates an informational diagnostic.</summary>
    public static RewriteDiagnostic Info(string id, string message, string? method = null, int ilOffset = -1) =>
        new(id, RewriteDiagnosticSeverity.Info, message, method, ilOffset);

    /// <summary>Creates a warning diagnostic.</summary>
    public static RewriteDiagnostic Warning(string id, string message, string? method = null, int ilOffset = -1) =>
        new(id, RewriteDiagnosticSeverity.Warning, message, method, ilOffset);

    /// <summary>Creates an error diagnostic.</summary>
    public static RewriteDiagnostic Error(string id, string message, string? method = null, int ilOffset = -1) =>
        new(id, RewriteDiagnosticSeverity.Error, message, method, ilOffset);

    /// <inheritdoc/>
    public override string ToString()
    {
        string location = Method is null
            ? string.Empty
            : ILOffset >= 0
                ? string.Create(CultureInfo.InvariantCulture, $" at {Method} +IL_{ILOffset:x4}")
                : $" at {Method}";
        return string.Create(CultureInfo.InvariantCulture, $"{Id} [{Severity}]: {Message}{location}");
    }
}
