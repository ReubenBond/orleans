using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Orleans.Durable.Storage.Tests.LazyFs;

/// <summary>
/// Manages one Linux LazyFS FUSE mount backed by a real local directory, exposing FIFO-driven
/// <c>lazyfs::clear-cache</c> fault injection to drop all unsynced dirty pages in the simulated OS page cache.
/// </summary>
internal sealed class LazyFsMount : IAsyncDisposable
{
    private const string ClearCacheCommand = "lazyfs::clear-cache\n";
    private const string CacheCheckpointCommand = "lazyfs::cache-checkpoint\n";

    private readonly Process _process;
    private readonly string _fifoPath;
    private readonly string _logPath;
    private readonly string _unmountTool;
    private bool _disposed;

    private LazyFsMount(
        Process process,
        string mountPath,
        string backingPath,
        string fifoPath,
        string logPath,
        string unmountTool)
    {
        _process = process;
        MountPath = mountPath;
        BackingPath = backingPath;
        _fifoPath = fifoPath;
        _logPath = logPath;
        _unmountTool = unmountTool;
    }

    /// <summary>Gets the FUSE mount point that silos write into.</summary>
    public string MountPath { get; }

    /// <summary>Gets the underlying native Linux backing directory that receives only synced data.</summary>
    public string BackingPath { get; }

    /// <summary>
    /// Locates the <c>lazyfs</c> binary, its <c>libpcache</c> library directory (if any), and the FUSE unmount tool,
    /// and verifies <c>/dev/fuse</c> is accessible.
    /// </summary>
    public static bool TryLocateEnvironment(out string binaryPath, out string? libraryDirectory, out string unmountTool, out string reason)
    {
        binaryPath = string.Empty;
        libraryDirectory = null;
        unmountTool = string.Empty;

        if (!OperatingSystem.IsLinux())
        {
            reason = "LazyFS FUSE crash soak tests require Linux.";
            return false;
        }

        if (!File.Exists("/dev/fuse"))
        {
            reason = "/dev/fuse is not available.";
            return false;
        }

        try
        {
            using var probe = File.Open("/dev/fuse", FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        }
        catch (Exception exception)
        {
            reason = $"/dev/fuse is not accessible: {exception.Message}";
            return false;
        }

        unmountTool = FindOnPath("fusermount3") ?? FindOnPath("fusermount") ?? string.Empty;
        if (unmountTool.Length == 0)
        {
            reason = "Neither fusermount3 nor fusermount was found on PATH.";
            return false;
        }

        var envBinary = Environment.GetEnvironmentVariable("DURABLE_STORAGE_LAZYFS_BIN");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] candidates =
        [
            .. string.IsNullOrWhiteSpace(envBinary) ? Array.Empty<string>() : [envBinary],
            .. FindOnPath("lazyfs") is { } onPath ? [onPath] : Array.Empty<string>(),
            Path.Combine(home, ".local", "bin", "lazyfs"),
            "/usr/local/bin/lazyfs",
        ];

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                binaryPath = candidate;
                break;
            }
        }

        if (binaryPath.Length == 0)
        {
            reason = "LazyFS binary not found (checked DURABLE_STORAGE_LAZYFS_BIN, PATH, ~/.local/bin/lazyfs, /usr/local/bin/lazyfs).";
            return false;
        }

        var binaryDir = Path.GetDirectoryName(binaryPath);
        string[] libCandidates =
        [
            .. string.IsNullOrWhiteSpace(binaryDir) ? Array.Empty<string>() : [Path.GetFullPath(Path.Combine(binaryDir, "..", "lib"))],
            Path.Combine(home, ".local", "lib"),
            "/usr/local/lib",
        ];
        libraryDirectory = libCandidates.FirstOrDefault(dir => Directory.Exists(dir) && Directory.EnumerateFiles(dir, "libpcache.so*").Any());

        reason = string.Empty;
        return true;
    }

    /// <summary>Mounts a new LazyFS instance under <paramref name="workDir"/>.</summary>
    public static async Task<LazyFsMount> StartAsync(
        string workDir,
        string binaryPath,
        string? libraryDirectory,
        string unmountTool,
        string cacheSize = "0.25GB",
        CancellationToken cancellationToken = default)
    {
        var backingPath = Path.Combine(workDir, "backing");
        var mountPath = Path.Combine(workDir, "mount");
        var configPath = Path.Combine(workDir, "lazyfs.toml");
        var fifoPath = Path.Combine(workDir, "faults.fifo");
        var logPath = Path.Combine(workDir, "lazyfs.log");

        _ = Directory.CreateDirectory(backingPath);
        _ = Directory.CreateDirectory(mountPath);

        // Use LF line endings explicitly and run LazyFS in foreground (-f) so libfuse does not fork away its fault worker thread.
        var toml = string.Create(
            CultureInfo.InvariantCulture,
            $"[faults]\nfifo_path = \"{EscapeToml(fifoPath)}\"\n\n[cache]\napply_eviction = false\n\n[cache.simple]\ncustom_size = \"{cacheSize}\"\nblocks_per_page = 1\n\n[filesystem]\nlog_all_operations = false\nlogfile = \"{EscapeToml(logPath)}\"\n");
        await File.WriteAllTextAsync(configPath, toml, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken).ConfigureAwait(false);

        var start = new ProcessStartInfo(binaryPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workDir,
        };
        start.ArgumentList.Add(mountPath);
        start.ArgumentList.Add("-f");
        start.ArgumentList.Add("--config-path");
        start.ArgumentList.Add(configPath);
        start.ArgumentList.Add("-o");
        start.ArgumentList.Add("modules=subdir");
        start.ArgumentList.Add("-o");
        start.ArgumentList.Add($"subdir={backingPath}");

        if (!string.IsNullOrEmpty(libraryDirectory))
        {
            var existing = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH");
            start.Environment["LD_LIBRARY_PATH"] = string.IsNullOrEmpty(existing)
                ? libraryDirectory
                : $"{libraryDirectory}:{existing}";
        }

        var process = Process.Start(start) ?? throw new InvalidOperationException($"Failed to start LazyFS at '{binaryPath}'.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

        var deadline = TimeProvider.System.GetUtcNow() + TimeSpan.FromSeconds(15);
        while (TimeProvider.System.GetUtcNow() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                var stdout = await stdoutTask.ConfigureAwait(false);
                var stderr = await stderrTask.ConfigureAwait(false);
                throw new InvalidOperationException(
                    $"LazyFS exited prematurely with code {process.ExitCode} while mounting '{mountPath}'.\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
            }

            if (File.Exists(fifoPath) && await IsMountedAsync(mountPath, cancellationToken).ConfigureAwait(false) && await IsFaultWorkerReadyAsync(logPath, cancellationToken).ConfigureAwait(false))
            {
                return new LazyFsMount(process, mountPath, backingPath, fifoPath, logPath, unmountTool);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), TimeProvider.System, cancellationToken).ConfigureAwait(false);
        }

        await UnmountBestEffortAsync(mountPath, unmountTool, process).ConfigureAwait(false);
        var outText = await stdoutTask.ConfigureAwait(false);
        var errText = await stderrTask.ConfigureAwait(false);
        throw new TimeoutException($"LazyFS did not mount '{mountPath}' within 15 seconds.\nSTDOUT:\n{outText}\nSTDERR:\n{errText}");
    }

    /// <summary>
    /// Drops every unsynced dirty page in LazyFS's simulated page cache, simulating sudden power loss immediately
    /// after <c>SIGKILL</c>.
    /// </summary>
    public async Task ClearCacheAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_process.HasExited)
        {
            throw new InvalidOperationException($"LazyFS process for '{MountPath}' exited unexpectedly with code {_process.ExitCode}.");
        }

        var beforeClears = await CountLogOccurrencesAsync("cache is cleared.", cancellationToken).ConfigureAwait(false);
        await SendFifoCommandAsync(ClearCacheCommand, cancellationToken).ConfigureAwait(false);

        var deadline = TimeProvider.System.GetUtcNow() + TimeSpan.FromSeconds(10);
        while (TimeProvider.System.GetUtcNow() < deadline)
        {
            if (await CountLogOccurrencesAsync("cache is cleared.", cancellationToken).ConfigureAwait(false) > beforeClears)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), TimeProvider.System, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException($"LazyFS did not confirm 'lazyfs::clear-cache' in '{_logPath}' within 10 seconds.");
    }

    /// <summary>Flushes all cached data in LazyFS down to the backing filesystem (used only for clean teardown).</summary>
    public Task CheckpointCacheAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return SendFifoCommandAsync(CacheCheckpointCommand, cancellationToken);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await UnmountBestEffortAsync(MountPath, _unmountTool, _process).ConfigureAwait(false);
        _process.Dispose();
    }

    private async Task SendFifoCommandAsync(string command, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(_fifoPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, bufferSize: 128, useAsync: true);
        var bytes = Encoding.UTF8.GetBytes(command);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> CountLogOccurrencesAsync(string marker, CancellationToken cancellationToken)
    {
        if (!File.Exists(_logPath))
        {
            return 0;
        }

        using var stream = new FileStream(_logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var content = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        var count = 0;
        var index = 0;
        while ((index = content.IndexOf(marker, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += marker.Length;
        }

        return count;
    }

    private static async Task<bool> IsFaultWorkerReadyAsync(string logPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(logPath))
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var content = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            return content.Contains("waiting for fault commands...", StringComparison.Ordinal);
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static async Task<bool> IsMountedAsync(string mountPath, CancellationToken cancellationToken)
    {
        var full = Path.GetFullPath(mountPath).TrimEnd('/');
        var mounts = await File.ReadAllLinesAsync("/proc/mounts", cancellationToken).ConfigureAwait(false);
        foreach (var line in mounts)
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && string.Equals(parts[1], full, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task UnmountBestEffortAsync(string mountPath, string unmountTool, Process process)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (!await IsMountedAsync(mountPath, CancellationToken.None).ConfigureAwait(false))
                {
                    break;
                }
            }
            catch
            {
                break;
            }

            try
            {
                using var unmount = Process.Start(new ProcessStartInfo(unmountTool)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    ArgumentList = { "-u", mountPath },
                });
                if (unmount is not null)
                {
                    await unmount.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                }
            }
            catch
            {
                // Retry lazy unmount on final attempt.
            }

            if (attempt == 3)
            {
                try
                {
                    using var lazyUnmount = Process.Start(new ProcessStartInfo(unmountTool)
                    {
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        ArgumentList = { "-uz", mountPath },
                    });
                    if (lazyUnmount is not null)
                    {
                        await lazyUnmount.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    }
                }
                catch
                {
                    // Best effort cleanup.
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), TimeProvider.System).ConfigureAwait(false);
        }

        try
        {
            if (!process.HasExited)
            {
                await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
        }
        catch (TimeoutException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            catch
            {
                // Best effort cleanup.
            }
        }
    }

    private static string? FindOnPath(string executable)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVar))
        {
            return null;
        }

        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, executable);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string EscapeToml(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
}
