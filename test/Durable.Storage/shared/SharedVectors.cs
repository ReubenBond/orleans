using System.Globalization;
using System.Text.Json;
using Orleans.Durable.Storage.Engine;

namespace Orleans.Durable.Storage.Testing;

/// <summary>
/// Reads the language-neutral golden vectors under <c>test/Durable.Storage/shared-vectors</c> (formats.md §6): 64-bit integers are
/// decimal strings and byte strings are lowercase hexadecimal.
/// </summary>
internal static class SharedVectors
{
    /// <summary>
    /// Returns the cases of one vector file, which must hold at least one.
    /// </summary>
    internal static JsonElement[] Cases(string area, string file, string property = "cases")
    {
        using var document = Load(area, file);
        var cases = document.RootElement.GetProperty(property).EnumerateArray().Select(static element => element.Clone()).ToArray();
        Assert.NotEmpty(cases);
        return cases;
    }

    internal static JsonDocument Load(string area, string file) => JsonDocument.Parse(File.ReadAllText(FilePath(area, file)));

    /// <summary>
    /// Returns the path of a vector file.
    /// </summary>
    internal static string FilePath(string area, string file) => Path.Combine(StorageRoot(), "shared-vectors", area, file);

    internal static string Name(JsonElement element) => element.TryGetProperty("name", out var name) ? name.GetString()! : "the vector";

    internal static byte[] Hex(JsonElement element, string property = "hex") =>
        Convert.FromHexString(element.GetProperty(property).GetString()!);

    internal static string Text(JsonElement element, string property) => element.GetProperty(property).GetString()!;

    internal static ulong UInt64(JsonElement element, string property) =>
        ulong.Parse(element.GetProperty(property).GetString()!, NumberStyles.None, CultureInfo.InvariantCulture);

    internal static long Int64(JsonElement element, string property) =>
        long.Parse(element.GetProperty(property).GetString()!, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);

    /// <summary>
    /// Asserts that encoding a case produced exactly its vector bytes.
    /// </summary>
    internal static void AssertBytes(JsonElement element, ReadOnlySpan<byte> encoded, string property = "hex")
    {
        var expected = element.GetProperty(property).GetString();
        var actual = Convert.ToHexStringLower(encoded);
        Assert.True(expected == actual, $"The encoding of '{Name(element)}' differs from the vector: {actual}.");
    }

    /// <summary>
    /// Asserts that a rejection vector's decoding threw <see cref="InvalidDataException"/> carrying the vector's
    /// <c>reason</c> as its reason code (<see cref="DecodeRejection"/>).
    /// </summary>
    internal static void AssertRejected(JsonElement element, Exception? exception)
    {
        var reason = Text(element, "reason");
        Assert.True(exception is InvalidDataException, $"'{Name(element)}' ({reason}) was not rejected as invalid data: {exception}");
        var rejected = DecodeRejection.ReasonOf((InvalidDataException)exception);
        Assert.True(reason == rejected, $"'{Name(element)}' was rejected as {rejected ?? "no reason"}, not {reason}: {exception.Message}");
    }

    /// <summary>
    /// Returns the Durable Storage root, <c>test/Durable.Storage</c>, in the repository above the test output: the nearest directory which holds <c>Orleans.slnx</c>.
    /// </summary>
    internal static string StorageRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Orleans.slnx")))
            {
                return Path.Combine(directory.FullName, "test", "Durable.Storage");
            }
        }

        throw new DirectoryNotFoundException("Could not find the Orleans repository root.");
    }
}
