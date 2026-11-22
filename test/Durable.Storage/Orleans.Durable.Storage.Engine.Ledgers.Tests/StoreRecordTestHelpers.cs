using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers.Store;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests;

/// <summary>
/// Encodes and decodes store records as the LedgerStore does, for tests which compare their bytes and reasons.
/// </summary>
internal static class StoreRecordTestHelpers
{
    /// <summary>
    /// Returns a store record's body as its write request encodes it.
    /// </summary>
    internal static byte[] Body(this StoreRecord record)
    {
        using var writer = new ArcBufferWriter(record.BodyLength);
        record.Encode(writer);
        using var body = writer.ConsumeSlice(writer.Length);
        return body.ToArray();
    }

    /// <summary>
    /// Decodes a store record, returning it when it keeps every rule, and otherwise <see langword="null"/> with the
    /// reason code of the rule it breaks; a record not keyed by the store handle is no store record, with no reason.
    /// </summary>
    internal static StoreRecord? Decode(ArcBuffer payload, out string? reason)
    {
        reason = null;
        if (LedgerRecords.Classify(payload, LedgerRecords.StoreHandle, out _, out _) != RecordClass.Store)
        {
            return null;
        }

        var decoding = LedgerRecords.DecodeStore(payload);
        reason = decoding.Value is InvalidDataException rejection ? DecodeRejection.ReasonOf(rejection) : null;
        return decoding.Value as StoreRecord;
    }
}
