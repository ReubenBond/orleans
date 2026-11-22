namespace Orleans.Durable.Storage.Engine;

/// <summary>
/// The local file system's answers, which the storage seams separate from failures (conventions.md §14): whether a path
/// exists, and the contention another process's handle causes on Windows. The seams alone call it; it reports nothing.
/// </summary>
public static class FileSystemAnswers
{
    /// <summary>
    /// How long a seam waits out contention before the refusal counts as a failure.
    /// </summary>
    public static readonly TimeSpan ContentionBound = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The first pause between attempts of an operation another handle refused.
    /// </summary>
    public static readonly TimeSpan FirstContentionPause = TimeSpan.FromMilliseconds(1);

    /// <summary>
    /// The longest pause between attempts, which doubling reaches.
    /// </summary>
    public static readonly TimeSpan LongestContentionPause = TimeSpan.FromMilliseconds(100);

    private const int WindowsSharingViolation = unchecked((int)0x80070020);
    private const int WindowsLockViolation = unchecked((int)0x80070021);
    private const int WindowsDirectoryNotEmpty = unchecked((int)0x80070091);

    /// <summary>
    /// Returns a path's attributes, or <see langword="null"/> when nothing exists there. Unlike
    /// <see cref="File.Exists"/> and <see cref="Directory.Exists"/>, it never reads an error as absence: any other failure
    /// propagates for its seam to report.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <returns>The attributes, or <see langword="null"/> for a missing path.</returns>
    public static FileAttributes? GetAttributes(string path)
    {
        try
        {
            return File.GetAttributes(path);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Returns whether <paramref name="exception"/>, from a rename or deletion, is contention on Windows: another
    /// process's handle refused the operation (a sharing violation, a lock violation, an access denial of a file another
    /// process holds open, or a directory not yet empty of a file another process deletes), which changed nothing.
    /// </summary>
    /// <param name="exception">The operation's failure.</param>
    /// <returns><see langword="true"/> when repeating the operation may succeed once the other handle closes.</returns>
    public static bool IsContention(Exception exception) =>
        OperatingSystem.IsWindows()
        && exception is UnauthorizedAccessException or IOException { HResult: WindowsSharingViolation or WindowsLockViolation or WindowsDirectoryNotEmpty };

    /// <summary>
    /// Returns whether <paramref name="exception"/>, from an open, is contention on Windows: another handle's sharing
    /// mode refused it. An open of a file also waits out an access denial (<see cref="IsFileOpenContention"/>).
    /// </summary>
    /// <param name="exception">The open's failure.</param>
    /// <returns><see langword="true"/> when repeating the open may succeed once the other handle closes.</returns>
    public static bool IsOpenContention(Exception exception) =>
        OperatingSystem.IsWindows() && exception is IOException { HResult: WindowsSharingViolation or WindowsLockViolation };

    /// <summary>
    /// Returns whether <paramref name="exception"/>, from an open of the file at <paramref name="path"/>, is contention on
    /// Windows: another handle's sharing mode refused it, or it met an access denial of a path which holds no directory.
    /// Such a denial is what a file whose deletion is pending answers: the name stays until the deleting handle closes,
    /// and the open then finds the file gone or the file a later publication put there. A denial which outlasts
    /// <see cref="ContentionBound"/> is the file's permissions, a failure; one of a directory is a failure at once.
    /// </summary>
    /// <param name="exception">The open's failure.</param>
    /// <param name="path">The file the open named.</param>
    /// <returns><see langword="true"/> when repeating the open may succeed, or find no file, once the other handle closes.</returns>
    public static bool IsFileOpenContention(Exception exception, string path) =>
        IsOpenContention(exception) || OperatingSystem.IsWindows() && exception is UnauthorizedAccessException && !IsDirectory(path);

    private static bool IsDirectory(string path) => GetAttributes(path) is { } attributes && attributes.HasFlag(FileAttributes.Directory);

    /// <summary>
    /// Returns the pause after <paramref name="pause"/>: doubled, up to <see cref="LongestContentionPause"/>.
    /// </summary>
    /// <param name="pause">The last pause.</param>
    /// <returns>The next pause.</returns>
    public static TimeSpan NextPause(TimeSpan pause) => pause * 2 < LongestContentionPause ? pause * 2 : LongestContentionPause;

    /// <summary>
    /// Runs <paramref name="operation"/> on the calling thread, repeating it while <paramref name="isContention"/> calls
    /// its failure contention, pausing from <see cref="FirstContentionPause"/> and doubling to
    /// <see cref="LongestContentionPause"/>, until <see cref="ContentionBound"/> has passed on
    /// <paramref name="timeProvider"/>; then the refusal propagates as a failure. A caller which holds a lock that
    /// others wait on repeats the operation itself, outside the lock, with an asynchronous delay instead.
    /// </summary>
    /// <typeparam name="T">The type of the operation's result.</typeparam>
    /// <param name="operation">The operation.</param>
    /// <param name="isContention">Which failures are contention.</param>
    /// <param name="timeProvider">The clock which measures the bound and the pauses.</param>
    /// <returns>What the operation returned.</returns>
    public static T WaitOutContention<T>(Func<T> operation, Func<Exception, bool> isContention, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(isContention);
        ArgumentNullException.ThrowIfNull(timeProvider);
        var started = timeProvider.GetTimestamp();
        var pause = FirstContentionPause;
        while (true)
        {
            try
            {
                return operation();
            }
            catch (Exception exception) when (isContention(exception) && timeProvider.GetElapsedTime(started) < ContentionBound)
            {
                // Another process's handle, not this process's storage, refused the operation; it ends on its own.
                using var paused = new ManualResetEventSlim();
                using var timer = timeProvider.CreateTimer(static state => ((ManualResetEventSlim)state!).Set(), paused, pause, Timeout.InfiniteTimeSpan);
                paused.Wait();
                pause = NextPause(pause);
            }
        }
    }
}
