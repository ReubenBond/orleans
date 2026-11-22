using System.Globalization;
using System.Text.Json;

namespace Orleans.Durable.Storage.Fuzz;

/// <summary>
/// Reads the golden vectors under <c>test/Durable.Storage/shared-vectors</c> (formats.md §6), which seed every target's corpus: 64-bit
/// integers are decimal strings and byte strings are lowercase hexadecimal.
/// </summary>
public sealed class GoldenVectors(string storageRoot)
{
    /// <summary>
    /// Gets the Durable Storage root, <c>test/Durable.Storage</c>, which holds the vectors and corpora.
    /// </summary>
    public string StorageRoot { get; } = storageRoot;

    /// <summary>
    /// Finds the Durable Storage root in the repository holding the running harness: the nearest directory above it with <c>Orleans.slnx</c>.
    /// </summary>
    public static GoldenVectors Find()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Orleans.slnx")))
            {
                return new GoldenVectors(Path.Combine(directory.FullName, "test", "Durable.Storage"));
            }
        }

        throw new DirectoryNotFoundException("Could not find the Orleans repository root above the harness.");
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
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(StorageRoot, "shared-vectors", area, file)));
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
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(StorageRoot, "shared-vectors", area, file)));
        return document.RootElement.Clone();
    }
}
