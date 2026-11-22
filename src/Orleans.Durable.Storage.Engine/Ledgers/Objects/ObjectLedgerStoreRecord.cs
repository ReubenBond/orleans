using System.Globalization;
using System.Text;

namespace Orleans.Durable.Storage.Engine.Ledgers.Objects;

/// <summary>What decoding an object-ledger store's record gives (conventions.md §15): the record, or its rejection.</summary>
public readonly union ObjectLedgerStoreRecordDecoding(ObjectLedgerStoreRecord, InvalidDataException);

/// <summary>
/// An object-ledger store's record, <c>ledgers/store</c> (object-ledgers.md §7, OL-I1), in the archive store record's
/// form restricted to its two fields: exactly two ASCII lines separated by one line feed, with no terminator,
/// <c>id={32 lowercase hexadecimal digits, not all zero}</c> and <c>layout={per-slot|append}</c>. The store's first user
/// creates it once, and every later user reads it.
/// </summary>
public sealed record ObjectLedgerStoreRecord
{
    internal const string ReasonBadLine = "bad-line";
    internal const string ReasonBadId = "bad-id";
    internal const string ReasonBadLayout = "bad-layout";

    private const string IdPrefix = "id=";
    private const string LayoutPrefix = "layout=";
    private const string PerSlotName = "per-slot";
    private const string AppendName = "append";

    /// <summary>Initializes a new instance of the <see cref="ObjectLedgerStoreRecord"/> class.</summary>
    /// <param name="id">The store's random non-zero 128-bit ID.</param>
    /// <param name="layout">The store's layout.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="id"/> is default, or <paramref name="layout"/> is not a layout.</exception>
    public ObjectLedgerStoreRecord(ObjectLedgerStoreId id, ObjectLedgerLayout layout)
    {
        if (id == default)
        {
            throw new ArgumentOutOfRangeException(nameof(id), "The store's ID cannot be the default value.");
        }

        _ = LayoutName(layout);
        Id = id;
        Layout = layout;
    }

    /// <summary>Gets the store's random non-zero 128-bit ID.</summary>
    public ObjectLedgerStoreId Id { get; }

    /// <summary>Gets the store's layout.</summary>
    public ObjectLedgerLayout Layout { get; }

    /// <summary>Returns a new record with a random ID from the platform's random number generator.</summary>
    /// <param name="layout">The store's layout.</param>
    public static ObjectLedgerStoreRecord NewRandom(ObjectLedgerLayout layout) =>
        new(ObjectLedgerStoreId.NewRandom(), layout);

    /// <summary>Returns a layout's name in the record, <c>per-slot</c> or <c>append</c>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="layout"/> is not a layout.</exception>
    public static string LayoutName(ObjectLedgerLayout layout) => layout switch
    {
        ObjectLedgerLayout.PerSlot => PerSlotName,
        ObjectLedgerLayout.Append => AppendName,
        _ => throw new ArgumentOutOfRangeException(nameof(layout), layout, "Not an object-ledger layout."),
    };

    /// <summary>
    /// Decodes a record, checking in order that it is two lines separated by one line feed (<c>bad-line</c>); that the
    /// first is <c>id=</c> and 32 lowercase hexadecimal digits, not all zero (<c>bad-id</c>); and that the second is
    /// <c>layout=per-slot</c> or <c>layout=append</c> (<c>bad-layout</c>).
    /// </summary>
    /// <returns>The record, or the rejection naming the first rule the bytes break.</returns>
    public static ObjectLedgerStoreRecordDecoding Decode(ReadOnlySpan<byte> bytes)
    {
        var feed = bytes.IndexOf((byte)'\n');
        if (feed < 0 || bytes[(feed + 1)..].Contains((byte)'\n'))
        {
            return DecodeRejection.Create(ReasonBadLine, "The store's record is not two lines separated by one line feed.");
        }

        var idLine = bytes[..feed];
        var digits = idLine.Length == IdPrefix.Length + 32 && idLine.StartsWith("id="u8) ? idLine[IdPrefix.Length..] : [];
        if (digits.IsEmpty || digits.ContainsAnyExcept("0123456789abcdef"u8) || !digits.ContainsAnyExcept((byte)'0'))
        {
            return DecodeRejection.Create(ReasonBadId, "The store's record does not begin with its ID, 32 lowercase hexadecimal digits, not all zero.");
        }

        var layoutLine = bytes[(feed + 1)..];
        ObjectLedgerLayout? layout = layoutLine.SequenceEqual("layout=per-slot"u8)
            ? ObjectLedgerLayout.PerSlot
            : layoutLine.SequenceEqual("layout=append"u8) ? ObjectLedgerLayout.Append : null;
        if (layout is not { } known)
        {
            return DecodeRejection.Create(ReasonBadLayout, "The store's record's second line is not layout=per-slot or layout=append.");
        }

        return new ObjectLedgerStoreRecord(new ObjectLedgerStoreId(UInt128.Parse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture)), known);
    }

    /// <summary>Returns the record's bytes.</summary>
    public byte[] Encode() =>
        Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{IdPrefix}{Id}\n{LayoutPrefix}{LayoutName(Layout)}"));

    /// <summary>Returns the ID as 32 lowercase hexadecimal digits, its stored form, and the layout.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Id} ({LayoutName(Layout)})");
}
