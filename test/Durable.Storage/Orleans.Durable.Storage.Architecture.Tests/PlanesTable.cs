using System.Text.RegularExpressions;

namespace Orleans.Durable.Storage.Architecture.Tests;

/// <summary>
/// A plane cell of the table of types by plane in spec README §2.1: the namespace it names and the types it names in
/// that namespace.
/// </summary>
internal sealed record PlanesTableCell(string Layer, string Plane, string Namespace, IReadOnlyList<string> TypeNames);

/// <summary>
/// Reads the table which follows the paragraph starting <c>**Types by plane.**</c> in spec README §2.1. Its columns are
/// <c>| Layer | Data plane | Control plane |</c>. In each plane cell, the first backticked token is the namespace and
/// each later one a type's simple name; text outside backticks is ignored, and a cell without backticks, such as
/// <c>None</c>, names nothing.
/// </summary>
internal static partial class PlanesTable
{
    private const string ParagraphStart = "**Types by plane.**";

    private static readonly string[] Columns = ["Layer", "Data plane", "Control plane"];

    public static IReadOnlyList<PlanesTableCell> Read()
    {
        var cells = new List<PlanesTableCell>();
        foreach (var values in SpecReadme.Table(ParagraphStart, Columns))
        {
            for (var column = 1; column < Columns.Length; column++)
            {
                string[] tokens = [.. Backticked().Matches(values[column]).Select(static match => match.Groups["token"].Value)];
                if (tokens.Length > 0)
                {
                    cells.Add(new(values[0], Columns[column], tokens[0], tokens[1..]));
                }
            }
        }

        return cells;
    }

    [GeneratedRegex("`(?<token>[^`]+)`", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 10_000)]
    private static partial Regex Backticked();
}
