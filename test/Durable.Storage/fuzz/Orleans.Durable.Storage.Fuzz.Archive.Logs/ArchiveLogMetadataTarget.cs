using System.Diagnostics;
using System.Globalization;
using Orleans.Durable.Storage.AzureStorage.Archive;
using Orleans.Durable.Storage.Engine;

namespace Orleans.Durable.Storage.Fuzz.Archive.Logs;

/// <summary>
/// Archive logs' Azure metadata reader (archive-logs.md §7.3): rejected with the reason <see cref="ArchiveLogMetadataRules"/>
/// gives, one of <c>metadata-rejections.json</c>'s reasons, or accepted with the fields the rules give.
/// </summary>
public sealed class ArchiveLogMetadataTarget : FuzzTarget
{
    private static readonly Lazy<HashSet<string>> Reasons = new(static () => GoldenVectors.Find().Reasons("archive-logs", "metadata-rejections.json"));

    public override string Name => "archive-log-metadata";

    public override int MaxLength => 2048;

    public override IEnumerable<byte[]> Seeds(GoldenVectors vectors)
    {
        ArgumentNullException.ThrowIfNull(vectors);
        return [.. new[] { "metadata.json", "metadata-rejections.json" }
            .SelectMany(file => vectors.Cases("archive-logs", file))
            .Select(static vector => InputOf(vector).Encode())];
    }

    /// <summary>Returns a metadata vector's input.</summary>
    public static ArchiveLogMetadataInput InputOf(System.Text.Json.JsonElement vector) =>
        new(
            GoldenVectors.Text(vector, "blob") == "wal",
            GoldenVectors.Text(vector, "log"),
            ulong.Parse(GoldenVectors.Text(vector, "keyHash"), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture),
            vector.GetProperty("tag").GetString(),
            [.. vector.GetProperty("metadata").EnumerateArray().Select(static pair => KeyValuePair.Create(pair[0].GetString()!, pair[1].GetString()!))]);

    protected override FuzzOutcome Decode(ReadOnlySpan<byte> input)
    {
        if (ArchiveLogMetadataInput.Parse(input) is not { } parsed)
        {
            return FuzzOutcome.None;
        }

        var (reason, fields) = ArchiveLogMetadataRules.Read(parsed);
        if (!parsed.Wal)
        {
            return Check(AzureArchiveLogMetadata.ReadCheckpoint(parsed.Metadata, parsed.KeyHash), reason);
        }

        switch (AzureArchiveLogMetadata.ReadWal(parsed.Metadata, parsed.Log, parsed.KeyHash, parsed.Tag).Value)
        {
            case InvalidDataException rejection:
                return Check(rejection, reason);
            case AzureWalMetadata read:
                Require(reason is null, $"accepted metadata which the rules reject as '{reason}'.");
                var actual = new ArchiveLogMetadataFields(read.Format, read.Generation, read.Checkpoint, read.Opener, read.BaseMd5, read.KeyHash);
                Require(actual == fields, $"read {actual} where the rules give {fields}.");
                return FuzzOutcome.Accepted;
            case null:
            default:
                throw new UnreachableException();
        }
    }

    private FuzzOutcome Check(InvalidDataException? rejection, string? reason)
    {
        var actual = rejection is null ? null : DecodeRejection.ReasonOf(rejection);
        Require(actual == reason, $"read the metadata as '{actual ?? "valid"}' where the rules give '{reason ?? "valid"}'.");
        if (actual is null)
        {
            return FuzzOutcome.Accepted;
        }

        Require(Reasons.Value.Contains(actual), $"rejected the metadata as '{actual}', which no metadata rejection vector names.");
        return FuzzOutcome.Rejected;
    }
}
