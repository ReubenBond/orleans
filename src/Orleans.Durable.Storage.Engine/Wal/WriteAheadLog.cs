using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO.Hashing;
using System.Runtime.CompilerServices;
using System.Threading.Tasks.Sources;
using Orleans.Durable.Storage.Diagnostics;
using Orleans.Durable.Storage.Engine.Buffers;

namespace Orleans.Durable.Storage.Engine.Wal;

/// <summary>
/// A page-oriented write-ahead log of independently durable records.
/// </summary>
/// <remarks>
/// <para>
/// Records are appended at a forward-only cursor in admission order. Small records pack into shared pages and a
/// longer record continues on the following pages; each page is written exactly once with one CRC-32C. A record is
/// durable when every page holding it has completed durable I/O, and replay accepts it when every page holding
/// it is valid. Different records may become durable in either order.
/// </para>
/// <para>
/// Each open creates a fresh file numbered above every existing one and appends only to files it created,
/// so log order (file, then offset) is admission order. A data page is submitted only when every page at least
/// W pages before it in its file is durable, and a file's first data page only when every page of the open's
/// previous file is durable, which lets replay tell a crash from damage to acknowledged data. Each open first
/// finalizes the previous one, so every open except the current one is final.
/// </para>
/// </remarks>
public sealed partial class WriteAheadLog : IAsyncDisposable
{
    private static readonly byte[] ZeroPage = new byte[WalOptions.MaximumPageSize];

    // Admission charges each record its bytes plus the largest frame overhead it can need, so even empty records
    // consume page-buffer budget.
    private const int RecordOverhead = WalFormat.FirstFrameHeaderLength + WalFormat.FrameWordLength;

    // Preallocated files start at this size and double up to the file size, so an open zero-fills little
    // before its first write while a busy log still reaches full-size files.
    private const long InitialPreallocatedFileSize = 4L << 20;

    // The next preallocated file is staged once the current file is PrepareStartFill full (or DoublingPrepareStartFill
    // while file sizes double, giving extra headroom for the 2x fill), and its zero-fill is paced to finish when the
    // current file is PrepareReadyFill full at the current file's observed fill rate. Crossing PrepareReadyFill cancels
    // the hurry token immediately so the fill finishes at full speed without waiting out MaxFillDelay.
    private const double PrepareStartFill = 0.25;
    private const double DoublingPrepareStartFill = 0.125;
    private const double PrepareReadyFill = 0.75;

    // The longest a paced zero-fill waits between writes, so it notices a change in the fill rate.
    private static readonly TimeSpan MaxFillDelay = TimeSpan.FromSeconds(1);

    // Bulk mode: while durable groups average at least BulkEnterGroupBytes, a flush's transfer dwarfs the metadata
    // commit a write into allocated, unwritten space costs, and the zero-fill's second write of every byte is the larger
    // cost, so rolls create allocated files. Below BulkLeaveGroupBytes the log returns to zero-filled files, whose small
    // durable writes need no metadata commit; the gap between the two keeps the mode from flapping
    // (io-uring-wal.md, "Adaptive preallocation"). The average weights each group by an eighth.
    internal const long BulkEnterGroupBytes = 3L << 20;
    internal const long BulkLeaveGroupBytes = 1L << 20;
    private const double GroupBytesWeight = 0.125;

    private readonly WalDirectory _directory;
    private readonly bool _ownsDirectory;
    private readonly WalOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly WriteGroupConcurrency _concurrency;
    private readonly int _pageSize;
    private readonly Lock _sync = new();
    private readonly ArcBufferWriter _builder = ArcBufferWriter.CreateAligned();
    private readonly Queue<PendingRecord> _queue = new();
    private readonly Queue<PendingRecord> _awaitingSubmission = new();
    private readonly Queue<PendingRecord> _order = new();
    private readonly Queue<BuiltRun> _runs = new();
    private readonly ConcurrentAppendRequestPool _appendRequests = new();

    private LogFile[] _files;
    private FileWriter _current;
    private BuiltRun? _lastRun;
    private PendingRecord? _placing;
    private long _openPage = -1;
    private int _openFill;
    private readonly Crc32 _openCrc = new(Crc32ParameterSet.Crc32C);
    private int _groupsInFlight;
    private double _groupBytes;
    private bool _bulk;
    private long _acceptedBytes;
    private int _outstanding;
    private bool _rollPending;
    private bool _wakeRequested;
    private bool _disposing;
    private TaskCompletionSource? _wake;
    private TaskCompletionSource? _capacity;

    // The failure which stopped the log: a failed storage operation its storage reported, or a defect the log reported, to
    // the fatal-error handler (Stop). The report stops the process; when a test or a simulation intercepts it, the log
    // stops instead, as the process would: it completes every accepted request with the failure (WAL-I8), answers nothing
    // more, and starts no further storage operation. It always holds a reported failure.
    private Exception? _stopped;
    private WalPosition _frontier;
    private long _readCopyBytes;
    private Task? _pump;
    private Task? _disposal;
    private PreparedFile? _prepared;

    private WriteAheadLog(WalDirectory directory, bool ownsDirectory, WalOptions options, LogFile[] existing, WalFile first, ulong firstNumber, long firstSize)
    {
        _directory = directory;
        _ownsDirectory = ownsDirectory;
        _options = options;
        _timeProvider = options.TimeProvider;
        _concurrency = new WriteGroupConcurrency(options.MinWriteGroupsInFlight, options.MaxWriteGroupsInFlight);
        _pageSize = options.PageSize;
        _current = new FileWriter(firstNumber, first, DataPageLimit(firstSize), previous: null, StartTimestamp());
        _files = [.. existing, new LogFile(firstNumber, first, new WalFileHeader(_pageSize, options.WriteWindowPages, FirstOfOpen: true), _current)];
        _frontier = new WalPosition(firstNumber, (ulong)_pageSize);
    }

    /// <summary>
    /// Gets the position below which every record has completed <see cref="WalWriteRequest.OnDurable"/>. It
    /// advances in log order, once the outcome callbacks of the records it passes have returned, so it follows their
    /// acknowledgements.
    /// </summary>
    public WalPosition DurableFrontier
    {
        get
        {
            lock (_sync)
            {
                ThrowIfStopped();
                return _frontier;
            }
        }
    }

    /// <summary>
    /// Gets how many admitted records have not yet completed, so a test can wait until a record is queued behind the
    /// write window.
    /// </summary>
    internal int OutstandingRecords
    {
        get
        {
            lock (_sync)
            {
                return _outstanding;
            }
        }
    }

    /// <summary>
    /// Gets whether the log creates allocated rather than zero-filled files, because its durable groups are large.
    /// </summary>
    internal bool BulkMode
    {
        get
        {
            lock (_sync)
            {
                return _bulk;
            }
        }
    }

    /// <summary>
    /// Gets whether the file the log appends to was allocated rather than zero-filled.
    /// </summary>
    internal bool CurrentFileAllocated
    {
        get
        {
            lock (_sync)
            {
                return _current.Allocated;
            }
        }
    }

    /// <summary>
    /// Gets how many file writers the current one keeps reachable, itself included.
    /// </summary>
    internal int WriterChainLength
    {
        get
        {
            lock (_sync)
            {
                var length = 0;
                for (var writer = _current; writer is not null; writer = writer.Previous)
                {
                    length++;
                }

                return length;
            }
        }
    }

    /// <summary>
    /// Gets or sets a failure which placing the next queued record throws, as a defect in placement would, so a test
    /// reaches the log's <c>WalPlacement</c> stop; production code leaves it unset.
    /// </summary>
    internal Exception? PlacementFault { get; set; }

    /// <summary>
    /// Gets the number of the file the log appends to: every record accepted from now on lies in this file or a
    /// later one.
    /// </summary>
    public ulong CurrentFile
    {
        get
        {
            lock (_sync)
            {
                ThrowIfStopped();
                return _current.Number;
            }
        }
    }

    /// <summary>
    /// Gets the number of record bytes which reads copied because the record spanned pages.
    /// </summary>
    public long ReadCopyBytes => Interlocked.Read(ref _readCopyBytes);

    /// <summary>
    /// Returns the replayed end of <paramref name="file"/>, a file of an earlier open: the position after its last
    /// page, below which <see cref="ReadAllAsync"/> reads it. Every record durable before the crash or close which ended
    /// that open lies below it.
    /// </summary>
    /// <returns>The replayed end, or <see langword="null"/> when the log holds no file of an earlier open numbered
    /// <paramref name="file"/>.</returns>
    public WalPosition? ReplayedEnd(ulong file)
    {
        lock (_sync)
        {
            ThrowIfUnavailable();
            return FindFile(_files, file) is { Writer: null } replayed
                ? new WalPosition(file, (ulong)replayed.Handle.Length)
                : null;
        }
    }

    /// <summary>
    /// Creates a write-ahead log and its first file in a directory it creates, or accepts, under an existing parent and
    /// owns.
    /// </summary>
    /// <exception cref="DirectoryNotFoundException">The directory's parent does not exist.</exception>
    /// <exception cref="WalExistsException">The directory already holds files.</exception>
    /// <exception cref="WalUnsupportedFormatException">The page size is below the storage's minimum.</exception>
    public static async ValueTask<WriteAheadLog> CreateAsync(
        WalStorage storage,
        string path,
        WalOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        await storage.CreateDirectoryAsync(path, cancellationToken).ConfigureAwait(false);
        var directory = await storage.OpenDirectoryAsync(path, cancellationToken).ConfigureAwait(false);
        try
        {
            return await CreateCoreAsync(directory, options, ownsDirectory: true, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await directory.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Creates a write-ahead log and its first file in a directory the caller owns and disposes after the log.
    /// </summary>
    /// <exception cref="WalExistsException">The directory already holds files.</exception>
    /// <exception cref="WalUnsupportedFormatException">The page size is below the storage's minimum.</exception>
    public static ValueTask<WriteAheadLog> CreateAsync(
        WalDirectory directory,
        WalOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        return CreateCoreAsync(directory, options, ownsDirectory: false, cancellationToken);
    }

    /// <summary>
    /// Opens an existing write-ahead log for writing: takes exclusive ownership, opens and flushes every file,
    /// validates each file header, finalizes the last open, then creates the next file.
    /// </summary>
    /// <remarks>
    /// Finalizing replaces each page which a crash left incomplete with a hole page and truncates the last open's
    /// files after their last durable page, so a scan reports every other invalid page as corruption. Enumerate
    /// <see cref="ReadAllAsync"/> to replay the records.
    /// </remarks>
    /// <exception cref="WalNotFoundException">The directory is missing or holds no files.</exception>
    /// <exception cref="WalUnsupportedFormatException">
    /// A file has an unknown version, or the page size is below the storage's minimum.
    /// </exception>
    public static async ValueTask<WriteAheadLog> OpenAsync(
        WalStorage storage,
        string path,
        WalOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        WalDirectory directory;
        try
        {
            directory = await storage.OpenDirectoryAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (DirectoryNotFoundException exception) when (!FatalErrorHandler.IsReported(exception))
        {
            // The storage's answer that the directory is missing; a missing directory a seam reported is a failure.
            throw new WalNotFoundException($"The write-ahead log at '{path}' does not exist.", exception);
        }

        try
        {
            return await OpenCoreAsync(directory, options, ownsDirectory: true, minimumFirstFile: 0, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await directory.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Opens an existing write-ahead log in a directory the caller owns and disposes after the log; see
    /// <see cref="OpenAsync(WalStorage, string, WalOptions, CancellationToken)"/>.
    /// </summary>
    /// <param name="directory">The directory, which the caller owns.</param>
    /// <param name="options">The options.</param>
    /// <param name="minimumFirstFile">
    /// The lowest number the new open's first file may take. A caller which holds pages or positions in files the
    /// directory has since lost passes a number above them, so a lost file's number is never reused. Scans skip the
    /// numbers skipped, as an earlier open's lost tail.
    /// </param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <exception cref="WalNotFoundException">The directory holds no files.</exception>
    /// <exception cref="WalUnsupportedFormatException">
    /// A file has an unknown version, or the page size is below the storage's minimum.
    /// </exception>
    public static ValueTask<WriteAheadLog> OpenAsync(
        WalDirectory directory,
        WalOptions options,
        ulong minimumFirstFile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        return OpenCoreAsync(directory, options, ownsDirectory: false, minimumFirstFile, cancellationToken);
    }

    private static async ValueTask<WriteAheadLog> CreateCoreAsync(
        WalDirectory directory, WalOptions options, bool ownsDirectory, CancellationToken cancellationToken)
    {
        ValidatePageSize(directory, options);
        if ((await directory.ListFilesAsync(cancellationToken).ConfigureAwait(false)).Count != 0)
        {
            throw new WalExistsException("The write-ahead log directory already holds files.");
        }

        return await StartAsync(directory, options, [], 1, ownsDirectory, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<WriteAheadLog> OpenCoreAsync(
        WalDirectory directory, WalOptions options, bool ownsDirectory, ulong minimumFirstFile, CancellationToken cancellationToken)
    {
        var files = new List<LogFile>();
        try
        {
            ValidatePageSize(directory, options);
            var numbers = await directory.ListFilesAsync(cancellationToken).ConfigureAwait(false);
            if (numbers.Count == 0)
            {
                throw new WalNotFoundException("The write-ahead log directory holds no files.");
            }

            foreach (var number in numbers)
            {
                var file = await directory.OpenFileAsync(number, cancellationToken).ConfigureAwait(false);
                WalFileHeader? header;
                try
                {
                    header = await ReadHeaderAsync(file, cancellationToken).ConfigureAwait(false);
                    if (header is { PageSize: var existing } && existing < directory.MinimumPageSize)
                    {
                        throw new WalUnsupportedFormatException(string.Create(
                            CultureInfo.InvariantCulture,
                            $"File {number} has page size {existing}, below the storage's minimum page size {directory.MinimumPageSize}."));
                    }
                }
                catch
                {
                    await file.DisposeAsync().ConfigureAwait(false);
                    throw;
                }

                files.Add(new LogFile(number, file, header, Writer: null));
            }

            await FinalizeLastOpenAsync(files, cancellationToken).ConfigureAwait(false);
            return await StartAsync(directory, options, [.. files], Math.Max(checked(numbers[^1] + 1), minimumFirstFile), ownsDirectory, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            foreach (var file in files)
            {
                await file.Handle.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    /// <summary>
    /// Waits for page-buffer capacity, then accepts <paramref name="request"/>, which receives exactly one
    /// <see cref="WalWriteRequest.OnDurable"/> or <see cref="WalWriteRequest.OnFailed"/> call.
    /// </summary>
    /// <remarks>
    /// Cancellation applies only before acceptance.
    /// </remarks>
    public async ValueTask WriteAsync(WalWriteRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var header = request.Header;
        var payload = request.Payload;
        var length = (long)header.Length + payload.Length;
        if (length > int.MaxValue)
        {
            throw new ArgumentException("A record holds at most int.MaxValue bytes.", nameof(request));
        }

        while (true)
        {
            Task capacity;
            lock (_sync)
            {
                ThrowIfUnavailable();
                if (_acceptedBytes < _options.PageBufferBudgetBytes)
                {
                    var record = new PendingRecord(
                        request,
                        header.Retain(),
                        payload.Retain(),
                        (int)length,
                        _timeProvider.GetTimestamp());
                    _acceptedBytes += length + RecordOverhead;
                    _outstanding++;
                    _queue.Enqueue(record);
                    Wake();
                    return;
                }

                _capacity ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                capacity = _capacity.Task;
            }

            await capacity.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Appends <paramref name="payload"/> as one record and returns its position once it is durable.
    /// </summary>
    /// <remarks>
    /// It completes from the record's <see cref="WalWriteRequest.OnDurable"/>, before <see cref="DurableFrontier"/>
    /// passes the record (WAL-I8), so a caller which reads the frontier or scans afterwards first waits for the frontier
    /// to exceed the returned position. Cancellation applies only before acceptance. The caller keeps the payload's
    /// bytes unchanged until this completes.
    /// </remarks>
    public async ValueTask<WalPosition> AppendAsync(ArcBuffer payload, CancellationToken cancellationToken = default)
    {
        var request = _appendRequests.Rent();
        var completion = request.Prepare(payload);
        try
        {
            await WriteAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            request.Abandon(_appendRequests);
            throw;
        }

        return await completion.ConfigureAwait(false);
    }

    /// <summary>
    /// Releases the prefix of files numbered below <paramref name="file"/>: closes and deletes them, oldest
    /// first, so the directory keeps a contiguous suffix of files, from which replay and scans start.
    /// </summary>
    /// <remarks>
    /// Only files below the file of <see cref="DurableFrontier"/> may be released: every record in them has completed
    /// <see cref="WalWriteRequest.OnDurable"/>, so every page of them is durable and no position or page an outcome
    /// callback is still delivering lies in them. The release closes every file it takes from the log, then deletes
    /// them. A deletion which fails stops the process (conventions.md §14), and it or a crash leaves a shorter released
    /// prefix: the files not yet deleted stay on disk, contiguous, where the next open finds them. Cancellation applies
    /// only before the release takes its files: once taken, they are deleted whatever becomes of the caller, since no
    /// later release would find them (WAL-I5). The caller ensures that no read or scan of a released file is in progress
    /// and that none starts.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="file"/> lies above the file of <see cref="DurableFrontier"/>.</exception>
    public async ValueTask ReleaseAsync(ulong file, CancellationToken cancellationToken = default)
    {
        LogFile[] released;
        lock (_sync)
        {
            ThrowIfUnavailable();
            cancellationToken.ThrowIfCancellationRequested();
            if (file > _frontier.File)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(file), file, string.Create(CultureInfo.InvariantCulture, $"File {file} lies above the durable frontier's file {_frontier.File}."));
            }

            var count = 0;
            while (count < _files.Length && _files[count].Number < file)
            {
                Debug.Assert(
                    _files[count].Writer is not { } writer || writer.Closed && writer.DurablePrefix == writer.BuiltPages,
                    "A file below the frontier's file is closed and durable (WAL-I5).");
                count++;
            }

            released = _files[..count];
            _files = _files[count..];
        }

        foreach (var releasedFile in released)
        {
            await releasedFile.Handle.DisposeAsync().ConfigureAwait(false);
        }

        // WAL-I5: oldest first, so the files left on disk stay a contiguous suffix of the log's. A failed deletion stops
        // the process (conventions.md §14), and the next open finds the files not yet deleted. The files are no longer
        // the log's, so the deletions run to completion: a cancelled one would leave a gap no later release deletes.
        try
        {
            foreach (var releasedFile in released)
            {
                await _directory.DeleteFileAsync(releasedFile.Number, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (FatalErrorHandler.IsReported(exception))
        {
            StopReported(exception);
            throw;
        }
    }

    /// <summary>
    /// Releases the log: rejects new requests, waits for accepted requests and storage I/O to retire, and releases
    /// files and buffers.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposal is null)
            {
                _disposing = true;
                _ = _capacity?.TrySetResult();
                _capacity = null;
                Wake();
                _disposal = DisposeCoreAsync();
            }

            return new ValueTask(_disposal);
        }
    }

    private static async ValueTask<WriteAheadLog> StartAsync(
        WalDirectory directory,
        WalOptions options,
        LogFile[] existing,
        ulong number,
        bool ownsDirectory,
        CancellationToken cancellationToken)
    {
        var size = options.PreallocateFiles ? Math.Min(options.FileSize, InitialPreallocatedFileSize) : options.FileSize;
        var length = options.PreallocateFiles ? size : 0;
        var file = await CreateFileAsync(directory, number, options, firstOfOpen: true, length, pace: null, allocated: false, cancellationToken)
            .ConfigureAwait(false);
        var wal = new WriteAheadLog(directory, ownsDirectory, options, existing, file, number, size);
        wal._pump = wal.PumpAsync();
        return wal;
    }

    private static async ValueTask<WalFile> CreateFileAsync(
        WalDirectory directory,
        ulong number,
        WalOptions options,
        bool firstOfOpen,
        long preallocatedLength,
        WalFillPace? pace,
        bool allocated,
        CancellationToken cancellationToken)
    {
        using var buffer = EncodeHeader(options, firstOfOpen);
        return allocated
            ? await directory.CreateAllocatedFileAsync(number, buffer, preallocatedLength, cancellationToken).ConfigureAwait(false)
            : await directory.CreateFileAsync(number, buffer, preallocatedLength, pace, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<WalFile> PublishPreparedFileAsync(
        WalDirectory directory,
        PreparedWalFile prepared,
        ulong number,
        WalOptions options,
        bool firstOfOpen,
        CancellationToken cancellationToken)
    {
        using var buffer = EncodeHeader(options, firstOfOpen);
        return await directory.PublishPreparedFileAsync(prepared, number, buffer, cancellationToken).ConfigureAwait(false);
    }

    private static ArcBuffer EncodeHeader(WalOptions options, bool firstOfOpen)
    {
        var page = new byte[options.PageSize];
        WalFormat.WriteHeader(page, options.WriteWindowPages, firstOfOpen);
        using var writer = ArcBufferWriter.CreateAligned();
        writer.Write(page);
        return writer.ConsumeSlice(options.PageSize);
    }

    /// <summary>
    /// Reads a file's header: its fields, or <see langword="null"/> for an invalid header, which is damage (WAL-I3).
    /// </summary>
    /// <exception cref="WalUnsupportedFormatException">The header carries the magic with another version.</exception>
    private static async ValueTask<WalFileHeader?> ReadHeaderAsync(WalFile file, CancellationToken cancellationToken)
    {
        byte[] bytes;
        using (var fields = await file.ReadAsync(0, WalFormat.HeaderFieldsLength, cancellationToken).ConfigureAwait(false))
        {
            bytes = fields.ToArray();
        }

        if (WalFormat.NamedPageSize(bytes) is { } size)
        {
            using var page = await file.ReadAsync(0, size, cancellationToken).ConfigureAwait(false);
            bytes = page.ToArray();
        }

        return ClassifyHeader(WalFormat.DecodeHeader(bytes));
    }

    /// <summary>
    /// Classifies a header's decoding, as the format's owner (conventions.md §15): its fields; <see langword="null"/>
    /// for damage (WAL-I3); or, for the magic with another version, <see cref="WalUnsupportedFormatException"/> carrying
    /// the rejection.
    /// </summary>
    internal static WalFileHeader? ClassifyHeader(WalHeaderDecoding decoding)
    {
        Debug.Assert(decoding.Value is not null, "conventions.md §15: a decoder answers a union which holds a case.");
        return decoding switch
        {
            WalFileHeader header => header,
            InvalidDataException rejection when DecodeRejection.ReasonOf(rejection) == "unsupported-version" =>
                throw new WalUnsupportedFormatException(rejection.Message, rejection),
            InvalidDataException => null,
            null => throw new UnreachableException(),
        };
    }

    private static void ValidatePageSize(WalDirectory directory, WalOptions options)
    {
        if (options.PageSize < directory.MinimumPageSize)
        {
            throw new WalUnsupportedFormatException(string.Create(
                CultureInfo.InvariantCulture,
                $"The page size {options.PageSize} is below the storage's minimum page size {directory.MinimumPageSize}."));
        }
    }

    private long DataPageLimit(long fileSize) => fileSize / _pageSize - 1;

    /// <summary>
    /// Returns the timestamp at which a file starts taking records, from which preparing the next preallocated file
    /// measures the fill rate; without preallocation the log reads no clock.
    /// </summary>
    private long StartTimestamp() => _options.PreallocateFiles ? _timeProvider.GetTimestamp() : 0;

    /// <summary>
    /// Gets the data pages of the file after the current one: double the current file, up to the file size,
    /// when files are preallocated.
    /// </summary>
    private long NextDataPageLimit() =>
        _options.PreallocateFiles
            ? DataPageLimit(Math.Min(_options.FileSize, 2 * _pageSize * (1 + _current.DataPageLimit)))
            : DataPageLimit(_options.FileSize);

    /// <summary>
    /// Starts staging and zero-filling an unpublished preallocated file in the background, paced so a roll finds it
    /// ready without assigning a log-file number until roll time.
    /// </summary>
    private void PrepareNext()
    {
        var dataPages = NextDataPageLimit();
        var length = _pageSize * (1 + dataPages);
#pragma warning disable CA2000 // Ownership of both sources transfers to the prepared file, whose taker or discarder disposes them.
        var hurry = new CancellationTokenSource();
        var cancel = new CancellationTokenSource();
#pragma warning restore CA2000
        var hurried = hurry.Token;
        var cancelled = cancel.Token;
        ValueTask Pace(long written, int count, CancellationToken _) =>
            PaceFillAsync(length, written, count, hurried, cancelled);

        // Leaving bulk mode from an allocated file, the log rolls to this file as soon as it is ready, so it goes unpaced.
        var pace = _current.Allocated ? null : (WalFillPace)Pace;
        var file = Task.Run(
            async () =>
            {
                try
                {
                    return await _directory.PrepareFileAsync(length, pace, cancelled).ConfigureAwait(false);
                }
                catch (Exception exception) when (FatalErrorHandler.IsReported(exception))
                {
                    // The log stops with the process at once, not at the roll which takes the file; a failure no seam
                    // reported is a defect the roll or the discard reports (conventions.md §14).
                    StopReported(exception);
                    throw;
                }
            },
            CancellationToken.None);
        _prepared = new PreparedFile(dataPages, file, hurry, cancel) { Hurried = pace is null };
    }

    /// <summary>
    /// Determines whether to start staging the next zero-filled file: files are preallocated, none is staged, the log
    /// is not in bulk mode, and the current file is past its start threshold (<see cref="DoublingPrepareStartFill"/>
    /// while file sizes double, <see cref="PrepareStartFill"/> otherwise) or allocated, which the log leaves as soon as
    /// the staged file is ready.
    /// </summary>
    private bool ShouldPrepareNext()
    {
        if (!_options.PreallocateFiles || _prepared is not null || _stopped is not null || _disposing || _rollPending || _bulk)
        {
            return false;
        }

        if (_current.Allocated)
        {
            return true;
        }

        var startFill = NextDataPageLimit() > _current.DataPageLimit ? DoublingPrepareStartFill : PrepareStartFill;
        return _current.BuiltPages >= (long)(_current.DataPageLimit * startFill);
    }

    /// <summary>
    /// Determines whether an in-flight paced preparation should be hurried now because the current file has reached
    /// <see cref="PrepareReadyFill"/> or is allocated.
    /// </summary>
    private bool ShouldHurryPrepared() =>
        _prepared is { Hurried: false, File.IsCompleted: false }
        && (_current.Allocated || _current.BuiltPages >= (long)(_current.DataPageLimit * PrepareReadyFill));

    /// <summary>
    /// Determines whether the next record starts the staged zero-filled file now: the log is out of bulk mode while
    /// appending to a non-empty allocated file (either after leaving bulk mode or after a burst rolled past an
    /// in-flight fill), and the staged file is ready and large enough for the record. Requiring at least one page in
    /// the allocated file avoids leaving an empty file behind if staging completes immediately after the roll.
    /// </summary>
    private bool ShouldRollEarly(long recordPages) =>
        _current.Allocated
        && _current.NextPage > 0
        && !_bulk
        && _prepared is { File.IsCompletedSuccessfully: true } prepared
        && prepared.DataPages >= recordPages;

    /// <summary>
    /// Folds a durable group's size into the average which chooses between bulk mode and zero-filled files. The caller
    /// holds the log's lock.
    /// </summary>
    private void RecordGroupBytes(long bytes)
    {
        _groupBytes += GroupBytesWeight * (bytes - _groupBytes);
        if (!_options.PreallocateFiles)
        {
            return;
        }

        if (!_bulk && _groupBytes >= BulkEnterGroupBytes)
        {
            _bulk = true;
        }
        else if (_bulk && _groupBytes < BulkLeaveGroupBytes)
        {
            _bulk = false;
        }
    }

    /// <summary>
    /// Waits until the next file's zero-fill may write <paramref name="count"/> more bytes. Once
    /// <paramref name="hurry"/> is cancelled, because the current file crossed <see cref="PrepareReadyFill"/> or rolled,
    /// the fill continues at full speed; once <paramref name="cancel"/> is cancelled, preparation aborts cleanly.
    /// </summary>
    private async ValueTask PaceFillAsync(long length, long written, int count, CancellationToken hurry, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        TimeSpan delay;
        lock (_sync)
        {
            delay = hurry.IsCancellationRequested ? TimeSpan.Zero : FillDelay(length, written, count);
        }

        if (delay > TimeSpan.Zero)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(hurry, cancel);
            try
            {
                await Task.Delay(delay, _timeProvider, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (hurry.IsCancellationRequested && !cancel.IsCancellationRequested)
            {
                // The log reached PrepareReadyFill or rolled; the rest of the fill goes at full speed.
            }
        }
    }

    private TimeSpan FillDelay(long length, long written, int count) =>
        FillDelay(_current.DataPageLimit, _current.BuiltPages, _timeProvider.GetElapsedTime(_current.Started), length - written, count);

    /// <summary>
    /// Returns how long the next file's zero-fill waits before writing <paramref name="count"/> of its
    /// <paramref name="remaining"/> bytes: the remaining bytes spread evenly over the time the current file, which
    /// holds <paramref name="built"/> of <paramref name="dataPages"/> pages after <paramref name="elapsed"/>, takes at
    /// that rate to become <see cref="PrepareReadyFill"/> full, and nothing once it is. The wait is at most
    /// <see cref="MaxFillDelay"/>, so the fill notices a change in the fill rate.
    /// </summary>
    internal static TimeSpan FillDelay(long dataPages, long built, TimeSpan elapsed, long remaining, int count)
    {
        var ready = (long)(dataPages * PrepareReadyFill);
        if (built >= ready)
        {
            return TimeSpan.Zero;
        }

        if (built == 0)
        {
            return MaxFillDelay;
        }

        var delay = elapsed * ((ready - built) / (double)built) * (count / (double)remaining);
        return delay < MaxFillDelay ? delay : MaxFillDelay;
    }

    /// <summary>
    /// Cancels an in-flight staged file or disposes an already completed unpublished staged file on shutdown, deleting
    /// its temporary cleanly without publishing a log file or waiting for remaining zero-fill.
    /// </summary>
    private async Task DiscardPreparedAsync()
    {
        if (_prepared is not { } prepared)
        {
            return;
        }

        _prepared = null;
        await prepared.Cancel.CancelAsync().ConfigureAwait(false);
        try
        {
            var staged = await prepared.File.ConfigureAwait(false);
            await staged.DisposeAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (prepared.Cancel.IsCancellationRequested)
        {
            // Disposal cancelled the in-flight preparation cleanly; its temporary was already removed.
        }
        catch (Exception exception) when (FatalErrorHandler.IsReported(exception))
        {
            // The storage already reported a failure and stopped the process; record it if the log has not stopped yet.
            lock (_sync)
            {
                _stopped ??= exception;
            }
        }
        catch (Exception exception)
        {
            // A defect no storage seam reported stops the process (conventions.md §14).
            lock (_sync)
            {
                Stop("WalDiscard", exception);
            }
        }
        finally
        {
            prepared.Hurry.Dispose();
            prepared.Cancel.Dispose();
        }
    }

    /// <summary>
    /// Records the failure which stops the log, after reporting it as the defect <paramref name="defect"/> unless its
    /// storage already reported it: from then on the log answers nothing (conventions.md §14). The caller holds the
    /// log's lock.
    /// </summary>
    /// <remarks>
    /// Whether a failure was reported is <see cref="FatalErrorHandler.IsReported"/>'s one fact, never a guess from its
    /// type: an answer the storage gave where none was expected, such as an existing name on a roll, is a defect too. The
    /// log disposes nothing it still uses, so no failure stems from its own disposal.
    /// </remarks>
    private void Stop(string defect, Exception exception)
    {
        FatalErrorHandler.StopOnDefect(defect, exception);
        _stopped ??= exception;
        Debug.Assert(FatalErrorHandler.IsReported(_stopped), "A stopped log holds a reported failure.");
    }

    /// <summary>
    /// Records a failure its storage reported, which stops the process: the log stops with it, fails every record not yet
    /// durable, and answers nothing more (conventions.md §14).
    /// </summary>
    private void StopReported(Exception exception)
    {
        lock (_sync)
        {
            _stopped ??= exception;
            Debug.Assert(FatalErrorHandler.IsReported(_stopped), "A stopped log holds a reported failure.");
            Wake();
        }
    }

    private void ThrowIfUnavailable()
    {
        ThrowIfStopped();
        ObjectDisposedException.ThrowIf(_disposing, this);
    }

    /// <summary>
    /// Throws, once the log stopped, a failure which carries the one that stopped it, so it reads as reported.
    /// </summary>
    private void ThrowIfStopped()
    {
        if (Volatile.Read(ref _stopped) is not null)
        {
            throw Stopped("The write-ahead log stopped at a failure reported to the fatal-error handler, which ends the process.");
        }
    }

    private IOException Stopped(string message) => new(message, Volatile.Read(ref _stopped));

    private void Wake()
    {
        _wakeRequested = true;
        _ = _wake?.TrySetResult();
        _wake = null;
    }

    private async Task DisposeCoreAsync()
    {
        await _pump!.ConfigureAwait(false);
        await DiscardPreparedAsync().ConfigureAwait(false);
        foreach (var file in Volatile.Read(ref _files))
        {
            await file.Handle.DisposeAsync().ConfigureAwait(false);
        }

        if (_ownsDirectory)
        {
            await _directory.DisposeAsync().ConfigureAwait(false);
        }

        _builder.Dispose();
    }

    /// <summary>
    /// Places accepted records, closes pages, and submits write groups until nothing more can be done.
    /// </summary>
    private async Task PumpAsync()
    {
        while (true)
        {
            List<Group>? groups = null;
            List<PendingRecord>? failed;
            CancellationTokenSource? hurry = null;
            long rollPages = -1;
            Task? wait = null;
            bool exit;
            lock (_sync)
            {
                _wakeRequested = false;
                try
                {
                    if (_stopped is null && !_rollPending && PlaceRecords(out var needed) == Placement.NeedsRoll)
                    {
                        _rollPending = true;
                        rollPages = needed;
                    }

                    groups = _stopped is null ? CollectGroups() : null;
                    if (ShouldPrepareNext())
                    {
                        PrepareNext();
                    }

                    if (ShouldHurryPrepared())
                    {
                        _prepared!.Hurried = true;
                        hurry = _prepared.Hurry;
                    }
                }
                catch (Exception exception)
                {
                    // A defect in placement leaves the cursor unusable, so the log stops with it, as with a failed write.
                    Stop("WalPlacement", exception);
                }

                failed = _stopped is not null && !_rollPending ? TakeUnfinishedRecords() : null;
                exit = _disposing && _outstanding == 0 && _groupsInFlight == 0 && !_rollPending;
                if (!exit && groups is null && failed is null && rollPages < 0 && !_wakeRequested)
                {
                    _wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    wait = _wake.Task;
                }
            }

            if (hurry is not null)
            {
                await hurry.CancelAsync().ConfigureAwait(false);
            }

            if (groups is not null)
            {
                foreach (var group in groups)
                {
                    StartGroup(group);
                }
            }

            if (failed is not null)
            {
                FinishFailed(failed);
            }

            if (exit)
            {
                return;
            }

            if (rollPages >= 0)
            {
                await RollAsync(rollPages).ConfigureAwait(false);
            }

            if (wait is not null)
            {
                await wait.ConfigureAwait(false);
            }
        }
    }

    private async Task RollAsync(long dataPages)
    {
        ulong number;
        PreparedFile? readyPrepared = null;
        PreparedFile? faultedPrepared = null;
        CancellationTokenSource? hurry = null;
        lock (_sync)
        {
            number = checked(_current.Number + 1);
            if (_prepared is { } prepared)
            {
                if (prepared.File.IsFaulted || prepared.File.IsCanceled)
                {
                    _prepared = null;
                    faultedPrepared = prepared;
                }
                else if (prepared.File.IsCompletedSuccessfully)
                {
                    if (prepared.DataPages >= dataPages)
                    {
                        _prepared = null;
                        readyPrepared = prepared;
                    }

                    // Otherwise a record larger than the prepared file rolls directly to its own allocated file,
                    // keeping the unpublished prepared file ready for the next normal roll (zero empty .clfl files).
                }
                else if (!prepared.Hurried)
                {
                    // Still zero-filling in flight: hurry it in the background and roll immediately without blocking.
                    prepared.Hurried = true;
                    hurry = prepared.Hurry;
                }
            }
        }

        if (hurry is not null)
        {
            await hurry.CancelAsync().ConfigureAwait(false);
        }

        var allocated = false;

        try
        {
            if (faultedPrepared is not null)
            {
                try
                {
                    _ = await faultedPrepared.File.ConfigureAwait(false);
                }
                finally
                {
                    faultedPrepared.Hurry.Dispose();
                    faultedPrepared.Cancel.Dispose();
                }
            }

            var header = new WalFileHeader(_pageSize, _options.WriteWindowPages, FirstOfOpen: false);
            WalFile file;
            if (readyPrepared is not null)
            {
                PreparedWalFile staged;
                try
                {
                    staged = await readyPrepared.File.ConfigureAwait(false);
                }
                finally
                {
                    readyPrepared.Hurry.Dispose();
                    readyPrepared.Cancel.Dispose();
                }

                file = await PublishPreparedFileAsync(_directory, staged, number, _options, firstOfOpen: false, CancellationToken.None)
                    .ConfigureAwait(false);
                dataPages = readyPrepared.DataPages;
            }
            else
            {
                var length = _options.PreallocateFiles ? _pageSize * (1 + dataPages) : 0;
                allocated = _options.PreallocateFiles;
                file = await CreateFileAsync(_directory, number, _options, firstOfOpen: false, length, pace: null, allocated, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            lock (_sync)
            {
                var previous = _current;
                previous.Closed = true;
                _current = new FileWriter(number, file, dataPages, previous, StartTimestamp()) { Allocated = allocated };
                _files = [.. _files, new LogFile(number, file, header, _current)];
                _rollPending = false;
                Wake();
            }
        }
        catch (Exception exception)
        {
            // A failure the storage reported, or a defect such as an existing name, which stops the process
            // (conventions.md §14).
            lock (_sync)
            {
                Stop("WalRoll", exception);
                _rollPending = false;
                Wake();
            }
        }
    }

    private enum Placement
    {
        Idle,
        Blocked,
        NeedsRoll,
    }

    /// <summary>
    /// Places queued records at the cursor in admission order, as far as the write window allows.
    /// </summary>
    private Placement PlaceRecords(out long rollPages)
    {
        rollPages = -1;
        while (true)
        {
            if (_placing is null)
            {
                if (!_queue.TryPeek(out var next))
                {
                    return Placement.Idle;
                }

                if (PlacementFault is { } fault)
                {
                    throw fault;
                }

                var started = StartRecord(next, out rollPages);
                if (started != Placement.Idle)
                {
                    return started;
                }

                _ = _queue.Dequeue();
                _placing = next;
            }

            if (!ContinueRecord(_placing))
            {
                return Placement.Blocked;
            }

            _placing = null;
        }
    }

    /// <summary>
    /// Writes a record's first frame: a Full frame when the record fits in the open page's remaining space,
    /// otherwise a First frame when its header and one byte fit, otherwise on the next page; a record which does
    /// not fit in the current file starts the next file.
    /// </summary>
    /// <returns><see cref="Placement.Idle"/> when the first frame was written.</returns>
    private Placement StartRecord(PendingRecord record, out long rollPages)
    {
        rollPages = -1;
        var fresh = _pageSize - WalFormat.CrcLength;
        var recordPages = LastPage(0, fresh, record.Length) + 1;
        if (ShouldRollEarly(recordPages))
        {
            if (_openPage >= 0)
            {
                ClosePage();
            }

            rollPages = recordPages;
            return Placement.NeedsRoll;
        }

        if (_openPage >= 0)
        {
            var remaining = _pageSize - _openFill;
            if (remaining >= WalFormat.FrameWordLength + record.Length)
            {
                WriteFirstFrame(record, FrameKind.Full, record.Length);
                return Placement.Idle;
            }

            if (remaining > WalFormat.FirstFrameHeaderLength && LastPage(_openPage, remaining, record.Length) < _current.DataPageLimit)
            {
                WriteFirstFrame(record, FrameKind.First, remaining - WalFormat.FirstFrameHeaderLength);
                return Placement.Idle;
            }

            ClosePage();
        }

        if (LastPage(_current.NextPage, fresh, record.Length) >= _current.DataPageLimit)
        {
            rollPages = Math.Max(NextDataPageLimit(), recordPages);
            return Placement.NeedsRoll;
        }

        if (!CanOpenPage())
        {
            return Placement.Blocked;
        }

        OpenPage();
        return StartRecord(record, out rollPages);
    }

    /// <summary>
    /// Writes Continuation frames until the record is complete or the write window blocks the next page.
    /// </summary>
    private bool ContinueRecord(PendingRecord record)
    {
        while (record.Placed < record.Length)
        {
            if (!CanOpenPage())
            {
                return false;
            }

            OpenPage();
            var fragment = Math.Min(_pageSize - WalFormat.CrcLength - WalFormat.FrameWordLength, record.Length - record.Placed);
            WriteWord(WalFormat.EncodeFrame(FrameKind.Continuation, fragment));
            CopyRecordBytes(record, fragment);
            if (record.Placed < record.Length)
            {
                ClosePage();
            }
        }

        record.LastPage = _openPage;
        record.End = new WalPosition(_current.Number, (ulong)(WalFormat.DataPageOffset(_pageSize, _openPage) + _openFill));
        return true;
    }

    private void WriteFirstFrame(PendingRecord record, FrameKind kind, int fragment)
    {
        record.Writer = _current;
        record.FirstPage = _openPage;
        record.Position = new WalPosition(_current.Number, (ulong)(WalFormat.DataPageOffset(_pageSize, _openPage) + _openFill));
        _awaitingSubmission.Enqueue(record);
        _order.Enqueue(record);
        WriteWord(WalFormat.EncodeFrame(kind, fragment));
        if (kind == FrameKind.First)
        {
            Span<byte> total = stackalloc byte[sizeof(ulong)];
            BinaryPrimitives.WriteUInt64LittleEndian(total, (ulong)record.Length);
            WriteBytes(total);
        }

        CopyRecordBytes(record, fragment);
        if (kind == FrameKind.First)
        {
            ClosePage();
        }
    }

    /// <summary>
    /// Returns the data page holding the last byte of a record which starts at <paramref name="page"/> with
    /// <paramref name="remaining"/> bytes left in that page.
    /// </summary>
    private long LastPage(long page, int remaining, int length)
    {
        if (remaining >= WalFormat.FrameWordLength + length)
        {
            return page;
        }

        var rest = (long)length - (remaining - WalFormat.FirstFrameHeaderLength);
        var perPage = _pageSize - WalFormat.CrcLength - WalFormat.FrameWordLength;
        return page + (rest + perPage - 1) / perPage;
    }

    /// <summary>
    /// WAL-I7: "A data page is submitted only when every page at least W pages before it in its file is durable."
    /// Pages are built only inside the window, so every built page may be submitted.
    /// </summary>
    private bool CanOpenPage() => _current.NextPage < _current.DurablePrefix + _options.WriteWindowPages;

    private void OpenPage()
    {
        _openPage = _current.NextPage++;
        _openFill = WalFormat.CrcLength;
        _openCrc.Reset();
        _builder.Write([0, 0, 0, 0]);
    }

    /// <summary>
    /// Pads the open page with zero, stores its CRC-32C, and queues it for submission. The page never changes again.
    /// </summary>
    private void ClosePage()
    {
        var start = _builder.Length - _openFill;
        var padding = _pageSize - _openFill;
        _builder.Write(ZeroPage.AsSpan(0, padding));
        _openCrc.Append(ZeroPage.AsSpan(0, padding));
        Span<byte> crc = stackalloc byte[WalFormat.CrcLength];
        BinaryPrimitives.WriteUInt32LittleEndian(crc, _openCrc.GetCurrentHashAsUInt32());
        _builder.WriteAt(start, crc);
        if (OpenPageContinuesLastRun())
        {
            _lastRun!.Count++;
        }
        else
        {
            _lastRun = new BuiltRun(_current, _openPage);
            _runs.Enqueue(_lastRun);
        }

        _current.BuiltPages = _openPage + 1;
        _openPage = -1;
    }

    private bool OpenPageContinuesLastRun() =>
        _runs.Count > 0 && _lastRun is { } last && ReferenceEquals(last.Writer, _current) && last.FirstPage + last.Count == _openPage;

    private void WriteWord(uint word)
    {
        Span<byte> bytes = stackalloc byte[WalFormat.FrameWordLength];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, word);
        WriteBytes(bytes);
    }

    private void WriteBytes(ReadOnlySpan<byte> bytes)
    {
        _builder.Write(bytes);
        _openCrc.Append(bytes);
        _openFill += bytes.Length;
    }

    private void CopyRecordBytes(PendingRecord record, int count)
    {
        while (count > 0)
        {
            var source = record.Next(count);
            WriteBytes(source);
            count -= source.Length;
        }
    }

    /// <summary>
    /// Gathers the closed pages of each run into write groups while group slots are free.
    /// </summary>
    /// <remarks>
    /// The open page is closed when a slot would remain free for it, so a lone record is not delayed. When the waiting
    /// runs leave such a slot, it is closed first, which joins it to a run it continues: a record spanning pages is then
    /// one write rather than its full pages and its last page apart.
    /// </remarks>
    private List<Group>? CollectGroups()
    {
        List<Group>? groups = null;
        while (_concurrency.Slots(_groupsInFlight) is var slots and > 0)
        {
            // Closing the open page costs no write when a slot would stay free after the waiting runs, or when the page
            // continues the last of them, which this pass submits; either way its records become durable now rather
            // than with a later write.
            if (_openPage >= 0
                && _openFill != WalFormat.CrcLength
                && PreviousFilesDurable(_current)
                && (_runs.Count < slots || _runs.Count == slots && OpenPageContinuesLastRun()))
            {
                ClosePage();
            }

            if (_runs.Count == 0)
            {
                break;
            }

            var run = _runs.Peek();
            if (!PreviousFilesDurable(run.Writer))
            {
                break;
            }

            _ = _runs.Dequeue();
            var buffer = _builder.ConsumeSlice(checked((int)(run.Count * _pageSize)));
            var group = new Group(run.Writer, run.FirstPage, run.Count, buffer);
            AssignRecords(group);
            _groupsInFlight++;
            _concurrency.Submitted(_groupsInFlight);
            group.Concurrency = _groupsInFlight;
            (groups ??= []).Add(group);
        }

        return groups;
    }

    /// <summary>
    /// Determines whether a file's first data page may be submitted.
    /// </summary>
    /// <remarks>
    /// WAL-I7: a file's first data page is submitted "only when every page of the open's previous file is durable"
    /// (and transitively every earlier file of the open, even if an intermediate file closed with zero data pages).
    /// A closed file whose pages and predecessors are durable stays so, so the writer then forgets its predecessor:
    /// otherwise each writer would keep every earlier one, with its file handle, for the life of the open.
    /// </remarks>
    private static bool PreviousFilesDurable(FileWriter file)
    {
        if (file.Previous is { } previous
            && !(previous.Closed && previous.DurablePrefix == previous.BuiltPages && PreviousFilesDurable(previous)))
        {
            return false;
        }

        file.Previous = null;
        return true;
    }

    /// <summary>
    /// Counts the pages each record holds in <paramref name="group"/>. A record holds one extra count from
    /// placement until the group holding its last page is submitted, so it completes only after every page holding
    /// it is durable.
    /// </summary>
    private void AssignRecords(Group group)
    {
        var end = group.FirstPage + group.Count;
        while (_awaitingSubmission.TryPeek(out var record)
            && ReferenceEquals(record.Writer, group.Writer)
            && record.FirstPage < end)
        {
            var endsHere = record.LastPage >= 0 && record.LastPage < end;
            var last = endsHere ? record.LastPage : end - 1;
            var pages = (int)(last - Math.Max(record.FirstPage, group.FirstPage) + 1);
            group.Records.Add((record, pages));
            record.Remaining += pages;
            if (!endsHere)
            {
                break;
            }

            // The group holds the record's last page, so every page holding it is now counted.
            record.Remaining--;
            record.Submitted = true;
            _ = _awaitingSubmission.Dequeue();
        }
    }

    private void StartGroup(Group group)
    {
        ValueTask write;
        try
        {
            group.Started = _timeProvider.GetTimestamp();
            write = group.Writer.File.WriteAsync(WalFormat.DataPageOffset(_pageSize, group.FirstPage), group.Buffers);
        }
        catch (Exception exception)
        {
            write = ValueTask.FromException(exception);
        }

        _ = CompleteGroupAsync(group, write);
    }

    private async Task CompleteGroupAsync(Group group, ValueTask write)
    {
        SyncedGroup? synced = null;
        Exception? error = null;
        try
        {
            await write.ConfigureAwait(false);
            synced = SyncedGroup.Confirm(group);
        }
        catch (Exception exception)
        {
            error = exception;
        }

        group.Buffers.Span[0].Dispose();
        List<DurablePendingRecord>? durable = null;
        lock (_sync)
        {
            _groupsInFlight--;
            if (synced is { } syncedGroup)
            {
                var elapsed = _timeProvider.GetElapsedTime(group.Started);
                _concurrency.Completed(group.Concurrency, elapsed);
                WalMetrics.RecordGroup(elapsed, group.Records.Count, group.Count, group.Concurrency);
                RecordGroupBytes(group.Count * _pageSize);
                group.Writer.MarkDurable(in syncedGroup);
                foreach (var (record, pages) in group.Records)
                {
                    if (record.TryCompleteDurable(in syncedGroup, pages, out var completed))
                    {
                        (durable ??= []).Add(completed);
                    }
                }
            }
            else
            {
                // A failure the storage reported, or a defect, which stops the process (conventions.md §14).
                Stop("WalWriteGroup", error ?? new InvalidOperationException("The write group ended without a sync proof or failure."));
                foreach (var (record, _) in group.Records)
                {
                    record.LostPage = true;
                }
            }

            Wake();
        }

        if (durable is not null)
        {
            FinishDurable(durable);
        }
    }

    /// <summary>
    /// Takes every accepted record which can no longer become durable after a failure: a record with a page in a
    /// failed group, or with a page never submitted. A record whose pages are all in flight waits for them.
    /// </summary>
    private List<PendingRecord>? TakeUnfinishedRecords()
    {
        List<PendingRecord>? failed = null;

        // Records whose pages all became durable have no remaining count and complete as durable.
        foreach (var record in _order)
        {
            if (record.Remaining > 0 && !record.Failing && (!record.Submitted || record.LostPage || _groupsInFlight == 0))
            {
                record.Failing = true;
                (failed ??= []).Add(record);
            }
        }

        while (_queue.TryDequeue(out var queued))
        {
            queued.Failing = true;
            (failed ??= []).Add(queued);
        }

        _placing = null;
        _awaitingSubmission.Clear();
        return failed;
    }

    /// <summary>
    /// Delivers each durable record's single outcome outside the lock using its post-sync proof, then advances the
    /// frontier past records whose <see cref="WalWriteRequest.OnDurable"/> returned cleanly. Once the log has stopped, a
    /// durable record fails instead: a stopped log answers nothing (conventions.md §13, §14).
    /// </summary>
    private void FinishDurable(List<DurablePendingRecord> records)
    {
        foreach (var durable in records)
        {
            durable.EnsureDurable();
            var record = durable.Record;
            try
            {
                if (Volatile.Read(ref _stopped) is null)
                {
                    WalMetrics.RecordDurable(_timeProvider.GetElapsedTime(record.Accepted));
                    record.Request.OnDurable(durable.Proof);
                    record.MarkDelivered(in durable);
                }
                else
                {
                    record.Request.OnFailed(Stopped("The write-ahead log stopped before the record's outcome was delivered."));
                }
            }
            catch (Exception exception)
            {
                // An outcome callback must not throw; one which did leaves its caller's state behind the log.
                lock (_sync)
                {
                    Stop("WalOutcomeCallback", exception);
                }
            }
            finally
            {
                record.Release();
            }
        }

        lock (_sync)
        {
            foreach (var durable in records)
            {
                var record = durable.Record;
                record.MarkFinished(in durable);
                _acceptedBytes -= record.Length + RecordOverhead;
                _outstanding--;
            }

            while (_order.TryPeek(out var first) && first.TryGetFinishedEnd(out var end))
            {
                _ = _order.Dequeue();
                _frontier = end;
            }

            _ = _capacity?.TrySetResult();
            _capacity = null;
            Wake();
        }
    }

    /// <summary>
    /// Delivers a failure outcome for each record that can no longer become durable. It holds no post-sync proof, never
    /// invokes <see cref="WalWriteRequest.OnDurable"/>, and never advances <see cref="_frontier"/> (conventions.md §13,
    /// §14).
    /// </summary>
    private void FinishFailed(List<PendingRecord> records)
    {
        foreach (var record in records)
        {
            try
            {
                record.Request.OnFailed(Stopped("The write-ahead log stopped before the record's outcome was delivered."));
            }
            catch (Exception exception)
            {
                // An outcome callback must not throw; one which did leaves its caller's state behind the log.
                lock (_sync)
                {
                    Stop("WalOutcomeCallback", exception);
                }
            }
            finally
            {
                record.Release();
            }
        }

        lock (_sync)
        {
            foreach (var record in records)
            {
                _acceptedBytes -= record.Length + RecordOverhead;
                _outstanding--;
            }

            _ = _capacity?.TrySetResult();
            _capacity = null;
            Wake();
        }
    }

    /// <summary>
    /// Zero-allocation proof that a write group completed durable storage I/O without error (WAL-I1, conventions.md §13).
    /// Constructed only in <see cref="CompleteGroupAsync"/> strictly after <c>await write</c> succeeds.
    /// </summary>
    private readonly struct SyncedGroup
    {
        private readonly Group _group;
        private readonly bool _synced;

        private SyncedGroup(Group group)
        {
            _group = group;
            _synced = true;
        }

        public static SyncedGroup Confirm(Group group)
        {
            ArgumentNullException.ThrowIfNull(group);
            return new SyncedGroup(group);
        }

        public Group Group => _synced ? _group : throw Uninitialized();

        public void EnsureSynced()
        {
            if (!_synced)
            {
                throw Uninitialized();
            }
        }

        private static InvalidOperationException Uninitialized() =>
            new("A default SyncedGroup does not witness completed durable WAL I/O (conventions.md §13).");
    }

    /// <summary>
    /// Zero-allocation proof that every page holding a <see cref="PendingRecord"/> has completed durable WAL I/O
    /// (WAL-I1, WAL-I8, conventions.md §13).
    /// </summary>
    private readonly struct DurablePendingRecord
    {
        private readonly PendingRecord _record;
        private readonly WalDurableRecord _proof;
        private readonly bool _durable;

        private DurablePendingRecord(PendingRecord record, WalDurableRecord proof)
        {
            proof.EnsureDurable();
            _record = record;
            _proof = proof;
            _durable = true;
        }

        public static DurablePendingRecord Mint(PendingRecord record, in SyncedGroup synced)
        {
            synced.EnsureSynced();
            Debug.Assert(record.Remaining == 0 && !record.Failing, "A durable record has no remaining pages and is not failing.");
            var page = new WalPage(record.Writer!.Number, checked((uint)record.FirstPage));
            return new DurablePendingRecord(record, new WalDurableRecord(record.Position, page));
        }

        public PendingRecord Record => _durable ? _record : throw Uninitialized();

        public WalDurableRecord Proof => _durable ? _proof : throw Uninitialized();

        public void EnsureDurable()
        {
            if (!_durable)
            {
                throw Uninitialized();
            }

            _proof.EnsureDurable();
        }

        private static InvalidOperationException Uninitialized() =>
            new("A default DurablePendingRecord does not witness a durable WAL record (conventions.md §13).");
    }

    private sealed class PendingRecord(WalWriteRequest request, ArcBuffer header, ArcBuffer payload, int length, long accepted)
    {
        private ArcBuffer.MemoryEnumerator _memory;
        private ReadOnlyMemory<byte> _current;
        private int _part = -1;
        private bool _delivered;
        private WalPosition? _finishedEnd;

        public WalWriteRequest Request { get; } = request;

        public int Length { get; } = length;

        /// <summary>
        /// Gets the timestamp at which the log accepted the record.
        /// </summary>
        public long Accepted { get; } = accepted;

        public int Placed { get; private set; }

        public FileWriter? Writer { get; set; }

        public long FirstPage { get; set; } = -1;

        public long LastPage { get; set; } = -1;

        public WalPosition Position { get; set; }

        public WalPosition End { get; set; }

        public int Remaining { get; set; } = 1;

        public bool Failing { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the group holding the record's last page has been submitted.
        /// </summary>
        public bool Submitted { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether a page holding the record failed its write.
        /// </summary>
        public bool LostPage { get; set; }

        /// <summary>
        /// Accounts for <paramref name="pages"/> confirmed durable by <paramref name="synced"/> and mints a
        /// <see cref="DurablePendingRecord"/> once every page holding the record is durable.
        /// </summary>
        public bool TryCompleteDurable(in SyncedGroup synced, int pages, out DurablePendingRecord completed)
        {
            synced.EnsureSynced();
            Remaining -= pages;
            if (Remaining == 0 && !Failing)
            {
                completed = DurablePendingRecord.Mint(this, in synced);
                return true;
            }

            completed = default;
            return false;
        }

        /// <summary>
        /// Records that <see cref="WalWriteRequest.OnDurable"/> returned cleanly for this record, gated by its
        /// <see cref="DurablePendingRecord"/> proof.
        /// </summary>
        public void MarkDelivered(in DurablePendingRecord durable)
        {
            durable.EnsureDurable();
            Debug.Assert(ReferenceEquals(durable.Record, this), "A durable proof marks only its own PendingRecord delivered.");
            _delivered = true;
        }

        /// <summary>
        /// Marks the record eligible to advance <see cref="_frontier"/> once its <see cref="WalWriteRequest.OnDurable"/>
        /// callback has returned cleanly.
        /// </summary>
        public void MarkFinished(in DurablePendingRecord durable)
        {
            durable.EnsureDurable();
            Debug.Assert(ReferenceEquals(durable.Record, this), "A durable proof marks only its own PendingRecord finished.");
            if (_delivered)
            {
                _finishedEnd = End;
            }
        }

        /// <summary>
        /// Returns the record's durable end position only after its <see cref="WalWriteRequest.OnDurable"/> completed
        /// with a verified <see cref="DurablePendingRecord"/>.
        /// </summary>
        public bool TryGetFinishedEnd(out WalPosition end)
        {
            if (_finishedEnd is { } finished)
            {
                end = finished;
                return true;
            }

            end = default;
            return false;
        }

        /// <summary>
        /// Returns up to <paramref name="count"/> of the record's next bytes.
        /// </summary>
        public ReadOnlySpan<byte> Next(int count)
        {
            while (_current.IsEmpty)
            {
                if (_part is 0 or 1 && _memory.MoveNext())
                {
                    _current = _memory.Current;
                    continue;
                }

                // Advance to the next part: the header, then the payload.
                _part = _part switch
                {
                    -1 => 0,
                    0 => 1,
                    _ => throw new InvalidOperationException("The record ended before its length."),
                };
                _memory = (_part == 0 ? header : payload).MemorySegments;
            }

            var result = _current.Span[..Math.Min(count, _current.Length)];
            _current = _current[result.Length..];
            Placed += result.Length;
            return result;
        }

        public void Release()
        {
            header.Dispose();
            payload.Dispose();
        }
    }

    private sealed class BuiltRun(FileWriter writer, long firstPage)
    {
        public FileWriter Writer { get; } = writer;

        public long FirstPage { get; } = firstPage;

        public long Count { get; set; } = 1;
    }

    private sealed class Group(FileWriter writer, long firstPage, long count, ArcBuffer buffer)
    {
        public FileWriter Writer { get; } = writer;

        public long FirstPage { get; } = firstPage;

        public long Count { get; } = count;

        public ReadOnlyMemory<ArcBuffer> Buffers { get; } = new[] { buffer };

        public List<(PendingRecord Record, int Pages)> Records { get; } = [];

        /// <summary>
        /// Gets or sets the timestamp at which the group's write was submitted.
        /// </summary>
        public long Started { get; set; }

        /// <summary>
        /// Gets or sets the number of groups in flight when the group was submitted, itself included.
        /// </summary>
        public int Concurrency { get; set; }
    }

    /// <summary>
    /// An unpublished preallocated file in preparation: its data-page capacity, staging task, hurry source (wakes
    /// paced waits at <see cref="PrepareReadyFill"/> or on a roll), and cancel source (aborts staging on disposal).
    /// </summary>
    private sealed class PreparedFile(long dataPages, Task<PreparedWalFile> file, CancellationTokenSource hurry, CancellationTokenSource cancel)
    {
        public long DataPages { get; } = dataPages;

        public Task<PreparedWalFile> File { get; } = file;

        public CancellationTokenSource Hurry { get; } = hurry;

        public CancellationTokenSource Cancel { get; } = cancel;

        public bool Hurried { get; set; }
    }

    /// <summary>
    /// Tracks the pages one open wrote to one of its files.
    /// </summary>
    private sealed class FileWriter(ulong number, WalFile file, long dataPageLimit, FileWriter? previous, long started)
    {
        private readonly List<(long Start, long End)> _durableRanges = [];

        public ulong Number { get; } = number;

        public WalFile File { get; } = file;

        public long DataPageLimit { get; } = dataPageLimit;

        public FileWriter? Previous { get; set; } = previous;

        /// <summary>
        /// Gets the timestamp at which the log started appending to the file.
        /// </summary>
        public long Started { get; } = started;

        public long NextPage { get; set; }

        public long BuiltPages { get; set; }

        public long DurablePrefix { get; private set; }

        public bool Closed { get; set; }

        /// <summary>
        /// Gets a value indicating whether the file was allocated without a zero-fill, in bulk mode.
        /// </summary>
        public bool Allocated { get; init; }

        /// <summary>
        /// Returns whether every data page from <paramref name="first"/> through <paramref name="last"/> has completed
        /// durable I/O; the caller holds the log's lock.
        /// </summary>
        public bool IsDurable(long first, long last)
        {
            for (var page = Math.Max(first, DurablePrefix); page <= last; page++)
            {
                if (!InDurableRange(page))
                {
                    return false;
                }
            }

            return true;
        }

        public void MarkDurable(in SyncedGroup synced)
        {
            synced.EnsureSynced();
            var group = synced.Group;
            Debug.Assert(ReferenceEquals(group.Writer, this), "A synced group marks pages durable only on its own FileWriter.");
            _durableRanges.Add((group.FirstPage, group.FirstPage + group.Count));
            var merged = true;
            while (merged)
            {
                merged = false;
                for (var index = 0; index < _durableRanges.Count; index++)
                {
                    if (_durableRanges[index].Start == DurablePrefix)
                    {
                        DurablePrefix = _durableRanges[index].End;
                        _durableRanges.RemoveAt(index);
                        merged = true;
                        break;
                    }
                }
            }
        }

        private bool InDurableRange(long page)
        {
            foreach (var (start, end) in _durableRanges)
            {
                if (page >= start && page < end)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// One file visible to readers.
    /// </summary>
    private sealed record LogFile(ulong Number, WalFile Handle, WalFileHeader? Header, FileWriter? Writer);

    private sealed class ConcurrentAppendRequestPool
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<AppendRequest> _requests = new();

        public AppendRequest Rent() => _requests.TryDequeue(out var request) ? request : new AppendRequest(this);

        public void Return(AppendRequest request) => _requests.Enqueue(request);
    }

    /// <summary>
    /// A pooled request with an empty header which completes a value task with the record's position.
    /// </summary>
    private sealed class AppendRequest(ConcurrentAppendRequestPool pool) : WalWriteRequest, IValueTaskSource<WalPosition>
    {
        private ManualResetValueTaskSourceCore<WalPosition> _core = new() { RunContinuationsAsynchronously = true };

        public ValueTask<WalPosition> Prepare(ArcBuffer payload)
        {
            Header = default;
            Payload = payload;
            return new ValueTask<WalPosition>(this, _core.Version);
        }

        public void Abandon(ConcurrentAppendRequestPool owner)
        {
            Payload = default;
            _core.Reset();
            owner.Return(this);
        }

        public WalPosition GetResult(short token)
        {
            try
            {
                return _core.GetResult(token);
            }
            finally
            {
                Payload = default;
                _core.Reset();
                pool.Return(this);
            }
        }

        public ValueTaskSourceStatus GetStatus(short token) => _core.GetStatus(token);

        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) =>
            _core.OnCompleted(continuation, state, token, flags);

        protected internal override void OnDurable(WalDurableRecord record) => _core.SetResult(record.Position);

        protected internal override void OnFailed(Exception error) => _core.SetException(error);
    }
}
