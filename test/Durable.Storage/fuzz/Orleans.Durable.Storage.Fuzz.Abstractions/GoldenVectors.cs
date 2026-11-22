using System.Globalization;
using System.Text.Json;

namespace Orleans.Durable.Storage.Fuzz;

/// <summary>
/// Reads the golden vectors under <c>tests/shared-vectors</c> (formats.md §6), which seed every target's corpus: 64-bit
/// integers are decimal strings and byte strings are lowercase hexadecimal.
/// </summary>
public sealed class GoldenVectors(string repositoryRoot)
{
    /// <summary>
    /// Gets the root of the repository the vectors belong to.
    /// </summary>
    public string RepositoryRoot { get; } = repositoryRoot;

    /// <summary>
    /// Finds the repository holding the running harness: the nearest directory above it with <c>Clog.slnx</c>.
    /// </summary>
    public static GoldenVectors Find()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Clog.slnx")))
            {
                return new GoldenVectors(directory.FullName);
            }
        }

        throw new DirectoryNotFoundException("Could not find the Clog repository root above the harness.");
    }

    public static byte[] Hex(JsonElement element, string property = "hex") =>
        Convert.FromHexString(Text(element, property));

    /// <summary>
    /// Expands a trimmed vector: its bytes through the last non-zero byte, then zero to <paramref name="length"/>.
    /// </summary>
    public static byte[] Expand(byte[] prefix, int length)
    {
        var bytes = new byte[Math.Max(length, prefix.Length)];
        prefix.CopyTo(bytes, 0);
        return bytes;
    }

    public static ulong UInt64(JsonElement element, string property) =>
        ulong.Parse(Text(element, property), NumberStyles.None, CultureInfo.InvariantCulture);

    /// <summary>
    /// Returns a string property of a vector: a byte string or a 64-bit integer.
    /// </summary>
    /// <exception cref="InvalidDataException">The property is not a string.</exception>
    public static string Text(JsonElement element, string property) =>
        element.GetProperty(property).GetString() ?? throw new InvalidDataException($"The vector property '{property}' is not a string.");

    /// <summary>
    /// Returns the cases of one vector file.
    /// </summary>
    public JsonElement[] Cases(string area, string file)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryRoot, "tests", "shared-vectors", area, file)));
        return [.. document.RootElement.GetProperty("cases").EnumerateArray().Select(static element => element.Clone())];
    }

    /// <summary>
    /// Returns the reason codes the rejection vectors of the named files give (conventions.md §15): the codes a decoder
    /// of the format may report, which its oracle loads rather than copies.
    /// </summary>
    public HashSet<string> Reasons(string area, params string[] files)
    {
        ArgumentNullException.ThrowIfNull(files);
        return [.. files.SelectMany(file => Cases(area, file)).Select(static vector => Text(vector, "reason"))];
    }

    /// <summary>
    /// Returns the root element of a vector file which holds one case.
    /// </summary>
    public JsonElement Document(string area, string file)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryRoot, "tests", "shared-vectors", area, file)));
        return document.RootElement.Clone();
    }
}
