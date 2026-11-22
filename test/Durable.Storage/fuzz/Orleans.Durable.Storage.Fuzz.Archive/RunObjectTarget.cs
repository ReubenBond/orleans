using System.Diagnostics;
using Orleans.Durable.Storage.Engine;
using Orleans.Durable.Storage.Engine.Archive.Lsm;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Fuzz.Archive;

/// <summary>
/// The run object, <c>CLAR</c> version 1 (archive §7), read whole: rejected with the reason <see cref="RunObjectRules"/>
/// gives, one of the shared run object rejection vectors' reasons, or accepted as the object the rules give. The partial
/// decoders run too: the trailer's and the metadata's rulings are the whole read's for the rules they check, and on an
/// accepted object every key's read, and a read of a key next to each which no row has, gives exactly that key's rows,
/// and the reads of each section's ranges one to four bits below its partition, as a push reads its children, give the
/// section's rows; a rejected object's halves, with their totals, are rejected too.
/// </summary>
public sealed class RunObjectTarget : FuzzTarget
{
    /// <summary>The reasons of <c>tests/shared-vectors/archive/run-object-rejections.json</c>.</summary>
    private static readonly Lazy<HashSet<string>> Reasons = new(static () =>
        GoldenVectors.Find().Cases("archive", "run-object-rejections.json").Select(static vector => GoldenVectors.Text(vector, "reason")).ToHashSet(StringComparer.Ordinal));

    public override string Name => "archive-run-object";

    public override int MaxLength => 4096;

    public override IEnumerable<byte[]> Seeds(GoldenVectors vectors)
    {
        ArgumentNullException.ThrowIfNull(vectors);
        return new[] { "run-objects.json", "run-object-rejections.json" }
            .SelectMany(file => vectors.Cases("archive", file))
            .Select(static vector => GoldenVectors.Hex(vector));
    }

    /// <summary>
    /// Returns whether <paramref name="value"/> is the object <paramref name="expected"/> gives, field by field: its
    /// condition and metadata offset, and each section's entry, blocks, Bloom filter, and rows.
    /// </summary>
    internal static bool Matches(RunObject value, RunRuleObject expected)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(expected);
        var conditionMatches = value.Trailer.Condition switch
        {
            RunObjectCondition.Drain drain => expected.Condition.Inputs is null
                && drain.Ledger.Value == expected.Condition.Ledger
                && (ulong)drain.From.Value == expected.Condition.From,
            RunObjectCondition.Compaction compaction => expected.Condition.Inputs is { } inputs
                && compaction.Inputs.Select(static input => (input.Object.Value, (ulong)input.Offset)).SequenceEqual(inputs),
            null => throw new UnreachableException(),
        };

        return conditionMatches
            && (ulong)value.Trailer.MetadataOffset == expected.MetadataOffset
            && value.Sections.Length == expected.Sections.Count
            && value.Sections.Zip(expected.Sections).All(static pair => Matches(pair.First, pair.Second));
    }

    protected override FuzzOutcome Decode(ReadOnlySpan<byte> input)
    {
        var reading = RunObjectRules.Read(input);
        using var content = ArcBuffer.Wrap(input.ToArray());
        var bound = MeasureAllocations(input.Length);
        var decoding = RunObjectFormat.Decode(content);
        bound.Check("RunObjectFormat.Decode");
        var reason = decoding.Value is InvalidDataException rejection ? DecodeRejection.ReasonOf(rejection) : null;
        CheckPartialDecoders(content, reason);
        if (reason is not null)
        {
            Require(reason == reading.Reason, $"rejected the bytes as '{reason}' where the format's rules give '{reading.Reason ?? "valid"}': {((InvalidDataException)decoding.Value!).Message}");
            Require(Reasons.Value.Contains(reason), $"rejected the bytes as '{reason}', which no run object rejection vector names.");
            return FuzzOutcome.Rejected;
        }

        using var value = (RunObject)decoding.Value!;
        var expected = reading.Object;
        Require(expected is not null, $"accepted the bytes, which the format's rules reject as '{reading.Reason}'.");
        Require(Matches(value, expected), "decoded the bytes otherwise than the format's rules give them.");
        CheckKeyReads(content, value);
        CheckRangeReads(content, value);
        return FuzzOutcome.Accepted;
    }

    protected override byte[]? Seal(ReadOnlySpan<byte> input) => RunObjectRules.Seal(input);

    private static bool Matches(RunSection section, RunRuleSection expected)
    {
        var (entry, index, filter) = section.Metadata;
        var rows = section.Rows.Rows;
        if ((ulong)entry.Offset != expected.Entry.Offset
            || (ulong)entry.Length != expected.Entry.Length
            || entry.Partition.Depth != expected.Entry.Depth
            || !entry.Partition.Prefix.SequenceEqual(expected.Entry.Prefix)
            || Tag(entry.MinTag) != expected.Entry.MinTag
            || Tag(entry.MaxTag) != expected.Entry.MaxTag
            || entry.RowCount != expected.Entry.RowCount
            || (ulong)entry.IndexOffset != expected.Entry.IndexOffset
            || (ulong)entry.FilterOffset != expected.Entry.FilterOffset
            || (uint)filter.HashCount != expected.HashCount
            || !filter.Bits.SequenceEqual(expected.FilterBits)
            || index.Blocks.Length != expected.Blocks.Count
            || !index.Blocks.Zip(expected.Blocks).All(static pair => (ulong)pair.First.Length == pair.Second.Length
                && pair.First.Continues == pair.Second.Continues
                && pair.First.FirstKey.Span.SequenceEqual(pair.Second.FirstKey))
            || rows.Length != expected.Rows.Count)
        {
            return false;
        }

        for (var row = 0; row < rows.Length; row++)
        {
            var (tag, actual) = rows[row];
            var wanted = expected.Rows[row];
            if (!actual.Key.Span.SequenceEqual(wanted.Key)
                || Tag(tag) != wanted.Tag
                || (byte)actual.Kind != wanted.Kind
                || !actual.Body.ToArray().AsSpan().SequenceEqual(wanted.Payload))
            {
                return false;
            }
        }

        return true;
    }

    private static RuleTag Tag(Orleans.Durable.Storage.Engine.Ledgers.Tag tag) => new(tag.Ledger.Value, (ulong)tag.Slot.Value, (uint)tag.Index);

    /// <summary>
    /// Runs the trailer's and the metadata's decoders, as a reader of the object's tail does, and requires each to rule as
    /// the whole read does for the rules it checks: a rejection is the whole read's, and an acceptance is followed by the
    /// whole read's acceptance or a rejection by a later rule.
    /// </summary>
    private void CheckPartialDecoders(ArcBuffer content, string? reason)
    {
        var bytes = content.AsReadOnlySequence();
        var trailerDecoding = RunObjectFormat.DecodeTrailerAt(bytes, 0);
        if (trailerDecoding.Value is InvalidDataException trailerRejection)
        {
            Require(DecodeRejection.ReasonOf(trailerRejection) == reason, $"the trailer's decoder rejected the bytes as '{DecodeRejection.ReasonOf(trailerRejection)}' where the whole read gives '{reason ?? "valid"}'.");
            return;
        }

        var trailer = (RunTrailer)trailerDecoding.Value;

        // The tail alone, as a reader holding a section's reference reads it, decodes the same trailer.
        var tail = bytes.Slice(trailer.MetadataOffset);
        Require(RunObjectFormat.DecodeTrailerAt(tail, trailer.MetadataOffset).Value is RunTrailer fromTail && fromTail.Equals(trailer), "the tail alone decodes another trailer.");
        var tailDecoding = RunObjectFormat.DecodeMetadata(tail.Slice(0, trailer.TrailerOffset - trailer.MetadataOffset), trailer);
        if (tailDecoding.Value is InvalidDataException metadataRejection)
        {
            Require(DecodeRejection.ReasonOf(metadataRejection) == reason, $"the metadata's decoder rejected the bytes as '{DecodeRejection.ReasonOf(metadataRejection)}' where the whole read gives '{reason ?? "valid"}'.");
            return;
        }

        // A key's read checks only the rows it reads, so on a rejected object it may accept; it never throws, and a
        // rejection names a rule of the format. A push's reads of a section's halves, with the totals over them, read
        // every block and pair of adjacent blocks, so they reject every section a whole read does.
        if (reason is not null)
        {
            var rejected = false;
            foreach (var section in ((RunTail)tailDecoding.Value).Sections)
            {
                foreach (var block in section.Index.Blocks)
                {
                    using var rows = ReadKey(content, section, block.FirstKey);
                }

                rejected |= RangeReadsReject(content, section);
            }

            Require(rejected, $"the reads of each section's halves and their totals accept bytes the whole read rejects as '{reason}'.");
        }
    }

    /// <summary>
    /// Returns the ranges <paramref name="levels"/> bits below <paramref name="partition"/>, in key order, as a push's
    /// children are.
    /// </summary>
    private static List<KeyRange> Descendants(KeyRange partition, int levels)
    {
        List<KeyRange> ranges = [partition];
        for (var level = 0; level < levels; level++)
        {
            ranges = [.. ranges.SelectMany(static range => (KeyRange[])[range.Children.Lower, range.Children.Upper])];
        }

        return ranges;
    }

    /// <summary>
    /// Returns whether reading <paramref name="section"/> by its halves, or whole at the deepest depth, rejects a block,
    /// or gives rows whose totals are not the section's entry's.
    /// </summary>
    private bool RangeReadsReject(ArcBuffer content, RunSectionMetadata section)
    {
        var partition = section.Entry.Partition;
        var totals = default(RunObjectFormat.SectionTotals);
        foreach (var range in Descendants(partition, partition.Depth < KeyRange.MaxDepth ? 1 : 0))
        {
            using var rows = ReadRange(content, section, range);
            if (rows is null)
            {
                return true;
            }

            totals.Add(rows.Rows);
        }

        return totals.Check(section.Entry) is not null;
    }

    /// <summary>
    /// Reads every section of an accepted object by the ranges one to four bits below its partition, as a push reads its
    /// children, and requires each range's read to give only rows of its range and the reads of each depth together to
    /// give the whole read's rows, in order.
    /// </summary>
    private void CheckRangeReads(ArcBuffer content, RunObject value)
    {
        foreach (var section in value.Sections)
        {
            var whole = section.Rows.Rows;
            var partition = section.Metadata.Entry.Partition;
            for (var levels = 1; levels <= 4 && partition.Depth + levels <= KeyRange.MaxDepth; levels++)
            {
                var read = 0;
                foreach (var range in Descendants(partition, levels))
                {
                    using var rows = ReadRange(content, section.Metadata, range);
                    Require(rows is not null, "a range's read rejected an accepted object.");
                    foreach (var (tag, row) in rows.Rows)
                    {
                        Require(range.Contains(row.Key.Span), "a range's read gives a row outside its range.");
                        Require(
                            read < whole.Length
                                && whole[read].Tag == tag
                                && whole[read].Row.Key.Span.SequenceEqual(row.Key.Span)
                                && whole[read].Row.Kind == row.Kind
                                && whole[read].Row.Body.ToArray().AsSpan().SequenceEqual(row.Body.ToArray()),
                            "a range's read gives other rows than the whole read.");
                        read++;
                    }
                }

                Require(read == whole.Length, "the reads of a section's ranges give fewer rows than the whole read.");
            }
        }
    }

    /// <summary>
    /// Reads the rows of a range as a push reads a child's, through the blocks the block index names for it; a rejection
    /// must name a rule.
    /// </summary>
    private RunRows? ReadRange(ArcBuffer content, RunSectionMetadata section, KeyRange range)
    {
        var blocks = section.Covering(range);
        if (blocks.Count == 0)
        {
            return RunRows.Empty;
        }

        var decoding = RunObjectFormat.DecodeRange(content.UnsafeSlice((int)blocks.Offset, (int)blocks.Length), section, blocks.First, blocks.Count, range);
        if (decoding.Value is InvalidDataException rejection)
        {
            var reason = DecodeRejection.ReasonOf(rejection);
            Require(reason is not null && Reasons.Value.Contains(reason), $"a range's read rejected the bytes as '{reason}', which no run object rejection vector names.");
            return null;
        }

        return (RunRows)decoding.Value;
    }
    /// <summary>
    /// Reads every key of an accepted object, and a key next to each which no row has, as a history read does, and
    /// requires exactly the key's rows.
    /// </summary>
    private void CheckKeyReads(ArcBuffer content, RunObject value)
    {
        foreach (var section in value.Sections)
        {
            var rows = section.Rows.Rows;
            for (var index = 0; index < rows.Length; index++)
            {
                var key = rows[index].Row.Key;
                if (index > 0 && key.Span.SequenceEqual(rows[index - 1].Row.Key.Span))
                {
                    continue;
                }

                using (var read = ReadKey(content, section.Metadata, key))
                {
                    var expected = 0;
                    for (var other = index; other < rows.Length && rows[other].Row.Key.Span.SequenceEqual(key.Span); other++)
                    {
                        Require(read is not null && expected < read.Rows.Length && read.Rows[expected].Tag == rows[other].Tag, "a key's read gives other rows than the whole read.");
                        expected++;
                    }

                    Require(read is not null && read.Rows.Length == expected, "a key's read gives more rows than the whole read.");
                }

                if (key.Length < Orleans.Durable.Storage.Engine.Ledgers.KeyRange.MaxKeyLength)
                {
                    using var absent = ReadKey(content, section.Metadata, (byte[])[.. key.Span, 0]);
                    Require(absent is not null && absent.Rows.Length == 0, "a read of a key no row has gives rows.");
                }
            }
        }
    }

    /// <summary>
    /// Reads one key's rows as a history read does, through the section's candidates; a rejection must name a rule.
    /// </summary>
    private RunRows? ReadKey(ArcBuffer content, RunSectionMetadata section, ReadOnlyMemory<byte> key)
    {
        var blocks = section.Candidates(key.Span);
        if (blocks.Count == 0)
        {
            return RunRows.Empty;
        }

        var decoding = RunObjectFormat.DecodeKey(content.UnsafeSlice((int)blocks.Offset, (int)blocks.Length), section, blocks.First, blocks.Count, key);
        if (decoding.Value is InvalidDataException rejection)
        {
            var reason = DecodeRejection.ReasonOf(rejection);
            Require(reason is not null && Reasons.Value.Contains(reason), $"a key's read rejected the bytes as '{reason}', which no run object rejection vector names.");
            return null;
        }

        return (RunRows)decoding.Value;
    }
}
