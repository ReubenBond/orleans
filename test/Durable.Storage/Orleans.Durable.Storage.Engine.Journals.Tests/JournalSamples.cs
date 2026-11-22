using System.Buffers.Binary;
using System.Text;
using CsCheck;
using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Archive.Memory;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Engine.Journals.Tests;

/// <summary>Entries, versions, rows, and generators the Journals tests share.</summary>
internal static class JournalSamples
{
    /// <summary>The row key of the journal the samples write by default.</summary>
    public static JournalRowKey Key { get; } = JournalRowKey.Encode("journal");

    /// <summary>
    /// Gets a memory store with no limits whose pure members plan tokens for folds; it holds no key, so it holds no
    /// pages.
    /// </summary>
    public static InMemoryArchiveStore Store { get; } = new();

    /// <summary>Gets a generator of versions: <c>Origin</c>'s base or a base of up to 32 bytes, and any count.</summary>
    public static Gen<JournalVersion> Versions { get; } = Gen.Select(
        Gen.OneOf(Gen.Const(Array.Empty<byte>()), Gen.Byte.Array[0, RowToken.MaxLength]),
        Gen.OneOf(Gen.ULong[0, 300], Gen.ULong)).Select(static (bytes, count) => new JournalVersion(new RowToken(bytes), count));

    /// <summary>Gets a generator of an entry's fields, of every kind.</summary>
    public static Gen<EntrySample> Entries { get; } = Gen.OneOf(
        Gen.Select(Versions, Gen.Byte.Array[1, 64]).Select(static (version, payload) => new EntrySample(JournalEntryKind.Append, version, payload)),
        Gen.Select(Versions, Gen.Byte.Array[0, 64]).Select(static (version, payload) => new EntrySample(JournalEntryKind.Replace, version, payload)),
        Gen.Select(Versions, Gen.Long[0, JournalEntry.MaxTicks]).Select(static (version, ticks) => new EntrySample(JournalEntryKind.Delete, version, [], ticks)),
        Versions.Select(static version => new EntrySample(JournalEntryKind.Expired, version, [])));

    /// <summary>Gets a generator of journal keys, short and long, ASCII and not.</summary>
    public static Gen<JournalRowKey> Keys { get; } = Gen.OneOf(
        Gen.Const("journal"),
        Gen.String[Gen.Char['a', 'z'], 1, 32],
        Gen.OneOfConst("日本語のキー", "naïve café", "\U0001F600"),
        Gen.String[Gen.Char['a', 'z'], 300, 600]).Select(JournalRowKey.Encode);

    /// <summary>A deletion time the samples stamp by default: the scripted ledger's <see cref="ScriptedLedger.Epoch"/>.</summary>
    public static long DefaultTicks => ScriptedLedger.Epoch.UtcTicks;

    /// <summary>Returns the version of <c>Origin</c>'s base at <paramref name="count"/>.</summary>
    public static JournalVersion V(ulong count) => new(default, count);

    /// <summary>Returns the version of a one-byte base <paramref name="baseByte"/> at <paramref name="count"/>.</summary>
    public static JournalVersion V(byte baseByte, ulong count) => new(new RowToken([baseByte]), count);

    /// <summary>Returns an entry's fields as a row applies them, with no record head.</summary>
    public static JournalEntry Entry(JournalEntryKind kind, JournalVersion expected, long deletionTicks = 0, long payloadLength = 0) =>
        new(kind, expected, deletionTicks, headLength: 0, payloadLength);

    /// <summary>Returns a tag of ledger 1 at <paramref name="slot"/> and <paramref name="index"/>.</summary>
    public static Tag At(long slot, int index = 0) => new(new LedgerId(1), new Slot(slot), index);

    /// <summary>Returns an <c>Append</c> of <paramref name="text"/>'s UTF-8 at <paramref name="expected"/>.</summary>
    public static JournalEntryRecord Append(JournalVersion expected, string text = "x", JournalRowKey? key = null)
    {
        using var payload = ArcBuffer.Wrap(Encoding.UTF8.GetBytes(text));
        return JournalEntryRecord.Append(key ?? Key, expected, payload);
    }

    /// <summary>Returns a <c>Replace</c> of <paramref name="text"/>'s UTF-8 at <paramref name="expected"/>.</summary>
    public static JournalEntryRecord Replace(JournalVersion expected, string text = "snapshot", JournalRowKey? key = null)
    {
        using var payload = ArcBuffer.Wrap(Encoding.UTF8.GetBytes(text));
        return JournalEntryRecord.Replace(key ?? Key, expected, payload);
    }

    /// <summary>Returns a <c>Delete</c> at <paramref name="expected"/>, stamped at tick <paramref name="ticks"/>, by default <see cref="DefaultTicks"/>.</summary>
    public static JournalEntryRecord Delete(JournalVersion expected, long? ticks = null, JournalRowKey? key = null) =>
        JournalEntryRecord.Delete(key ?? Key, expected, new DateTimeOffset(ticks ?? DefaultTicks, TimeSpan.Zero));

    /// <summary>Returns an <c>Expired</c> of the deleted state at <paramref name="expected"/>.</summary>
    public static JournalEntryRecord Expired(JournalVersion expected, JournalRowKey? key = null) => JournalEntryRecord.Expired(key ?? Key, expected);

    /// <summary>Returns the tombstone of a deletion at <paramref name="ticks"/>: its 8 bytes of UTC ticks.</summary>
    public static byte[] Tombstone(long ticks)
    {
        var bytes = new byte[JournalEntry.TombstoneLength];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, ticks);
        return bytes;
    }

    /// <summary>Returns the bytes of <paramref name="record"/> as lowercase hex.</summary>
    public static string Hex(JournalEntryRecord record)
    {
        using var bytes = record.ToArcBuffer();
        return Convert.ToHexStringLower(bytes.ToArray());
    }

    /// <summary>Returns the bytes of <paramref name="record"/>.</summary>
    public static byte[] BytesOf(JournalEntryRecord record)
    {
        using var bytes = record.ToArcBuffer();
        return bytes.ToArray();
    }

    /// <summary>
    /// Returns a live row not held, in <paramref name="segment"/> or by default its ledger's segment from slot 0, which
    /// owns a copy of <paramref name="body"/>; the caller disposes it.
    /// </summary>
    public static ArchiveRow Live(Tag tag, RowKind kind, JournalVersion version, byte[] body, RowToken token = default, LedgerSegmentId? segment = null)
    {
        using var buffer = body.Length == 0 ? default : ArcBuffer.Wrap(body);
        return ArchiveRow.Live(tag, kind, version.ToRowVersion(), segment ?? new LedgerSegmentId(tag.Ledger, Slot.Zero), token, buffer);
    }

    /// <summary>
    /// Returns the load of a key's prefix and live rows as a history read gives them (journals.md §8.3), over
    /// <paramref name="store"/> or the samples' <see cref="Store"/>: the token after their last write is <c>Plan</c> of
    /// the newest segment's write when no row of it is held, and otherwise the prefix's, which a read whose prefix holds
    /// the newest write whole gives.
    /// </summary>
    public static JournalLoad Load(ArchivePrefix? prefix, ReadOnlySpan<ArchiveRow> rows, IArchiveStore? store = null)
    {
        store ??= Store;
        var start = rows.Length;
        var held = false;
        while (start > 0 && rows[start - 1].Segment == rows[^1].Segment)
        {
            held |= rows[--start].IsHeld;
        }

        var nextToken = rows.IsEmpty || held ? prefix?.Token ?? default : store.Plan(ArchiveWrite.OfRows(rows[start..]));
        return new JournalLoad(JournalState.Fold(prefix, rows), JournalPlanning.Load(store, rows, nextToken));
    }

    /// <summary>Disposes every row.</summary>
    public static void Release(IEnumerable<ArchiveRow> rows)
    {
        foreach (var row in rows)
        {
            row.Dispose();
        }
    }
}

/// <summary>An entry's fields, which <see cref="Encode"/> writes as a record.</summary>
internal sealed record EntrySample(JournalEntryKind Kind, JournalVersion Version, byte[] Payload, long Ticks = 0)
{
    /// <summary>Encodes the fields as a record of <paramref name="key"/>, which the caller disposes.</summary>
    public JournalEntryRecord Encode(JournalRowKey key)
    {
        switch (Kind)
        {
            case JournalEntryKind.Delete:
                return JournalEntryRecord.Delete(key, Version, new DateTimeOffset(Ticks, TimeSpan.Zero));
            case JournalEntryKind.Expired:
                return JournalEntryRecord.Expired(key, Version);

            case JournalEntryKind.Append or JournalEntryKind.Replace:
                using (var payload = Payload.Length == 0 ? default : ArcBuffer.Wrap(Payload))
                {
                    return Kind == JournalEntryKind.Append ? JournalEntryRecord.Append(key, Version, payload) : JournalEntryRecord.Replace(key, Version, payload);
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(Kind), Kind, "Undefined kind.");
        }
    }

    /// <summary>Asserts that <paramref name="entry"/> holds these fields.</summary>
    public void AssertDecoded(JournalEntry entry)
    {
        Assert.Equal(Kind, entry.Kind);
        Assert.Equal(Version, entry.ExpectedVersion);
        Assert.Equal(Kind == JournalEntryKind.Delete ? Ticks : 0, entry.DeletionTicks);
        Assert.Equal(Payload.Length, entry.PayloadLength);
    }
}
