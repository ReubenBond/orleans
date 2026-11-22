using System.Globalization;
using System.Text;
using System.Text.Unicode;

namespace Orleans.Durable.Storage.Fuzz.Archive.Logs;

/// <summary>One input of the metadata target: a blob's metadata pairs, read against a log, its key's hash, and a tag.</summary>
/// <param name="Wal">Whether the blob is a write-ahead blob, not a checkpoint.</param>
/// <param name="Log">The log's name.</param>
/// <param name="KeyHash">The key's hash.</param>
/// <param name="Tag">The tag a response reports, or <see langword="null"/>.</param>
/// <param name="Metadata">The metadata's name and value pairs, in order.</param>
public sealed record ArchiveLogMetadataInput(bool Wal, string Log, ulong KeyHash, string? Tag, IReadOnlyList<KeyValuePair<string, string>> Metadata)
{
    /// <summary>
    /// Parses the target's input: strict UTF-8 lines separated by line feeds, the blob (<c>wal</c> or <c>checkpoint</c>),
    /// the log, the key hash in 16 lowercase hexadecimal digits, and the tag or nothing, then one <c>name=value</c> line per
    /// pair, split at its first equals sign; <see langword="null"/> for an input outside that form.
    /// </summary>
    public static ArchiveLogMetadataInput? Parse(ReadOnlySpan<byte> input)
    {
        if (!Utf8.IsValid(input))
        {
            return null;
        }

        var lines = Encoding.UTF8.GetString(input).Split('\n');
        if (lines.Length < 4 || lines[0] is not ("wal" or "checkpoint") || lines[1].Length == 0 || lines[2].Length != 16
            || lines[2].AsSpan().ContainsAnyExcept("0123456789abcdef"))
        {
            return null;
        }

        var pairs = new List<KeyValuePair<string, string>>(lines.Length - 4);
        foreach (var line in lines.AsSpan(4))
        {
            var equals = line.IndexOf('=', StringComparison.Ordinal);
            if (equals < 0)
            {
                return null;
            }

            pairs.Add(KeyValuePair.Create(line[..equals], line[(equals + 1)..]));
        }

        var keyHash = ulong.Parse(lines[2], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        return new ArchiveLogMetadataInput(lines[0] == "wal", lines[1], keyHash, lines[3].Length == 0 ? null : lines[3], pairs);
    }

    /// <summary>Returns the input's bytes, in the form <see cref="Parse"/> reads.</summary>
    public byte[] Encode() =>
        Encoding.UTF8.GetBytes(string.Join('\n', new[] { Wal ? "wal" : "checkpoint", Log, KeyHash.ToString("x16", CultureInfo.InvariantCulture), Tag ?? string.Empty }
            .Concat(Metadata.Select(static pair => pair.Key + "=" + pair.Value))));
}
