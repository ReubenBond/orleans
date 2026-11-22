using System.Text.Json;
using CsCheck;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.ObjectStore;
using static Orleans.Durable.Storage.Testing.SharedVectors;

namespace Orleans.Durable.Storage.Engine.Archive.Lsm.Tests;

/// <summary>
/// Manifest values for the archive's tests: generators of valid values, and the values the shared vectors hold.
/// </summary>
internal static class ManifestSamples
{
    /// <summary>
    /// Gets ledger IDs, mostly small so values share and order them, and sometimes at the type's bound.
    /// </summary>
    internal static Gen<ulong> LedgerValues { get; } = Gen.Frequency((6, Gen.ULong[1, 12]), (1, Gen.ULong[ulong.MaxValue - 3, ulong.MaxValue]));

    /// <summary>
    /// Gets slots, mostly small, and sometimes at the bound of a tag's slot.
    /// </summary>
    internal static Gen<long> Slots { get; } = Gen.Frequency((6, Gen.Long[0, 5_000]), (1, Gen.Long[long.MaxValue - 4, long.MaxValue - 1]));

    /// <summary>
    /// Gets object names under <c>d/</c>, <c>c/</c>, and the root, of lengths up to <see cref="Orleans.Durable.Storage.Engine.ObjectStore.ObjectName.MaxLength"/>.
    /// </summary>
    internal static Gen<ObjectName> ObjectNames { get; } =
        Gen.Select(Gen.OneOfConst("d/", "c/", "x"), Gen.Char["abcdefghijklmnopqrstuvwxyz0123456789-_"].Array[1, 40], Gen.Int[0, 3])
            .Select(static (prefix, characters, padding) => new ObjectName(prefix + new string(characters) + new string('0', padding * 50)));

    /// <summary>
    /// Gets partitions of depths 0 to 40, and sometimes the deepest.
    /// </summary>
    internal static Gen<KeyRange> Partitions { get; } =
        Gen.Frequency((40, Gen.Int[0, 40]), (1, Gen.Const(KeyRange.MaxDepth)))
            .SelectMany(static depth => Gen.Byte.Array[(depth + 7) / 8].Select(prefix => (depth, prefix)))
            .Select(static sample => KeyRange.Containing(Key(sample.prefix, sample.depth), sample.depth));

    /// <summary>
    /// Gets tags of the generated ledgers and slots.
    /// </summary>
    internal static Gen<Tag> Tags { get; } =
        Gen.Select(LedgerValues, Slots, Gen.Int[0, int.MaxValue]).Select(static (ledger, slot, index) => new Tag(new LedgerId(ledger), new Slot(slot), index));

    /// <summary>
    /// Gets sections at random positions, with ordered tags.
    /// </summary>
    internal static Gen<SectionRef> Sections { get; } =
        Gen.Select(ObjectNames, Gen.Long[0, 1L << 40], Gen.Long[1, 1L << 30], Partitions, Tags, Tags, Gen.Long[1, 4096])
            .Select(static (name, offset, length, partition, first, second, tail) => new SectionRef(
                name,
                offset,
                length,
                partition,
                first <= second ? first : second,
                first <= second ? second : first,
                new ObjectRange(offset + length, tail)));

    /// <summary>
    /// Gets valid manifest values of epoch at least 1.
    /// </summary>
    internal static Gen<ManifestValue> Values { get; } =
        Gen.Select(Gen.ULong[1, ulong.MaxValue], Gen.ULong[1, 10], Gen.Select(Gen.ULong[0, 20], Gen.Long[0, 10_000], Gen.Bool).List[0, 6], Sections.List[0, 6])
            .Select(static (epoch, drainedBelow, entries, sections) => Value(epoch, drainedBelow, entries, sections));

    /// <summary>
    /// Returns the value a manifest vector holds.
    /// </summary>
    internal static ManifestValue FromVector(JsonElement vector) =>
        new(
            UInt64(vector, "epoch"),
            new LedgerId(UInt64(vector, "drainedBelow")),
            [
                .. vector.GetProperty("watermarks").EnumerateArray().Select(static entry => KeyValuePair.Create(
                    new LedgerId(UInt64(entry, "ledger")),
                    new ManifestWatermark(new Slot(Int64(entry, "watermark")), entry.GetProperty("complete").GetBoolean()))),
            ],
            [
                .. vector.GetProperty("sections").EnumerateArray().Select(static section => new SectionRef(
                    new ObjectName(Text(section, "object")),
                    Int64(section, "offset"),
                    Int64(section, "length"),
                    new KeyRange(Hex(section.GetProperty("partition"), "prefix"), section.GetProperty("partition").GetProperty("depth").GetInt32()),
                    TagOf(section.GetProperty("minTag")),
                    TagOf(section.GetProperty("maxTag")),
                    new ObjectRange(Int64(section.GetProperty("tail"), "offset"), Int64(section.GetProperty("tail"), "length")))),
            ]);

    /// <summary>
    /// Returns a valid value from a generator's choices: watermarks at or above <paramref name="drainedBelow"/>, the
    /// entry of <paramref name="drainedBelow"/> incomplete, none empty, and sections at distinct positions.
    /// </summary>
    private static ManifestValue Value(ulong epoch, ulong drainedBelow, List<(ulong Offset, long Watermark, bool Complete)> entries, List<SectionRef> sections)
    {
        var watermarks = new Dictionary<LedgerId, ManifestWatermark>();
        foreach (var (offset, watermark, complete) in entries)
        {
            var ledger = new LedgerId(drainedBelow + offset);
            var isComplete = complete && offset != 0;
            if (watermark > 0 || isComplete)
            {
                watermarks[ledger] = new ManifestWatermark(new Slot(watermark), isComplete);
            }
        }

        var distinct = sections.DistinctBy(static section => (section.Object, section.Offset));
        return new ManifestValue(epoch, new LedgerId(drainedBelow), watermarks, distinct);
    }

    private static Tag TagOf(JsonElement tag) =>
        new(new LedgerId(UInt64(tag, "ledger")), new Slot(Int64(tag, "slot")), tag.GetProperty("index").GetInt32());

    /// <summary>
    /// Returns a key whose first <paramref name="depth"/> bits are <paramref name="prefix"/>'s.
    /// </summary>
    private static byte[] Key(byte[] prefix, int depth) => depth == 0 ? [0] : prefix;
}
