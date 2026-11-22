using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using CsCheck;
using Orleans.Durable.Storage.Fuzz.Ledgers;
using Orleans.Durable.Storage.Testing;
using Xunit.Sdk;
using static Orleans.Durable.Storage.Engine.Ledgers.Tests.LedgerMapSamples;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests;

/// <summary>
/// CsCheck properties of the ledger map codec (ledgers.md §7): it decodes the values it encodes, from any division of the
/// bytes into pages, and re-encodes every value it accepts to the same bytes; <see cref="LedgerMapFormatRules"/>, one
/// independent reading of the rules, judges every mutated encoding; and a range's binary form reads back as the range.
/// Each case runs in its own page leak scope (verification.md §11.1).
/// </summary>
public sealed class LedgerMapCodecPropertyTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    private enum MutationKind
    {
        /// <summary>A bit flipped anywhere.</summary>
        Flip,

        /// <summary>A bit flipped in the header.</summary>
        FlipHeader,

        /// <summary>The value cut short.</summary>
        Truncate,

        /// <summary>Bytes inserted before the CRC-32C.</summary>
        Extend,

        /// <summary>A header or ledger field set to a bound.</summary>
        SetField,

        /// <summary>A ledger's second replica set to its first.</summary>
        Duplicate,
    }

    public void Dispose() => _leaks.Dispose();

    [Fact]
    public void TheCodecDecodesTheValuesItEncodesFromAnyPages() =>
        Gen.Select(Values, Gen.Int[1, 64]).Sample((value, pageLength) => _leaks.InScope(() =>
        {
            var bytes = Encoded(value);
            Assert.Equal(value.EncodedLength, bytes.Length);
            using (var encoded = value.Encode())
            {
                Assert.Equal(bytes, encoded.ToArray());
            }

            var decoded = Value(LedgerMapValue.Decode(bytes.AsSpan()));
            Assert.Equal(Describe(value), Describe(decoded));
            Assert.Equal(value.Ledgers, decoded.Ledgers);
            Assert.Equal(bytes, Encoded(decoded));
            Assert.NotNull(LedgerMapFormatRules.Read(bytes).Map);
            using var paged = PagedBuffers.Create(bytes, pageLength);
            Assert.Equal(Describe(value), Describe(Value(LedgerMapValue.Decode(paged.AsReadOnlySequence()))));
        }));

    /// <summary>
    /// Every mutated encoding, with its CRC-32C as mutated or recomputed, is rejected for the reason
    /// <see cref="LedgerMapFormatRules"/> names or accepted as the value it reads, which re-encodes to the same bytes;
    /// <c>classify</c> reports each outcome. The sweep of <see cref="Swept"/>'s mutations, judged first, reaches acceptance
    /// and every reason of the rejection vectors, so the coverage holds by construction; the random samples reach no other.
    /// </summary>
    [Fact]
    public void EveryMutatedEncodingIsJudgedAsTheRulesJudgeIt()
    {
        var reasons = SharedVectors.Cases("ledgers", "map-rejections.json")
            .Select(static vector => SharedVectors.Text(vector, "reason"))
            .Append("accepted")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);
        var swept = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (mutation, seal) in SweptMutations())
        {
            try
            {
                _ = swept.Add(Judge(Swept, mutation, seal));
            }
            catch (XunitException failure)
            {
                throw new XunitException($"The swept value's mutation {mutation}, {(seal ? "resealed" : "as mutated")}: {failure.Message}", failure);
            }
        }

        Assert.Equal(reasons, swept.Order(StringComparer.Ordinal));

        var outcomes = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        Gen.Select(Values, Mutations, Gen.Bool).Sample(
            sample =>
            {
                var outcome = Judge(sample.Item1, sample.Item2, sample.Item3);
                _ = outcomes.AddOrUpdate(outcome, 1, static (_, count) => count + 1);
                return outcome;
            },
            TestDiagnostics.WriteLine,
            iter: 6_000);

        Assert.Subset(swept, outcomes.Keys.ToHashSet(StringComparer.Ordinal));
    }

    /// <summary>A range's binary form reads back as the range, from the front of longer bytes, with no rejection.</summary>
    [Fact]
    public void ARangesBinaryFormReadsBackAsTheRange() =>
        Gen.Select(Ranges, Gen.Int[0, 4]).Sample((range, trailing) =>
        {
            var bytes = new byte[range.EncodedLength + trailing];
            range.Write(bytes);
            Assert.Null(KeyRange.Read(bytes, out var read));
            Assert.Equal(range, read);
            Assert.Equal(range.EncodedLength, read.EncodedLength);
        });

    private static Gen<(MutationKind Kind, int Position, int Bit, int Field, int Value)> Mutations { get; } =
        Gen.Select(
            Gen.Frequency(
                (2, Gen.Const(MutationKind.Flip)),
                (1, Gen.Const(MutationKind.FlipHeader)),
                (1, Gen.Const(MutationKind.Truncate)),
                (1, Gen.Const(MutationKind.Extend)),
                (6, Gen.Const(MutationKind.SetField)),
                (1, Gen.Const(MutationKind.Duplicate))),
            Gen.Int.NonNegative,
            Gen.Int[0, 7],
            Gen.Int.NonNegative,
            Gen.Int.NonNegative);

    /// <summary>
    /// Gets a value whose mutations reach every reason: a closed ledger and two open ones, each with three replicas, the
    /// open ones over ranges with a partial prefix byte.
    /// </summary>
    private static LedgerMapValue Swept { get; } = SweptValue();

    private static LedgerMapValue SweptValue()
    {
        var (lower, upper) = KeyRange.Root.Children;
        var shape = (ImmutableArray.Create(Nodes[0], Nodes[1], Nodes[2]), 4, new PrimaryClaim(Nodes[0], new SequencerInstanceId(Guid.AllBitsSet)));
        return new LedgerMapValue(
            2,
            new LedgerId(4),
            0,
            [
                Ledger(1, KeyRange.Root, shape, new LedgerStatus.Closed(new Slot(5))),
                Ledger(2, lower, shape, LedgerStatus.Open.Instance),
                Ledger(3, upper, shape, LedgerStatus.Open.Instance),
            ]);
    }

    /// <summary>
    /// The mutations of <see cref="Swept"/>'s encoding, each kind at every position it takes: each bit flipped, each cut,
    /// each length of extension, each field set to each of its bounds, and each replica duplicated, with its CRC-32C as
    /// mutated and as recomputed.
    /// </summary>
    private static IEnumerable<((MutationKind Kind, int Position, int Bit, int Field, int Value) Mutation, bool Seal)> SweptMutations()
    {
        var fields = FieldsOf(Swept);
        IEnumerable<(MutationKind Kind, int Position, int Bit, int Field, int Value)> mutations =
        [
            .. from position in Enumerable.Range(0, Swept.EncodedLength) from bit in Enumerable.Range(0, 8) select (MutationKind.Flip, position, bit, 0, 0),
            .. Enumerable.Range(0, Swept.EncodedLength + 1).Select(static position => (MutationKind.Truncate, position, 0, 0, 0)),
            .. Enumerable.Range(0, 4).Select(static bit => (MutationKind.Extend, 0, bit, 0, 0)),
            .. from field in Enumerable.Range(0, fields.Count) from bound in Enumerable.Range(0, Bounds(fields[field].Width).Length) select (MutationKind.SetField, 0, 0, field, bound),
            .. Enumerable.Range(0, ReplicaOffsets(Swept).Count(static pair => pair.Second > 0)).Select(static replica => (MutationKind.Duplicate, 0, 0, replica, 0)),
        ];
        return from mutation in mutations from seal in (bool[])[false, true] select (mutation, seal);
    }

    /// <summary>
    /// Judges a mutation of <paramref name="value"/>'s encoding: the decoder rejects it for the reason the rules name, or
    /// accepts it as a value which re-encodes to the same bytes. Returns the reason, or <c>accepted</c>.
    /// </summary>
    private static string Judge(LedgerMapValue value, (MutationKind Kind, int Position, int Bit, int Field, int Value) mutation, bool seal)
    {
        var bytes = Mutate(Encoded(value), value, mutation);
        bytes = seal ? LedgerMapFormatRules.Seal(bytes) ?? bytes : bytes;
        var reading = LedgerMapFormatRules.Read(bytes);
        var decoding = LedgerMapValue.Decode(bytes.AsSpan());
        var rejection = Rejection(decoding);
        Assert.Equal(reading.Reason, BatchSamples.Reason(rejection));
        if (rejection is null)
        {
            Assert.Equal(bytes, Encoded(Value(decoding)));
        }

        return reading.Reason ?? "accepted";
    }

    private static byte[] Mutate(byte[] bytes, LedgerMapValue value, (MutationKind Kind, int Position, int Bit, int Field, int Value) mutation)
    {
        switch (mutation.Kind)
        {
            case MutationKind.Flip:
                bytes[mutation.Position % bytes.Length] ^= (byte)(1 << mutation.Bit);
                return bytes;
            case MutationKind.FlipHeader:
                bytes[mutation.Position % LedgerMapValue.HeaderLength] ^= (byte)(1 << mutation.Bit);
                return bytes;
            case MutationKind.Truncate:
                return bytes[..(mutation.Position % (bytes.Length + 1))];
            case MutationKind.Extend:
                var crcAt = bytes.Length - LedgerMapValue.CrcLength;
                return [.. bytes.AsSpan(0, crcAt), .. Enumerable.Repeat((byte)(mutation.Value & 0xff), 1 + mutation.Bit % 4), .. bytes.AsSpan(crcAt)];
            case MutationKind.SetField:
                var fields = FieldsOf(value);
                var (offset, width) = fields[mutation.Field % fields.Count];
                var bounds = Bounds(width);
                bounds[mutation.Value % bounds.Length].AsSpan(0, width).CopyTo(bytes.AsSpan(offset));
                return bytes;
            case MutationKind.Duplicate:
                var replicas = ReplicaOffsets(value).Where(static pair => pair.Second > 0).ToArray();
                if (replicas.Length > 0)
                {
                    var (first, second) = replicas[mutation.Field % replicas.Length];
                    bytes.AsSpan(first, 16).CopyTo(bytes.AsSpan(second));
                }

                return bytes;
            default:
                throw new UnreachableException();
        }
    }

    /// <summary>The offset and width of the header's fields and of each ledger's fields, with a partial prefix's last byte.</summary>
    private static List<(int Offset, int Width)> FieldsOf(LedgerMapValue value)
    {
        var fields = new List<(int, int)> { (8, 8), (16, 8), (24, 8), (32, 4) };
        var offset = LedgerMapValue.HeaderLength;
        foreach (var ledger in value.Ledgers)
        {
            fields.Add((offset, 8));
            offset += 8;
            fields.Add((offset, 4));
            if (ledger.Range.Depth % 8 != 0)
            {
                fields.Add((offset + ledger.Range.EncodedLength - 1, 1));
            }

            offset += ledger.Range.EncodedLength;
            fields.Add((offset, 1));
            offset += 1;
            for (var replica = 0; replica < ReplicaCount(ledger); replica++)
            {
                fields.Add((offset, 16));
                offset += 16;
            }

            fields.Add((offset, 4));
            fields.Add((offset + 4, 16));
            fields.Add((offset + 20, 16));
            fields.Add((offset + 36, 1));
            offset += 37;
            if (!ledger.IsOpen)
            {
                fields.Add((offset, 8));
                offset += 8;
            }
        }

        return fields;
    }

    /// <summary>The offsets of each ledger's first and second replicas, the second 0 when it has one.</summary>
    private static IEnumerable<(int First, int Second)> ReplicaOffsets(LedgerMapValue value)
    {
        var offset = LedgerMapValue.HeaderLength;
        foreach (var ledger in value.Ledgers)
        {
            var first = offset + 8 + ledger.Range.EncodedLength + 1;
            yield return (first, ReplicaCount(ledger) > 1 ? first + 16 : 0);
            offset = first + 16 * ReplicaCount(ledger) + 37 + (ledger.IsOpen ? 0 : 8);
        }
    }

    /// <summary>The replica count a ledger's entry encodes: its nodes', or 0 in the object store.</summary>
    private static int ReplicaCount(LedgerMetadata ledger) => ledger.Location switch
    {
        LedgerLocation.Nodes nodes => nodes.Replicas.Length,
        LedgerLocation.ObjectStore => 0,
        null => throw new UnreachableException("conventions.md §15: a listed ledger's location holds a case."),
    };

    /// <summary>Each width's bounds and their neighbors, little-endian; the depth's and the window's limits for 4 bytes.</summary>
    private static byte[][] Bounds(int width)
    {
        List<ulong> values = [0, 1, 2, 3, ulong.MaxValue];
        if (width == sizeof(uint))
        {
            values.AddRange([1UL << 31, (1UL << 31) - 1, KeyRange.MaxDepth, KeyRange.MaxDepth + 1]);
        }

        if (width == sizeof(ulong))
        {
            values.AddRange([1UL << 63, (1UL << 63) - 1]);
        }

        return
        [
            .. values.Select(value =>
            {
                var bytes = new byte[Math.Max(width, 16)];
                BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
                if (value == ulong.MaxValue)
                {
                    bytes.AsSpan().Fill(0xff);
                }

                return bytes;
            }),
        ];
    }
}
