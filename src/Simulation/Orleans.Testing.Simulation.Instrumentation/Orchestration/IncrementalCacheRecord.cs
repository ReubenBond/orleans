using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orleans.Testing.Simulation.Instrumentation.Orchestration;

internal sealed record IncrementalCacheRecord(string IncrementalKey, string ManifestSha256)
{
    private const int SchemaVersion = 1;
    private const int MaxDocumentBytes = 512;
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        AllowTrailingCommas = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 4,
    };

    public string ToJson() => Serialize(new Document
    {
        SchemaVersion = SchemaVersion,
        IncrementalKey = IncrementalKey,
        ManifestSha256 = ManifestSha256,
    });

    public static bool TryRead(string path, out IncrementalCacheRecord record)
    {
        record = null!;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is <= 0 or > MaxDocumentBytes)
            {
                return false;
            }

            byte[] utf8 = File.ReadAllBytes(path);
            if (utf8.Length is <= 0 or > MaxDocumentBytes)
            {
                return false;
            }

            Document? document = JsonSerializer.Deserialize<Document>(utf8, SerializerOptions);
            if (document is null
                || document.SchemaVersion != SchemaVersion
                || !IsSha256(document.IncrementalKey)
                || !IsSha256(document.ManifestSha256))
            {
                return false;
            }

            byte[] canonicalUtf8 = Encoding.UTF8.GetBytes(Serialize(document));
            if (!utf8.AsSpan().SequenceEqual(canonicalUtf8))
            {
                return false;
            }

            record = new IncrementalCacheRecord(document.IncrementalKey, document.ManifestSha256);
            return true;
        }
        catch (Exception exception) when (
            exception is JsonException
                or IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException)
        {
            return false;
        }
    }

    private static string Serialize(Document document) =>
        JsonSerializer.Serialize(document, SerializerOptions);

    private static bool IsSha256(string? value) =>
        value is { Length: ClosureManifestLimits.Sha256Length }
            && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private sealed record Document
    {
        public required int SchemaVersion { get; init; }

        public required string IncrementalKey { get; init; }

        public required string ManifestSha256 { get; init; }
    }
}
