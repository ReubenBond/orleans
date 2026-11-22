namespace Orleans.Durable.Storage.Architecture.Tests;

/// <summary>Reads the tables of spec README which the architecture tests pin.</summary>
internal static class SpecReadme
{
    /// <summary>
    /// The body rows, each as its trimmed cells, of the first table which follows the line starting
    /// <paramref name="anchor"/> before the next heading. Its header must be <paramref name="columns"/>, and each row
    /// must have as many cells.
    /// </summary>
    public static IReadOnlyList<string[]> Table(string anchor, string[] columns)
    {
        var lines = File.ReadAllLines(Path.Combine(Repository.FindRoot(), "docs", "spec", "README.md"));
        var anchorLine = Array.FindIndex(lines, line => line.StartsWith(anchor, StringComparison.Ordinal));
        if (anchorLine < 0)
        {
            throw new InvalidDataException($"Spec README has no line starting {anchor}.");
        }

        var header = Array.FindIndex(lines, anchorLine + 1, static line => line.StartsWith('|') || line.StartsWith('#'));
        if (header < 0 || !lines[header].StartsWith('|') || !Cells(lines[header]).SequenceEqual(columns, StringComparer.Ordinal))
        {
            throw new InvalidDataException($"The table after {anchor} does not have the columns {string.Join(", ", columns)}.");
        }

        var rows = new List<string[]>();

        // The row after the header separates it from the body.
        for (var row = header + 2; row < lines.Length && lines[row].StartsWith('|'); row++)
        {
            var cells = Cells(lines[row]);
            if (cells.Length != columns.Length)
            {
                throw new InvalidDataException($"A row of the table after {anchor} has {cells.Length} cells, not {columns.Length}: {lines[row]}");
            }

            rows.Add(cells);
        }

        return rows;
    }

    private static string[] Cells(string row) =>
        [.. row.Trim().Trim('|').Split('|').Select(static cell => cell.Trim())];
}
