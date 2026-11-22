using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Orleans.Durable.Storage.Diagnostics;
using Orleans.Durable.Storage.Engine.Buffers;

namespace Orleans.Durable.Storage.Engine.Wal;

public sealed partial class WriteAheadLog
{
    // A scan reads at most this many bytes of pages at once.
    private const int ScanChunkBytes = 1024 * 1024;

    /// <summary>
    /// Enumerates log entries at or above <paramref name="from"/> and below <see cref="DurableFrontier"/> as
    /// captured when enumeration starts, in log order.
    /// </summary>
    /// <remarks>
    /// Each complete record is a <see cref="WalRecord"/>, whose payload is borrowed until the enumeration advances.
    /// Each maximal run of hole pages, which replay wrote over pages a crash left incomplete, is a
    /// <see cref="WalTorn"/>. Each maximal run of invalid pages, and each valid page which breaks the frame rules, is a
    /// <see cref="WalCorruption"/>. Enumeration may run concurrently with writes.
    /// <para>
    /// Files of one open are numbered consecutively. Missing bytes are a gap in file numbering, starting at
    /// <paramref name="from"/> when its file no longer exists, or the range from the replayed end of
    /// <paramref name="from"/>'s file to <paramref name="from"/> when <paramref name="from"/> lies beyond it. When a
    /// later data page of the same open survives, they lost records within the open, and the enumeration yields a
    /// <see cref="WalCorruption"/> covering them before that page. Otherwise they are the open's lost tail, which the
    /// enumeration skips. <paramref name="from"/> equal to the replayed end is valid.
    /// </para>
    /// </remarks>
    public IAsyncEnumerable<WalEntry> ReadAllAsync(WalPosition from, CancellationToken cancellationToken = default)
    {
        WalScanner.ScanFile[] files;
        WalPosition frontier;
        WalCorruption? lost = null;
        lock (_sync)
        {
            ThrowIfUnavailable();
            frontier = _frontier;
            files =
            [
                .. _files
                    .Where(file => file.Number >= from.File && file.Number <= frontier.File)
                    .Select(file => new WalScanner.ScanFile(
                        file.Number,
                        file.Handle,
                        file.Header,
                        file.Writer is null ? null : ScanLimit(file, frontier))),
            ];

            if (from.File != 0
                && FindFile(_files, from.File) is { Writer: null } start
                && from.Offset > (ulong)start.Handle.Length)
            {
                lost = new WalCorruption(new WalPosition(from.File, (ulong)start.Handle.Length), from);
            }
        }

        return new WalScanner(files, from, frontier, lost, this).ScanAsync(cancellationToken);
    }

    /// <summary>
    /// Reads the last record whose first frame lies on <paramref name="page"/> and whose first 8 bytes, read as a
    /// little-endian integer, equal <paramref name="key"/>.
    /// </summary>
    /// <remarks>
    /// The page comes from <see cref="WalWriteRequest.OnDurable"/> or a scan, and must satisfy the same validation as a
    /// position read. Every case in which no such record can be read is reported as corruption, never thrown: an absent
    /// file, a page outside its file, an invalid page, and a valid page holding no matching record. A record is a match
    /// only once every page holding it is settled: a record which continues on a page not yet durable in this open, or,
    /// in a final open, beyond its file's end or on a hole page, was torn or is not yet durable, and is no match. A
    /// record which begins on the page and whose key the next page completes may be the last match, so a next page
    /// which breaks the frame rules is corruption of that page.
    /// </remarks>
    /// <returns>An owned buffer holding the record, or the <see cref="WalCorruption"/> range which should hold it.</returns>
    public async ValueTask<WalRead> ReadAsync(WalPage page, ulong key, CancellationToken cancellationToken = default)
    {
        LogFile? file;
        lock (_sync)
        {
            ThrowIfUnavailable();
            file = FindFile(_files, page.File);
        }

        if (file is null)
        {
            return new WalRead(new WalCorruption(new WalPosition(page.File, 0), new WalPosition(page.File + 1, 0)));
        }

        if (file.Header is not { PageSize: var pageSize })
        {
            return new WalRead(new WalCorruption(new WalPosition(file.Number, 0), new WalPosition(file.Number, (ulong)file.Handle.Length)));
        }

        var corrupt = Corrupt(file.Number, pageSize, page.Index, page.Index + 1L);
        var pageStart = WalFormat.DataPageOffset(pageSize, page.Index);
        if (pageStart + pageSize > file.Handle.Length)
        {
            return corrupt;
        }

        Debug.Assert(file.Writer is null || IsDurable(file.Writer, page.Index, page.Index), "A keyed read's page comes from OnDurable or a scan, so it is durable.");
        using var first = await ReadStorageAsync(file.Handle, pageStart, pageSize, cancellationToken).ConfigureAwait(false);
        var scratch = ArrayPool<byte>.Shared.Rent(pageSize);
        try
        {
            return await ReadKeyedAsync(file, pageSize, page, key, first, scratch, corrupt, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    /// <summary>
    /// Reads the record at a position delivered by <see cref="ReadAllAsync"/> or
    /// <see cref="WalWriteRequest.OnDurable"/>.
    /// </summary>
    /// <returns>
    /// An owned buffer holding the record, or the <see cref="WalCorruption"/> range of a page holding it which fails
    /// validation.
    /// </returns>
    public async ValueTask<WalRead> ReadAsync(WalPosition position, CancellationToken cancellationToken = default)
    {
        LogFile? file;
        lock (_sync)
        {
            ThrowIfUnavailable();
            file = FindFile(_files, position.File);
        }

        if (file is null)
        {
            throw new ArgumentOutOfRangeException(nameof(position), position, "The position names no file.");
        }

        if (file.Header is not { PageSize: var pageSize })
        {
            return new WalRead(new WalCorruption(new WalPosition(file.Number, 0), new WalPosition(file.Number, (ulong)file.Handle.Length)));
        }

        var offset = checked((long)position.Offset);
        var page = offset / pageSize - 1;
        var within = (int)(offset % pageSize);
        if (page < 0 || within < WalFormat.CrcLength)
        {
            throw new ArgumentOutOfRangeException(nameof(position), position, "The position names no frame.");
        }

        var pageStart = WalFormat.DataPageOffset(pageSize, page);
        using var first = await ReadStorageAsync(file.Handle, pageStart, pageSize, cancellationToken).ConfigureAwait(false);
        var scratch = ArrayPool<byte>.Shared.Rent(pageSize);
        try
        {
            // Every page holding the record must satisfy the frame rules as a whole, as a scan requires.
            if (!TryGetValidPage(first, 0, pageSize, scratch, out var bytes) || !WalFormat.IsWellFormed(bytes, within))
            {
                return Corrupt(file.Number, pageSize, page, page + 1);
            }

            return await AssembleAsync(file, pageSize, page, first, within, scratch, keyed: false, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    /// <summary>
    /// Reads from one of the log's files. A failure its storage reported stops the process, and the log stops with it
    /// (conventions.md §14); any other failure, such as the caller's cancellation, is the read's own.
    /// </summary>
    private async ValueTask<ArcBuffer> ReadStorageAsync(WalFile file, long offset, int length, CancellationToken cancellationToken)
    {
        try
        {
            return await file.ReadAsync(offset, length, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (FatalErrorHandler.IsReported(exception))
        {
            StopReported(exception);
            throw;
        }
    }

    /// <summary>
    /// Finds and reads the record a keyed page read names, from its already read first page.
    /// </summary>
    /// <remarks>
    /// A record is a candidate only once every page holding it is settled: durable in this open, or, in a final open,
    /// within its file and not a hole page. Any other record was torn, or is not yet durable, and is no match; the read
    /// then names the last earlier match (WAL-I2, conventions §13).
    /// </remarks>
    private async ValueTask<WalRead> ReadKeyedAsync(
        LogFile file, int pageSize, WalPage page, ulong key, ArcBuffer first, byte[] scratch, WalRead corrupt, CancellationToken cancellationToken)
    {
        if (!TryGetValidPage(first, 0, pageSize, scratch, out var bytes) || !WalFormat.IsWellFormed(bytes))
        {
            return corrupt;
        }

        var (match, continuing) = FindKey(bytes, key);
        if (continuing is { } within)
        {
            // The page's last record continues on later pages; it is the last match when it is settled and its key
            // matches.
            var matches = await ContinuingKeyMatchesAsync(file, pageSize, page.Index, first, within, key, scratch, cancellationToken).ConfigureAwait(false);
            if (matches is null)
            {
                return Corrupt(file.Number, pageSize, page.Index + 1L, page.Index + 2L);
            }

            if (matches is true)
            {
                var read = await AssembleAsync(file, pageSize, page.Index, first, within, scratch, keyed: true, cancellationToken).ConfigureAwait(false);
                if (read.HasValue)
                {
                    return read;
                }
            }
        }

        return match is { } at
            ? await AssembleAsync(file, pageSize, page.Index, first, at, scratch, keyed: false, cancellationToken).ConfigureAwait(false)
            : corrupt;
    }

    /// <summary>
    /// Finds the last Full frame on a valid, well-formed page whose record starts with <paramref name="key"/>, and the
    /// page's final First frame, whose record continues on later pages.
    /// </summary>
    private static (int? Match, int? Continuing) FindKey(ReadOnlySpan<byte> bytes, ulong key)
    {
        int? match = null;
        var offset = WalFormat.CrcLength;
        var leading = BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
        if (WalFormat.GetKind(leading) == FrameKind.Continuation)
        {
            offset += WalFormat.FrameWordLength + WalFormat.GetLength(leading);
        }

        while (offset <= bytes.Length - WalFormat.FrameWordLength)
        {
            var word = BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
            if (word == 0)
            {
                break;
            }

            if (WalFormat.GetKind(word) != FrameKind.Full)
            {
                // A First frame fills the rest of the page.
                return (match, offset);
            }

            var length = WalFormat.GetLength(word);
            if (length >= sizeof(ulong) && BinaryPrimitives.ReadUInt64LittleEndian(bytes[(offset + WalFormat.FrameWordLength)..]) == key)
            {
                match = offset;
            }

            offset += WalFormat.FrameWordLength + length;
        }

        return (match, null);
    }

    /// <summary>
    /// Decides whether the record whose First frame starts <paramref name="within"/> bytes into a validated page is
    /// settled and starts with <paramref name="key"/>. When the First frame holds fewer than 8 bytes, only the next page
    /// completes the key.
    /// </summary>
    /// <returns>Whether the record is settled and its key matches, or <see langword="null"/> when the next page, which
    /// completes the key, breaks the frame rules.</returns>
    private async ValueTask<bool?> ContinuingKeyMatchesAsync(
        LogFile file, int pageSize, long page, ArcBuffer first, int within, ulong key, byte[] scratch, CancellationToken cancellationToken)
    {
        var (length, total) = FirstFrame(first, pageSize, within, scratch);
        if (total < sizeof(ulong) || !IsSettled(file, pageSize, page, total - length))
        {
            return false;
        }

        var head = GetPage(first, 0, pageSize, scratch).Slice(within + WalFormat.FirstFrameHeaderLength, Math.Min(length, sizeof(ulong)));
        ulong value = 0;
        for (var index = 0; index < head.Length; index++)
        {
            value |= (ulong)head[index] << (8 * index);
        }

        if (length >= sizeof(ulong))
        {
            return value == key;
        }

        var perPage = pageSize - WalFormat.CrcLength - WalFormat.FrameWordLength;
        using var next = await ReadStorageAsync(file.Handle, WalFormat.DataPageOffset(pageSize, page + 1), pageSize, cancellationToken).ConfigureAwait(false);
        if (TryGetValidPage(next, 0, pageSize, scratch, out var nextPage) && WalFormat.IsHolePage(nextPage))
        {
            return false;
        }

        if (ContinuationFragment(next, 0, pageSize, Math.Min(perPage, total - length), scratch) < 0)
        {
            return null;
        }

        var tail = GetPage(next, 0, pageSize, scratch).Slice(WalFormat.CrcLength + WalFormat.FrameWordLength, sizeof(ulong) - length);
        for (var index = 0; index < tail.Length; index++)
        {
            value |= (ulong)tail[index] << (8 * (length + index));
        }

        return value == key;
    }

    /// <summary>
    /// Determines whether every continuation page of a record which begins on <paramref name="page"/> and has
    /// <paramref name="rest"/> bytes after its First frame is settled without reading it: durable in this open, or, in
    /// a final open, within the file, which finalization truncated after its last durable page.
    /// </summary>
    private bool IsSettled(LogFile file, int pageSize, long page, int rest)
    {
        var perPage = pageSize - WalFormat.CrcLength - WalFormat.FrameWordLength;
        var last = page + ((long)rest + perPage - 1) / perPage;
        if (file.Writer is not { } writer)
        {
            return WalFormat.DataPageOffset(pageSize, last) + pageSize <= file.Handle.Length;
        }

        return IsDurable(writer, page + 1, last);
    }

    private bool IsDurable(FileWriter writer, long first, long last)
    {
        lock (_sync)
        {
            return writer.IsDurable(first, last);
        }
    }

    /// <summary>
    /// Reads the record whose first frame starts <paramref name="within"/> bytes into a validated first page,
    /// reading and validating its continuation pages.
    /// </summary>
    /// <returns>
    /// The record; the <see cref="WalCorruption"/> range of a continuation page which breaks the frame rules; or, for a
    /// <paramref name="keyed"/> read whose record a hole page shows was torn, no value.
    /// </returns>
    /// <remarks>
    /// A hole page is one replay wrote over a page a crash left incomplete, so the torn record is no match.
    /// </remarks>
    private async ValueTask<WalRead> AssembleAsync(
        LogFile file, int pageSize, long page, ArcBuffer first, int within, byte[] scratch, bool keyed, CancellationToken cancellationToken)
    {
        var (length, total) = FirstFrame(first, pageSize, within, scratch);
        if (total < 0)
        {
            return new WalRead(first.Slice(within + WalFormat.FrameWordLength, length));
        }

        var rest = total - length;
        using var writer = new ArcBufferWriter(total);
        writer.Write(GetPage(first, 0, pageSize, scratch).Slice(within + WalFormat.FirstFrameHeaderLength, length));
        var pageStart = WalFormat.DataPageOffset(pageSize, page);
        var perPage = pageSize - WalFormat.CrcLength - WalFormat.FrameWordLength;
        var continuations = (int)(((long)rest + perPage - 1) / perPage);
        var perChunk = Math.Max(1, ScanChunkBytes / pageSize);
        for (var index = 0; index < continuations;)
        {
            var count = Math.Min(perChunk, continuations - index);
            using var chunk = await ReadStorageAsync(file.Handle, pageStart + (1L + index) * pageSize, count * pageSize, cancellationToken)
                .ConfigureAwait(false);
            for (var inChunk = 0; inChunk < count; inChunk++, index++)
            {
                var fragment = ContinuationFragment(chunk, inChunk * pageSize, pageSize, Math.Min(perPage, rest), scratch);
                if (fragment < 0)
                {
                    if (keyed && TryGetValidPage(chunk, inChunk * pageSize, pageSize, scratch, out var bytes) && WalFormat.IsHolePage(bytes))
                    {
                        return default;
                    }

                    return Corrupt(file.Number, pageSize, page + 1 + index, page + 2 + index);
                }

                writer.Write(GetPage(chunk, inChunk * pageSize, pageSize, scratch).Slice(WalFormat.CrcLength + WalFormat.FrameWordLength, fragment));
                rest -= fragment;
            }
        }

        _ = Interlocked.Add(ref _readCopyBytes, writer.Length);
        return new WalRead(writer.ConsumeSlice(writer.Length));
    }

    /// <summary>
    /// Reads the frame starting <paramref name="within"/> bytes into a page: its fragment length, and the record's
    /// total length for a First frame or -1 for a Full frame.
    /// </summary>
    private static (int Length, int Total) FirstFrame(ArcBuffer first, int pageSize, int within, byte[] scratch)
    {
        var bytes = GetPage(first, 0, pageSize, scratch);
        var word = BinaryPrimitives.ReadUInt32LittleEndian(bytes[within..]);
        var length = WalFormat.GetLength(word);
        return WalFormat.GetKind(word) == FrameKind.Full
            ? (length, -1)
            : (length, (int)BinaryPrimitives.ReadUInt64LittleEndian(bytes[(within + WalFormat.FrameWordLength)..]));
    }

    /// <summary>
    /// Validates a continuation page and returns its fragment length, or -1 when it breaks the frame rules.
    /// </summary>
    private static int ContinuationFragment(ArcBuffer chunk, int offset, int pageSize, int expected, byte[] scratch)
    {
        if (!TryGetValidPage(chunk, offset, pageSize, scratch, out var bytes) || !WalFormat.IsWellFormed(bytes))
        {
            return -1;
        }

        var continuation = BinaryPrimitives.ReadUInt32LittleEndian(bytes[WalFormat.CrcLength..]);
        var fragment = WalFormat.GetLength(continuation);
        return WalFormat.GetKind(continuation) == FrameKind.Continuation && fragment == expected ? fragment : -1;
    }

    /// <summary>
    /// Returns the number of data pages of a file written by this open which a scan bounded by
    /// <paramref name="frontier"/> may read: every page holding a record below the frontier.
    /// </summary>
    private long ScanLimit(LogFile file, WalPosition frontier)
    {
        if (file.Number < frontier.File)
        {
            return file.Writer!.BuiltPages;
        }

        // The page holding the frontier's preceding byte holds the last record below it.
        return ((long)frontier.Offset + _pageSize - 1) / _pageSize - 1;
    }

    private static WalRead Corrupt(ulong file, int pageSize, long firstPage, long endPage) =>
        new(new WalCorruption(
            new WalPosition(file, (ulong)WalFormat.DataPageOffset(pageSize, firstPage)),
            new WalPosition(file, (ulong)WalFormat.DataPageOffset(pageSize, endPage))));

    /// <summary>
    /// Finds a file by number in files ordered by ascending number.
    /// </summary>
    private static LogFile? FindFile(LogFile[] files, ulong number)
    {
        int low = 0, high = files.Length - 1;
        while (low <= high)
        {
            var middle = (low + high) >>> 1;
            var candidate = files[middle].Number;
            if (candidate == number)
            {
                return files[middle];
            }

            if (candidate < number)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return null;
    }

    /// <summary>
    /// Gets one page of <paramref name="buffer"/> as contiguous bytes, copying into <paramref name="scratch"/> only
    /// when the page spans buffer segments, and validates its CRC.
    /// </summary>
    internal static bool TryGetValidPage(ArcBuffer buffer, int offset, int pageSize, byte[] scratch, out ReadOnlySpan<byte> page)
    {
        page = GetPage(buffer, offset, pageSize, scratch);
        return page.Length == pageSize && WalFormat.IsValidPage(page);
    }

    internal static ReadOnlySpan<byte> GetPage(ArcBuffer buffer, int offset, int pageSize, byte[] scratch)
    {
        var length = Math.Min(pageSize, buffer.Length - offset);
        if (length <= 0)
        {
            return [];
        }

        var view = buffer.UnsafeSlice(offset, length);
        if (view.First.Length - view.Offset >= length)
        {
            return view.First.AsSpan(view.Offset, length);
        }

        _ = view.CopyTo(scratch);
        return scratch.AsSpan(0, length);
    }

    private void AddReadCopyBytes(long count) => Interlocked.Add(ref _readCopyBytes, count);

    /// <summary>
    /// Scans files in number order, data page by data page, and reports every page it cannot read records from.
    /// </summary>
    /// <remarks>
    /// Every page the scan reads belongs to a final open or lies below the current open's durable frontier, so it was
    /// valid or a hole page when replay finished. A run of hole pages is <see cref="WalTorn"/>; a run of invalid
    /// pages, and a valid page which breaks the frame rules, is <see cref="WalCorruption"/>.
    /// </remarks>
    private sealed class WalScanner(WalScanner.ScanFile[] files, WalPosition from, WalPosition frontier, WalCorruption? lost, WriteAheadLog owner)
    {
        private readonly List<WalEntry> _ready = [];
        private readonly List<ArcBuffer> _owned = [];

        // Missing bytes are reported only once a later data page of their open shows records followed them.
        private WalCorruption? _pendingLoss = lost;
        private FaultRun? _run;
        private Assembly? _assembly;
        private PreviousPage _previous;

        private enum PreviousPage
        {
            None,
            Invalid,
            Settled,
            RecordInProgress,
        }

        public async IAsyncEnumerable<WalEntry> ScanAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            try
            {
                // Files of one open are numbered consecutively, so a gap in numbering is missing bytes. An open's
                // first file ends the previous open, whose missing bytes were then its lost tail.
                ulong? expected = from == default ? null : from.File;
                foreach (var file in files)
                {
                    if (file.Header is { FirstOfOpen: true } && file.Number != from.File)
                    {
                        _pendingLoss = null;
                    }
                    else if (expected is { } next && file.Number > next)
                    {
                        AddLoss(new WalPosition(next, 0), new WalPosition(file.Number, 0));
                    }

                    expected = file.Number + 1;
                    if (file.Header is not { } header)
                    {
                        // A file name is published only with its durable header, so an invalid header is damage.
                        FlushLoss();
                        Emit(torn: false, new WalPosition(file.Number, 0), new WalPosition(file.Number, (ulong)file.File.Length));
                        continue;
                    }

                    var pageSize = header.PageSize;
                    var pages = file.DataPageLimit ?? WalFormat.DataPageCount(file.File.Length, pageSize);
                    var page = file.Number == from.File ? Math.Max(0, (long)from.Offset / pageSize - 1) : 0;
                    if (page < pages)
                    {
                        FlushLoss();
                    }

                    _previous = page == 0 ? PreviousPage.Settled : PreviousPage.None;
                    var scratch = new byte[pageSize];
                    var perChunk = Math.Max(1, ScanChunkBytes / pageSize);
                    while (page < pages)
                    {
                        var count = (int)Math.Min(perChunk, pages - page);
                        using var chunk = await owner.ReadStorageAsync(file.File, WalFormat.DataPageOffset(pageSize, page), count * pageSize, cancellationToken)
                            .ConfigureAwait(false);
                        for (var index = 0; index < count; index++)
                        {
                            ScanPage(file.Number, pageSize, page + index, chunk, index * pageSize, scratch);
                        }

                        page += count;

                        // A stopped log answers nothing (conventions.md §14).
                        owner.ThrowIfStopped();
                        foreach (var entry in _ready)
                        {
                            yield return entry;
                        }

                        ReleaseYielded();
                    }

                    CloseRun();
                    DiscardAssembly();
                }

                owner.ThrowIfStopped();
                foreach (var entry in _ready)
                {
                    yield return entry;
                }
            }
            finally
            {
                ReleaseYielded();
                _assembly?.Writer.Dispose();
            }
        }

        /// <summary>
        /// Adds missing bytes to the pending loss, from the scan's start at the earliest.
        /// </summary>
        private void AddLoss(WalPosition start, WalPosition end)
        {
            if (end <= from)
            {
                return;
            }

            _pendingLoss = new WalCorruption(_pendingLoss?.Start ?? (start < from ? from : start), end);
        }

        /// <summary>
        /// Reports the pending loss: a later data page of its open shows that records followed it.
        /// </summary>
        private void FlushLoss()
        {
            if (_pendingLoss is { } loss)
            {
                _ready.Add(new WalEntry(loss));
                _pendingLoss = null;
            }
        }

        private void ReleaseYielded()
        {
            _ready.Clear();
            foreach (var buffer in _owned)
            {
                buffer.Dispose();
            }

            _owned.Clear();
        }

        private void ScanPage(ulong file, int pageSize, long page, ArcBuffer chunk, int offset, byte[] scratch)
        {
            var bytes = GetPage(chunk, offset, pageSize, scratch);
            if (bytes.Length != pageSize || !WalFormat.IsValidPage(bytes))
            {
                AddFault(file, pageSize, page, torn: false);
                _previous = PreviousPage.Invalid;
                return;
            }

            if (WalFormat.IsHolePage(bytes))
            {
                // A record with bytes on a hole page was never acknowledged, and an orphan fragment may follow it.
                AddFault(file, pageSize, page, torn: true);
                _previous = PreviousPage.Invalid;
                return;
            }

            CloseRun();
            if (!WalFormat.IsWellFormed(bytes))
            {
                AddBrokenPage(file, pageSize, page);
                _previous = PreviousPage.None;
                return;
            }

            ParseFrames(file, page, pageSize, bytes, chunk.UnsafeSlice(offset, pageSize));
        }

        /// <summary>
        /// Applies the frame rules to one valid page, yielding complete records and reporting a violation as
        /// corruption of the page.
        /// </summary>
        private void ParseFrames(ulong file, long page, int pageSize, ReadOnlySpan<byte> bytes, ArcBuffer view)
        {
            var pageStart = WalFormat.DataPageOffset(pageSize, page);
            var offset = WalFormat.CrcLength;
            var perPage = pageSize - WalFormat.CrcLength - WalFormat.FrameWordLength;
            var leading = BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
            if (WalFormat.GetKind(leading) == FrameKind.Continuation)
            {
                var fragment = WalFormat.GetLength(leading);
                if (fragment == 0 || fragment > perPage)
                {
                    Violation();
                    return;
                }

                if (_assembly is { } assembly)
                {
                    var remaining = assembly.Total - assembly.Writer.Length;
                    if (fragment > remaining || fragment < remaining && fragment != perPage)
                    {
                        Violation();
                        return;
                    }

                    assembly.Writer.Write(bytes.Slice(offset + WalFormat.FrameWordLength, fragment));
                    if (fragment < remaining)
                    {
                        _previous = PreviousPage.RecordInProgress;
                        return;
                    }

                    _assembly = null;
                    var payload = assembly.Writer.ConsumeSlice(assembly.Total);
                    assembly.Writer.Dispose();
                    owner.AddReadCopyBytes(assembly.Total);
                    AddRecord(new WalRecord(assembly.Position, assembly.Page, payload), owned: true);
                }
                else if (_previous == PreviousPage.Settled)
                {
                    // The previous page is valid and holds no unfinished record.
                    Violation();
                    return;
                }
                else if (fragment == perPage)
                {
                    // An orphan fragment which fills its page may continue on the next page.
                    _previous = PreviousPage.None;
                    return;
                }

                offset += WalFormat.FrameWordLength + fragment;
            }
            else if (_assembly is not null)
            {
                Violation();
                return;
            }

            while (offset <= pageSize - WalFormat.FrameWordLength)
            {
                var word = BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
                var length = WalFormat.GetLength(word);
                var kind = WalFormat.GetKind(word);
                var position = new WalPosition(file, (ulong)(pageStart + offset));
                if (word == 0)
                {
                    if (bytes[offset..].ContainsAnyExcept((byte)0))
                    {
                        Violation();
                        return;
                    }

                    _previous = PreviousPage.Settled;
                    return;
                }

                if (kind == FrameKind.Full && offset + WalFormat.FrameWordLength + length <= pageSize)
                {
                    if (Includes(position))
                    {
                        AddRecord(new WalRecord(position, new WalPage(file, checked((uint)page)), view.UnsafeSlice(offset + WalFormat.FrameWordLength, length)), owned: false);
                    }

                    offset += WalFormat.FrameWordLength + length;
                    continue;
                }

                if (kind != FrameKind.First || length == 0 || offset + WalFormat.FirstFrameHeaderLength + length != pageSize)
                {
                    Violation();
                    return;
                }

                var total = BinaryPrimitives.ReadUInt64LittleEndian(bytes[(offset + WalFormat.FrameWordLength)..]);
                if (total <= (ulong)length || total > int.MaxValue)
                {
                    Violation();
                    return;
                }

                if (Includes(position))
                {
#pragma warning disable CA2000 // The assembly owns the writer until the record completes or is discarded.
                    var writer = new ArcBufferWriter((int)total);
#pragma warning restore CA2000
                    writer.Write(bytes.Slice(offset + WalFormat.FirstFrameHeaderLength, length));
                    _assembly = new Assembly(position, new WalPage(file, checked((uint)page)), (int)total, writer);
                    _previous = PreviousPage.RecordInProgress;
                }
                else
                {
                    // A record which starts below the scan's start is not yielded.
                    _previous = PreviousPage.None;
                }

                return;
            }

            if (bytes[offset..].ContainsAnyExcept((byte)0))
            {
                Violation();
                return;
            }

            _previous = PreviousPage.Settled;

            void Violation()
            {
                AddBrokenPage(file, pageSize, page);
                _previous = PreviousPage.None;
            }
        }

        /// <summary>
        /// Determines whether a record at <paramref name="position"/> belongs to the scan.
        /// </summary>
        private bool Includes(WalPosition position) => position >= from && position < frontier;

        private void DiscardAssembly()
        {
            _assembly?.Writer.Dispose();
            _assembly = null;
        }

        private void AddRecord(WalRecord record, bool owned)
        {
            _ready.Add(new WalEntry(record));
            if (owned)
            {
                _owned.Add(record.Payload);
            }
        }

        /// <summary>
        /// Adds a page to the current fault run, or starts a run when the page does not extend it.
        /// </summary>
        private void AddFault(ulong file, int pageSize, long page, bool torn)
        {
            DiscardAssembly();
            if (_run is { } run && run.Torn == torn && run.End == page)
            {
                run.End++;
                return;
            }

            CloseRun();
            _run = new FaultRun(file, pageSize, page, torn);
        }

        /// <summary>
        /// Reports a valid page which breaks the frame rules as an entry of its own, which no adjacent page extends.
        /// </summary>
        private void AddBrokenPage(ulong file, int pageSize, long page)
        {
            AddFault(file, pageSize, page, torn: false);
            CloseRun();
        }

        private void CloseRun()
        {
            if (_run is { } run)
            {
                _run = null;
                Emit(run.Torn, run.Start, new WalPosition(run.File, (ulong)WalFormat.DataPageOffset(run.PageSize, run.End)));
            }
        }

        private void Emit(bool torn, WalPosition start, WalPosition end)
        {
            if (end <= from)
            {
                return;
            }

            if (start < from)
            {
                start = from;
            }

            _ready.Add(torn ? new WalEntry(new WalTorn(start, end)) : new WalEntry(new WalCorruption(start, end)));
        }

        /// <summary>
        /// A file visible to one scan.
        /// </summary>
        /// <param name="Number">The file number.</param>
        /// <param name="File">The file.</param>
        /// <param name="Header">The validated header, or <see langword="null"/> when it is invalid.</param>
        /// <param name="DataPageLimit">
        /// For a file of the current open, the number of data pages the scan may read; otherwise
        /// <see langword="null"/>.
        /// </param>
        internal sealed record ScanFile(ulong Number, WalFile File, WalFileHeader? Header, long? DataPageLimit);

        private sealed record Assembly(WalPosition Position, WalPage Page, int Total, ArcBufferWriter Writer);

        /// <summary>
        /// A maximal run of hole pages, or of invalid pages and valid pages which break the frame rules.
        /// </summary>
        private sealed class FaultRun(ulong file, int pageSize, long firstPage, bool torn)
        {
            public ulong File { get; } = file;

            public int PageSize { get; } = pageSize;

            public bool Torn { get; } = torn;

            public WalPosition Start { get; } = new(file, (ulong)WalFormat.DataPageOffset(pageSize, firstPage));

            public long End { get; set; } = firstPage + 1;
        }
    }
}
