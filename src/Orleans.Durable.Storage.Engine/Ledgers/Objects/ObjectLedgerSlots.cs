using System.Buffers;
using Orleans.Durable.Storage.Engine.ObjectStore;

namespace Orleans.Durable.Storage.Engine.Ledgers.Objects;

/// <summary>What a per-slot object holds (object-ledgers.md §7): a batch, a SEAL, or neither, corruption.</summary>
internal enum ObjectSlotKind
{
    /// <summary>A non-empty object whose bytes match the CRC-32C its create recorded.</summary>
    Batch,

    /// <summary>An empty object: a recovery's fence, which no batch ever replaces (OL-I8).</summary>
    Seal,

    /// <summary>A non-empty object whose CRC-32C is absent or does not match its bytes.</summary>
    Corrupt,
}

/// <summary>How a writer settles a slot's create which met an occupant or lost its answer (OL-I2).</summary>
internal enum ObjectSlotSettlement
{
    /// <summary>The object is absent, so the create may still land: the writer repeats it, byte-identical.</summary>
    Repeat,

    /// <summary>The object holds the writer's batch: its length and CRC-32C.</summary>
    Written,

    /// <summary>The object is a SEAL: a recovery fenced the slot.</summary>
    Sealed,

    /// <summary>The object holds other bytes, which no writer of the slot wrote: corruption.</summary>
    Corrupt,
}

/// <summary>
/// The per-slot layout's classification and settlement rules (object-ledgers.md §8.1, OL-I2), the object profile's
/// revived over <see cref="ObjectProperties"/>, which carry the CRC-32C each provider records.
/// </summary>
internal static class ObjectLedgerSlots
{
    /// <summary>
    /// Classifies a slot's object by its length first, then its bytes against the CRC-32C its create recorded.
    /// </summary>
    /// <param name="properties">The object's properties.</param>
    /// <param name="content">The object's whole content; read only for a non-empty object.</param>
    public static ObjectSlotKind Classify(ObjectProperties properties, ReadOnlySequence<byte> content)
    {
        ArgumentNullException.ThrowIfNull(properties);
        if (properties.Length == 0)
        {
            return ObjectSlotKind.Seal;
        }

        return properties.Crc32c is { } crc32c
            && content.Length == properties.Length
            && ObjectStoreChecksums.Crc32C(content) == crc32c
                ? ObjectSlotKind.Batch
                : ObjectSlotKind.Corrupt;
    }

    /// <summary>
    /// Settles a slot's create from the properties of its object read after it (OL-I2): absent repeats the create, empty is
    /// a SEAL, the writer's own length and CRC-32C its batch, and anything else corruption.
    /// </summary>
    /// <param name="observed">The object's properties, or <see langword="null"/> when it is absent.</param>
    /// <param name="length">The batch's length.</param>
    /// <param name="crc32c">The batch's CRC-32C.</param>
    public static ObjectSlotSettlement Settle(ObjectProperties? observed, long length, uint crc32c) =>
        observed switch
        {
            null => ObjectSlotSettlement.Repeat,
            { Length: 0 } => ObjectSlotSettlement.Sealed,
            { Crc32c: { } recorded } when observed.Length == length && recorded == crc32c => ObjectSlotSettlement.Written,
            _ => ObjectSlotSettlement.Corrupt,
        };
}
