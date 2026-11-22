using System.Buffers;
using Orleans.Durable.Storage.Engine;
using Orleans.Durable.Storage.Engine.MetaStore;
using Orleans.Durable.Storage.FileSystem.MetaStore;

namespace Orleans.Durable.Storage.Fuzz.MetaStore;

/// <summary>
/// The file-system MetaStore's register file, <c>CLFM</c> version 1 (metastore.md §7): rejected with the reason of the
/// first rule it breaks, shorter than its 21-byte header (<c>truncated</c>), another magic (<c>bad-magic</c>), or another
/// format version (<c>bad-version</c>), and otherwise the version and value which encode to exactly its bytes. The input
/// is the file.
/// </summary>
public sealed class RegisterFileTarget : FuzzTarget
{
    private static readonly Guid Version = new("0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0");

    /// <summary>
    /// The reasons of <c>tests/shared-vectors/metastore/register-file-rejections.json</c>.
    /// </summary>
    private static readonly Lazy<HashSet<string>> Reasons = new(static () => GoldenVectors.Find().Reasons("metastore", "register-file-rejections.json"));

    public override string Name => "metastore-register";

    public override int MaxLength => 256;

    public override IEnumerable<byte[]> Seeds(GoldenVectors vectors)
    {
        ArgumentNullException.ThrowIfNull(vectors);
        return
        [
            FileSystemConditionalRegister.Encode(Version, ReadOnlySequence<byte>.Empty),
            FileSystemConditionalRegister.Encode(Version, new ReadOnlySequence<byte>("register value"u8.ToArray())),
            .. vectors.Cases("metastore", "register-file-rejections.json").Select(static vector => GoldenVectors.Hex(vector)),
        ];
    }

    protected override FuzzOutcome Decode(ReadOnlySpan<byte> input)
    {
        var encoded = input.ToArray();
        var expected = Rule(input);
        var bound = MeasureAllocations(encoded.Length);
        var decoding = FileSystemConditionalRegister.Decode(encoded);
        bound.Check("FileSystemConditionalRegister.Decode");
        if (decoding is InvalidDataException rejection)
        {
            var reason = DecodeRejection.ReasonOf(rejection);
            Require(reason is not null && Reasons.Value.Contains(reason), $"rejected a register file for '{reason}', which the register file vectors do not name.");
            Require(reason == expected, $"rejected a register file for '{reason}' where the format's rules give '{expected ?? "valid"}'.");
            return FuzzOutcome.Rejected;
        }

        Require(expected is null, $"accepted a register file which breaks '{expected}'.");
        Require(decoding.Value is MetaStoreEntry, "decoded a register file as no entry.");
        using var entry = (MetaStoreEntry)decoding.Value;
        Require(Guid.TryParseExact(entry.Version.Value, "N", out var version), "decoded a version which is not a canonical Guid.");
        Require(
            input.SequenceEqual(FileSystemConditionalRegister.Encode(version, entry.Value.AsReadOnlySequence())),
            "accepted a register file which its version and value do not encode to.");
        return FuzzOutcome.Accepted;
    }

    /// <summary>
    /// Returns the first rule of metastore.md §7 a register file breaks, by its reason code, or <see langword="null"/>.
    /// </summary>
    private static string? Rule(ReadOnlySpan<byte> file)
    {
        if (file.Length < 21)
        {
            return "truncated";
        }

        if (!file[..4].SequenceEqual("CLFM"u8))
        {
            return "bad-magic";
        }

        return file[4] == 1 ? null : "bad-version";
    }
}
