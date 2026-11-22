using System.Collections.Immutable;
using System.Globalization;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Objects;

namespace Orleans.Durable.Storage;

/// <summary>
/// Builds a value from a surrogate's fields, refusing fields its constructor refuses with
/// <see cref="InvalidDataException"/>, as every surrogate refuses an unknown tag (orleans.md §7.2).
/// </summary>
internal static class SurrogateFields
{
    /// <summary>Returns <paramref name="build"/>'s value, or throws <see cref="InvalidDataException"/> around its refusal.</summary>
    /// <typeparam name="T">The value's type.</typeparam>
    /// <param name="what">Names the value in the refusal.</param>
    /// <param name="build">Builds the value from the surrogate's fields.</param>
    /// <exception cref="InvalidDataException">The constructor refused the fields.</exception>
    public static T Build<T>(string what, Func<T> build)
    {
        try
        {
            return build();
        }
        catch (ArgumentException refused)
        {
            throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture, $"A grain message carries an invalid {what}: {refused.Message}"), refused);
        }
    }

    /// <summary>
    /// Returns <paramref name="build"/>'s value from <paramref name="state"/>, the surrogate's fields, or throws
    /// <see cref="InvalidDataException"/> around its refusal; a static <paramref name="build"/> captures nothing.
    /// </summary>
    /// <exception cref="InvalidDataException">The constructor refused the fields.</exception>
    public static T Build<TState, T>(string what, TState state, Func<TState, T> build)
    {
        try
        {
            return build(state);
        }
        catch (ArgumentException refused)
        {
            throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture, $"A grain message carries an invalid {what}: {refused.Message}"), refused);
        }
    }

    /// <summary>
    /// Returns a surrogate's array as an immutable array; a surrogate writes every array, so an absent one is a field
    /// missing, which <see cref="InvalidDataException"/> refuses.
    /// </summary>
    /// <exception cref="InvalidDataException"><paramref name="values"/> is absent.</exception>
    public static ImmutableArray<T> Required<T>(T[]? values, string what) =>
        values is null ? throw new InvalidDataException($"A grain message lacks its {what}.") : [.. values];
}

/// <summary>A ledger ID as a grain message carries it: its number, 0 for the default ID.</summary>
[GenerateSerializer]
[Immutable]
internal readonly struct LedgerIdSurrogate
{
    /// <summary>Gets or initializes the ledger's number.</summary>
    [Id(0)]
    public ulong Value { get; init; }
}

/// <summary>Converts a ledger ID to its surrogate and back; the default ID crosses as 0.</summary>
[RegisterConverter]
internal sealed class LedgerIdConverter : IConverter<LedgerId, LedgerIdSurrogate>
{
    /// <inheritdoc/>
    public LedgerId ConvertFromSurrogate(in LedgerIdSurrogate surrogate) => surrogate.Value == 0 ? default : new LedgerId(surrogate.Value);

    /// <inheritdoc/>
    public LedgerIdSurrogate ConvertToSurrogate(in LedgerId value) => new() { Value = value == default ? 0 : value.Value };
}

/// <summary>A slot as a grain message carries it.</summary>
[GenerateSerializer]
[Immutable]
internal readonly struct SlotSurrogate
{
    /// <summary>Gets or initializes the slot's position.</summary>
    [Id(0)]
    public long Value { get; init; }
}

/// <summary>Converts a slot to its surrogate and back, refusing a negative position.</summary>
[RegisterConverter]
internal sealed class SlotConverter : IConverter<Slot, SlotSurrogate>
{
    /// <inheritdoc/>
    /// <exception cref="InvalidDataException">The position is negative.</exception>
    public Slot ConvertFromSurrogate(in SlotSurrogate surrogate)
    {
        var value = surrogate.Value;
        return SurrogateFields.Build("slot", () => new Slot(value));
    }

    /// <inheritdoc/>
    public SlotSurrogate ConvertToSurrogate(in Slot value) => new() { Value = value.Value };
}

/// <summary>A key range as a grain message carries it: its depth in bits and the prefix bytes which hold them.</summary>
[GenerateSerializer]
[Immutable]
internal readonly struct KeyRangeSurrogate
{
    /// <summary>Gets or initializes the range's depth in bits.</summary>
    [Id(0)]
    public int Depth { get; init; }

    /// <summary>Gets or initializes the prefix, <c>⌈depth / 8⌉</c> bytes whose bits past the depth are 0.</summary>
    [Id(1)]
    public byte[]? Prefix { get; init; }
}

/// <summary>Converts a key range to its surrogate and back, refusing a prefix which does not match its depth.</summary>
[RegisterConverter]
internal sealed class KeyRangeConverter : IConverter<KeyRange, KeyRangeSurrogate>
{
    /// <inheritdoc/>
    /// <exception cref="InvalidDataException">The depth is out of range, or the prefix's length or trailing bits do not match it.</exception>
    public KeyRange ConvertFromSurrogate(in KeyRangeSurrogate surrogate)
    {
        var (depth, prefix) = (surrogate.Depth, surrogate.Prefix ?? []);
        return SurrogateFields.Build("key range", () => new KeyRange(prefix, depth));
    }

    /// <inheritdoc/>
    public KeyRangeSurrogate ConvertToSurrogate(in KeyRange value) => new() { Depth = value.Depth, Prefix = value.Prefix.ToArray() };
}

/// <summary>A primary claim as a grain message carries it: its node and its sequencer lifetime's UUID.</summary>
[GenerateSerializer]
[Immutable]
internal readonly struct PrimaryClaimSurrogate
{
    /// <summary>Gets or initializes the claim's node.</summary>
    [Id(0)]
    public NodeId Node { get; init; }

    /// <summary>Gets or initializes the claim's sequencer lifetime.</summary>
    [Id(1)]
    public Guid Sequencer { get; init; }
}

/// <summary>Converts a primary claim to its surrogate and back; the default claim crosses with an empty node and lifetime.</summary>
[RegisterConverter]
internal sealed class PrimaryClaimConverter : IConverter<PrimaryClaim, PrimaryClaimSurrogate>
{
    /// <inheritdoc/>
    /// <exception cref="InvalidDataException">The claim names a node without a lifetime, or a lifetime without a node.</exception>
    public PrimaryClaim ConvertFromSurrogate(in PrimaryClaimSurrogate surrogate)
    {
        var (node, sequencer) = (surrogate.Node, surrogate.Sequencer);
        return node == default && sequencer == Guid.Empty
            ? default
            : SurrogateFields.Build("primary claim", () => new PrimaryClaim(node, new SequencerInstanceId(sequencer)));
    }

    /// <inheritdoc/>
    public PrimaryClaimSurrogate ConvertToSurrogate(in PrimaryClaim value) =>
        value == default ? default : new() { Node = value.Node, Sequencer = value.Sequencer.Value };
}

/// <summary>A row's tag as a grain message carries it: its ledger, slot, and index in the slot's batch.</summary>
[GenerateSerializer]
[Immutable]
internal readonly struct TagSurrogate
{
    /// <summary>Gets or initializes the ledger.</summary>
    [Id(0)]
    public LedgerId Ledger { get; init; }

    /// <summary>Gets or initializes the slot.</summary>
    [Id(1)]
    public Slot Slot { get; init; }

    /// <summary>Gets or initializes the index in the slot's batch.</summary>
    [Id(2)]
    public int Index { get; init; }
}

/// <summary>Converts a tag to its surrogate and back, refusing fields its constructor refuses.</summary>
[RegisterConverter]
internal sealed class TagConverter : IConverter<Tag, TagSurrogate>
{
    /// <inheritdoc/>
    /// <exception cref="InvalidDataException">The tag names no ledger, or a negative index.</exception>
    public Tag ConvertFromSurrogate(in TagSurrogate surrogate)
    {
        var (ledger, slot, index) = (surrogate.Ledger, surrogate.Slot, surrogate.Index);
        return SurrogateFields.Build("tag", () => new Tag(ledger, slot, index));
    }

    /// <inheritdoc/>
    public TagSurrogate ConvertToSurrogate(in Tag value) => new() { Ledger = value.Ledger, Slot = value.Slot, Index = value.Index };
}

/// <summary>
/// A <see cref="LedgerLocation"/> as a grain message carries it: tag 1 <c>Nodes</c> with its replicas, or tag 2
/// <c>ObjectStore</c>.
/// </summary>
[GenerateSerializer]
[Immutable]
internal readonly struct LedgerLocationSurrogate
{
    /// <summary>The tag of <see cref="LedgerLocation.Nodes"/>.</summary>
    public const byte NodesTag = 1;

    /// <summary>The tag of <see cref="LedgerLocation.ObjectStore"/>.</summary>
    public const byte ObjectStoreTag = 2;

    /// <summary>Gets or initializes the case's tag.</summary>
    [Id(0)]
    public byte Case { get; init; }

    /// <summary>Gets or initializes the replicas for <see cref="NodesTag"/>; absent for <see cref="ObjectStoreTag"/>.</summary>
    [Id(1)]
    public NodeId[]? Replicas { get; init; }
}

/// <summary>Converts a <see cref="LedgerLocation"/> to its surrogate and back, refusing an unknown tag or invalid replicas.</summary>
[RegisterConverter]
internal sealed class LedgerLocationConverter : IConverter<LedgerLocation, LedgerLocationSurrogate>
{
    /// <inheritdoc/>
    /// <exception cref="InvalidDataException">The tag names no case, or a nodes location lacks valid replicas.</exception>
    public LedgerLocation ConvertFromSurrogate(in LedgerLocationSurrogate surrogate) => surrogate.Case switch
    {
        LedgerLocationSurrogate.NodesTag => SurrogateFields.Build(
            "ledger location",
            SurrogateFields.Required(surrogate.Replicas, "ledger location's replicas"),
            static replicas => (LedgerLocation)new LedgerLocation.Nodes(replicas)),
        LedgerLocationSurrogate.ObjectStoreTag => surrogate.Replicas is null
            ? LedgerLocation.ObjectStore.Instance
            : throw new InvalidDataException("An object-store ledger location carries no replicas."),
        var tag => throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture, $"A ledger location's case tag is 1 to {LedgerLocationSurrogate.ObjectStoreTag}, not {tag}.")),
    };

    /// <inheritdoc/>
    public LedgerLocationSurrogate ConvertToSurrogate(in LedgerLocation value) => value switch
    {
        LedgerLocation.Nodes nodes => new() { Case = LedgerLocationSurrogate.NodesTag, Replicas = [.. nodes.Replicas] },
        LedgerLocation.ObjectStore => new() { Case = LedgerLocationSurrogate.ObjectStoreTag },
        null => throw new System.Diagnostics.UnreachableException("conventions.md §15: a ledger location holds a case."),
    };
}

/// <summary>An <see cref="ObjectLedgerStoreId"/> as a grain message carries it: its 128-bit ID in two halves.</summary>
[GenerateSerializer]
[Immutable]
internal readonly struct ObjectLedgerStoreIdSurrogate
{
    /// <summary>Gets or initializes the upper 64 bits of the store's ID.</summary>
    [Id(0)]
    public ulong Upper { get; init; }

    /// <summary>Gets or initializes the lower 64 bits of the store's ID.</summary>
    [Id(1)]
    public ulong Lower { get; init; }
}

/// <summary>
/// Converts an <see cref="ObjectLedgerStoreId"/> to its surrogate and back. All zeros crosses as the default identity,
/// for its receiver to refuse; every non-zero surrogate constructs its ID.
/// </summary>
[RegisterConverter]
internal sealed class ObjectLedgerStoreIdConverter : IConverter<ObjectLedgerStoreId, ObjectLedgerStoreIdSurrogate>
{
    /// <inheritdoc/>
    public ObjectLedgerStoreId ConvertFromSurrogate(in ObjectLedgerStoreIdSurrogate surrogate) =>
        surrogate is { Upper: 0, Lower: 0 } ? default : new ObjectLedgerStoreId(new UInt128(surrogate.Upper, surrogate.Lower));

    /// <inheritdoc/>
    public ObjectLedgerStoreIdSurrogate ConvertToSurrogate(in ObjectLedgerStoreId value) =>
        value == default ? default : new() { Upper = (ulong)(value.Value >> 64), Lower = unchecked((ulong)value.Value) };
}

