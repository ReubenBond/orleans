namespace Orleans.Durable.Storage.Architecture.Tests;

/// <summary>
/// Reads the table of spec README §5, which follows the heading <c>## 5. Implementation status</c> and has the columns
/// <c>| Part | Status |</c>.
/// </summary>
internal static class ImplementationStatus
{
    private const string Heading = "## 5. Implementation status";

    private const string NotYetInCode = "not yet in code";

    private static readonly string[] Columns = ["Part", "Status"];

    /// <summary>
    /// Whether §5 lists <paramref name="part"/>, by the label in its Part column, as not yet in code: its Status cell
    /// says so, in any case.
    /// </summary>
    /// <exception cref="InvalidDataException">§5 has no row for <paramref name="part"/>.</exception>
    public static bool IsNotYetInCode(string part)
    {
        var row = SpecReadme.Table(Heading, Columns).SingleOrDefault(candidate => string.Equals(candidate[0], part, StringComparison.Ordinal))
            ?? throw new InvalidDataException($"Spec README §5 has no row whose Part is {part}.");
        return row[1].Contains(NotYetInCode, StringComparison.OrdinalIgnoreCase);
    }
}
