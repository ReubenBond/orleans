using System.Buffers.Binary;
using System.Diagnostics;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Wal;
using Orleans.Durable.Storage.Engine.Wal.Simulation;

namespace Orleans.Durable.Storage.Fuzz.Wal;

/// <summary>
/// The WAL's reader over one log file (wal.md §6, §7): the input is the file, a header page and data pages, which an
/// open finalizes and a scan and point reads then read. The format's rules, applied to the bytes independently of the
/// reader, give what replay leaves on disk, every record a scan yields (each its Full frame, or its First fragment
/// and the Continuations which finish it on the following valid pages), and the entries it reports: each maximal run
/// of hole pages as one <see cref="WalTorn"/>, each maximal run of invalid pages as one <see cref="WalCorruption"/>,
/// and each valid page breaking a frame rule as a <see cref="WalCorruption"/> of its own. Point reads return each yielded
/// record at its position, and no record at a First frame whose record the rules abandon (WAL-I2).
/// </summary>
public sealed class WalScanTarget : FuzzTarget
{
    private const string LogPath = "wal";
    private const ulong File = 1;

    public override string Name => "wal-scan";

    public override int MaxLength => 16 * 1024;

    public override IEnumerable<byte[]> Seeds(GoldenVectors vectors)
    {
        ArgumentNullException.ThrowIfNull(vectors);
        return SeedsOf(vectors);
    }

    private static IEnumerable<byte[]> SeedsOf(GoldenVectors vectors)
    {
        // The worked example's header and its record's pages, from the file's first data page.
        var example = vectors.Document("wal", "example.json");
        var pageSize = example.GetProperty("pageSize").GetInt32();
        yield return
        [
            .. GoldenVectors.Expand(GoldenVectors.Hex(example, "headerHex"), pageSize),
            .. example.GetProperty("pages").EnumerateArray().SelectMany(page => GoldenVectors.Expand(GoldenVectors.Hex(page), pageSize)),
        ];

        // Each header, followed by every page vector of its page size, accepted and rejected.
        var pages = vectors.Cases("wal", "pages.json").Concat(vectors.Cases("wal", "page-rejections.json")).ToArray();
        foreach (var header in vectors.Cases("wal", "headers.json"))
        {
            var size = header.GetProperty("pageSize").GetInt32();
            yield return
            [
                .. GoldenVectors.Expand(GoldenVectors.Hex(header), size),
                .. pages.Where(page => page.GetProperty("pageSize").GetInt32() == size).SelectMany(page => GoldenVectors.Expand(GoldenVectors.Hex(page), size)),
            ];
        }

        foreach (var header in vectors.Cases("wal", "header-rejections.json"))
        {
            yield return GoldenVectors.Expand(GoldenVectors.Hex(header), header.GetProperty("pageSize").GetInt32());
        }
    }

    protected override FuzzOutcome Decode(ReadOnlySpan<byte> input)
    {
        var file = input.ToArray();
        var header = Header(file);
        var expected = Replay(file, header);
        var model = header is { } known ? Scan(expected, known.PageSize) : null;

        // The log runs its pump on the thread pool, so the decoding runs there and is measured process-wide.
        var bound = MeasureProcessAllocations(file.Length);
        var (unsupported, replayed, entries, reads, abandonedReads) = Task.Run(() => ScanAsync(file, model?.Abandoned ?? [])).GetAwaiter().GetResult();
        bound.Check("WriteAheadLog.OpenAsync and ReadAllAsync");
        if (unsupported)
        {
            Require(
                file.Length >= WalRules.HeaderFieldsLength && file.AsSpan(WalFormat.HeaderMagicOffset, WalRules.Magic.Length).SequenceEqual(WalRules.Magic)
                    && BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(WalFormat.HeaderVersionOffset)) != WalFormat.Version,
                "failed the open as unsupported for a file without the CLFL magic and another version.");
            return FuzzOutcome.Rejected;
        }

        Require(replayed.AsSpan().SequenceEqual(expected), "left a file which replay's rules do not give.");
        if (header is not { } fields || model is not { } scan)
        {
            Require(
                entries.Count == 1 && entries[0] is { Kind: EntryKind.Corruption } fault
                    && fault.Start == new WalPosition(File, 0) && fault.End == new WalPosition(File, (ulong)replayed.Length),
                "reported a file with an invalid header otherwise than as corruption of the whole file.");
            return FuzzOutcome.Rejected;
        }

        var (records, faults, _) = scan;
        var yielded = entries.Where(static entry => entry.Kind == EntryKind.Record).ToArray();
        Require(
            yielded.Length == records.Count && yielded.Zip(records).All(static pair => pair.First.Start == pair.Second.Position
                && pair.First.Page == pair.Second.Page && pair.First.Payload is { } payload && payload.AsSpan().SequenceEqual(pair.Second.Payload)),
            "yielded records which the frames of the file's valid pages do not hold.");
        for (var index = 0; index < records.Count; index++)
        {
            Require(reads[index] is { } read && read.AsSpan().SequenceEqual(records[index].Payload), "read a record at its position otherwise than a scan yields it.");
        }

        Require(abandonedReads.TrueForAll(static read => read is null), "read a record at a First frame whose record the format's rules do not complete.");
        RequireFaultsReported(entries, faults, fields.PageSize);
        return FuzzOutcome.Accepted;
    }

    /// <summary>
    /// Recomputes the CRC of the header page and of every data page whose CRC field is not zero, so a zero field leaves
    /// a page invalid and the fuzzer chooses which pages are.
    /// </summary>
    protected override byte[]? Seal(ReadOnlySpan<byte> input)
    {
        if (input.Length < WalRules.HeaderFieldsLength)
        {
            return null;
        }

        var pageSize = BinaryPrimitives.ReadUInt32LittleEndian(input[WalFormat.HeaderPageSizeOffset..]);
        if (!WalRules.IsPageSize(pageSize) || pageSize > (uint)input.Length)
        {
            return null;
        }

        var bytes = input.ToArray();
        WalRules.Seal(bytes.AsSpan(0, (int)pageSize));
        for (var start = (int)pageSize; start + pageSize <= bytes.Length; start += (int)pageSize)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(start)) != 0)
            {
                WalRules.Seal(bytes.AsSpan(start, (int)pageSize));
            }
        }

        return bytes;
    }

    /// <summary>
    /// Returns the header a log file starts with, as the reader finds it: the fixed fields name a supported page size,
    /// and the file holds a valid header page of that size.
    /// </summary>
    private static (int PageSize, int Window, bool FirstOfOpen)? Header(byte[] file)
    {
        if (WalRules.NamedPageSize(file.AsSpan(0, Math.Min(file.Length, WalRules.HeaderFieldsLength))) is not { } pageSize
            || pageSize > file.Length)
        {
            return null;
        }

        var verdict = WalRules.Header(file.AsSpan(0, pageSize));
        return verdict.Reason is null ? (verdict.PageSize, verdict.Window, verdict.FirstOfOpen) : null;
    }

    /// <summary>
    /// Returns the file as replay leaves the last open's only file (wal.md §6): with L its last valid data page, each
    /// invalid page within W pages before L becomes a hole page and the file ends after L. A file with an invalid
    /// header is left as it is.
    /// </summary>
    private static byte[] Replay(byte[] file, (int PageSize, int Window, bool FirstOfOpen)? header)
    {
        if (header is not { } fields)
        {
            return file;
        }

        var pageSize = fields.PageSize;
        var pages = Math.Max(0, (file.Length - 1) / pageSize);
        var last = -1;
        for (var page = pages - 1; page >= 0 && last < 0; page--)
        {
            var start = pageSize * (1 + page);
            if (file.Length - start >= pageSize && WalRules.HasValidCrc(file.AsSpan(start, pageSize)))
            {
                last = page;
            }
        }

        var replayed = file[..Math.Min(file.Length, pageSize * (last + 2))];
        for (var page = Math.Max(0, last - fields.Window + 1); page < last; page++)
        {
            var bytes = replayed.AsSpan(pageSize * (1 + page), pageSize);
            if (!WalRules.HasValidCrc(bytes))
            {
                bytes.Clear();
                BinaryPrimitives.WriteUInt32LittleEndian(bytes[WalRules.CrcLength..], (uint)(pageSize - WalRules.CrcLength - WalRules.WordLength));
                WalRules.Seal(bytes);
            }
        }

        return replayed;
    }

    /// <summary>
    /// Scans a replayed file's data pages by the frame rules (wal.md §7): each record is a Full frame, or a First
    /// frame followed on each following page by a Continuation of exactly <c>min(P − 8, bytes still required)</c>.
    /// A Continuation leading a page with no record in progress is ill-formed after a settled page, the file's first
    /// page included, and is an orphan fragment, skipped, after a page the scan could not read records from.
    /// </summary>
    /// <returns>The records, the fault of each data page which is reported, and the position of each First frame whose record is abandoned.</returns>
    private static ScanModel Scan(byte[] file, int pageSize)
    {
        var records = new List<Record>();
        var faults = new Dictionary<int, Fault>();
        var abandoned = new List<WalPosition>();
        var perPage = pageSize - WalRules.CrcLength - WalRules.WordLength;
        Pending? pending = null;
        var settled = true;
        for (var page = 0; pageSize * (2L + page) <= file.Length; page++)
        {
            var start = pageSize * (1 + page);
            var verdict = WalRules.Classify(file.AsSpan(start, pageSize));
            if (verdict.Kind != PageKind.Valid)
            {
                faults[page] = verdict.Kind switch
                {
                    PageKind.Hole => Fault.Torn,
                    PageKind.Invalid => Fault.Invalid,
                    PageKind.IllFormed => Fault.Broken,
                    PageKind.Valid or _ => throw new UnreachableException("Only a page which is not valid is a fault."),
                };
                Abandon();
                continue;
            }

            var frames = verdict.Frames;
            var next = 0;
            if (frames is [{ Kind: FrameKind.Continuation } continuation, ..])
            {
                next = 1;
                if (pending is { } record)
                {
                    if (continuation.Bytes.Length != Math.Min(perPage, record.Total - record.Bytes.Count))
                    {
                        faults[page] = Fault.Broken;
                        Abandon();
                        continue;
                    }

                    record.Bytes.AddRange(continuation.Bytes);
                    if (record.Bytes.Count < record.Total)
                    {
                        settled = false;
                        continue;
                    }

                    records.Add(new Record(record.Position, record.Page, [.. record.Bytes]));
                    pending = null;
                }
                else if (settled)
                {
                    faults[page] = Fault.Broken;
                    settled = false;
                    continue;
                }
                else if (continuation.Bytes.Length == perPage)
                {
                    continue;
                }
            }
            else if (pending is not null)
            {
                faults[page] = Fault.Broken;
                Abandon();
                continue;
            }

            foreach (var frame in frames.Skip(next))
            {
                var position = new WalPosition(File, (ulong)(start + frame.Offset));
                var holding = new WalPage(File, (uint)page);
                if (frame.Kind == FrameKind.Full)
                {
                    records.Add(new Record(position, holding, frame.Bytes));
                }
                else
                {
                    pending = new Pending(position, holding, frame.Total, [.. frame.Bytes]);
                }
            }

            settled = pending is null;
        }

        Abandon();
        return new ScanModel(records, faults, abandoned);

        // A record in progress which the next page does not continue, or which the file ends before, is abandoned.
        void Abandon()
        {
            if (pending is { } unfinished)
            {
                abandoned.Add(unfinished.Position);
            }

            (pending, settled) = (null, false);
        }
    }

    /// <summary>
    /// Opens a log whose only file is <paramref name="file"/>, then scans it and reads each record it yields at its
    /// position.
    /// </summary>
    private static async Task<(bool Unsupported, byte[] Replayed, List<Entry> Entries, List<byte[]?> Reads, List<byte[]?> AbandonedReads)> ScanAsync(
        byte[] file, List<WalPosition> abandoned)
    {
        var storage = new SimulatedWalStorage();
        await storage.CreateDirectoryAsync(LogPath, CancellationToken.None).ConfigureAwait(false);
        await using (var directory = await storage.OpenDirectoryAsync(LogPath, CancellationToken.None).ConfigureAwait(false))
        {
            using var writer = new ArcBufferWriter();
            writer.Write(file);
            using var contents = writer.ConsumeSlice(file.Length);
            await (await directory.CreateFileAsync(File, contents, 0, null, CancellationToken.None).ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false);
        }

        var options = new WalOptions
        {
            PageSize = Header(file)?.PageSize ?? 4096,
            PreallocateFiles = false,
        };
        Orleans.Durable.Storage.Engine.Wal.WriteAheadLog log;
        try
        {
            log = await Orleans.Durable.Storage.Engine.Wal.WriteAheadLog.OpenAsync(storage, LogPath, options, CancellationToken.None).ConfigureAwait(false);
        }
        catch (WalUnsupportedFormatException)
        {
            return (true, [], [], [], []);
        }

        var entries = new List<Entry>();
        var reads = new List<byte[]?>();
        var abandonedReads = new List<byte[]?>();
        await using (log.ConfigureAwait(false))
        {
            await foreach (var entry in log.ReadAllAsync(default, CancellationToken.None).ConfigureAwait(false))
            {
                entries.Add(entry switch
                {
                    WalRecord record => new Entry(EntryKind.Record, record.Position, default, record.Page, record.Payload.ToArray()),
                    WalTorn torn => new Entry(EntryKind.Torn, torn.Start, torn.End, default, null),
                    WalCorruption corruption => new Entry(EntryKind.Corruption, corruption.Start, corruption.End, default, null),
                    _ => throw new InvalidOperationException("A scan entry has no value."),
                });
            }

            foreach (var record in entries.Where(static entry => entry.Kind == EntryKind.Record))
            {
                reads.Add(await log.ReadAsync(record.Start, CancellationToken.None).ConfigureAwait(false) switch
                {
                    ArcBuffer payload => Take(payload),
                    _ => null,
                });
            }

            foreach (var position in abandoned)
            {
                abandonedReads.Add(await log.ReadAsync(position, CancellationToken.None).ConfigureAwait(false) switch
                {
                    ArcBuffer payload => Take(payload),
                    _ => null,
                });
            }
        }

        return (false, storage.ReadDurable(LogPath, File), entries, reads, abandonedReads);
    }

    private static byte[] Take(ArcBuffer payload)
    {
        using (payload)
        {
            return payload.ToArray();
        }
    }

    /// <summary>
    /// Requires the scan to report exactly the entries the faulty pages give, in order (wal.md §6): each maximal run
    /// of adjacent hole pages as one <see cref="WalTorn"/>, each maximal run of adjacent invalid pages as one
    /// <see cref="WalCorruption"/>, and each valid page which breaks the frame rules as a <see cref="WalCorruption"/> of
    /// its own.
    /// </summary>
    private void RequireFaultsReported(List<Entry> entries, Dictionary<int, Fault> faults, int pageSize)
    {
        var expected = new List<(Fault Fault, int Start, int End)>();
        foreach (var (page, fault) in faults.OrderBy(static fault => fault.Key))
        {
            if (fault != Fault.Broken && expected.Count > 0 && expected[^1].Fault == fault && expected[^1].End == page)
            {
                expected[^1] = (fault, expected[^1].Start, page + 1);
            }
            else
            {
                expected.Add((fault, page, page + 1));
            }
        }

        WalPosition Position(int page) => new(File, (ulong)pageSize * (ulong)(1 + page));
        var reported = entries.Where(static entry => entry.Kind != EntryKind.Record).Select(static entry => (entry.Kind, entry.Start, entry.End));
        Require(
            reported.SequenceEqual(expected.Select(run => (run.Fault == Fault.Torn ? EntryKind.Torn : EntryKind.Corruption, Position(run.Start), Position(run.End)))),
            "reported faults other than the runs and pages the format's rules give.");
    }

    /// <summary>
    /// How a scan reports a data page it yields no record from: a hole page, an invalid page, or a valid page which breaks
    /// the frame rules.
    /// </summary>
    private enum Fault
    {
        Torn,
        Invalid,
        Broken,
    }

    private enum EntryKind
    {
        Record,
        Torn,
        Corruption,
    }

    private sealed record Entry(EntryKind Kind, WalPosition Start, WalPosition End, WalPage Page, byte[]? Payload);

    private sealed record ScanModel(List<Record> Records, Dictionary<int, Fault> Faults, List<WalPosition> Abandoned);

    private sealed record Record(WalPosition Position, WalPage Page, byte[] Payload);

    private sealed record Pending(WalPosition Position, WalPage Page, int Total, List<byte> Bytes);
}
