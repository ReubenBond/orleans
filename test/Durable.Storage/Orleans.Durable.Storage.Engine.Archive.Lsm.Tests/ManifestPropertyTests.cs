using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Hashing;
using CsCheck;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Durable.Storage.Fuzz.Archive;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Archive.Lsm.Tests;

/// <summary>
/// Properties of the manifest's format (archive §7) over generated values: each encodes to bytes which decode to it, as
/// the format's independent reading of the rules also reads them, and every mutated encoding is judged as that reading
/// judges it: rejected for the first rule it breaks, or accepted as a value which encodes back to exactly its bytes.
/// </summary>
public sealed class ManifestPropertyTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    /// <summary>
    /// Gets mutations: each one's kind, and choices of position, bit, field, and value.
    /// </summary>
    private static Gen<(int Kind, int Position, int Bit, int Field, int Value)> Mutations { get; } =
        Gen.Select(Gen.Int[0, 5], Gen.Int.NonNegative, Gen.Int[0, 7], Gen.Int.NonNegative, Gen.Int.NonNegative);

    public void Dispose() => _leaks.Dispose();

    /// <summary>
    /// A manifest of more than 10,000 sections, whose encoding spans several buffer pages, encodes, and decodes to itself
    /// from those pages and from one contiguous copy.
    /// </summary>
    [Fact]
    public void AManifestOfManyPagesRoundTrips()
    {
        var value = new ManifestValue(1, new LedgerId(1), [], ManifestChanges.ManySections(12_000));
        using var encoded = ManifestFormat.Encode(value);

        var pages = 0;
        foreach (var _ in encoded.Content)
        {
            pages++;
        }

        Assert.True(pages > 1, $"The encoding of {encoded.Content.Length} bytes lies in {pages} page.");
        Assert.Equal(value, Assert.IsType<ManifestValue>(ManifestFormat.Decode(encoded.Content.AsReadOnlySequence()).Value));
        Assert.Equal(value, Assert.IsType<ManifestValue>(ManifestFormat.Decode(new ReadOnlySequence<byte>(encoded.Content.ToArray())).Value));
    }

    [Fact]
    public void EveryValueRoundTripsThroughItsEncoding() =>
        ManifestSamples.Values.Sample(
            value => _leaks.InScope(() =>
            {
                using var encoded = ManifestFormat.Encode(value);
                var bytes = encoded.Content.ToArray();
                Assert.Equal(Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, bytes), encoded.Crc32C);
                Assert.Equal(value, Assert.IsType<ManifestValue>(ManifestFormat.Decode(new ReadOnlySequence<byte>(bytes)).Value));
                Assert.Null(ManifestRules.Read(bytes).Reason);
            }),
            iter: 1_000);

    /// <summary>
    /// Every mutated encoding (flipped bits, truncations, extensions, and integer fields set to their bounds), with its
    /// header as mutated or resealed, is rejected for the reason <see cref="ManifestRules"/> names or accepted as the
    /// value it reads, which encodes back to the same bytes; <c>classify</c> reports each outcome.
    /// </summary>
    [Fact]
    public void EveryMutatedEncodingIsJudgedAsTheRulesJudgeIt()
    {
        var outcomes = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        Gen.Select(ManifestSamples.Values, Mutations, Gen.Bool).Sample(
            sample =>
            {
                var (value, mutation, seal) = sample;
                var outcome = "accepted";
                _leaks.InScope(() =>
                {
                    byte[] bytes;
                    using (var encoded = ManifestFormat.Encode(value))
                    {
                        bytes = Mutate(encoded.Content.ToArray(), value, mutation);
                    }

                    bytes = seal ? ManifestRules.Seal(bytes) ?? bytes : bytes;
                    var reading = ManifestRules.Read(bytes);
                    var decoding = ManifestFormat.Decode(new ReadOnlySequence<byte>(bytes));
                    if (decoding.Value is InvalidDataException rejection)
                    {
                        outcome = DecodeRejection.ReasonOf(rejection);
                        Assert.Equal(reading.Reason, outcome);
                        return;
                    }

                    Assert.Null(reading.Reason);
                    using var reencoded = ManifestFormat.Encode(Assert.IsType<ManifestValue>(decoding.Value));
                    Assert.Equal(bytes, reencoded.Content.ToArray());
                });
                _ = outcomes.AddOrUpdate(outcome, 1, static (_, count) => count + 1);
                return outcome;
            },
            TestDiagnostics.WriteLine,
            iter: 4_000);

        Assert.Superset(
            new HashSet<string>(StringComparer.Ordinal)
            {
                "accepted",
                ManifestFormat.Truncated,
                ManifestFormat.BadMagic,
                ManifestFormat.BadVersion,
                ManifestFormat.LengthMismatch,
                ManifestFormat.CrcMismatch,
                ManifestFormat.BodyTruncated,
                ManifestFormat.ZeroEpoch,
                ManifestFormat.ZeroDrainedBelow,
                ManifestFormat.TrailingBytes,
            },
            outcomes.Keys.ToHashSet(StringComparer.Ordinal));
    }

    private static byte[] Mutate(byte[] bytes, ManifestValue value, (int Kind, int Position, int Bit, int Field, int Value) mutation)
    {
        switch (mutation.Kind)
        {
            case 0:
                bytes[mutation.Position % bytes.Length] ^= (byte)(1 << mutation.Bit);
                return bytes;
            case 1:
                // A flip in the header, whose fields a flip anywhere seldom reaches.
                bytes[mutation.Position % ManifestFormat.HeaderLength] ^= (byte)(1 << mutation.Bit);
                return bytes;
            case 2:
                return bytes[..(mutation.Position % (mutation.Bit < 2 ? ManifestFormat.HeaderLength + 1 : bytes.Length + 1))];
            case 3:
                return [.. bytes, .. Enumerable.Repeat((byte)(mutation.Value & 0xff), 1 + mutation.Bit % 4)];
            default:
                // A quarter of field mutations zero the epoch or DrainedBelow, the two fields whose zero only they reach.
                var fields = FieldsOf(value);
                var (offset, width) = mutation.Field % 4 == 0 ? fields[mutation.Field / 4 % 2] : fields[mutation.Field % fields.Count];
                var bounds = Bounds(width);
                var bound = mutation.Field % 4 == 0 ? 0 : mutation.Value % bounds.Length;
                bounds[bound].AsSpan(0, width).CopyTo(bytes.AsSpan(offset));
                return bytes;
        }
    }

    /// <summary>
    /// Every value a <see cref="SectionRef"/> can hold encodes to bytes the decoder accepts: a tag at slot 2^63 - 1, which
    /// the format reserves, a tag of no ledger, and the default, empty tail are refused when the section is built.
    /// </summary>
    [Fact]
    public void ASectionRefusesWhatTheFormatRejects()
    {
        var name = new ObjectName("d/1/0/a");
        var lowest = new Tag(new LedgerId(1), new Slot(0), 0);
        var reserved = new Tag(new LedgerId(1), new Slot(long.MaxValue), 0);
        var last = new Tag(new LedgerId(1), new Slot(long.MaxValue - 1), 0);
        var tail = new ObjectRange(100, 16);

        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new SectionRef(name, 0, 100, KeyRange.Root, lowest, reserved, tail));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new SectionRef(name, 0, 100, KeyRange.Root, default, lowest, tail));
        _ = Assert.Throws<ArgumentException>(() => new SectionRef(name, 0, 100, KeyRange.Root, lowest, lowest, default));
        var section = new SectionRef(name, 0, 100, KeyRange.Root, lowest, last, tail);
        var value = new ManifestValue(1, new LedgerId(1), [], [section]);
        using var encoded = ManifestFormat.Encode(value);
        Assert.Equal(value, Assert.IsType<ManifestValue>(ManifestFormat.Decode(encoded.Content.AsReadOnlySequence()).Value));
    }

    /// <summary>
    /// The offset and width of each integer field of <paramref name="value"/>'s encoding: the epoch, <c>DrainedBelow</c>,
    /// the counts, each entry's fields, and each section's name lengths, offsets, lengths, depth, tags, and tail.
    /// </summary>
    private static List<(int Offset, int Width)> FieldsOf(ManifestValue value)
    {
        var offset = ManifestFormat.HeaderLength;
        var fields = new List<(int, int)> { (offset, 8), (offset + 8, 8), (offset + 16, 4) };
        offset += 20;
        foreach (var _ in value.Watermarks)
        {
            fields.AddRange([(offset, 8), (offset + 8, 8), (offset + 16, 1)]);
            offset += 17;
        }

        fields.Add((offset, 4));
        offset += 4;
        foreach (var section in value.Sections)
        {
            fields.Add((offset, 1));
            offset += 1 + section.Object.Value.Length;
            fields.AddRange([(offset, 8), (offset + 8, 8), (offset + 16, 4)]);
            offset += 20 + section.Partition.Prefix.Length;
            for (var tag = 0; tag < 2; tag++)
            {
                fields.AddRange([(offset, 8), (offset + 8, 8), (offset + 16, 4)]);
                offset += 20;
            }

            fields.AddRange([(offset, 8), (offset + 8, 8)]);
            offset += 16;
        }

        return fields;
    }

    /// <summary>
    /// Returns the little-endian encodings of the bounds a field of <paramref name="width"/> bytes may take: 0, 1, 2, and
    /// the bounds of each width's signed and unsigned range.
    /// </summary>
    private static byte[][] Bounds(int width)
    {
        ulong[] values = [0, 1, 2, byte.MaxValue, int.MaxValue, (ulong)int.MaxValue + 1, uint.MaxValue, long.MaxValue - 1, long.MaxValue, (ulong)long.MaxValue + 1, ulong.MaxValue];
        return
        [
            .. values.Where(candidate => width == 8 || candidate <= (width == 4 ? uint.MaxValue : byte.MaxValue)).Select(static candidate =>
            {
                var encoded = new byte[8];
                BinaryPrimitives.WriteUInt64LittleEndian(encoded, candidate);
                return encoded;
            }),
        ];
    }
}
