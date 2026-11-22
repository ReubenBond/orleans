using System.Text;
using Orleans.Durable.Storage.Engine.Journals;

namespace Orleans.Durable.Storage.Fuzz.Journals;

/// <summary>
/// A journal version's text form (journals.md §7), which arrives from Orleans as a grain's ETag: a canonical format which
/// <see cref="JournalVersion.TryParse"/> accepts exactly when <see cref="VersionRules"/> gives a version, as that version,
/// which formats back to the text, and otherwise rejects, as <see cref="JournalVersion.Parse"/> does with a
/// <see cref="FormatException"/>. An input is the text's UTF-8; the parser allocates nothing.
/// </summary>
public sealed class VersionTarget : FuzzTarget
{
    public override string Name => "journals-version";

    public override int MaxLength => 128;

    public override IEnumerable<byte[]> Seeds(GoldenVectors vectors)
    {
        ArgumentNullException.ThrowIfNull(vectors);
        return new[] { "versions.json", "version-rejections.json" }
            .SelectMany(file => vectors.Cases("journals", file))
            .Select(static vector => Encoding.UTF8.GetBytes(vector.GetProperty("text").GetString()!));
    }

    protected override FuzzOutcome Decode(ReadOnlySpan<byte> input)
    {
        var text = Encoding.UTF8.GetString(input);
        var reading = VersionRules.Read(text);
        var bound = MeasureAllocations(text.Length);
        var parsed = JournalVersion.TryParse(text, out var version);
        bound.Check("JournalVersion.TryParse");
        if (!parsed)
        {
            Require(reading is null, "rejected a text the format's rules accept.");
            Require(version == JournalVersion.Origin, "rejected a text and gave a version other than Origin.");
            var refused = false;
            try
            {
                _ = JournalVersion.Parse(text);
            }
            catch (FormatException)
            {
                refused = true;
            }

            Require(refused, "parsed a text TryParse rejects.");

            return FuzzOutcome.Rejected;
        }

        Require(reading is not null, "accepted a text the format's rules reject.");
        Require(version.Base.AsSpan().SequenceEqual(reading.Base) && version.Count == reading.Count, "parsed a text otherwise than the format's rules give it.");
        Require(version.ToString() == text, "accepted a text which is not its version's canonical text.");
        return FuzzOutcome.Accepted;
    }
}
