using System.Diagnostics;
using System.Globalization;
using Orleans.Durable.Storage.Engine.Archive.Logs;

namespace Orleans.Durable.Storage.Fuzz.Archive.Logs;

/// <summary>The fields a write-ahead blob's metadata records, as <see cref="ArchiveLogMetadataRules"/> reads them.</summary>
/// <param name="Format">The <c>format</c>.</param>
/// <param name="Generation">The generation, <c>wal_generation</c>.</param>
/// <param name="Checkpoint">The checkpoint, or <see langword="null"/>.</param>
/// <param name="Opener">The opener, or <see langword="null"/>.</param>
/// <param name="BaseMd5">The base's MD5 in hexadecimal, or <see langword="null"/>.</param>
/// <param name="KeyHash">The key's hash, <c>durable_key</c>.</param>
public sealed record ArchiveLogMetadataFields(string Format, ulong Generation, string? Checkpoint, ArchiveLogOpener? Opener, string? BaseMd5, ulong KeyHash);

/// <summary>
/// One reading of archive logs' Azure metadata rules (archive-logs.md §7.3), written apart from the reader: the oracle of
/// the <c>archive-log-metadata</c> target. It answers the first rule the metadata breaks, in §7.3's order, or the fields
/// it records.
/// </summary>
public static class ArchiveLogMetadataRules
{
    private static readonly string[] WalNames = ["format", "checkpoint", "checkpoint_offset", "wal_generation", "durable-gen", "durable_kind", "durable_tomb", "durable_base_md5", "durable_base_rows", "durable_key"];

    /// <summary>Reads <paramref name="input"/>: the first rule it breaks, or, for a write-ahead blob, the fields it records.</summary>
    public static (string? Reason, ArchiveLogMetadataFields? Fields) Read(ArchiveLogMetadataInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var names = input.Metadata.Select(static pair => pair.Key.ToUpperInvariant()).ToList();
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Count)
        {
            return ("duplicate-name", null);
        }

        string? Value(string name) => input.Metadata.FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var name in input.Wal ? WalNames : ["format", "durable_key"])
        {
            var value = name == "durable-gen" ? input.Tag : Value(name);
            values[name] = value;
            if (!WellFormed(name, value, values))
            {
                return ("bad-" + name, null);
            }
        }

        var key = ulong.Parse(values["durable_key"]!, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        if (!input.Wal)
        {
            return key == input.KeyHash ? (null, null) : ("key-mismatch", null);
        }

        var generation = ulong.Parse(values["wal_generation"]!, CultureInfo.InvariantCulture);
        var checkpoint = values["checkpoint"];
        var kind = values["durable_kind"];
        var tomb = values["durable_tomb"];
        var md5 = values["durable_base_md5"];
        var rows = values["durable_base_rows"];
        var opened = kind is not null;
        var reason = checkpoint is not null != opened || md5 is not null != opened || rows is not null != opened ? "kind-mismatch"
            : !opened && generation > 1 ? "opener-missing"
            : tomb is not null && kind != "delete" ? "tomb-mismatch"
            : input.Tag is { } tag && ulong.Parse(tag, CultureInfo.InvariantCulture) != generation ? "generation-mismatch"
            : checkpoint is not null && checkpoint != "checkpoints/" + input.Log + "/" + generation.ToString("D20", CultureInfo.InvariantCulture) ? "checkpoint-mismatch"
            : key != input.KeyHash ? "key-mismatch"
            : null;
        if (reason is not null)
        {
            return (reason, null);
        }

        ArchiveLogOpener? opener = kind is null ? null : new ArchiveLogOpener(
            kind == "replace" ? ArchiveLogOpenerKind.Replace : ArchiveLogOpenerKind.Delete,
            tomb is null ? null : ulong.Parse(tomb, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture),
            ulong.Parse(rows!, CultureInfo.InvariantCulture));
        return (null, new ArchiveLogMetadataFields(values["format"]!, generation, checkpoint, opener, md5, key));
    }

    private static bool WellFormed(string name, string? value, Dictionary<string, string?> earlier) => name switch
    {
        "format" => !string.IsNullOrEmpty(value),
        "checkpoint" => value is null || value.Length > 0,
        "checkpoint_offset" => earlier["checkpoint"] is null ? value is null : value == "0",
        "wal_generation" => value is not null && IsPositiveDecimal(value) && value.Length <= 20,
        "durable-gen" => value is null || value.Length == 20 && value.All(char.IsAsciiDigit) && IsPositiveDecimal(value.TrimStart('0')),
        "durable_kind" => value is null or "replace" or "delete",
        "durable_tomb" => value is null || IsHex(value, 16),
        "durable_base_md5" => value is null || IsHex(value, 32),
        "durable_base_rows" => value is null || IsPositiveDecimal(value) && ulong.Parse(value, CultureInfo.InvariantCulture) < 1UL << 35,
        "durable_key" => value is not null && IsHex(value, 16),
        _ => throw new UnreachableException(),
    };

    private static bool IsPositiveDecimal(string value) =>
        value.Length > 0 && value[0] != '0' && value.All(char.IsAsciiDigit) && ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _);

    private static bool IsHex(string value, int digits) => value.Length == digits && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
