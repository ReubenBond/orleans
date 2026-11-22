using System.Buffers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Fuzz.Journals;
using Orleans.Durable.Storage.Testing;
using static Orleans.Durable.Storage.Testing.SharedVectors;

namespace Orleans.Durable.Storage.Engine.Journals.Tests;

/// <summary>
/// Checks the shared Journals vectors under <c>tests/shared-vectors/journals</c> (journals.md §4.1, §4.2, §7): each entry
/// record's fields encode, for its row key, to its bytes, and its bytes decode under that key to its fields, contiguous
/// and one byte per segment, each written as its row: its row kind, its version before, and its native body; each rejection is rejected for its reason, the cases which break two rules for the earlier,
/// and together they cover journals.md §7's reasons in order; each version's text form in both directions, and each
/// non-canonical text rejected; and each journal key's row key.
/// </summary>
public sealed partial class JournalVectorTests : IDisposable
{
    private const string Area = "journals";

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    [Fact]
    public void EntryVectorsMatchTheCodecInBothDirections()
    {
        foreach (var vector in Cases(Area, "entries.json"))
        {
            var bytes = Hex(vector);
            var key = RowKey(vector);
            using (var record = Encode(key, vector))
            {
                using var encoded = record.ToArcBuffer();
                AssertBytes(vector, encoded.ToArray());
                Assert.Equal(bytes.Length, record.Length);
                AssertRow(vector, record);
            }

            foreach (var sequence in (ReadOnlySequence<byte>[])[new(bytes), Bytewise(bytes)])
            {
                Assert.Null(JournalEntry.Decode(key.Span, sequence, out var entry));
                AssertFields(vector, entry);
            }

            using var buffer = ArcBuffer.Wrap(bytes);
            Assert.Null(JournalEntryRecord.Decode(key, buffer, out var forwarded));
            using (forwarded)
            {
                AssertFields(vector, forwarded.Entry);
                AssertRow(vector, forwarded);
                using var again = forwarded.ToArcBuffer();
                AssertBytes(vector, again.ToArray());
            }
        }
    }

    [Fact]
    public void EntryRejectionVectorsAreRejectedForTheirReason()
    {
        foreach (var vector in Cases(Area, "entry-rejections.json"))
        {
            var bytes = Hex(vector);
            var key = RowKey(vector);
            foreach (var sequence in (ReadOnlySequence<byte>[])[new(bytes), Bytewise(bytes)])
            {
                var rejection = JournalEntry.Decode(key.Span, sequence, out var entry);
                AssertRejected(vector, rejection);
                Assert.Equal(default(JournalEntry).Kind, entry.Kind);
                Assert.Equal(JournalVersion.Origin, entry.ExpectedVersion);
            }

            using var buffer = ArcBuffer.Wrap(bytes);
            AssertRejected(vector, JournalEntryRecord.Decode(key, buffer, out var forwarded));
            Assert.Equal(0, forwarded.Length);
            Assert.Equal(Text(vector, "reason"), EntryRules.Read(key.Span, bytes).Reason);
        }
    }

    /// <summary>The vectors name journals.md §7's reasons, in its order.</summary>
    [Fact]
    public void TheVectorsNameJournalsSevensReasonsInItsOrder()
    {
        string[] reasons = [.. Cases(Area, "entry-rejections.json").Select(static vector => Text(vector, "reason")).Distinct(StringComparer.Ordinal)];
        Assert.Equal(SpecReasons(), reasons);
    }

    [Fact]
    public void EntryVectorsAgreeWithTheIndependentReadingOfTheRules()
    {
        foreach (var vector in Cases(Area, "entries.json"))
        {
            var reading = EntryRules.Read(RowKey(vector).Span, Hex(vector));
            Assert.Null(reading.Reason);
            var entry = reading.Entry!;
            var kind = Enum.Parse<JournalEntryKind>(Text(vector, "kind"));
            var version = ExpectedVersion(vector);
            Assert.Equal((byte)kind, entry.Kind);
            Assert.Equal(version.Base.ToArray(), entry.Base);
            Assert.Equal(version.Count, entry.Count);
            Assert.Equal(kind == JournalEntryKind.Delete ? Int64(vector, "deletedAt") : 0, entry.Ticks);
            Assert.Equal(vector.TryGetProperty("payload", out _) ? Hex(vector, "payload") : [], entry.Payload);
        }
    }

    /// <summary>Each version formats to its text and its text parses to it; each other text is no version's.</summary>
    [Fact]
    public void VersionTextVectorsMatchInBothDirections()
    {
        foreach (var vector in Cases(Area, "versions.json"))
        {
            var version = new JournalVersion(new RowToken(Hex(vector, "base")), UInt64(vector, "count"));
            var text = Text(vector, "text");

            Assert.Equal(text, version.ToString());
            Assert.True(JournalVersion.TryParse(text, out var parsed));
            Assert.Equal(version, parsed);
            Assert.Equal(version, JournalVersion.Parse(text));
        }

        foreach (var vector in Cases(Area, "version-rejections.json"))
        {
            var text = Text(vector, "text");
            Assert.False(JournalVersion.TryParse(text, out var parsed), Text(vector, "name"));
            Assert.Equal(JournalVersion.Origin, parsed);
            _ = Assert.Throws<FormatException>(() => JournalVersion.Parse(text));
        }
    }

    [Fact]
    public void RowKeyVectorsMatchTheEncoding()
    {
        foreach (var vector in Cases(Area, "row-keys.json"))
        {
            AssertBytes(vector, JournalRowKey.Encode(Text(vector, "key")).Span);
        }
    }

    /// <summary>Returns the vector's row key, which is a journal's: the hash prefix of the UTF-8 after it.</summary>
    private static JournalRowKey RowKey(JsonElement vector)
    {
        var bytes = Hex(vector, "rowKey");
        var key = JournalRowKey.Encode(System.Text.Encoding.UTF8.GetString(bytes.AsSpan(JournalRowKey.PrefixLength)));
        Assert.Equal(bytes, key.Span.ToArray());
        return key;
    }

    private static JournalVersion ExpectedVersion(JsonElement vector)
    {
        var version = vector.GetProperty("expectedVersion");
        return new JournalVersion(new RowToken(Hex(version, "base")), UInt64(version, "count"));
    }

    private static JournalEntryRecord Encode(JournalRowKey key, JsonElement vector)
    {
        var kind = Enum.Parse<JournalEntryKind>(Text(vector, "kind"));
        var expected = ExpectedVersion(vector);
        switch (kind)
        {
            case JournalEntryKind.Delete:
                return JournalEntryRecord.Delete(key, expected, new DateTimeOffset(Int64(vector, "deletedAt"), TimeSpan.Zero));
            case JournalEntryKind.Expired:
                return JournalEntryRecord.Expired(key, expected);

            case JournalEntryKind.Append:
                using (var payload = ArcBuffer.Wrap(Hex(vector, "payload")))
                {
                    return JournalEntryRecord.Append(key, expected, payload);
                }

            case JournalEntryKind.Replace:
                var bytes = Hex(vector, "payload");
                using (var snapshot = bytes.Length == 0 ? default : ArcBuffer.Wrap(bytes))
                {
                    return JournalEntryRecord.Replace(key, expected, snapshot);
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(vector), kind, "Undefined kind.");
        }
    }

    private static void AssertFields(JsonElement vector, JournalEntry entry)
    {
        var kind = Enum.Parse<JournalEntryKind>(Text(vector, "kind"));
        Assert.Equal(kind, entry.Kind);
        Assert.Equal(ExpectedVersion(vector), entry.ExpectedVersion);
        Assert.Equal(kind == JournalEntryKind.Delete ? Int64(vector, "deletedAt") : 0, entry.DeletionTicks);
        Assert.Equal(vector.TryGetProperty("payload", out _) ? Hex(vector, "payload").Length : 0, entry.PayloadLength);
    }

    /// <summary>
    /// Asserts the row the record is written as (journals.md §4.2): its row kind, its version before, which is the expected
    /// version, and its native body, the bytes the record's head holds before the payload and then the payload.
    /// </summary>
    private static void AssertRow(JsonElement vector, JournalEntryRecord record)
    {
        Assert.Equal(Enum.Parse<RowKind>(Text(vector, "rowKind")), record.Entry.RowKind);
        var before = vector.GetProperty("versionBefore");
        Assert.Equal(new RowVersion(new RowToken(Hex(before, "base")), UInt64(before, "count")), record.Entry.ExpectedVersion.ToRowVersion());
        var body = Hex(vector, "rowBody");
        Assert.Equal(body.Length, record.Entry.RowBodyLength);
        Span<byte> rowBodyHead = stackalloc byte[JournalEntry.TombstoneLength];
        Assert.Equal(body, (byte[])[.. record.WriteRowBodyHead(rowBodyHead), .. record.Payload.ToArray()]);
    }

    /// <summary>Returns <paramref name="bytes"/> as a sequence of one-byte segments, so every field spans segments.</summary>
    private static ReadOnlySequence<byte> Bytewise(byte[] bytes) => Segments.Of(bytes, 1);

    /// <summary>A reason code in backticks: lowercase words joined by hyphens.</summary>
    [GeneratedRegex("`([a-z][a-z0-9-]*)`")]
    private static partial Regex ReasonCode();

    /// <summary>The reasons journals.md §7's decoding rules name for the entry record, distinct, in their order.</summary>
    private static string[] SpecReasons()
    {
        var lines = File.ReadAllLines(Path.Combine(RepositoryRoot(), "docs", "spec", "components", "journals.md"));
        var start = Array.FindIndex(lines, static line => line.StartsWith("Decoding takes the row key and the record", StringComparison.Ordinal));
        Assert.True(start >= 0, "journals.md §7 has no decoding rules.");
        var rules = lines.Skip(start + 1).SkipWhile(static line => !line.StartsWith("1. ", StringComparison.Ordinal)).TakeWhile(static line => line.Length > 0);
        return
        [
            .. rules
                .SelectMany(static line => ReasonCode().Matches(line).Select(static match => match.Groups[1].Value))
                .Distinct(StringComparer.Ordinal),
        ];
    }
}
