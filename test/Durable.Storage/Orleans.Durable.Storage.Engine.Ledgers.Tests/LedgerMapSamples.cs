using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using CsCheck;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests;

/// <summary>
/// Ledger map values for the map's tests: generators of values whose open ranges partition the keyspace, the shared
/// vectors' values, and a text rendering of a value.
/// </summary>
internal static class LedgerMapSamples
{
    /// <summary>The deepest range the generators split to.</summary>
    public const int MaxSampleDepth = 10;

    /// <summary>Gets the node identities the generators draw from.</summary>
    public static NodeId[] Nodes { get; } = [.. Enumerable.Range(1, 12).Select(Node)];

    /// <summary>Gets generated histories' steps: kinds of change, biased toward transitions, retirements, and stale installs.</summary>
    public static Gen<LedgerMapStep[]> Steps { get; } =
        Gen.Select(Gen.Int[0, 99], Gen.Int.NonNegative, Gen.Int[0, 99], Gen.Int[0, 2])
            .Select(static (kind, choice, staleness, ends) => new LedgerMapStep(kind, choice, staleness, ends != 0))
            .Array[1, 40];

    /// <summary>Gets partitions of the keyspace: the whole keyspace, split at random up to <see cref="MaxSampleDepth"/>.</summary>
    public static Gen<KeyRange[]> Partitions { get; } =
        Gen.Int[0, 1_000_000].Array[0, 24].Select(static choices =>
        {
            var ranges = new List<KeyRange> { KeyRange.Root };
            foreach (var choice in choices)
            {
                var index = choice % ranges.Count;
                if (ranges[index].Depth < MaxSampleDepth)
                {
                    var (lower, upper) = ranges[index].Children;
                    ranges[index] = lower;
                    ranges.Insert(index + 1, upper);
                }
            }

            return ranges.ToArray();
        });

    /// <summary>Gets ranges of any depth up to <see cref="MaxSampleDepth"/>.</summary>
    public static Gen<KeyRange> Ranges { get; } =
        Gen.Select(Gen.ULong, Gen.Int[0, MaxSampleDepth]).Select(static (bits, depth) => KeyRange.Containing(Key(bits), depth));

    /// <summary>
    /// Gets ledgers' fields other than their ID, range, and status: location, on 1, 3, or 5 nodes or in the object store,
    /// window, and primary claim.
    /// </summary>
    public static Gen<(LedgerLocation Location, int K, PrimaryClaim Primary)> Shapes { get; } =
        Gen.Select(Gen.Int[0, 3], Gen.Shuffle(Nodes, 5), Gen.OneOf(Gen.Int[1, 64], Gen.Const(int.MaxValue)), Gen.Int[0, 11], Gen.Guid)
            .Select(static (size, shuffled, k, primary, sequencer) => (
                size == 3 ? (LedgerLocation)LedgerLocation.ObjectStore.Instance : new LedgerLocation.Nodes(ImmutableArray.Create(shuffled, 0, 2 * size + 1)),
                k,
                new PrimaryClaim(Nodes[primary], new SequencerInstanceId(sequencer == Guid.Empty ? Guid.AllBitsSet : sequencer))));

    /// <summary>Gets recorded tails, with the bounds.</summary>
    public static Gen<Slot> Tails { get; } = Gen.OneOf(Gen.Long[0, 1_000], Gen.Const(long.MaxValue), Gen.Long[0, long.MaxValue]).Select(static tail => new Slot(tail));

    /// <summary>
    /// Gets map values whose open ranges partition the keyspace, with closed ledgers over any ranges, IDs ascending with
    /// gaps, and fields at their bounds.
    /// </summary>
    public static Gen<LedgerMapValue> Values { get; } =
        Gen.Select(
                Partitions,
                Gen.Select(Ranges, Tails).Array[0, 6],
                Shapes.Array[40],
                Gen.Int[1, 3].Array[40],
                Gen.OneOf(Gen.ULong[1, 100], Gen.Const(ulong.MaxValue)),
                Gen.OneOf(Gen.ULong[0, 100], Gen.Const(ulong.MaxValue)),
                Gen.ULong[0, 5])
            .Select(static (open, closed, shapes, gaps, epoch, floor, nextGap) =>
            {
                var ledgers = ImmutableArray.CreateBuilder<LedgerMetadata>();
                var id = 0UL;
                var index = 0;
                foreach (var (range, tail) in closed)
                {
                    id += (ulong)gaps[index];
                    ledgers.Add(Ledger(id, range, shapes[index++], new LedgerStatus.Closed(tail)));
                }

                foreach (var range in open)
                {
                    id += (ulong)gaps[index];
                    ledgers.Add(Ledger(id, range, shapes[index++], LedgerStatus.Open.Instance));
                }

                return new LedgerMapValue(epoch, new LedgerId(id + 1 + nextGap), floor, ledgers.ToImmutable());
            });

    public static NodeId Node(int number) => LedgerMapWorld.Node(number);

    /// <summary>Returns an 8-byte key holding <paramref name="bits"/> big-endian.</summary>
    public static byte[] Key(ulong bits)
    {
        var key = new byte[sizeof(ulong)];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(key, bits);
        return key;
    }

    public static LedgerMetadata Ledger(ulong id, KeyRange range, (LedgerLocation Location, int K, PrimaryClaim Primary) shape, LedgerStatus status) =>
        new(new LedgerId(id), range, shape.Location, shape.K, shape.Primary, status);

    public static LedgerMetadata Ledger(ulong id, KeyRange range, (ImmutableArray<NodeId> Replicas, int K, PrimaryClaim Primary) shape, LedgerStatus status) =>
        new(new LedgerId(id), range, shape.Replicas, shape.K, shape.Primary, status);

    /// <summary>Returns the value a <c>maps.json</c> case describes.</summary>
    public static LedgerMapValue FromVector(JsonElement vector) =>
        new(
            SharedVectors.UInt64(vector, "epoch"),
            new LedgerId(SharedVectors.UInt64(vector, "nextLedgerId")),
            SharedVectors.UInt64(vector, "retirementFloor"),
            [
                .. vector.GetProperty("ledgers").EnumerateArray().Select(static ledger => new LedgerMetadata(
                    new LedgerId(SharedVectors.UInt64(ledger, "id")),
                    new KeyRange(SharedVectors.Hex(ledger, "prefix"), ledger.GetProperty("depth").GetInt32()),
                    Location(ledger),
                    ledger.GetProperty("k").GetInt32(),
                    new PrimaryClaim(new NodeId(Guid.Parse(SharedVectors.Text(ledger, "primary"))), new SequencerInstanceId(Guid.Parse(SharedVectors.Text(ledger, "sequencer")))),
                    SharedVectors.Text(ledger, "status") == "open"
                        ? LedgerStatus.Open.Instance
                        : new LedgerStatus.Closed(new Slot(SharedVectors.Int64(ledger, "tail"))))),
            ]);

    /// <summary>
    /// Returns the location a <c>maps.json</c> ledger names: its <c>replicas</c> on nodes, or the object store when its
    /// <c>location</c> is <c>object-store</c>.
    /// </summary>
    public static LedgerLocation Location(JsonElement ledger) =>
        ledger.TryGetProperty("location", out var location) && location.GetString() == "object-store"
            ? LedgerLocation.ObjectStore.Instance
            : new LedgerLocation.Nodes([.. ledger.GetProperty("replicas").EnumerateArray().Select(static node => new NodeId(Guid.Parse(node.GetString()!)))]);

    /// <summary>Returns the bytes of a value's encoding.</summary>
    public static byte[] Encoded(LedgerMapValue value)
    {
        var bytes = new byte[value.EncodedLength];
        value.Write(bytes);
        return bytes;
    }

    /// <summary>Returns the value a decoding holds, failing the test on a rejection.</summary>
    public static LedgerMapValue Value(LedgerMapDecoding decoding) => decoding switch
    {
        LedgerMapValue value => value,
        InvalidDataException rejection => throw new Xunit.Sdk.XunitException($"The value was rejected: {rejection.Message}"),
        null => throw new System.Diagnostics.UnreachableException(),
    };

    /// <summary>Returns the rejection a decoding holds, or <see langword="null"/> for a value.</summary>
    public static InvalidDataException? Rejection(LedgerMapDecoding decoding) => decoding switch
    {
        LedgerMapValue => null,
        InvalidDataException rejection => rejection,
        null => throw new System.Diagnostics.UnreachableException(),
    };

    /// <summary>Renders a value: its header fields, then one line per ledger.</summary>
    public static string Describe(LedgerMapValue value)
    {
        var text = new StringBuilder();
        _ = text.AppendLine(CultureInfo.InvariantCulture, $"epoch {value.Epoch}, next ledger {value.NextLedgerId}, floor {value.RetirementFloor}, {value.Ledgers.Length} ledgers");
        foreach (var ledger in value.Ledgers)
        {
            var status = ledger.Status switch
            {
                LedgerStatus.Open => "open",
                LedgerStatus.Closed closed => string.Create(CultureInfo.InvariantCulture, $"closed at {closed.T}"),
                null => throw new System.Diagnostics.UnreachableException(),
            };
            _ = text.AppendLine(CultureInfo.InvariantCulture, $"  ledger {ledger.Id} over {ledger.Range}: {(ledger.Location.Value is LedgerLocation.ObjectStore ? "in the object store" : $"N {ledger.Replicas.Length} [{string.Join(", ", ledger.Replicas)}]")}, K {ledger.K}, primary {ledger.Primary.Node} sequencer {ledger.Primary.Sequencer}, {status}");
        }

        return text.ToString();
    }
}
