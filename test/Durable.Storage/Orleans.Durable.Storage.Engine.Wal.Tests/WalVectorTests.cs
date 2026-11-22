using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Orleans.Durable.Storage.Engine.Wal.Simulation;
using Orleans.Durable.Storage.Testing;
using static Orleans.Durable.Storage.Testing.SharedVectors;

namespace Orleans.Durable.Storage.Engine.Wal.Tests;

/// <summary>
/// Checks the shared the WAL vectors under <c>test/Durable.Storage/shared-vectors/wal</c> (wal.md §7): header pages, data pages
/// of frames, the hole page, and the worked example written by the log itself, with a reviewed rendering of it.
/// </summary>
public sealed class WalVectorTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    private const string Area = "wal";

    [Fact]
    public void HeaderVectorsMatchTheCodecInBothDirections()
    {
        foreach (var vector in Cases(Area, "headers.json"))
        {
            var pageSize = vector.GetProperty("pageSize").GetInt32();
            var window = vector.GetProperty("writeWindowPages").GetInt32();
            var firstOfOpen = vector.GetProperty("firstOfOpen").GetBoolean();
            var page = new byte[pageSize];
            WalFormat.WriteHeader(page, window, firstOfOpen);

            AssertBytes(vector, Trim(page));
            Assert.Equal(new WalFileHeader(pageSize, window, firstOfOpen), WalTestHelpers.ReadHeader(Page(vector, pageSize)));
        }
    }

    /// <summary>
    /// Each header rejection vector is rejected for its reason, which the WAL classifies as the vector's outcome: damage
    /// (WAL-I3) or unsupported (conventions.md §15).
    /// </summary>
    [Fact]
    public void HeaderRejectionVectorsAreRejectedForTheirReasonAndClassifiedAsTheirOutcome()
    {
        foreach (var vector in Cases(Area, "header-rejections.json"))
        {
            var page = Page(vector, vector.GetProperty("pageSize").GetInt32());
            var decoding = WalFormat.DecodeHeader(page);
            var rejection = Assert.IsType<InvalidDataException>(decoding.Value);
            Assert.Equal(Text(vector, "reason"), DecodeRejection.ReasonOf(rejection));
            switch (Text(vector, "outcome"))
            {
                case "damage":
                    Assert.True(WriteAheadLog.ClassifyHeader(decoding) is null, $"'{Name(vector)}' was not classified as damage.");
                    break;
                case "unsupported":
                    var unsupported = Assert.Throws<WalUnsupportedFormatException>(() => WriteAheadLog.ClassifyHeader(decoding));
                    Assert.Same(rejection, unsupported.InnerException);
                    break;
                default:
                    throw new InvalidDataException($"Unknown outcome in '{Name(vector)}'.");
            }
        }
    }

    [Fact]
    public void PageVectorsMatchTheFrameLayoutInBothDirections()
    {
        foreach (var vector in Cases(Area, "pages.json"))
        {
            var pageSize = vector.GetProperty("pageSize").GetInt32();
            var frames = vector.GetProperty("frames").EnumerateArray().ToArray();
            var page = new byte[pageSize];
            if (frames is [var only] && Text(only, "kind") == "hole")
            {
                WalFormat.WriteHolePage(page);
                AssertBytes(vector, Trim(page));
                var decoded = Page(vector, pageSize);
                Assert.True(WalFormat.IsValidPage(decoded) && WalFormat.IsHolePage(decoded) && !WalFormat.IsWellFormed(decoded));
                continue;
            }

            var offset = WalFormat.CrcLength;
            foreach (var frame in frames)
            {
                var kind = ParseKind(Text(frame, "kind"));
                var payload = Hex(frame, "payloadHex");
                Assert.Equal(frame.GetProperty("length").GetInt32(), payload.Length);
                BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(offset), WalFormat.EncodeFrame(kind, payload.Length));
                offset += WalFormat.FrameWordLength;
                if (kind == FrameKind.First)
                {
                    BinaryPrimitives.WriteUInt64LittleEndian(page.AsSpan(offset), UInt64(frame, "total"));
                    offset += sizeof(ulong);
                }

                payload.CopyTo(page, offset);
                offset += payload.Length;
            }

            WalFormat.SealPage(page);
            AssertBytes(vector, Trim(page));

            var bytes = Page(vector, pageSize);
            Assert.True(WalFormat.IsValidPage(bytes) && WalFormat.IsWellFormed(bytes) && !WalFormat.IsHolePage(bytes), $"'{Name(vector)}' is not a well-formed data page.");
            Assert.Equal(frames.Select(Describe), ReadFrames(bytes));
        }
    }

    [Fact]
    public void PageRejectionVectorsAreInvalidOrIllFormedForTheirReason()
    {
        foreach (var vector in Cases(Area, "page-rejections.json"))
        {
            var page = Page(vector, vector.GetProperty("pageSize").GetInt32());
            var rejected = Text(vector, "outcome") switch
            {
                "invalid" => !WalFormat.IsValidPage(page),
                "ill-formed" => WalFormat.IsValidPage(page) && !WalFormat.IsWellFormed(page) && !WalFormat.IsHolePage(page),
                _ => throw new InvalidDataException($"Unknown outcome in '{Name(vector)}'."),
            };
            Assert.True(rejected, $"'{Name(vector)}' was not rejected as {Text(vector, "outcome")}.");
            Assert.Equal(Text(vector, "reason"), DecodeRejection.ReasonOf(WalFormat.ValidatePage(page)!));
        }
    }

    /// <summary>
    /// Writes the worked example through the log and compares the file's header and record pages with the vector.
    /// </summary>
    [Fact]
    public async Task TheWorkedExampleMatchesTheLogsOwnLayout()
    {
        using var document = Load(Area, "example.json");
        var vector = document.RootElement;
        var example = await WriteWorkedExampleAsync(vector);
        var pageSize = example.PageSize;
        Assert.Equal(Position(vector.GetProperty("position")), example.RecordPosition);
        Assert.Equal(Position(vector.GetProperty("frontier")), example.Frontier);
        Assert.Equal(example.Payload, example.ReadBack);

        var bytes = example.Bytes;
        AssertBytes(vector, Trim(bytes.AsSpan(0, pageSize)), "headerHex");
        foreach (var page in vector.GetProperty("pages").EnumerateArray())
        {
            var index = page.GetProperty("index").GetInt32();
            var content = bytes.AsSpan((int)WalFormat.DataPageOffset(pageSize, index), pageSize);
            Assert.True(page.GetProperty("hex").GetString() == Convert.ToHexStringLower(Trim(content)), string.Create(CultureInfo.InvariantCulture, $"Data page {index} differs from the vector."));
        }

        var firstPage = vector.GetProperty("pages")[0].GetProperty("index").GetInt32();
        Assert.Equal((int)WalFormat.DataPageOffset(pageSize, firstPage + vector.GetProperty("pages").GetArrayLength()), bytes.Length);
    }

    /// <summary>
    /// Renders the worked example as the log writes and scans it (wal.md §7): the header page, each data page's
    /// frames and the record bytes they carry, and the entries a scan returns. The vector pins the bytes; the snapshot
    /// is their reviewed reading.
    /// </summary>
    [Fact]
    public async Task TheWorkedExampleReadsAsTheFormatDescribesIt()
    {
        using var document = Load(Area, "example.json");
        await Verify(Render(await WriteWorkedExampleAsync(document.RootElement)));
    }

    /// <summary>
    /// Writes the worked example through the log: opens which write nothing up to the example's file, then an open
    /// which fills the data pages before the example's first page with one whole-page record each and appends the
    /// example's record. Within that open, once the frontier passes the record, the record reads back and a scan observes
    /// every entry.
    /// </summary>
    private static async Task<WorkedExample> WriteWorkedExampleAsync(JsonElement vector)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var pageSize = vector.GetProperty("pageSize").GetInt32();
        var file = UInt64(vector, "file");
        var options = WalTestHelpers.Options(pageSize) with { PreallocateFiles = false };
        var storage = new SimulatedWalStorage();
        await (await WriteAheadLog.CreateAsync(storage, WalTestHelpers.LogPath, options, cancellationToken)).DisposeAsync();
        for (var open = 2UL; open < file; open++)
        {
            await (await WriteAheadLog.OpenAsync(storage, WalTestHelpers.LogPath, options, cancellationToken)).DisposeAsync();
        }

        var firstPage = vector.GetProperty("pages")[0].GetProperty("index").GetInt32();
        var payload = Enumerable.Range(0, vector.GetProperty("recordLength").GetInt32()).Select(static index => (byte)(index % 251)).ToArray();
        WalPosition position;
        WalPosition frontier;
        byte[] readBack;
        var scan = new List<string>();
        await using (var wal = await WriteAheadLog.OpenAsync(storage, WalTestHelpers.LogPath, options, cancellationToken))
        {
            for (var page = 0; page < firstPage; page++)
            {
                _ = await WalTestHelpers.AppendAsync(wal, WalTestHelpers.Bytes(pageSize - 8, page));
            }

            position = await WalTestHelpers.AppendAsync(wal, payload);
            await WalTestHelpers.WaitForFrontierAsync(wal, position);
            frontier = wal.DurableFrontier;
            readBack = (await wal.ReadAsync(position, cancellationToken)).ToBytes();
            await foreach (var entry in wal.ReadAllAsync(default, cancellationToken))
            {
                scan.Add(entry switch
                {
                    WalRecord record => string.Create(CultureInfo.InvariantCulture, $"a record at {At(record.Position)} on page ({record.Page.File}, {record.Page.Index}), {record.Payload.Length} bytes"),
                    WalTorn torn => $"torn from {At(torn.Start)} to {At(torn.End)}",
                    WalCorruption corruption => $"corruption from {At(corruption.Start)} to {At(corruption.End)}",
                    null => throw new InvalidOperationException("An entry has no value."),
                });
            }
        }

        return new WorkedExample(pageSize, file, payload, position, frontier, readBack, scan, storage.ReadDurable(WalTestHelpers.LogPath, file));
    }

    /// <summary>
    /// Renders a log file page by page: the header's fields, then each data page's validity and frames, with the
    /// record bytes each frame carries.
    /// </summary>
    private static string Render(WorkedExample example)
    {
        var pageSize = example.PageSize;
        var bytes = example.Bytes;
        var pages = WalFormat.DataPageCount(bytes.Length, pageSize);
        var text = new StringBuilder();
        _ = text.AppendLine(CultureInfo.InvariantCulture, $"File {example.File}: a header page and data pages 0 to {pages - 1}, P = {pageSize}.");
        _ = text.AppendLine();
        var header = WalTestHelpers.ReadHeader(bytes.AsSpan(0, pageSize));
        if (header is not { } fields)
        {
            _ = text.AppendLine("Header page at 0x0000: damaged.");
        }
        else
        {
            var open = fields.FirstOfOpen ? "the first file of its open" : "a later file of its open";
            _ = text.AppendLine(CultureInfo.InvariantCulture, $"Header page at 0x0000: CLFL version {WalFormat.Version}, page size {fields.PageSize}, write window {fields.WriteWindowPages} pages, {open}.");
        }

        var recordBytes = 0L;
        for (var index = 0L; index < pages; index++)
        {
            var start = WalFormat.DataPageOffset(pageSize, index);
            var page = bytes.AsSpan((int)start, pageSize);
            _ = text.AppendLine();
            _ = text.AppendLine(CultureInfo.InvariantCulture, $"Data page {index} at 0x{start:X4}: {(WalFormat.ValidatePage(page) is { } rejection ? DecodeRejection.ReasonOf(rejection) : "valid and well-formed")}.");
            var end = WalFormat.CrcLength;
            foreach (var frame in Frames(page))
            {
                var carries = frame.Kind switch
                {
                    FrameKind.End => string.Create(CultureInfo.InvariantCulture, $"a hole of {frame.Length} bytes"),
                    FrameKind.Full => string.Create(CultureInfo.InvariantCulture, $"a whole record of {frame.Length} bytes"),
                    FrameKind.First => string.Create(CultureInfo.InvariantCulture, $"record bytes [0, {frame.Length}) of {frame.Total}"),
                    FrameKind.Continuation => string.Create(CultureInfo.InvariantCulture, $"record bytes [{recordBytes}, {recordBytes + frame.Length})"),
                    _ => throw new InvalidDataException("A frame word has no kind."),
                };
                recordBytes = frame.Kind == FrameKind.First ? frame.Length : recordBytes + frame.Length;
                _ = text.AppendLine(CultureInfo.InvariantCulture, $"  0x{start + frame.Offset:X4}  {frame.Kind,-12}  0x{frame.Word:X8}  {carries}");
                end = frame.PayloadOffset + frame.Length;
            }

            if (pageSize - end >= WalFormat.FrameWordLength)
            {
                _ = text.AppendLine(CultureInfo.InvariantCulture, $"  0x{start + end:X4}  {"End",-12}  0x{0:X8}  zero to 0x{start + pageSize:X4}");
            }
        }

        _ = text.AppendLine();
        _ = text.AppendLine("A scan returns:");
        foreach (var entry in example.Scan)
        {
            _ = text.AppendLine(CultureInfo.InvariantCulture, $"  {entry}");
        }

        _ = text.AppendLine();
        _ = text.AppendLine(CultureInfo.InvariantCulture, $"The {example.Payload.Length}-byte record's position is {At(example.RecordPosition)}, and the durable frontier after it {At(example.Frontier)}.");
        return text.ToString();
    }

    private static string At(WalPosition position) => string.Create(CultureInfo.InvariantCulture, $"({position.File}, 0x{position.Offset:X4})");

    private static WalPosition Position(JsonElement element) => new(UInt64(element, "file"), UInt64(element, "offset"));

    private static FrameKind ParseKind(string kind) => kind switch
    {
        "full" => FrameKind.Full,
        "first" => FrameKind.First,
        "continuation" => FrameKind.Continuation,
        _ => throw new InvalidDataException($"Unknown frame kind '{kind}'."),
    };

    private static string Describe(JsonElement frame) =>
        string.Create(CultureInfo.InvariantCulture, $"{Text(frame, "kind")} {frame.GetProperty("length").GetInt32()} {(frame.TryGetProperty("total", out var total) ? total.GetString() : "-")} {Text(frame, "payloadHex")}");

    /// <summary>
    /// Reads a well-formed data page's frames in the form <see cref="Describe"/> renders them.
    /// </summary>
    private static List<string> ReadFrames(byte[] page) =>
    [
        .. Frames(page).Select(frame => string.Create(
            CultureInfo.InvariantCulture,
            $"{frame.Kind.ToString().ToLowerInvariant()} {frame.Length} {(frame.Total is { } total ? total.ToString(CultureInfo.InvariantCulture) : "-")} {Convert.ToHexStringLower(page.AsSpan(frame.PayloadOffset, frame.Length))}")),
    ];

    /// <summary>
    /// Walks a data page's frames from offset 4 up to the End word or the page's last 4 bytes.
    /// </summary>
    private static List<Frame> Frames(ReadOnlySpan<byte> page)
    {
        var frames = new List<Frame>();
        var offset = WalFormat.CrcLength;
        while (offset <= page.Length - WalFormat.FrameWordLength)
        {
            var word = BinaryPrimitives.ReadUInt32LittleEndian(page[offset..]);
            if (word == 0)
            {
                break;
            }

            var payload = offset + WalFormat.FrameWordLength;
            ulong? total = null;
            if (WalFormat.GetKind(word) == FrameKind.First)
            {
                total = BinaryPrimitives.ReadUInt64LittleEndian(page[payload..]);
                payload += sizeof(ulong);
            }

            var frame = new Frame(offset, word, total, payload);
            frames.Add(frame);
            offset = payload + frame.Length;
        }

        return frames;
    }

    /// <summary>
    /// Expands a page vector: its bytes through the last non-zero byte, then zero to the page size.
    /// </summary>
    private static byte[] Page(JsonElement vector, int pageSize)
    {
        var page = new byte[pageSize];
        Hex(vector).CopyTo(page, 0);
        return page;
    }

    private static ReadOnlySpan<byte> Trim(ReadOnlySpan<byte> page) => page[..(page.LastIndexOfAnyExcept((byte)0) + 1)];

    /// <summary>
    /// A frame at <paramref name="Offset"/> of its page: its word, a First frame's total record length, and the offset
    /// of its fragment.
    /// </summary>
    private readonly record struct Frame(int Offset, uint Word, ulong? Total, int PayloadOffset)
    {
        public FrameKind Kind => WalFormat.GetKind(Word);

        public int Length => WalFormat.GetLength(Word);
    }

    /// <summary>
    /// The worked example as one open of the log wrote it: the record, its position and the durable frontier after it,
    /// the record read back, the scan's entries, and the bytes of the example's file.
    /// </summary>
    private sealed record WorkedExample(
        int PageSize,
        ulong File,
        byte[] Payload,
        WalPosition RecordPosition,
        WalPosition Frontier,
        byte[] ReadBack,
        IReadOnlyList<string> Scan,
        byte[] Bytes);
}
