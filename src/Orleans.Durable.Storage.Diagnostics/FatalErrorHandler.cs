using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Orleans.Durable.Storage.Diagnostics;

/// <summary>
/// Stops the process after a failed local storage operation, or after a defect which would leave a component's memory
/// out of step with its write-ahead log (conventions.md §14).
/// </summary>
/// <remarks>
/// <para>
/// After a local write, flush, rename, or read fails, the process cannot know what the device holds: Linux can report an
/// <c>fsync</c> error once and mark the failed pages clean. After a defect such as a publication which threw, the
/// process's memory no longer follows its write-ahead log. Either way only the next incarnation, recovering from what
/// the device holds, answers again; restart or replacement is the orchestrator's. The storage seams report storage
/// failures (<see cref="StopOnStorageFailure"/>), and the component which detects a defect reports it
/// (<see cref="StopOnDefect"/>); components never end the process themselves.
/// </para>
/// <para>
/// The exit is immediate: the handler writes one line to standard error, waiting at most
/// <see cref="MessageTimeout"/> for it, and then ends the process with <c>_exit</c> on Unix and
/// <c>TerminateProcess</c> on Windows. The runtime's own exit would run <c>ProcessExit</c> handlers, which can hang, and
/// libc's <c>exit</c>, whose static destructors (RocksDB's among them) can crash while their background threads still run
/// and replace the site's exit code with a signal's. A write which fails (a full or failed disk behind standard error)
/// or stalls (a pipe no reader drains) delays the exit by at most the bound. The reporting thread never returns, so no
/// answer rests on the failed operation.
/// </para>
/// <para>
/// A test or a simulation sets <see cref="Interceptor"/>, which takes each report in place of the stop; the reporting
/// code then rethrows the failure, and <see cref="IsReported"/> tells every catch it passes through that it was
/// reported. A failure is reported once: a report of a failure already reported, as itself or as the inner exception of
/// the one reported, is ignored.
/// </para>
/// </remarks>
public static partial class FatalErrorHandler
{
    /// <summary>
    /// The longest the exit waits for its message to reach standard error.
    /// </summary>
    public static readonly TimeSpan MessageTimeout = TimeSpan.FromSeconds(5);

    private static readonly Lock Exiting = new();
    private static readonly ConditionalWeakTable<Exception, object> Reported = [];
    private static readonly object Marker = new();

    // Why the last message failed to reach standard error, for a debugger attached to the stopping process.
    private static Exception? _messageFailure;

    /// <summary>
    /// Gets or sets the action which takes a report in place of stopping the process, so tests and simulations record
    /// it; production code leaves it unset.
    /// </summary>
    public static Action<FatalError>? Interceptor
    {
        get => Volatile.Read(ref field);
        set => Volatile.Write(ref field, value);
    }

    /// <summary>
    /// Reports that the local storage operation of <paramref name="site"/> failed on <paramref name="path"/> with
    /// <paramref name="exception"/>, and stops the process with the site's exit code. Only a storage seam calls it.
    /// Returns only when an <see cref="Interceptor"/> takes the report, or the failure was already reported; the seam then
    /// rethrows <paramref name="exception"/>.
    /// </summary>
    /// <param name="site">The seam's operation, a storage site, whose value is the exit code.</param>
    /// <param name="path">The file or directory the operation named.</param>
    /// <param name="exception">The operating system's or the storage engine's error.</param>
    public static void StopOnStorageFailure(FatalErrorSite site, string path, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(exception);
        Stop(new FatalError(site, path, exception));
    }

    /// <summary>
    /// Reports a defect which would leave the reporting component's memory out of step with its write-ahead log, and
    /// stops the process with <see cref="FatalErrorSite.Defect"/>'s exit code, the report naming the defect. Returns only
    /// when an <see cref="Interceptor"/> takes the report, or <paramref name="exception"/> was already reported: a storage
    /// failure a seam reported needs no second report.
    /// </summary>
    /// <param name="defect">The defect's name, as <c>WalRoll</c>, which the report and the process's message carry.</param>
    /// <param name="exception">The defect.</param>
    public static void StopOnDefect(string defect, Exception exception)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(defect);
        ArgumentNullException.ThrowIfNull(exception);
        Stop(new FatalError(FatalErrorSite.Defect, null, exception, defect));
    }

    /// <summary>
    /// Returns whether <paramref name="exception"/>, or any exception it carries as an inner exception or an aggregate's
    /// member, was reported. Only a process whose <see cref="Interceptor"/> took the report observes one.
    /// </summary>
    /// <param name="exception">The exception.</param>
    /// <returns><see langword="true"/> when a report already stopped the process for it.</returns>
    public static bool IsReported(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (Reported.TryGetValue(exception, out _))
        {
            return true;
        }

        return exception is AggregateException aggregate
            ? aggregate.InnerExceptions.Any(IsReported)
            : exception.InnerException is { } next && IsReported(next);
    }

    private static void Stop(FatalError error)
    {
        if (IsReported(error.Exception))
        {
            return;
        }

        _ = Reported.TryAdd(error.Exception, Marker);
        if (Interceptor is { } interceptor)
        {
            interceptor(error);
            return;
        }

        // The first report writes its message and exits; one which races it waits here until the process ends.
        lock (Exiting)
        {
            try
            {
                WriteBounded(error.Describe());
            }
            finally
            {
                ExitImmediately((int)error.Site);
            }
        }
    }

    /// <summary>
    /// Writes <paramref name="message"/> to standard error on a thread of its own, waiting for it at most
    /// <see cref="MessageTimeout"/>: a stalled pipe blocks that thread, not the exit.
    /// </summary>
    private static void WriteBounded(string message)
    {
        var writer = new Thread(() => Write(message))
        {
            IsBackground = true,
            Name = "durable-fatal-error",
        };
        writer.Start();
        _ = writer.Join(MessageTimeout);
    }

    private static void Write(string message)
    {
        try
        {
            Console.Error.WriteLine(message);
            Console.Error.Flush();
        }
#pragma warning disable CA1031 // The process exits whether or not standard error takes the message.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            // A failed standard error loses the message; the exit code still names the site.
            Volatile.Write(ref _messageFailure, exception);
        }
    }

    /// <summary>
    /// Ends the process with <paramref name="code"/> at once, running no <c>ProcessExit</c> handler, finalizer, or static
    /// destructor: <c>TerminateProcess</c> on Windows, and <c>_exit</c>, which every POSIX libc provides, elsewhere.
    /// </summary>
    [DoesNotReturn]
    private static void ExitImmediately(int code)
    {
        if (OperatingSystem.IsWindows())
        {
            _ = TerminateProcess(GetCurrentProcess(), (uint)code);
        }
        else
        {
            UnixExit(code);
        }

        throw new UnreachableException("The process outlived its own termination.");
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(nint process, uint exitCode);

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint GetCurrentProcess();

    [LibraryImport("libc", EntryPoint = "_exit")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial void UnixExit(int status);
}
