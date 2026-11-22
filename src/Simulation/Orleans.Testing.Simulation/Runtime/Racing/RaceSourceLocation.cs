using System.Globalization;

namespace Orleans.Testing.Simulation.Runtime.Racing;

/// <summary>Exact IL and source metadata for an injected race-exploration scheduling point.</summary>
/// <param name="Method">The Cecil full name of the containing method.</param>
/// <param name="ILOffset">The original IL offset before instrumentation.</param>
/// <param name="SourceFile">The source document path, or <see langword="null"/> when symbols were unavailable.</param>
/// <param name="SourceLine">The source line, or <c>-1</c> when symbols were unavailable.</param>
public readonly record struct RaceSourceLocation(
    string Method,
    int ILOffset,
    string? SourceFile,
    int SourceLine)
{
    /// <inheritdoc />
    public override string ToString() =>
        SourceFile is null || SourceLine < 0
            ? string.Create(CultureInfo.InvariantCulture, $"{Method} IL_{ILOffset:x4}")
            : string.Create(CultureInfo.InvariantCulture, $"{SourceFile}:{SourceLine} ({Method} IL_{ILOffset:x4})");
}
