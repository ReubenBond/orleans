using System.Diagnostics;
using System.Globalization;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Wal;

namespace Orleans.Durable.Storage.Bench;

[GenerateSerializer]
[Immutable]
public sealed record BenchLifecycleEvent(
    [property: Id(0)] DateTimeOffset UtcTimestamp,
    [property: Id(1)] string SiloName,
    [property: Id(2)] string Category,
    [property: Id(3)] int EventId,
    [property: Id(4)] string Level,
    [property: Id(5)] string Message);

/// <summary>
/// Bounded timestamped ring buffer of storage lifecycle events (WAL file creation/deletion, ledger open/seal/drop,
/// segment drains, LSM/jobs blob flushes, CASPaxos voter events, and <c>Orleans.Durable.Storage*</c> logs).
/// </summary>
public sealed class BenchEventLogStore(string siloName, int capacity = 8192)
{
    private readonly Lock _lock = new();
    private readonly BenchLifecycleEvent?[] _ring = new BenchLifecycleEvent?[capacity];
    private int _head;
    private int _count;

    public void Record(string category, int eventId, string level, string message, bool writeConsole = false)
    {
        var now = DateTimeOffset.UtcNow;
        var entry = new BenchLifecycleEvent(now, siloName, category, eventId, level, message);
        lock (_lock)
        {
            _ring[_head] = entry;
            _head = (_head + 1) % _ring.Length;
            if (_count < _ring.Length)
            {
                _count++;
            }
        }

        if (writeConsole)
        {
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"[{now:HH:mm:ss.fff} {level[..Math.Min(4, level.Length)].ToUpperInvariant()} {category}] {message}"));
        }
    }

    public List<BenchLifecycleEvent> GetEvents(DateTimeOffset? sinceUtc = null)
    {
        lock (_lock)
        {
            var result = new List<BenchLifecycleEvent>(_count);
            var start = (_head - _count + _ring.Length) % _ring.Length;
            for (var i = 0; i < _count; i++)
            {
                var ev = _ring[(start + i) % _ring.Length];
                if (ev is null)
                {
                    continue;
                }

                if (sinceUtc is null || ev.UtcTimestamp >= sinceUtc.Value)
                {
                    result.Add(ev);
                }
            }

            return result;
        }
    }
}

/// <summary>
/// Lightweight <see cref="ILoggerProvider"/> capturing <see cref="LogLevel.Information"/>+ events from
/// <c>Orleans.Durable.Storage*</c> and <c>Orleans.Consensus.CasPaxos*</c> into <see cref="BenchEventLogStore"/>.
/// </summary>
public sealed class BenchEventLogProvider(BenchEventLogStore store) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) =>
        ShouldCapture(categoryName)
            ? new CapturingLogger(store, categoryName)
            : NullBenchLogger.Instance;

    public void Dispose()
    {
    }

    private static bool ShouldCapture(string categoryName) =>
        categoryName.StartsWith("Orleans.Durable.Storage", StringComparison.Ordinal)
        || categoryName.StartsWith("Orleans.Consensus.CasPaxos", StringComparison.Ordinal);

    private sealed class CapturingLogger(BenchEventLogStore store, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var text = formatter(state, exception);
            if (exception is not null)
            {
                text = $"{text} | {exception.GetType().Name}: {exception.Message}";
            }

            store.Record(category, eventId.Id, logLevel.ToString(), text, writeConsole: false);
        }
    }

    private sealed class NullBenchLogger : ILogger
    {
        public static NullBenchLogger Instance { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => false;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
        }
    }
}

/// <summary>
/// Wraps a <see cref="WalStorage"/> to emit correlated lifecycle events on WAL file creation, deletion, and checkpoint
/// writes while delegating <see cref="WalFile"/> reads and writes directly with zero hot-path overhead.
/// </summary>
internal sealed class ObservingWalStorage(WalStorage inner, BenchEventLogStore eventLog) : WalStorage, IDisposable
{
    public override ValueTask CreateDirectoryAsync(string path, CancellationToken cancellationToken) =>
        inner.CreateDirectoryAsync(path, cancellationToken);

    public override async ValueTask<WalDirectory> OpenDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        var dir = await inner.OpenDirectoryAsync(path, cancellationToken).ConfigureAwait(false);
        return new ObservingWalDirectory(dir, eventLog);
    }

    public override ValueTask DeleteDirectoryAsync(string path, CancellationToken cancellationToken) =>
        inner.DeleteDirectoryAsync(path, cancellationToken);

    public override ValueTask<IReadOnlyList<string>> ListDirectoriesAsync(string path, CancellationToken cancellationToken) =>
        inner.ListDirectoriesAsync(path, cancellationToken);

    public override ValueTask<WalDirectoryUsage> MeasureDirectoryAsync(string path, CancellationToken cancellationToken) =>
        inner.MeasureDirectoryAsync(path, cancellationToken);

    public void Dispose()
    {
        if (inner is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private sealed class ObservingWalDirectory(WalDirectory innerDir, BenchEventLogStore eventLog) : WalDirectory
    {
        private readonly string _dirName = System.IO.Path.GetFileName(innerDir.Path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));

        public override string Path => innerDir.Path;

        public override int MinimumPageSize => innerDir.MinimumPageSize;

        public override ValueTask<IReadOnlyList<ulong>> ListFilesAsync(CancellationToken cancellationToken) =>
            innerDir.ListFilesAsync(cancellationToken);

        public override async ValueTask<WalFile> CreateFileAsync(
            ulong file, ArcBuffer header, long preallocatedLength, WalFillPace? pace, CancellationToken cancellationToken)
        {
            var sw = Stopwatch.StartNew();
            var result = await innerDir.CreateFileAsync(file, header, preallocatedLength, pace, cancellationToken).ConfigureAwait(false);
            sw.Stop();
            eventLog.Record(
                "Bench.Wal",
                eventId: 7001,
                level: "Information",
                message: string.Create(
                    CultureInfo.InvariantCulture,
                    $"WalFileCreated dir={_dirName} file={file} preallocatedMb={preallocatedLength / (1024.0 * 1024.0):F1} elapsedMs={sw.Elapsed.TotalMilliseconds:F1}"),
                writeConsole: true);
            return result;
        }

        public override async ValueTask<WalFile> CreateAllocatedFileAsync(ulong file, ArcBuffer header, long length, CancellationToken cancellationToken)
        {
            var sw = Stopwatch.StartNew();
            var result = await innerDir.CreateAllocatedFileAsync(file, header, length, cancellationToken).ConfigureAwait(false);
            sw.Stop();
            eventLog.Record(
                "Bench.Wal",
                eventId: 7002,
                level: "Information",
                message: string.Create(
                    CultureInfo.InvariantCulture,
                    $"WalFileCreatedSparse dir={_dirName} file={file} lengthMb={length / (1024.0 * 1024.0):F1} elapsedMs={sw.Elapsed.TotalMilliseconds:F1}"),
                writeConsole: true);
            return result;
        }

        public override ValueTask<WalFile> OpenFileAsync(ulong file, CancellationToken cancellationToken) =>
            innerDir.OpenFileAsync(file, cancellationToken);

        public override async ValueTask DeleteFileAsync(ulong file, CancellationToken cancellationToken)
        {
            await innerDir.DeleteFileAsync(file, cancellationToken).ConfigureAwait(false);
            eventLog.Record(
                "Bench.Wal",
                eventId: 7003,
                level: "Information",
                message: string.Create(CultureInfo.InvariantCulture, $"WalFileDeleted dir={_dirName} file={file}"),
                writeConsole: true);
        }

        public override async ValueTask<WalFile> CreateNamedFileAsync(string name, ArcBuffer contents, CancellationToken cancellationToken)
        {
            var length = contents.Length;
            var sw = Stopwatch.StartNew();
            var result = await innerDir.CreateNamedFileAsync(name, contents, cancellationToken).ConfigureAwait(false);
            sw.Stop();
            eventLog.Record(
                "Bench.Wal",
                eventId: 7004,
                level: "Information",
                message: string.Create(
                    CultureInfo.InvariantCulture,
                    $"WalCheckpointWritten dir={_dirName} name={name} bytes={length} elapsedMs={sw.Elapsed.TotalMilliseconds:F1}"),
                writeConsole: true);
            return result;
        }

        public override ValueTask<WalFile> OpenNamedFileAsync(string name, CancellationToken cancellationToken) =>
            innerDir.OpenNamedFileAsync(name, cancellationToken);

        public override ValueTask<IReadOnlyList<string>> ListNamedFilesAsync(string extension, CancellationToken cancellationToken) =>
            innerDir.ListNamedFilesAsync(extension, cancellationToken);

        public override async ValueTask DeleteNamedFileAsync(string name, CancellationToken cancellationToken)
        {
            await innerDir.DeleteNamedFileAsync(name, cancellationToken).ConfigureAwait(false);
            eventLog.Record(
                "Bench.Wal",
                eventId: 7005,
                level: "Information",
                message: string.Create(CultureInfo.InvariantCulture, $"WalNamedFileDeleted dir={_dirName} name={name}"),
                writeConsole: false);
        }

        public override ValueTask DisposeAsync() => innerDir.DisposeAsync();
    }
}
