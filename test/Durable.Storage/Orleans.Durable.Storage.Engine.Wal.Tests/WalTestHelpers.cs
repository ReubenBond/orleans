using Microsoft.Extensions.Time.Testing;
using Orleans.Durable.Storage.Diagnostics;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Wal.Tests;

internal static class WalTestHelpers
{
    internal const string LogPath = "wal";

    private static readonly TimeSpan FrontierDeadline = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Reads a header page as the WAL does: its fields, or <see langword="null"/> for damage, or
    /// <see cref="WalUnsupportedFormatException"/> for the magic with another version.
    /// </summary>
    internal static WalFileHeader? ReadHeader(ReadOnlySpan<byte> page) => WriteAheadLog.ClassifyHeader(WalFormat.DecodeHeader(page));

    internal static WalOptions Options(int pageSize = 4096, long fileSize = 1 << 20, int window = 256, int groups = 2) => new()
    {
        PageSize = pageSize,
        FileSize = fileSize,
        WriteWindowPages = window,
        MinWriteGroupsInFlight = groups,
        MaxWriteGroupsInFlight = groups,
    };

    internal static byte[] Bytes(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    /// <summary>
    /// Takes the reports the current test raised after the first <paramref name="since"/> reports, and asserts they are
    /// one report of <paramref name="operation"/> on <paramref name="path"/>, <see langword="null"/> for a defect
    /// (conventions.md §14).
    /// </summary>
    internal static ReportedFatalError AssertReported(long since, FatalErrorSite operation, string? path)
    {
        var report = Assert.Single(FatalErrors.TakeMine(since));
        Assert.Equal((operation, path), (report.Site, report.Path));
        return report;
    }

    /// <summary>
    /// Takes the reports the current test raised after the first <paramref name="since"/> reports, and asserts they are
    /// one report of the defect named <paramref name="defect"/> (conventions.md §14).
    /// </summary>
    internal static ReportedFatalError AssertDefect(long since, string defect)
    {
        var report = Assert.Single(FatalErrors.TakeMine(since));
        Assert.Equal((FatalErrorSite.Defect, null, defect), (report.Site, report.Path, report.Failure.Defect));
        return report;
    }

    /// <summary>
    /// Returns the path of log file <paramref name="number"/> in the directory at <see cref="LogPath"/>, by which the
    /// simulated storage reports a failure of its storage.
    /// </summary>
    internal static string LogFile(ulong number) => System.IO.Path.Combine(LogPath, WalFormat.GetFileName(number));

    /// <summary>
    /// Advances <paramref name="time"/> until <paramref name="operation"/>, which waits out contention on it, completes,
    /// and returns how far it advanced.
    /// </summary>
    internal static async Task<TimeSpan> AdvanceUntilCompletedAsync(FakeTimeProvider time, Task operation)
    {
        var started = time.GetUtcNow();
        while (!operation.IsCompleted)
        {
            Assert.True(time.GetUtcNow() - started < FileSystemAnswers.ContentionBound * 2, "The contention outlasted its bound.");
            time.Advance(TimeSpan.FromMilliseconds(250));
            await Task.Delay(TimeSpan.FromMilliseconds(1), TimeProvider.System, TestContext.Current.CancellationToken);
        }

        return time.GetUtcNow() - started;
    }

    internal static Task PauseAsync(int milliseconds = 1) =>
        Task.Delay(TimeSpan.FromMilliseconds(milliseconds), TimeProvider.System, TestContext.Current.CancellationToken);

    /// <summary>
    /// Waits for every task, whatever its outcome, so that no append a test started outlives it.
    /// </summary>
    internal static async Task SettleAsync(IEnumerable<Task> tasks)
    {
        var all = Task.WhenAll(tasks);
        await all.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        _ = all.Exception;
    }

    internal static async ValueTask<WalPosition> AppendAsync(WriteAheadLog wal, byte[] bytes)
    {
        using var writer = new ArcBufferWriter();
        writer.Write(bytes);
        using var payload = writer.PeekSlice(writer.Length);
        return await wal.AppendAsync(payload, TestContext.Current.CancellationToken);
    }

    internal static RecordingRequest Request(byte[] bytes) => new(default, Buffer(bytes));

    /// <summary>
    /// Returns an owned buffer of <paramref name="bytes"/> in aligned pages.
    /// </summary>
    internal static ArcBuffer Buffer(byte[] bytes)
    {
        using var writer = ArcBufferWriter.CreateAligned();
        writer.Write(bytes);
        return writer.ConsumeSlice(bytes.Length);
    }

    /// <summary>
    /// Waits until <see cref="WriteAheadLog.DurableFrontier"/> passes the record at <paramref name="position"/>. An append
    /// completes from its record's <see cref="WalWriteRequest.OnDurable"/>, before the frontier passes the record
    /// (WAL-I8), so a test which reads the frontier or scans after an append waits here first. Fails when the frontier
    /// has not passed the record within <see cref="FrontierDeadline"/>, so a regression fails rather than hangs.
    /// </summary>
    internal static async Task WaitForFrontierAsync(WriteAheadLog wal, WalPosition position)
    {
        var started = TimeProvider.System.GetTimestamp();
        while (wal.DurableFrontier is var frontier && frontier <= position)
        {
            if (TimeProvider.System.GetElapsedTime(started) > FrontierDeadline)
            {
                Assert.Fail($"The durable frontier {frontier} did not pass the record at {position} within {FrontierDeadline}.");
            }

            await PauseAsync();
        }
    }

    /// <summary>
    /// Asserts that a stopped log answers nothing (conventions.md §14): each of its operations which answers throws a
    /// failure which carries the reported one that stopped it, so no caller reports it again.
    /// </summary>
    internal static async Task AssertAnswersNothingAsync(WriteAheadLog wal)
    {
        var token = TestContext.Current.CancellationToken;
        AssertCarriesReport(Record.Exception(() => wal.DurableFrontier));
        AssertCarriesReport(Record.Exception(() => wal.CurrentFile));
        AssertCarriesReport(Record.Exception(() => wal.ReplayedEnd(1)));
        AssertCarriesReport(await Record.ExceptionAsync(async () => await AppendAsync(wal, [9])));
        AssertCarriesReport(await Record.ExceptionAsync(async () => _ = (await wal.ReadAsync(new WalPosition(1, 4096), token)).ToBytes()));
        AssertCarriesReport(await Record.ExceptionAsync(async () => _ = (await wal.ReadAsync(new WalPage(1, 0), 0, token)).ToBytes()));
        AssertCarriesReport(await Record.ExceptionAsync(async () => _ = await ReadAllAsync(wal)));
        AssertCarriesReport(await Record.ExceptionAsync(async () => await wal.ReleaseAsync(1, token)));
    }

    /// <summary>
    /// Waits until the log has stopped: until reading <see cref="WriteAheadLog.DurableFrontier"/> throws. Fails when it has not
    /// stopped within <see cref="FrontierDeadline"/>.
    /// </summary>
    internal static async Task WaitForStopAsync(WriteAheadLog wal)
    {
        var started = TimeProvider.System.GetTimestamp();
        while (Record.Exception(() => wal.DurableFrontier) is null)
        {
            if (TimeProvider.System.GetElapsedTime(started) > FrontierDeadline)
            {
                Assert.Fail($"The log did not stop within {FrontierDeadline}.");
            }

            await PauseAsync();
        }
    }

    private static void AssertCarriesReport(Exception? exception)
    {
        Assert.NotNull(exception);
        Assert.True(FatalErrorHandler.IsReported(exception), $"The stopped log answered with a failure which carries no report: {exception}");
    }

    internal static async Task<List<Observed>> ReadAllAsync(WriteAheadLog wal, WalPosition from = default)
    {
        var entries = new List<Observed>();
        await foreach (var entry in wal.ReadAllAsync(from, TestContext.Current.CancellationToken))
        {
            entries.Add(entry switch
            {
                WalRecord record => new Observed(record.Position, record.Payload.ToArray(), null),
                WalTorn torn => new Observed(torn.Start, null, entry),
                WalCorruption corruption => new Observed(corruption.Start, null, entry),
                null => throw new InvalidOperationException("An entry has no value."),
            });
        }

        return entries;
    }

    internal static byte[] ToBytes(this WalRead read) => read switch
    {
        ArcBuffer buffer => Take(buffer),
        WalCorruption corruption => throw new InvalidDataException($"Corruption {corruption}."),
        null => throw new InvalidOperationException("The read has no value."),
    };

    private static byte[] Take(ArcBuffer buffer)
    {
        using (buffer)
        {
            return buffer.ToArray();
        }
    }

    internal sealed record Observed(WalPosition Position, byte[]? Payload, WalEntry? Fault)
    {
        public bool IsRecord => Payload is not null;

        public bool IsTorn => Fault is { } fault && fault.TryGetValue(out WalTorn _);

        public bool IsCorruption => Fault is { } fault && fault.TryGetValue(out WalCorruption _);
    }

    /// <summary>
    /// A write request which records its outcome. Disposing it releases its header and payload.
    /// </summary>
    internal sealed class RecordingRequest : WalWriteRequest, IDisposable
    {
        private readonly TaskCompletionSource<WalPosition> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public RecordingRequest(ArcBuffer header, ArcBuffer payload)
        {
            Header = header;
            Payload = payload;
        }

        public Task<WalPosition> Completion => _completion.Task;

        public int Outcomes { get; private set; }

        public WalPage Page { get; private set; }

        public void Dispose()
        {
            Header.Dispose();
            Payload.Dispose();
        }

        protected internal override void OnDurable(WalDurableRecord record)
        {
            Outcomes++;
            Page = record.Page;
            _ = _completion.TrySetResult(record.Position);
        }

        protected internal override void OnFailed(Exception error)
        {
            Outcomes++;
            _ = _completion.TrySetException(error);
        }
    }
}
