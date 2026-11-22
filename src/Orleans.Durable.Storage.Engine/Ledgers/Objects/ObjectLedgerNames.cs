using System.Globalization;
using Orleans.Durable.Storage.Engine.ObjectStore;

namespace Orleans.Durable.Storage.Engine.Ledgers.Objects;

/// <summary>
/// The names of an object store's ledgers (object-ledgers.md §7), relative to the store, which lies under the cluster's
/// store prefix: <c>ledgers/store</c>, and under <c>ledgers/l/{id:020}/</c> each ledger's <c>sealed</c> and
/// <c>dropped</c> markers, its per-slot objects <c>s/{rev}</c>, and its append-layout extents <c>x/{rev}</c>, where
/// <c>{rev}</c>, a slot's reverse key, is <c>(2^63 − 2) − slot</c> as 20 decimal digits, so a listing in ordinal order
/// of name returns the newest slots or extents first.
/// </summary>
internal static class ObjectLedgerNames
{
    /// <summary>The prefix of every ledger's objects, which the sweeper lists whole.</summary>
    public const string LedgersPrefix = "ledgers/l/";

    /// <summary>The greatest slot a ledger writes, <c>2^63 − 2</c>, whose reverse key is all zeros.</summary>
    public const long MaxSlot = long.MaxValue - 1;

    /// <summary>The digits of a ledger ID and of a reverse key.</summary>
    public const int DigitCount = 20;

    private const string SealedName = "sealed";
    private const string DroppedName = "dropped";
    private const string SlotsName = "s/";
    private const string ExtentsName = "x/";

    /// <summary>Gets the store's record's name.</summary>
    public static ObjectName Store { get; } = new("ledgers/store");

    /// <summary>Returns the prefix of the ledger's objects, <c>ledgers/l/{id:020}/</c>.</summary>
    public static string Ledger(LedgerId id) =>
        string.Create(CultureInfo.InvariantCulture, $"{LedgersPrefix}{id.Value:D20}/");

    /// <summary>Returns the name of the ledger's <c>sealed</c> marker, which a recovery creates (OL-I4).</summary>
    public static ObjectName Sealed(LedgerId id) => new(Ledger(id) + SealedName);

    /// <summary>Returns the name of the ledger's <c>dropped</c> marker, which the sweeper creates (OL-I4).</summary>
    public static ObjectName Dropped(LedgerId id) => new(Ledger(id) + DroppedName);

    /// <summary>Returns the prefix of the ledger's per-slot objects, <c>ledgers/l/{id:020}/s/</c>.</summary>
    public static string Slots(LedgerId id) => Ledger(id) + SlotsName;

    /// <summary>Returns the name of a slot's object, <c>ledgers/l/{id:020}/s/{rev}</c>.</summary>
    public static ObjectName Slot(LedgerId id, Slot slot) => new(Slots(id) + ReverseKey(slot));

    /// <summary>Returns the prefix of the ledger's append-layout extents, <c>ledgers/l/{id:020}/x/</c>.</summary>
    public static string Extents(LedgerId id) => Ledger(id) + ExtentsName;

    /// <summary>Returns the name of an extent's object, <c>ledgers/l/{id:020}/x/{rev}</c>, for its first slot.</summary>
    public static ObjectName Extent(LedgerId id, Slot firstSlot) => new(Extents(id) + ReverseKey(firstSlot));

    /// <summary>Returns a slot's reverse key, <c>(2^63 − 2) − slot</c> as 20 decimal digits.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The slot is above <see cref="MaxSlot"/>.</exception>
    public static string ReverseKey(Slot slot)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(slot.Value, MaxSlot);
        return (MaxSlot - slot.Value).ToString("D20", CultureInfo.InvariantCulture);
    }

    /// <summary>Parses a reverse key: exactly 20 decimal digits, at most <see cref="MaxSlot"/>.</summary>
    /// <returns>The slot, or <see langword="null"/> when <paramref name="key"/> is not a reverse key.</returns>
    public static Slot? ParseReverseKey(ReadOnlySpan<char> key) =>
        key.Length == DigitCount
            && !key.ContainsAnyExceptInRange('0', '9')
            && long.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var reverse)
            && reverse <= MaxSlot
                ? new Slot(MaxSlot - reverse)
                : null;

    /// <summary>
    /// Parses a slot object's name in the ledger's listing of <see cref="Slots"/>: the prefix and a reverse key.
    /// </summary>
    /// <returns>The slot, or <see langword="null"/> when <paramref name="name"/> is not one of the ledger's slots.</returns>
    public static Slot? ParseSlot(LedgerId id, ObjectName name) => ParseSlot(Slots(id), name);

    /// <summary>
    /// Parses a slot object's name under <paramref name="slotsPrefix"/>, a ledger's <see cref="Slots"/>, without allocating.
    /// </summary>
    /// <returns>The slot, or <see langword="null"/> when <paramref name="name"/> is not one of the ledger's slots.</returns>
    public static Slot? ParseSlot(string slotsPrefix, ObjectName name)
    {
        ArgumentNullException.ThrowIfNull(slotsPrefix);
        ArgumentNullException.ThrowIfNull(name);
        return name.Value.StartsWith(slotsPrefix, StringComparison.Ordinal) ? ParseReverseKey(name.Value.AsSpan(slotsPrefix.Length)) : null;
    }

    /// <summary>
    /// Parses an extent object's name in the ledger's listing of <see cref="Extents"/>: the prefix and a reverse key.
    /// </summary>
    /// <returns>The extent's first slot, or <see langword="null"/> when <paramref name="name"/> is not one of the ledger's extents.</returns>
    public static Slot? ParseExtent(LedgerId id, ObjectName name) => ParseExtent(Extents(id), name);

    /// <summary>
    /// Parses an extent object's name under <paramref name="extentsPrefix"/>, a ledger's <see cref="Extents"/>, without allocating.
    /// </summary>
    /// <returns>The extent's first slot, or <see langword="null"/> when <paramref name="name"/> is not one of the ledger's extents.</returns>
    public static Slot? ParseExtent(string extentsPrefix, ObjectName name)
    {
        ArgumentNullException.ThrowIfNull(extentsPrefix);
        ArgumentNullException.ThrowIfNull(name);
        return name.Value.StartsWith(extentsPrefix, StringComparison.Ordinal) ? ParseReverseKey(name.Value.AsSpan(extentsPrefix.Length)) : null;
    }

    /// <summary>
    /// Parses an extent's first slot from a name under a ledger's prefix, as <see cref="ParseLedger"/> found it.
    /// </summary>
    /// <returns>The extent's first slot, or <see langword="null"/> when <paramref name="name"/> is not an extent.</returns>
    public static Slot? ParseExtentFromLedgerObject(ObjectName name)
    {
        var part = Object(name);
        return part.StartsWith(ExtentsName, StringComparison.Ordinal) ? ParseReverseKey(part[ExtentsName.Length..]) : null;
    }

    /// <summary>
    /// Returns a listing start after every name of the ledger and before the next ledger's: its prefix with its final
    /// <c>/</c> replaced by <c>0</c>, which sorts after <c>/</c>.
    /// </summary>
    public static string AfterLedger(LedgerId id) =>
        string.Create(CultureInfo.InvariantCulture, $"{LedgersPrefix}{id.Value:D20}0");

    /// <summary>
    /// Parses the ledger ID of a name under <see cref="LedgersPrefix"/>: 20 decimal digits, not zero, then <c>/</c>.
    /// </summary>
    /// <returns>The ledger's ID, or <see langword="null"/> when <paramref name="name"/> lies under no ledger's prefix.</returns>
    public static LedgerId? ParseLedger(ObjectName name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var value = name.Value.AsSpan();
        if (!value.StartsWith(LedgersPrefix, StringComparison.Ordinal) || value.Length <= LedgersPrefix.Length + DigitCount)
        {
            return null;
        }

        var digits = value.Slice(LedgersPrefix.Length, DigitCount);
        return value[LedgersPrefix.Length + DigitCount] == '/'
            && !digits.ContainsAnyExceptInRange('0', '9')
            && ulong.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            && id != 0
                ? new LedgerId(id)
                : null;
    }

    /// <summary>
    /// Returns whether a name under a ledger's prefix, as <see cref="ParseLedger"/> found it, is one of the ledger's
    /// markers, which a sweep keeps (OL-I8).
    /// </summary>
    public static bool IsMarker(ObjectName name) => IsDropped(name) || Object(name).SequenceEqual(SealedName);

    /// <summary>Returns whether a name under a ledger's prefix, as <see cref="ParseLedger"/> found it, is its <c>dropped</c> marker.</summary>
    public static bool IsDropped(ObjectName name) => Object(name).SequenceEqual(DroppedName);

    /// <summary>Returns the part of a ledger's object name after the ledger's prefix.</summary>
    private static ReadOnlySpan<char> Object(ObjectName name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var start = LedgersPrefix.Length + DigitCount + 1;
        return name.Value.Length > start ? name.Value.AsSpan(start) : [];
    }
}
