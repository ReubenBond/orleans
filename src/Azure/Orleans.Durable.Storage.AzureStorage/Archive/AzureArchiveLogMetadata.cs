using System.Globalization;
using Orleans.Durable.Storage.Engine;
using Orleans.Durable.Storage.Engine.Archive.Logs;

namespace Orleans.Durable.Storage.AzureStorage.Archive;

/// <summary>A write-ahead blob's metadata, as <see cref="AzureArchiveLogMetadata.ReadWal"/> reads it (archive-logs.md §7.3).</summary>
/// <param name="Format">The <c>format</c> caller parameter of the store's record.</param>
/// <param name="Generation">The current generation, <c>wal_generation</c>.</param>
/// <param name="Checkpoint">The current generation's checkpoint, present exactly when an opener created it.</param>
/// <param name="Opener">The opener which created the generation, from <c>durable_kind</c> and <c>durable_tomb</c>.</param>
/// <param name="BaseMd5">The MD5 of the generation's base in 32 lowercase hexadecimal digits, <c>durable_base_md5</c>, present exactly with the opener.</param>
/// <param name="KeyHash">The xxHash64 of the log's key, <c>durable_key</c>.</param>
internal sealed record AzureWalMetadata(string Format, ulong Generation, string? Checkpoint, ArchiveLogOpener? Opener, string? BaseMd5, ulong KeyHash);

/// <summary>What reading a write-ahead blob's metadata gives (conventions.md §15): its fields, or the rejection naming the rule it breaks.</summary>
internal readonly union AzureWalMetadataDecoding(AzureWalMetadata, InvalidDataException);

/// <summary>
/// The metadata of archive logs on Azure (archive-logs.md §7.3), in Orleans Journaling's own layout: the write-ahead
/// blob <c>wal/{id}</c> and each generation's checkpoint <c>checkpoints/{id}/{g:D20}</c>, with Orleans' <c>format</c>,
/// <c>checkpoint</c>, <c>checkpoint_offset</c>, and <c>wal_generation</c>, Durable Storage's <c>durable_kind</c>, <c>durable_tomb</c>,
/// <c>durable_base_md5</c>, and <c>durable_key</c>, and the index tag <c>durable-gen</c>. Metadata and the tag are written only when
/// a generation is created. <c>format</c> is not compared with the store's own: the store's record fixes it for the
/// cluster's life, and <c>durable_key</c> already tells a blob of another writer.
/// </summary>
/// <remarks>
/// A reader rejects, with the first reason in this order: a name present twice, or in two cases (<c>duplicate-name</c>),
/// since Azure's metadata names are case-insensitive; a missing or malformed value, in table order (<c>bad-{name}</c>):
/// <c>format</c>, <c>checkpoint</c>, <c>checkpoint_offset</c>, which is <c>0</c> exactly when <c>checkpoint</c> is present,
/// <c>wal_generation</c>, the tag <c>durable-gen</c> when a response reports it, <c>durable_kind</c>, <c>durable_tomb</c>,
/// <c>durable_base_md5</c>, <c>durable_base_rows</c>, and <c>durable_key</c>; <c>checkpoint</c>, <c>durable_kind</c>,
/// <c>durable_base_md5</c>, and <c>durable_base_rows</c> not all present or all absent (<c>kind-mismatch</c>); a generation above 1 with none of them, though only an opener creates
/// one (<c>opener-missing</c>); <c>durable_tomb</c> without <c>durable_kind = delete</c> (<c>tomb-mismatch</c>); a
/// <c>wal_generation</c> other than the tag's
/// (<c>generation-mismatch</c>); a <c>checkpoint</c> which names another log or generation (<c>checkpoint-mismatch</c>);
/// and a <c>durable_key</c> other than its key's (<c>key-mismatch</c>). Other names, as an Orleans caller's metadata, are
/// left alone.
/// </remarks>
internal static class AzureArchiveLogMetadata
{
    /// <summary>Orleans' metadata of the journal's format, on both blobs.</summary>
    public const string Format = "format";

    /// <summary>Orleans' metadata naming the current generation's checkpoint, on the write-ahead blob.</summary>
    public const string Checkpoint = "checkpoint";

    /// <summary>Orleans' metadata of the checkpoint's offset in the write-ahead blob, always 0.</summary>
    public const string CheckpointOffset = "checkpoint_offset";

    /// <summary>Orleans' metadata of the write-ahead blob's generation: <c>g</c> in decimal.</summary>
    public const string WalGeneration = "wal_generation";

    /// <summary>The opener's kind, <c>replace</c> or <c>delete</c>.</summary>
    public const string Kind = "durable_kind";

    /// <summary>The opener's tombstone, 16 lowercase hexadecimal digits.</summary>
    public const string Tombstone = "durable_tomb";

    /// <summary>The MD5 of the generation's base, 32 lowercase hexadecimal digits, which every read checks.</summary>
    public const string BaseMd5 = "durable_base_md5";

    /// <summary>The rows of the key the generation's base folds, in decimal, from 1 (archive-logs.md §7.2).</summary>
    public const string BaseRows = "durable_base_rows";

    /// <summary>The key's xxHash64, 16 lowercase hexadecimal digits.</summary>
    public const string Key = "durable_key";

    /// <summary>The index tag of the generation, <c>{g:D20}</c>, which every write's condition checks.</summary>
    public const string GenerationTag = "durable-gen";

    internal const string ReasonDuplicateName = "duplicate-name";
    internal const string ReasonKindMismatch = "kind-mismatch";
    internal const string ReasonOpenerMissing = "opener-missing";
    internal const string ReasonTombMismatch = "tomb-mismatch";
    internal const string ReasonGenerationMismatch = "generation-mismatch";
    internal const string ReasonCheckpointMismatch = "checkpoint-mismatch";
    internal const string ReasonKeyMismatch = "key-mismatch";

    private const string ReplaceKind = "replace";
    private const string DeleteKind = "delete";
    private const string WalPrefix = "wal/";
    private const string CheckpointPrefix = "checkpoints/";

    /// <summary>The length of a generation in a checkpoint's name and the tag: 20 decimal digits.</summary>
    private const int GenerationDigits = 20;

    /// <summary>The longest blob name Azure accepts: 1,024 characters.</summary>
    private const int MaxBlobNameLength = 1024;

    /// <summary>
    /// The most path segments in a blob name the store forms: 254. Azure documents 254 and accepts 255, refusing 256 with
    /// 400 and no error code; the margin of one is deliberate, so the rule holds whichever bound the service enforces.
    /// </summary>
    private const int MaxBlobNameSegments = 254;

    /// <summary>The longest log name whose blobs' names fit Azure's: <c>checkpoints/{id}/{g:D20}</c>, the longer.</summary>
    public const int MaxLogNameLength = MaxBlobNameLength - 12 /* "checkpoints/" */ - 1 /* "/" */ - GenerationDigits;

    /// <summary>The version of <see cref="IsAdmissibleLogName"/>, which the store's record records as <c>name-rule</c>.</summary>
    public const string NameRule = "azure-blob-2";

    /// <summary>The MD5 of no bytes, an empty base's, in 32 lowercase hexadecimal digits.</summary>
    public const string EmptyMd5 = "d41d8cd98f00b204e9800998ecf8427e";

    /// <summary>
    /// Returns whether <paramref name="log"/> names blobs Azure stores under exactly the names Durable Storage forms, version
    /// <see cref="NameRule"/>: at most <see cref="MaxLogNameLength"/> printable ASCII characters, U+0020 to U+007E, since
    /// an opener's write-ahead blob carries its checkpoint's name in its <c>checkpoint</c> metadata, an HTTP header, which
    /// holds only ASCII, and no backslash, which a URI turns into a slash; and <c>/</c>-separated segments, at most 252 so
    /// a checkpoint's name has at most 254, one below the 255 Azure accepts, each non-empty and not ending in <c>.</c>,
    /// since a URI collapses <c>.</c> and <c>..</c> and may drop a trailing dot. Any other name could alias another log's
    /// blobs, or name a checkpoint no write-ahead blob can record. Under a store's blob-name prefix the bounds shrink by the
    /// prefix's characters and segments, so every name the store forms still fits.
    /// </summary>
    /// <param name="log">The log's name.</param>
    /// <param name="prefix">The store's blob-name prefix, empty or ending in <c>/</c>.</param>
    public static bool IsAdmissibleLogName(string log, string prefix = "")
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(prefix);
        if (log.Length == 0 || log.Length > MaxLogNameLength - prefix.Length)
        {
            return false;
        }

        var segments = 1 + prefix.AsSpan().Count('/');
        for (var index = 0; index < log.Length; index++)
        {
            var character = log[index];
            if (character is < ' ' or > '~' or '\\')
            {
                return false;
            }

            if (character == '/')
            {
                if (index == 0 || log[index - 1] is '/' or '.')
                {
                    return false;
                }

                segments++;
            }
        }

        return log[^1] is not ('/' or '.') && segments <= MaxBlobNameSegments - 2;
    }

    /// <summary>Returns the name of a log's write-ahead blob, <c>{prefix}wal/{id}</c>.</summary>
    public static string WalName(string log, string prefix = "") => prefix + WalPrefix + log;

    /// <summary>Returns the name of a generation's checkpoint, <c>{prefix}checkpoints/{id}/{g:D20}</c>.</summary>
    public static string CheckpointName(string log, ulong generation, string prefix = "") =>
        string.Create(CultureInfo.InvariantCulture, $"{prefix}{CheckpointPrefix}{log}/{generation:D20}");

    /// <summary>Returns the prefix of every checkpoint's name under a store's blob-name prefix, <c>{prefix}checkpoints/</c>.</summary>
    public static string CheckpointsPrefix(string prefix = "") => prefix + CheckpointPrefix;

    /// <summary>Returns the index tag's value of a generation, <c>{g:D20}</c>.</summary>
    public static string TagOf(ulong generation) => generation.ToString("D20", CultureInfo.InvariantCulture);

    /// <summary>Returns the tag condition which fences a write to <paramref name="generation"/>.</summary>
    public static string TagCondition(ulong generation) =>
        string.Create(CultureInfo.InvariantCulture, $"\"{GenerationTag}\" = '{TagOf(generation)}'");

    /// <summary>
    /// Returns the log and generation a checkpoint's blob name names, parsed strictly: the last component is 20 decimal
    /// digits of a generation above 0, and everything between <c>{prefix}checkpoints/</c> and it, non-empty, is the log.
    /// </summary>
    public static (string Log, ulong Generation)? ParseCheckpointName(string name, string prefix = "")
    {
        ArgumentNullException.ThrowIfNull(name);
        var start = CheckpointsPrefix(prefix);
        var slash = name.LastIndexOf('/');
        if (!name.StartsWith(start, StringComparison.Ordinal) || slash <= start.Length || name.Length - slash - 1 != GenerationDigits)
        {
            return null;
        }

        var digits = name.AsSpan(slash + 1);
        var log = name[start.Length..slash];
        return !digits.ContainsAnyExceptInRange('0', '9') && ulong.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var generation) && generation > 0 && IsAdmissibleLogName(log, prefix)
            ? (log, generation)
            : null;
    }

    /// <summary>
    /// Returns the key hash a checkpoint's metadata records as <c>durable_key</c>, 16 lowercase hexadecimal digits, or
    /// <see langword="null"/> when its names repeat or the value is missing or malformed.
    /// </summary>
    public static ulong? CheckpointKeyHash(IEnumerable<KeyValuePair<string, string>>? metadata) =>
        metadata is not null && Named(metadata) is { } named ? HexValue(named.GetValueOrDefault(Key)) : null;

    /// <summary>
    /// Returns the metadata of a write-ahead blob of <paramref name="generation"/>: Orleans' keys, naming the checkpoint
    /// when an opener created the generation, and Durable Storage's, with the base's MD5 <paramref name="baseMd5"/> exactly when an
    /// opener created it.
    /// </summary>
    /// <exception cref="ArgumentException">An opener comes without its base's MD5, or the reverse.</exception>
    public static Dictionary<string, string> ForWal(string format, string log, ulong generation, ArchiveLogOpener? opener, byte[]? baseMd5, ulong keyHash, string prefix = "")
    {
        if (opener is null != baseMd5 is null || baseMd5 is { Length: not 16 })
        {
            throw new ArgumentException("A generation an opener created records its base's 16-byte MD5, and only it.", nameof(baseMd5));
        }

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [Format] = format,
            [WalGeneration] = generation.ToString(CultureInfo.InvariantCulture),
            [Key] = Hex(keyHash),
        };
        if (opener is { } created)
        {
            metadata[Checkpoint] = CheckpointName(log, generation, prefix);
            metadata[CheckpointOffset] = "0";
            metadata[Kind] = created.Kind == ArchiveLogOpenerKind.Replace ? ReplaceKind : DeleteKind;
            if (created.Tombstone is { } tombstone)
            {
                metadata[Tombstone] = Hex(tombstone);
            }

            metadata[BaseMd5] = Convert.ToHexStringLower(baseMd5!);
            metadata[BaseRows] = created.Rows.ToString(CultureInfo.InvariantCulture);
        }

        return metadata;
    }

    /// <summary>Returns the metadata of a checkpoint: Orleans' <c>format</c> and Durable Storage's <c>durable_key</c>.</summary>
    public static Dictionary<string, string> ForCheckpoint(string format, ulong keyHash) =>
        new(StringComparer.Ordinal) { [Format] = format, [Key] = Hex(keyHash) };

    /// <summary>
    /// Reads a write-ahead blob's metadata, checking the rules of archive-logs.md §7.3 in order, against the log
    /// <paramref name="log"/>, the key hash <paramref name="keyHash"/>, and the tag <paramref name="tag"/>, when a response
    /// reports it.
    /// </summary>
    /// <returns>The metadata's fields, or the rejection naming the first rule it breaks.</returns>
    public static AzureWalMetadataDecoding ReadWal(IEnumerable<KeyValuePair<string, string>> metadata, string log, ulong keyHash, string? tag, string prefix = "")
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (Named(metadata) is not { } named)
        {
            return DuplicateName();
        }

        var format = named.GetValueOrDefault(Format);
        if (string.IsNullOrEmpty(format))
        {
            return Bad(Format);
        }

        var checkpoint = named.GetValueOrDefault(Checkpoint);
        if (checkpoint is { Length: 0 })
        {
            return Bad(Checkpoint);
        }

        var offset = named.GetValueOrDefault(CheckpointOffset);
        if (checkpoint is null ? offset is not null : offset != "0")
        {
            return Bad(CheckpointOffset);
        }

        if (Decimal(named.GetValueOrDefault(WalGeneration)) is not { } generation)
        {
            return Bad(WalGeneration);
        }

        ulong? tagged = null;
        if (tag is not null)
        {
            if (tag.Length != GenerationDigits || Decimal(tag.TrimStart('0')) is not { } value)
            {
                return Bad(GenerationTag);
            }

            tagged = value;
        }

        var kind = named.GetValueOrDefault(Kind);
        if (kind is not (null or ReplaceKind or DeleteKind))
        {
            return Bad(Kind);
        }

        var tombstoneText = named.GetValueOrDefault(Tombstone);
        var tombstone = tombstoneText is null ? null : HexValue(tombstoneText);
        if (tombstoneText is not null && tombstone is null)
        {
            return Bad(Tombstone);
        }

        var baseMd5 = named.GetValueOrDefault(BaseMd5);
        if (baseMd5 is not null && (baseMd5.Length != 32 || baseMd5.AsSpan().ContainsAnyExcept("0123456789abcdef")))
        {
            return Bad(BaseMd5);
        }

        var baseRowsText = named.GetValueOrDefault(BaseRows);
        var baseRows = baseRowsText is null ? null : Decimal(baseRowsText);
        if (baseRowsText is not null && baseRows is not (>= 1 and <= ArchiveLogOpener.MaxRows))
        {
            return Bad(BaseRows);
        }

        if (HexValue(named.GetValueOrDefault(Key)) is not { } key)
        {
            return Bad(Key);
        }

        var opened = kind is not null;
        if (checkpoint is not null != opened || baseMd5 is not null != opened || baseRows is not null != opened)
        {
            return Reject(ReasonKindMismatch, $"names a checkpoint, an opener's kind, a base's MD5, and its rows not all together");
        }

        if (!opened && generation > 1)
        {
            return Reject(ReasonOpenerMissing, $"records generation {generation} without the opener which created it");
        }

        if (tombstone is not null && kind != DeleteKind)
        {
            return Reject(ReasonTombMismatch, $"records a tombstone on a generation no Delete opened");
        }

        if (tagged is { } fenced && fenced != generation)
        {
            return Reject(ReasonGenerationMismatch, $"records generation {generation} under the tag of {fenced}");
        }

        if (checkpoint is not null && checkpoint != CheckpointName(log, generation, prefix))
        {
            return Reject(ReasonCheckpointMismatch, $"names the checkpoint '{checkpoint}', not its log's generation {generation}");
        }

        if (key != keyHash)
        {
            return KeyMismatch(key, keyHash);
        }

        var opener = kind switch
        {
            null => (ArchiveLogOpener?)null,
            ReplaceKind => new ArchiveLogOpener(ArchiveLogOpenerKind.Replace, tombstone, baseRows!.Value),
            _ => new ArchiveLogOpener(ArchiveLogOpenerKind.Delete, tombstone, baseRows!.Value),
        };
        return new AzureWalMetadata(format, generation, checkpoint, opener, baseMd5, key);
    }


    /// <summary>
    /// Reads a checkpoint's metadata, checking in order its names (<c>duplicate-name</c>), its <c>format</c> and
    /// <c>durable_key</c> (<c>bad-{name}</c>), and its key (<c>key-mismatch</c>).
    /// </summary>
    /// <returns>The rejection naming the first rule the metadata breaks, or <see langword="null"/> when it is valid.</returns>
    public static InvalidDataException? ReadCheckpoint(IEnumerable<KeyValuePair<string, string>> metadata, ulong keyHash)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (Named(metadata) is not { } named)
        {
            return DuplicateName();
        }

        if (string.IsNullOrEmpty(named.GetValueOrDefault(Format)))
        {
            return Bad(Format);
        }

        return HexValue(named.GetValueOrDefault(Key)) is not { } key ? Bad(Key)
            : key != keyHash ? KeyMismatch(key, keyHash)
            : null;
    }

    /// <summary>Returns the 16 lowercase hexadecimal digits of <paramref name="value"/>.</summary>
    public static string Hex(ulong value) => value.ToString("x16", CultureInfo.InvariantCulture);

    /// <summary>Returns the metadata by name, case-insensitively, or <see langword="null"/> when a name repeats in any case.</summary>
    private static Dictionary<string, string>? Named(IEnumerable<KeyValuePair<string, string>> metadata)
    {
        var named = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in metadata)
        {
            if (!named.TryAdd(name, value ?? string.Empty))
            {
                return null;
            }
        }

        return named;
    }

    /// <summary>Returns a decimal of 1 to 20 digits with no leading zero, above 0, or <see langword="null"/>.</summary>
    private static ulong? Decimal(string? text) =>
        text is { Length: > 0 and <= GenerationDigits } && text[0] != '0' && !text.AsSpan().ContainsAnyExceptInRange('0', '9')
        && ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    /// <summary>Returns the value of 16 lowercase hexadecimal digits, or <see langword="null"/>.</summary>
    private static ulong? HexValue(string? text) =>
        text is { Length: 16 } && !text.AsSpan().ContainsAnyExcept("0123456789abcdef")
            ? ulong.Parse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture)
            : null;

    private static InvalidDataException DuplicateName() =>
        DecodeRejection.Create(ReasonDuplicateName, "An archive log's blob holds a metadata name twice, or in two cases.");

    private static InvalidDataException Bad(string name) =>
        DecodeRejection.Create("bad-" + name, $"An archive log's blob holds no well-formed '{name}'.");

    private static InvalidDataException KeyMismatch(ulong key, ulong keyHash) =>
        DecodeRejection.Create(ReasonKeyMismatch, $"An archive log's blob records the key hash {Hex(key)}, not its key's {Hex(keyHash)}: it names another key.");

    private static InvalidDataException Reject(string reason, FormattableString message) =>
        DecodeRejection.Create(reason, "An archive log's write-ahead blob " + message.ToString(CultureInfo.InvariantCulture) + ".");
}
