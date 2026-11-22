using Orleans.Durable.Storage.Engine.Buffers;

namespace Orleans.Durable.Storage.Engine.Wal;

public sealed partial class WriteAheadLog
{
    /// <summary>
    /// Finalizes the last open, so that every open except the one about to start is final: no page of it is invalid
    /// unless it was damaged after it became durable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WAL-I7 bounds what a crash leaves incomplete. A data page is submitted only when every page at least W
    /// pages before it is durable, and a file's first data page only when every page of the open's previous
    /// file is durable. In the final file F, the last one of the open holding a valid data page, let L be the
    /// last valid page: the invalid pages within W before L, the pages after L, and every page of a later file
    /// were in flight at the crash, so no acknowledged record has bytes on them. Every other page was durable.
    /// </para>
    /// <para>
    /// Replay replaces each invalid page within W before L with a hole page, truncates F after L, truncates each
    /// later file to its header, and truncates each earlier file after its last non-zero page, keeping its
    /// damaged pages for a scan to report. Each step is idempotent, and the new open's first file is published
    /// only afterwards, so an interrupted replay is repeated with the same result.
    /// </para>
    /// </remarks>
    private static async ValueTask FinalizeLastOpenAsync(List<LogFile> files, CancellationToken cancellationToken)
    {
        var first = Math.Max(0, files.FindLastIndex(static file => file.Header is { FirstOfOpen: true }));

        // A file name is published only with its durable header, so an invalid header is damage, left for the scan.
        var open = files.Skip(first).Where(static file => file.Header is not null).ToArray();
        var ends = new long[open.Length];
        var final = -1;
        for (var index = open.Length - 1; index >= 0; index--)
        {
            ends[index] = await FindLastPageAsync(open[index], requireValid: final < 0, cancellationToken).ConfigureAwait(false);
            if (final < 0 && ends[index] >= 0)
            {
                final = index;
            }
        }

        if (final >= 0)
        {
            await WriteHolePagesAsync(open[final], ends[final], cancellationToken).ConfigureAwait(false);
        }

        for (var index = 0; index < open.Length; index++)
        {
            var length = WalFormat.DataPageOffset(open[index].Header!.Value.PageSize, ends[index] + 1);
            if (open[index].Handle.Length > length)
            {
                await open[index].Handle.TruncateAsync(length, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Reads a file backward from its physical end and returns its last valid data page, or its last non-zero data
    /// page when <paramref name="requireValid"/> is <see langword="false"/>; -1 when it has none.
    /// </summary>
    /// <remarks>
    /// Reading to the physical end finds a valid page even after a long damaged run.
    /// </remarks>
    private static async ValueTask<long> FindLastPageAsync(LogFile file, bool requireValid, CancellationToken cancellationToken)
    {
        var pageSize = file.Header!.Value.PageSize;
        var scratch = new byte[pageSize];
        var perChunk = Math.Max(1, ScanChunkBytes / pageSize);
        for (var end = WalFormat.DataPageCount(file.Handle.Length, pageSize); end > 0;)
        {
            var start = Math.Max(0, end - perChunk);
            using var chunk = await file.Handle
                .ReadAsync(WalFormat.DataPageOffset(pageSize, start), (int)(end - start) * pageSize, cancellationToken)
                .ConfigureAwait(false);
            for (var page = end - 1; page >= start; page--)
            {
                var bytes = GetPage(chunk, (int)(page - start) * pageSize, pageSize, scratch);
                if (requireValid ? bytes.Length == pageSize && WalFormat.IsValidPage(bytes) : bytes.ContainsAnyExcept((byte)0))
                {
                    return page;
                }
            }

            end = start;
        }

        return -1;
    }

    /// <summary>
    /// Replaces each invalid page within W pages before <paramref name="last"/> with a hole page.
    /// </summary>
    private static async ValueTask WriteHolePagesAsync(LogFile file, long last, CancellationToken cancellationToken)
    {
        var (pageSize, window, _) = file.Header!.Value;
        var scratch = new byte[pageSize];
        var perChunk = Math.Max(1, ScanChunkBytes / pageSize);
        var invalid = new List<long>();
        for (var start = Math.Max(0, last - window + 1); start < last;)
        {
            var end = Math.Min(last, start + perChunk);
            using var chunk = await file.Handle
                .ReadAsync(WalFormat.DataPageOffset(pageSize, start), (int)(end - start) * pageSize, cancellationToken)
                .ConfigureAwait(false);
            for (var page = start; page < end; page++)
            {
                if (!TryGetValidPage(chunk, (int)(page - start) * pageSize, pageSize, scratch, out _))
                {
                    invalid.Add(page);
                }
            }

            start = end;
        }

        var hole = new byte[pageSize];
        WalFormat.WriteHolePage(hole);
        for (var index = 0; index < invalid.Count;)
        {
            var runStart = invalid[index];
            var count = 1;
            while (index + count < invalid.Count && invalid[index + count] == runStart + count)
            {
                count++;
            }

            using var writer = ArcBufferWriter.CreateAligned();
            for (var page = 0; page < count; page++)
            {
                writer.Write(hole);
            }

            using var buffer = writer.ConsumeSlice(count * pageSize);
            await file.Handle.WriteAsync(WalFormat.DataPageOffset(pageSize, runStart), new[] { buffer }).ConfigureAwait(false);
            index += count;
        }
    }
}
