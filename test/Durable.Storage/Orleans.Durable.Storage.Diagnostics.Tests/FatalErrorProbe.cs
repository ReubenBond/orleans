using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Testing.Platform.Builder;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Journals;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Store;
using Orleans.Durable.Storage.Engine.Wal.Portable;
using Orleans.Durable.Storage.RocksDb;

namespace Orleans.Durable.Storage.Diagnostics.Tests;

/// <summary>
/// A process which opens a LedgerStore with a written replica and a RocksDB state database, writes a listing of its
/// node root to standard output, and reports a failed local storage operation, as a host's storage seam does. The test
/// host runs it in place of the tests when <see cref="Variable"/> names the node root: the project declares it as a
/// <c>TestingPlatformBuilderHook</c>, which the generated entry point calls before the test platform starts.
/// </summary>
internal static class FatalErrorProbe
{
    /// <summary>
    /// The environment variable which names the probe's node root.
    /// </summary>
    internal const string Variable = "DURABLE_STORAGE_FATAL_ERROR_PROBE";

    /// <summary>
    /// The environment variable which names how the probe's standard error fails, if it does: <see cref="FailingError"/>
    /// or <see cref="StalledError"/>.
    /// </summary>
    internal const string ErrorVariable = "DURABLE_STORAGE_FATAL_ERROR_PROBE_STDERR";

    /// <summary>
    /// The standard error whose every write throws, as a full or failed disk behind it does.
    /// </summary>
    internal const string FailingError = "failing";

    /// <summary>
    /// The standard error whose writes never return, as a pipe no reader drains does.
    /// </summary>
    internal const string StalledError = "stalled";

    /// <summary>
    /// The site the probe reports, whose value is the exit code it expects.
    /// </summary>
    internal const FatalErrorSite Site = FatalErrorSite.HandleFlush;

    /// <summary>
    /// The name of the file a <c>ProcessExit</c> handler of the probe's writes, beside the node root, before it hangs: an
    /// immediate exit runs no such handler.
    /// </summary>
    internal const string ProcessExitMarker = "process-exit-ran";

    /// <summary>
    /// The line which ends the listing the probe writes before it reports.
    /// </summary>
    internal const string ListingEnd = "-- end of listing --";

    /// <summary>
    /// The path the probe's report names.
    /// </summary>
    internal static string FailedPath(string root) => Path.Combine(root, "wal", "probe.clfl");

    /// <summary>
    /// Runs the probe before the test platform starts, when <see cref="Variable"/> is set; it never returns then.
    /// </summary>
    /// <param name="builder">The test application's builder, which the probe leaves untouched.</param>
    /// <param name="arguments">The command line.</param>
    public static void AddExtensions(ITestApplicationBuilder builder, string[] arguments)
    {
        _ = builder;
        _ = arguments;
        if (Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } root)
        {
            RunAsync(root, Environment.GetEnvironmentVariable(ErrorVariable)).GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// Returns every file below <paramref name="root"/> with its length and the SHA-256 of its contents, in ordinal order
    /// of path, so a rewrite which keeps a file's length still changes its line. The empty lock files are listed by length,
    /// and RocksDB's info logs not at all.
    /// </summary>
    internal static string[] List(string root)
    {
        var files = new List<string>();
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetRelativePath(root, path);
            if (IsRocksDbInfoLog(Path.GetFileName(path)))
            {
                // RocksDB's diagnostic log, which a live database writes on its own: on Linux its logger buffers lines,
                // such as the statistics a background thread dumps just after the open, and flushes them seconds later,
                // as while the probe waits on its stalled standard error (#160). It holds no state, so its growth is no
                // shutdown writing storage.
                continue;
            }

            if (string.Equals(Path.GetFileName(path), "lock", StringComparison.OrdinalIgnoreCase))
            {
                // The lock files of the WAL and RocksDB, empty, which their holders keep without sharing reads on Windows.
                files.Add(string.Create(CultureInfo.InvariantCulture, $"{name} {new FileInfo(path).Length}"));
                continue;
            }

            // Another handle may hold a file the probe left open; share every access, as a reader must.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var hash = Convert.ToHexStringLower(SHA256.HashData(stream));
            files.Add(string.Create(CultureInfo.InvariantCulture, $"{name} {stream.Length} {hash}"));
        }

        files.Sort(StringComparer.Ordinal);
        return [.. files];
    }

    /// <summary>
    /// Returns whether <paramref name="name"/> names one of RocksDB's info logs: <c>LOG</c>, or <c>LOG.old.</c> and a
    /// timestamp once it rolls.
    /// </summary>
    private static bool IsRocksDbInfoLog(string name) =>
        string.Equals(name, "LOG", StringComparison.Ordinal) || name.StartsWith("LOG.old.", StringComparison.Ordinal);

    /// <summary>
    /// Returns the path of the marker a <c>ProcessExit</c> handler of the probe of <paramref name="root"/> writes.
    /// </summary>
    internal static string MarkerPath(string root) => Path.Combine(Path.GetDirectoryName(root)!, Path.GetFileName(root) + "." + ProcessExitMarker);

    private static async Task RunAsync(string root, string? error)
    {
        var store = await LedgerStore.OpenAsync(
            new PortableWalStorage(),
            new LedgerStoreOptions { DirectoryPath = root },
            Guid.NewGuid(),
            create: true,
            new LedgerStoreCheckpointOptions(),
            TimeProvider.System);

        // A written replica, which a store's disposal would checkpoint.
        var replica = store.OpenReplica(new LedgerId(1), new LedgerReplicaOptions { WindowSize = 4 });
        using (var writer = new ArcBufferWriter())
        {
            writer.Write("probe"u8);
            using var payload = writer.ConsumeSlice(5);
            _ = await replica.WriteAsync(new Slot(0), payload, new Slot(0), recovery: false);
        }

        var index = RocksDbJournalIndex.ForNode(root);
        _ = await index.OpenAsync();
        await index.ApplyBatchAsync(new JournalIndexBatch(
            new LedgerId(1),
            new Slot(1),
            new[] { new JournalIndexKeyWrite(JournalKeyHash.Compute("probe"u8), Slot.Zero, 0, Slot.Zero, 0) },
            ReadOnlyMemory<JournalIndexTombstoneWrite>.Empty));
        await index.FlushAsync();

        foreach (var line in List(root))
        {
            await Console.Out.WriteLineAsync(line);
        }

        await Console.Out.WriteLineAsync(ListingEnd);
        await Console.Out.FlushAsync();
        GC.KeepAlive(store);
        GC.KeepAlive(index);

        // A third-party handler which the runtime's own exit would run, and which would hang it.
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            File.WriteAllText(MarkerPath(root), "ran");
            using var never = new ManualResetEventSlim();
            never.Wait();
        };

        if (error is FailingError or StalledError)
        {
            Console.SetError(new BrokenWriter(stalls: error == StalledError));
        }

        // The probe stands for a host, which intercepts nothing.
        FatalErrorHandler.Interceptor = null;
        FatalErrorHandler.StopOnStorageFailure(Site, FailedPath(root), new IOException("The probe's flush failed.", new Win32Exception(5)));
        throw new UnreachableException("The fatal-error handler returned without an interceptor.");
    }

    /// <summary>
    /// A standard error whose every write throws, or never returns.
    /// </summary>
    private sealed class BrokenWriter(bool stalls) : TextWriter
    {
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

        public override void Write(char value) => Fail();

        public override void Flush() => Fail();

        private void Fail()
        {
            if (stalls)
            {
                using var never = new ManualResetEventSlim();
                never.Wait();
            }

            throw new IOException("No space left on device.", 28);
        }
    }
}
