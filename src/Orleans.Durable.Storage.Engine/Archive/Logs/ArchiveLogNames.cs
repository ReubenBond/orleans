using System.Globalization;
using System.IO.Hashing;
using System.Text;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Engine.Archive.Logs;

/// <summary>Maps a key to the name of its archive log; the mapping is injective (LOG-I5).</summary>
/// <param name="key">The key, 1 to 65,535 bytes.</param>
/// <returns>
/// The archive log's name, non-empty and outside <see cref="ArchiveLogNames.ReservedPrefix"/>; or <see langword="null"/>
/// for a key the mapping names no log for, such as one the composition never writes, which the store does not admit.
/// </returns>
public delegate string? ArchiveLogNaming(ReadOnlySpan<byte> key);

/// <summary>
/// The names of an archive-log store (archive-logs.md §2.1, §7.1): each key's log, under an injective mapping the
/// composition chooses, with the key's xxHash64, and durable storage's own objects under the reserved prefix <c>durable-storage/</c>, which no
/// archive log is named under (LOG-I5). The mapping carries a version, its <see cref="Scheme"/>, which the store's record
/// records, so a process whose mapping changed is refused rather than orphaning every log (§7.5).
/// </summary>
public sealed class ArchiveLogNames
{
    /// <summary>The prefix under which only durable storage's own objects lie.</summary>
    public const string ReservedPrefix = "durable-storage/";

    /// <summary>The store's record (archive.md §2.4), created once.</summary>
    public const string StoreRecord = "durable-storage/store";

    /// <summary>The prefix of the drain markers, <c>durable-storage/d/{S:D20}/{start:D20}</c> (archive-logs.md §7.5).</summary>
    public const string DrainMarkerPrefix = "durable-storage/d/";

    // A ledger ID and a slot in a marker's name, and a marker's content, are 20 decimal digits, so names sort as numbers.
    private const int MarkerDigits = 20;

    private readonly ArchiveLogNaming _naming;

    /// <summary>Initializes a new instance of the <see cref="ArchiveLogNames"/> class over an injective mapping.</summary>
    /// <param name="scheme">The mapping's version, of <c>[a-z0-9-]</c>, which a change to the mapping changes.</param>
    /// <param name="naming">The mapping, which names nothing under <see cref="ReservedPrefix"/>.</param>
    /// <exception cref="ArgumentException">The scheme is empty or not of <c>[a-z0-9-]</c>.</exception>
    public ArchiveLogNames(string scheme, ArchiveLogNaming naming)
    {
        ArgumentException.ThrowIfNullOrEmpty(scheme);
        ArgumentNullException.ThrowIfNull(naming);
        if (scheme.AsSpan().ContainsAnyExcept("abcdefghijklmnopqrstuvwxyz0123456789-"))
        {
            throw new ArgumentException($"A naming scheme is of [a-z0-9-], not '{scheme}'.", nameof(scheme));
        }

        Scheme = scheme;
        _naming = naming;
    }

    /// <summary>Gets the default mapping, scheme <c>hex-1</c>: <c>k/</c> followed by the key in lowercase hexadecimal.</summary>
    public static ArchiveLogNames Hexadecimal { get; } = new("hex-1", static key => "k/" + Convert.ToHexStringLower(key));

    /// <summary>Gets the mapping's version, which the store's record records as <c>name-scheme</c>.</summary>
    public string Scheme { get; }

    /// <summary>Returns the prefix of <paramref name="ledger"/>'s drain markers, <c>durable-storage/d/{S:D20}/</c>.</summary>
    public static string DrainMarkersOf(LedgerId ledger) =>
        string.Create(CultureInfo.InvariantCulture, $"{DrainMarkerPrefix}{ledger.Value:D20}/");

    /// <summary>Returns the name of the marker of <paramref name="ledger"/>'s segment which starts at <paramref name="start"/>.</summary>
    public static string DrainMarker(LedgerId ledger, Slot start) =>
        string.Create(CultureInfo.InvariantCulture, $"{DrainMarkerPrefix}{ledger.Value:D20}/{start.Value:D20}");

    /// <summary>
    /// Parses a drain marker's name strictly, as <see cref="DrainMarker"/> forms it: its ledger and its segment's start; or
    /// <see langword="null"/> for any other name.
    /// </summary>
    public static (LedgerId Ledger, Slot Start)? ParseDrainMarker(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var rest = name.AsSpan();
        if (!rest.StartsWith(DrainMarkerPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        rest = rest[DrainMarkerPrefix.Length..];
        return rest.Length == 2 * MarkerDigits + 1 && rest[MarkerDigits] == '/'
            && ParseDigits(rest[..MarkerDigits]) is { } ledger and > 0
            && ParseDigits(rest[(MarkerDigits + 1)..]) is { } start and <= long.MaxValue
            ? (new LedgerId(ledger), new Slot((long)start))
            : null;
    }

    /// <summary>Returns a drain marker's content: the slot after its segment's last, in 20 decimal digits.</summary>
    public static byte[] DrainMarkerContent(Slot end) =>
        Encoding.ASCII.GetBytes(end.Value.ToString("D20", CultureInfo.InvariantCulture));

    /// <summary>Parses a drain marker's content strictly: the slot after its segment's last; or <see langword="null"/>.</summary>
    public static Slot? ParseDrainMarkerContent(ReadOnlySpan<byte> content)
    {
        Span<char> digits = stackalloc char[MarkerDigits];
        if (content.Length != MarkerDigits)
        {
            return null;
        }

        for (var index = 0; index < MarkerDigits; index++)
        {
            digits[index] = (char)content[index];
        }

        return ParseDigits(digits) is { } end and <= long.MaxValue ? new Slot((long)end) : null;
    }

    /// <summary>Returns the xxHash64, seed 0, of <paramref name="key"/>, which every write of its log records and every read checks.</summary>
    public static ulong KeyHash(ReadOnlySpan<byte> key) => XxHash64.HashToUInt64(key);

    /// <summary>Returns the name of <paramref name="key"/>'s log, with the key's hash.</summary>
    /// <exception cref="ArgumentException"><paramref name="key"/> is not a key, or the mapping names no log for it.</exception>
    /// <exception cref="InvalidOperationException">The mapping named the key under <see cref="ReservedPrefix"/>, a defect of the mapping.</exception>
    public ArchiveLogName Of(ReadOnlySpan<byte> key) =>
        TryOf(key) ?? throw new ArgumentException($"The naming scheme {Scheme} names no archive log for the key.", nameof(key));

    /// <summary>
    /// Returns the name of <paramref name="key"/>'s log, with the key's hash; or <see langword="null"/> when the mapping
    /// names no log for the key, which the store then does not admit.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="key"/> is not a key.</exception>
    /// <exception cref="InvalidOperationException">The mapping named the key under <see cref="ReservedPrefix"/>, a defect of the mapping.</exception>
    public ArchiveLogName? TryOf(ReadOnlySpan<byte> key)
    {
        KeyRange.CheckKey(key);
        if (_naming(key) is not { } name)
        {
            return null;
        }

        if (name.Length == 0 || name.StartsWith(ReservedPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"An archive log's name is not empty and lies outside {ReservedPrefix}, which holds durable storage's own objects; the mapping gave '{name}'.");
        }

        return new ArchiveLogName(name, KeyHash(key));
    }

    /// <summary>Parses exactly 20 decimal digits, or answers <see langword="null"/>.</summary>
    private static ulong? ParseDigits(ReadOnlySpan<char> digits) =>
        digits.Length == MarkerDigits && !digits.ContainsAnyExceptInRange('0', '9')
            && ulong.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
}
