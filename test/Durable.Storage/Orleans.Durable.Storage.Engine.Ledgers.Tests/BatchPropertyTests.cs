using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using CsCheck;
using Orleans.Durable.Storage.Fuzz.Ledgers;
using Orleans.Durable.Storage.Testing;
using static Orleans.Durable.Storage.Engine.Ledgers.Tests.BatchSamples;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests;

/// <summary>
/// CsCheck properties of the batch codec (ledgers.md §7, formats.md §7): it decodes the rows it encodes, re-encodes
/// every batch it accepts to the same bytes, decodes the same rows from any division of its buffer into pages, lends
/// rows equal to their owned copies, whose bodies outlive the batch's buffer, and borrows the rows it encodes; and
/// <see cref="LedgerSegmentRules"/>, one independent reading of the rules, judges every mutated encoding. Each case runs
/// in its own page leak scope (verification.md §11.1).
/// </summary>
public sealed class BatchPropertyTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    private enum MutationKind
    {
        /// <summary>A bit flipped anywhere.</summary>
        Flip,

        /// <summary>A bit flipped in the fixed header, whose fields a flip anywhere seldom reaches.</summary>
        FlipHeader,

        /// <summary>The batch cut short.</summary>
        Truncate,

        /// <summary>Bytes appended.</summary>
        Extend,

        /// <summary>A flags, count, ordinal, or length field set to a bound.</summary>
        SetField,

        /// <summary>The batch cut just after a field, which is set to a bound.</summary>
        TruncateAfterField,
    }

    public void Dispose() => _leaks.Dispose();

    /// <summary>
    /// ledgers.md §2.5, §7: <see cref="BatchView.FindDefining"/> finds the batch's defining row at the ordinal's distance
    /// from the base, for an ordinal from the base to the base plus the defining rows, and answers <c>unknown-ordinal</c>
    /// for 0, an ordinal below the base, one past the defining rows, and the <c>u64</c> maximum, also for a base so near
    /// the maximum that implied ordinals wrap.
    /// </summary>
    [Fact]
    public void FindDefiningFindsExactlyTheDefinedOrdinals() =>
        Gen.Select(Gen.OneOf(Gen.ULong[0, 1_000], Gen.ULong[ulong.MaxValue - 4, ulong.MaxValue]), Gen.Int[1, 6]).Sample((ordinalBase, count) => _leaks.InScope(() =>
        {
            var start = ordinalBase == 0;
            var dictionary = new LedgerSegmentDictionary();
            using var encoder = new LedgerBatch(KeyRange.Root, int.MaxValue, count, new LedgerBatchSegment(start, start ? 0UL : 1UL, ordinalBase, dictionary));
            for (var index = 0; index < count; index++)
            {
                _ = encoder.AddRow([(byte)(index + 1)], RowKind.Append, default, default, default);
            }

            using var bytes = encoder.Complete();
            Assert.Null(BatchView.Decode(bytes, out var view));
            foreach (var ordinal in unchecked((ulong[])[0, ordinalBase - 1, ordinalBase, ordinalBase + 1, ordinalBase + (ulong)count - 1, ordinalBase + (ulong)count, ulong.MaxValue]))
            {
                var distance = unchecked(ordinal - ordinalBase);
                var defined = ordinal >= ordinalBase && distance < (ulong)count;
                var rejection = view.FindDefining(ordinal, out var row);
                if (defined)
                {
                    Assert.Null(rejection);
                    Assert.Equal([(byte)(distance + 1)], row.Key.ToArray());
                }
                else
                {
                    Assert.Equal("unknown-ordinal", Reason(rejection));
                }
            }
        }));

    /// <summary>
    /// ledgers.md §7: a varint round-trips at its shortest length; every proper prefix of it is unterminated; and the same
    /// value with a zero byte appended to a longer form, or an 11th byte, is invalid.
    /// </summary>
    [Fact]
    public void AVarintRoundTripsAndOnlyItsShortestFormIsValid() =>
        Gen.OneOf(Gen.ULong, Gen.ULong[0, 1 << 14], Gen.Const(ulong.MaxValue)).Sample(value =>
        {
            Span<byte> bytes = stackalloc byte[Varint.MaxLength + 1];
            var length = Varint.Write(bytes, value);
            Assert.Equal(Varint.Length(value), length);
            Assert.Equal(Varint.Status.Valid, Varint.Read(bytes[..length], out var read, out var consumed));
            Assert.Equal((value, length), (read, consumed));
            for (var prefix = 0; prefix < length; prefix++)
            {
                Assert.Equal(Varint.Status.Unterminated, Varint.Read(bytes[..prefix], out _, out _));
            }

            if (length < Varint.MaxLength)
            {
                // The overlong form: the last byte continues, and a zero byte ends it.
                bytes[length - 1] |= 0x80;
                bytes[length] = 0;
                Assert.Equal(Varint.Status.Invalid, Varint.Read(bytes[..(length + 1)], out _, out _));
            }
            else
            {
                bytes[length - 1] |= 0x80;
                bytes[length] = 1;
                Assert.Equal(Varint.Status.Invalid, Varint.Read(bytes[..(length + 1)], out _, out _));
            }
        });

    [Fact]
    public void TheCodecDecodesTheRowsItEncodesAndReencodesThemToTheSameBytes() =>
        Gen.Select(Rows, PageLengths).Sample((rows, bodyPages) => _leaks.InScope(() =>
        {
            var bytes = Encode(rows, bodyPages);
            using var buffer = PagedBuffers.Create(bytes, []);
            var decoded = Decode(buffer);
            AssertRows(rows, decoded);
            Assert.Equal(bytes, Encode(decoded));
            AssertRows(rows, LedgerSegmentRules.ReadSegment([bytes], firstSlot: 0).Rows.Select(RowSample.Of));
        }));

    /// <summary>
    /// Any division of a batch's buffer into pages decodes to the same rows (verification.md §4.2); each lent row equals
    /// its owned copy; the owned copies outlive the buffer; and encoding borrows them, so they encode twice to the same
    /// bytes with their bodies intact.
    /// </summary>
    [Fact]
    public void AnyDivisionIntoPagesDecodesTheSameRowsWhichOwnedCopiesOutlive() =>
        Gen.Select(Rows, PageLengths, PageLengths).Sample((rows, pageLengths, bodyPages) => _leaks.InScope(() =>
        {
            var owned = new List<Row>();
            var bytes = Encode(rows, bodyPages);
            var buffer = PagedBuffers.Create(bytes, pageLengths);
            try
            {
                Assert.Null(BatchView.Decode(buffer, out var batch));
                Assert.Equal(rows.Length, batch.Count);
                using (var segment = new LedgerSegmentView(new LedgerId(1)))
                {
                    Assert.Null(segment.Advance(Slot.Zero, batch));
                    foreach (var row in segment.Rows)
                    {
                        var copy = row.ToRow();
                        owned.Add(copy);
                        Assert.Equal(RowSample.Of(row).Describe(), RowSample.Of(copy).Describe());
                    }
                }

                buffer.Dispose();
                AssertRows(rows, owned.Select(RowSample.Of));
                for (var round = 0; round < 2; round++)
                {
                    using var encoded = BatchView.Encode(CollectionsMarshal.AsSpan(owned));
                    Assert.Equal(bytes, encoded.ToArray());
                }

                AssertRows(rows, owned.Select(RowSample.Of));
            }
            finally
            {
                buffer.Dispose();
                foreach (var row in owned)
                {
                    row.Dispose();
                }
            }
        }));

    /// <summary>
    /// Every mutated encoding of a batch of a generated segment (flipped bits, truncations, extensions, and flags, counts,
    /// kinds, varints, ordinals, and lengths set to their bounds, alone or as the last field before the CRC-32C), with its
    /// CRC-32C as mutated or recomputed, is rejected for the reason <see cref="LedgerSegmentRules"/> names or accepted as
    /// the rows it reads, which re-encode to the same bytes; <c>classify</c> reports each outcome, and acceptance and every
    /// reason of the rejection vectors arise.
    /// </summary>
    [Fact]
    public void EveryMutatedEncodingIsJudgedAsTheRulesJudgeIt()
    {
        var outcomes = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        Gen.Select(Segments.Where(Writable), Gen.Int.NonNegative, Mutations, PageLengths, Gen.Bool).Sample(
            sample =>
            {
                var (segment, pick, mutation, pageLengths, seal) = sample;
                var outcome = "accepted";
                _leaks.InScope(() =>
                {
                    var batches = EncodeSegment(segment);
                    var bytes = Mutate(batches[pick % batches.Count], mutation);
                    bytes = seal ? LedgerSegmentRules.Seal(bytes) ?? bytes : bytes;
                    var reading = LedgerSegmentRules.ReadBatch(bytes);
                    using var buffer = PagedBuffers.Create(bytes, pageLengths);
                    var rejection = BatchView.Decode(buffer, out var batch);
                    Assert.Equal(reading.Reason, Reason(rejection));
                    if (rejection is not null)
                    {
                        outcome = reading.Reason ?? "rejected";
                        Assert.Equal(0, batch.Count);
                        return;
                    }

                    Assert.Equal(reading.Rows.Count, batch.Count);
                    foreach (var row in batch)
                    {
                        Assert.True(BatchTarget.Matches(row, reading.Rows[row.Index]));
                    }

                    if (LedgerBatches.Encode(reading.Batch!) is { } encoded)
                    {
                        Assert.Equal(bytes, encoded);
                    }
                });
                _ = outcomes.AddOrUpdate(outcome, 1, static (_, count) => count + 1);
                return outcome;
            },
            TestDiagnostics.WriteLine,
            iter: 6_000);

        Assert.Equal(VectorReasons().Append("accepted").Order(StringComparer.Ordinal), outcomes.Keys.Order(StringComparer.Ordinal));
    }

    /// <summary>Gets mutations: each one's kind, and choices of position, bit, field, and value.</summary>
    private static Gen<(MutationKind Kind, int Position, int Bit, int Field, int Value)> Mutations { get; } =
        Gen.Select(
            Gen.Frequency(
                (3, Gen.Const(MutationKind.Flip)),
                (1, Gen.Const(MutationKind.FlipHeader)),
                (1, Gen.Const(MutationKind.Truncate)),
                (1, Gen.Const(MutationKind.Extend)),
                (5, Gen.Const(MutationKind.SetField)),
                (2, Gen.Const(MutationKind.TruncateAfterField))),
            Gen.Int.NonNegative,
            Gen.Int[0, 7],
            Gen.Int.NonNegative,
            Gen.Int.NonNegative);

    /// <summary>Each varint field's bounds and their neighbors, as bytes: shortest forms, overlong and overflowing ones.</summary>
    private static byte[][] VarintBounds { get; } =
    [
        .. new ulong[] { 0, 1, 2, 3, 32, 33, 127, 128, 65_535, 65_536, ulong.MaxValue }.Select(static value =>
        {
            var bytes = new List<byte>();
            LedgerSegmentRules.AddVarint(bytes, value);
            return bytes.ToArray();
        }),
        [0x80, 0x00],
        [0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x00],
        [0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0x02],
        [0x80],
    ];

    /// <summary>Each byte field's bounds and their neighbors.</summary>
    private static byte[][] ByteBounds { get; } = [[0], [1], [2], [3], [4], [5], [6], [7], [8], [32], [33], [0xff]];

    private static byte[] Mutate(byte[] bytes, (MutationKind Kind, int Position, int Bit, int Field, int Value) mutation)
    {
        switch (mutation.Kind)
        {
            case MutationKind.Flip:
                bytes[mutation.Position % bytes.Length] ^= (byte)(1 << mutation.Bit);
                return bytes;
            case MutationKind.FlipHeader:
                bytes[mutation.Position % BatchView.FixedHeaderLength] ^= (byte)(1 << mutation.Bit);
                return bytes;
            case MutationKind.Truncate:
                return bytes[..(mutation.Position % (mutation.Bit < 2 ? BatchView.FixedHeaderLength + BatchView.CrcLength + 1 : bytes.Length + 1))];
            case MutationKind.Extend:
                return [.. bytes, .. Enumerable.Repeat((byte)(mutation.Value & 0xff), 1 + mutation.Bit % 4)];
            case MutationKind.SetField:
            case MutationKind.TruncateAfterField:
                var fields = FieldsOf(bytes);
                var (offset, width, isVarint) = fields[mutation.Field % fields.Count];
                var bounds = isVarint ? VarintBounds : ByteBounds;
                var bound = bounds[mutation.Value % bounds.Length];
                byte[] mutated = [.. bytes.AsSpan(0, offset), .. bound, .. bytes.AsSpan(offset + width)];
                return mutation.Kind == MutationKind.SetField
                    ? mutated
                    : [.. mutated.AsSpan(0, offset + bound.Length), .. new byte[BatchView.CrcLength]];
            default:
                throw new UnreachableException();
        }
    }

    /// <summary>
    /// The offset, width, and form of each field of a valid batch: its flags and header varints, and each row's flags,
    /// key length, version base length, count, token length, ordinal, and body length.
    /// </summary>
    private static List<(int Offset, int Width, bool IsVarint)> FieldsOf(byte[] bytes)
    {
        var fields = new List<(int, int, bool)> { (8, 1, false) };
        var offset = BatchView.FixedHeaderLength;
        _ = Varint(bytes, ref offset, fields);
        _ = Varint(bytes, ref offset, fields);
        var count = Varint(bytes, ref offset, fields);
        for (var row = 0UL; row < count; row++)
        {
            var flags = bytes[offset];
            fields.Add((offset++, 1, false));
            if ((flags & BatchView.DefiningFlag) != 0)
            {
                var keyLength = Varint(bytes, ref offset, fields);
                offset += (int)keyLength;
                fields.Add((offset, 1, false));
                offset += 1 + bytes[offset];
                _ = Varint(bytes, ref offset, fields);
                fields.Add((offset, 1, false));
                offset += 1 + bytes[offset];
            }
            else
            {
                _ = Varint(bytes, ref offset, fields);
            }

            var bodyLength = Varint(bytes, ref offset, fields);
            offset += (int)bodyLength;
        }

        return fields;

        static ulong Varint(byte[] bytes, ref int offset, List<(int, int, bool)> fields)
        {
            Assert.Equal(Ledgers.Varint.Status.Valid, Ledgers.Varint.Read(bytes.AsSpan(offset), out var value, out var length));
            fields.Add((offset, length, true));
            offset += length;
            return value;
        }
    }
}
