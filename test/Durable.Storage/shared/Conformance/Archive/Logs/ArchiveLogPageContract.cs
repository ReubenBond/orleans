using System.Buffers;
using System.Globalization;
using Orleans.Durable.Storage.Engine.Archive.Logs;

namespace Orleans.Durable.Storage.Testing;

/// <summary>
/// The page contract of <see cref="IArchiveLogStore.ListGenerationsAsync"/> (archive-logs.md §3), which a store gives
/// whatever its page size, checked over a store whose pages hold two bases: an empty store lists one empty page; the pages,
/// over bases of nested log names, concatenate to every base in the ordinal order of <c>{log}/{g:D20}</c>; only the last
/// page's continuation is null; a listing from each page's continuation returns exactly the pages after it; and a
/// continuation stays valid after the bases about its position are deleted and others are created before and after it,
/// a listing from it returning the names after its position.
/// </summary>
internal static class ArchiveLogPageContract
{
    private static readonly ArchiveLogOpener Opener = new(ArchiveLogOpenerKind.Replace, null, 1);

    /// <summary>Checks the contract over <paramref name="store"/>, empty, whose pages hold two bases.</summary>
    public static async Task CheckAsync(IArchiveLogStore store, CancellationToken cancellationToken)
    {
        var empty = await PagesAsync(store, null, cancellationToken);
        var only = Assert.Single(empty);
        Assert.Empty(only.Generations);
        Assert.Null(only.Continuation);

        var bases = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (log, generations) in new (string, int)[] { ("k/a", 3), ("k/a/b", 1), ("k/a-c", 1), ("k/b", 1) })
        {
            await OpenAsync(store, log, generations, bases, cancellationToken);
        }

        var pages = await PagesAsync(store, null, cancellationToken);
        Assert.Equal(bases, pages.SelectMany(static page => page.Generations).Select(Name));
        Assert.Equal([3, 2, 2, 2], [pages.Count, .. pages.Select(static page => page.Generations.Count)]);
        Assert.All(pages.SkipLast(1), static page => Assert.NotNull(page.Continuation));
        Assert.Null(pages[^1].Continuation);
        for (var index = 0; index < pages.Count - 1; index++)
        {
            var resumed = await PagesAsync(store, pages[index].Continuation, cancellationToken);
            Assert.Equal(pages.Skip(index + 1).Select(static page => string.Join(",", page.Generations.Select(Name))), resumed.Select(static page => string.Join(",", page.Generations.Select(Name))));
        }

        // The superseded bases about the first continuation's position go, a base before it and one after it come; the
        // continuation still lists the names after its position.
        var continuation = pages[0].Continuation;
        var (last, next) = (pages[0].Generations[^1], pages[1].Generations[0]);
        await store.DeleteGenerationAsync(last.Log, last.Generation, cancellationToken);
        await store.DeleteGenerationAsync(next.Log, next.Generation, cancellationToken);
        _ = bases.Remove(Name(last));
        _ = bases.Remove(Name(next));
        await OpenAsync(store, "k/a-b", 1, bases, cancellationToken);
        await OpenAsync(store, "k/c", 1, bases, cancellationToken);
        var after = await PagesAsync(store, continuation, cancellationToken);
        Assert.Equal(bases.Where(name => string.CompareOrdinal(name, Name(last)) > 0), after.SelectMany(static page => page.Generations).Select(Name));
    }

    private static string Name(ArchiveLogGeneration generation) => string.Create(CultureInfo.InvariantCulture, $"{generation.Log}/{generation.Generation:D20}");

    /// <summary>Creates <paramref name="generations"/> generations of the log <paramref name="log"/>, each with its base, and records their names.</summary>
    private static async Task OpenAsync(IArchiveLogStore store, string log, int generations, SortedSet<string> bases, CancellationToken cancellationToken)
    {
        var name = new ArchiveLogName(log, BitConverter.ToUInt64(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(log))));
        for (var generation = 0UL; generation < (ulong)generations; generation++)
        {
            var from = generation == 0 ? ArchiveLogPosition.Origin : new ArchiveLogPosition(generation, 1, 1, 0);
            _ = Assert.IsType<ArchiveLogWriteOutcome.Applied>((await store.OpenAsync(name, from, Opener, new ReadOnlySequence<byte>([1]), cancellationToken)).Value);
            _ = bases.Add(string.Create(CultureInfo.InvariantCulture, $"{log}/{generation + 1:D20}"));
        }
    }

    private static async Task<List<ArchiveLogGenerationPage>> PagesAsync(IArchiveLogStore store, string? continuation, CancellationToken cancellationToken)
    {
        var pages = new List<ArchiveLogGenerationPage>();
        await foreach (var page in store.ListGenerationsAsync(continuation, cancellationToken))
        {
            pages.Add(page);
        }

        return pages;
    }
}
