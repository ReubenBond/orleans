using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Unicode;
using Orleans.Durable.Storage.Engine;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Journals;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Fuzz.Journals;

/// <summary>
/// The entry record (journals.md §7): a canonical format, rejected with the reason <see cref="EntryRules"/>
/// gives, one of the shared entry rejection vectors' reasons, or accepted as the fields the rules give, which encode to
/// exactly its bytes. An input frames a row key and a record (<see cref="EntryRules.Frame"/>), since the record's CRC-32C
/// covers its row key. The target decodes the record from contiguous bytes and from segments of a few bytes each, as a
/// record read across buffer pages arrives, and, when the key is a journal's row key, through
/// <see cref="JournalEntryRecord.Decode"/>, as a forwarded entry arrives.
/// </summary>
public sealed class EntryTarget : FuzzTarget
{
    /// <summary>The reasons of <c>tests/shared-vectors/journals/entry-rejections.json</c>.</summary>
    private static readonly Lazy<HashSet<string>> Reasons = new(static () => GoldenVectors.Find().Reasons("journals", "entry-rejections.json"));

    public override string Name => "journals-entry";

    public override int MaxLength => 2048;

    public override IEnumerable<byte[]> Seeds(GoldenVectors vectors)
    {
        ArgumentNullException.ThrowIfNull(vectors);
        return new[] { "entries.json", "entry-rejections.json" }
            .SelectMany(file => vectors.Cases("journals", file))
            .Select(static vector => EntryRules.Frame(GoldenVectors.Hex(vector, "rowKey"), GoldenVectors.Hex(vector)));
    }

    protected override FuzzOutcome Decode(ReadOnlySpan<byte> input)
    {
        if (!EntryRules.TrySplit(input, out var key, out var record))
        {
            return FuzzOutcome.None;
        }

        var reading = EntryRules.Read(key, record);
        var rowKey = RowKeyOf(key);
        var contiguous = new ReadOnlySequence<byte>(record.ToArray());
        var bound = MeasureAllocations(record.Length);
        var rejection = JournalEntry.Decode(key, contiguous, out var entry);
        bound.Check("JournalEntry.Decode");
        var outcome = Check(rowKey, record, rejection, entry, reading, "contiguous bytes");

        var segmentLength = 1 + record.Length % 29;
        rejection = JournalEntry.Decode(key, Segments(record, segmentLength), out entry);
        _ = Check(rowKey, record, rejection, entry, reading, string.Create(CultureInfo.InvariantCulture, $"segments of {segmentLength} bytes"));

        if (rowKey is null)
        {
            return outcome;
        }

        using var bytes = ArcBuffer.Wrap(record.ToArray());
        rejection = JournalEntryRecord.Decode(rowKey, bytes, out var forwarded);
        using (forwarded)
        {
            _ = Check(rowKey, record, rejection, forwarded.Entry, reading, "a forwarded record");
            if (rejection is null)
            {
                using var copy = forwarded.ToArcBuffer();
                Require(record.SequenceEqual(copy.ToArray()), "decoded a forwarded record whose bytes differ from the input.");
            }
        }

        return outcome;
    }

    protected override byte[]? Seal(ReadOnlySpan<byte> input) => EntryRules.Seal(input);

    /// <summary>Encodes the fields the rules give with the codec's factories, for the row key <paramref name="key"/>.</summary>
    private static JournalEntryRecord Encode(JournalRowKey key, RuleEntry expected)
    {
        var version = new JournalVersion(new RowToken(expected.Base), expected.Count);
        switch ((JournalEntryKind)expected.Kind)
        {
            case JournalEntryKind.Expired:
                return JournalEntryRecord.Expired(key, version);
            case JournalEntryKind.Delete:
                return JournalEntryRecord.Delete(key, version, new DateTimeOffset(expected.Ticks, TimeSpan.Zero));
            case JournalEntryKind.Append:
                using (var payload = ArcBuffer.Wrap(expected.Payload))
                {
                    return JournalEntryRecord.Append(key, version, payload);
                }

            case JournalEntryKind.Replace:
                using (var snapshot = ArcBuffer.Wrap(expected.Payload))
                {
                    return JournalEntryRecord.Replace(key, version, snapshot);
                }

            default:
                throw new InvalidOperationException("The rules give only the four kinds.");
        }
    }

    /// <summary>
    /// Returns the journal row key whose bytes <paramref name="key"/> are, or <see langword="null"/> when they are none: a
    /// hash prefix of the strict UTF-8 after it.
    /// </summary>
    private static JournalRowKey? RowKeyOf(ReadOnlySpan<byte> key)
    {
        if (key.Length <= JournalRowKey.PrefixLength || !Utf8.IsValid(key[JournalRowKey.PrefixLength..]))
        {
            return null;
        }

        var rowKey = JournalRowKey.Encode(Encoding.UTF8.GetString(key[JournalRowKey.PrefixLength..]));
        return rowKey.Span.SequenceEqual(key) ? rowKey : null;
    }

    private static bool Matches(JournalEntry entry, RuleEntry expected) =>
        (byte)entry.Kind == expected.Kind
        && entry.ExpectedVersion.Base.AsSpan().SequenceEqual(expected.Base)
        && entry.ExpectedVersion.Count == expected.Count
        && entry.DeletionTicks == expected.Ticks
        && entry.PayloadLength == expected.Payload.Length;

    /// <summary>
    /// Returns <paramref name="input"/> as a sequence of segments of <paramref name="length"/> bytes each, the last
    /// holding what is left.
    /// </summary>
    private static ReadOnlySequence<byte> Segments(ReadOnlySpan<byte> input, int length)
    {
        if (input.Length <= length)
        {
            return new ReadOnlySequence<byte>(input.ToArray());
        }

        var first = new Segment(input[..length].ToArray(), 0);
        var last = first;
        for (var offset = length; offset < input.Length; offset += length)
        {
            last = last.Append(input[offset..Math.Min(offset + length, input.Length)].ToArray());
        }

        return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
    }

    private FuzzOutcome Check(JournalRowKey? key, ReadOnlySpan<byte> record, InvalidDataException? rejection, JournalEntry entry, EntryReading reading, string layout)
    {
        if (rejection is not null)
        {
            var reason = DecodeRejection.ReasonOf(rejection);
            Require(reason == reading.Reason, $"rejected {layout} as '{reason}' where the format's rules give '{reading.Reason ?? "valid"}': {rejection.Message}");
            Require(reason is not null && Reasons.Value.Contains(reason), $"rejected {layout} as '{reason}', which no entry rejection vector names.");
            return FuzzOutcome.Rejected;
        }

        var expected = reading.Entry;
        Require(expected is not null, $"accepted {layout}, which the format's rules reject as '{reading.Reason}'.");
        Require(Matches(entry, expected), $"decoded {layout} otherwise than the format's rules give it.");
        if (key is not null)
        {
            using var encodedRecord = Encode(key, expected);
            using var encoded = encodedRecord.ToArcBuffer();
            Require(record.SequenceEqual(encoded.ToArray()), $"accepted {layout}, whose fields do not encode back to its bytes.");
        }

        return FuzzOutcome.Accepted;
    }

    /// <summary>One segment of a sequence <see cref="Segments"/> builds.</summary>
    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(byte[] bytes, long runningIndex)
        {
            Memory = bytes;
            RunningIndex = runningIndex;
        }

        public Segment Append(byte[] bytes)
        {
            var next = new Segment(bytes, RunningIndex + Memory.Length);
            Next = next;
            return next;
        }
    }
}
