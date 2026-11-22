using Orleans.Durable.Storage.Engine.Archive.Logs;
using Orleans.Durable.Storage.Engine.Journals;

namespace Orleans.Durable.Storage;

/// <summary>
/// The Orleans composition's names of its journals' archive logs (orleans.md §5.13): scheme <see cref="Scheme"/>, which
/// names the log of a journal's row key (<see cref="JournalRowKey"/>) by the journal's identity, <c>{id}</c>, so the log
/// is <c>wal/{id}</c> with its checkpoints <c>checkpoints/{id}/{g:D20}</c>, the write-ahead blob Orleans' own
/// <c>AzureBlobJournalStorage</c> reads for that journal. The store's record pins the scheme (archive-logs.md §7.5).
/// </summary>
/// <remarks>
/// The mapping is injective because it is <see cref="JournalRowKey.TryDecode"/>, the row key format's own inverse: it names
/// no log for a key which is no journal's row key, nor for an identity under <see cref="ArchiveLogNames.ReservedPrefix"/>,
/// which durable storage's own objects hold, and the store admits none of them. The set of keys it declines is part of
/// <c>orleans-journal-1</c>, as the names are: a mapping which declines other keys is another scheme.
/// </remarks>
internal static class OrleansJournalLogNames
{
    /// <summary>The scheme's version, which the store's record records as <c>name-scheme</c>.</summary>
    public const string Scheme = "orleans-journal-1";

    /// <summary>Gets the composition's names.</summary>
    public static ArchiveLogNames Names { get; } = new(Scheme, NameOf);

    /// <summary>
    /// Returns the identity of the journal whose row key is <paramref name="key"/>, which names its log; or
    /// <see langword="null"/> when <paramref name="key"/> is no journal's row key, or names a journal under
    /// <see cref="ArchiveLogNames.ReservedPrefix"/>.
    /// </summary>
    public static string? NameOf(ReadOnlySpan<byte> key) =>
        JournalRowKey.TryDecode(key) is { } id && !id.StartsWith(ArchiveLogNames.ReservedPrefix, StringComparison.Ordinal) ? id : null;
}
