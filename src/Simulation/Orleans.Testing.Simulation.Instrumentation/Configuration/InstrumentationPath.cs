using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Orleans.Testing.Simulation.Instrumentation.Configuration;

internal static class InstrumentationPath
{
    private const uint FileShareReadWriteDelete = 0x00000001 | 0x00000002 | 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;

    public static string GetFullPath(string path, string description)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        string normalizedPath = NormalizeDirectorySeparators(path);
        RejectWindowsDeviceNamespace(normalizedPath, path, description);

        string fullPath = NormalizeDirectorySeparators(Path.GetFullPath(normalizedPath));
        RejectWindowsDeviceNamespace(fullPath, path, description);
        if (OperatingSystem.IsWindows() && !IsOrdinaryWindowsPath(fullPath))
        {
            throw new ArgumentException(
                $"{description} '{path}' is not an ordinary drive or UNC path.");
        }

        return fullPath;
    }

    public static string CombineAndGetFullPath(string basePath, string path, string description)
    {
        ArgumentException.ThrowIfNullOrEmpty(basePath);
        ArgumentException.ThrowIfNullOrEmpty(path);

        string normalizedPath = NormalizeDirectorySeparators(path);
        RejectWindowsDeviceNamespace(normalizedPath, path, description);
        return GetFullPath(Path.Combine(basePath, normalizedPath), description);
    }

    public static string GetCanonicalPath(string path, string description)
    {
        string fullPath = GetFullPath(path, description);
        if (!OperatingSystem.IsWindows())
        {
            return fullPath;
        }

        try
        {
            return GetCanonicalWindowsPath(fullPath, description);
        }
        catch (Win32Exception exception)
        {
            throw new ArgumentException(
                $"{description} '{path}' could not be resolved to a canonical filesystem path: {exception.Message}",
                nameof(path),
                exception);
        }
    }

    private static string GetCanonicalWindowsPath(string fullPath, string description)
    {
        var remainingComponents = new Stack<string>();
        string candidate = fullPath;
        SafeFileHandle? handle = null;
        try
        {
            while (!TryOpenExistingPath(candidate, out handle))
            {
                string component = Path.GetFileName(
                    candidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                ValidateRemainingComponent(component, fullPath, description);
                remainingComponents.Push(component);

                string? parent = Path.GetDirectoryName(candidate);
                if (string.IsNullOrEmpty(parent) || string.Equals(parent, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException(
                        $"{description} '{fullPath}' has no existing filesystem ancestor.",
                        nameof(fullPath));
                }

                candidate = parent;
            }

            if (remainingComponents.Count > 0
                && (File.GetAttributes(candidate) & FileAttributes.Directory) == 0)
            {
                throw new ArgumentException(
                    $"{description} '{fullPath}' has existing file ancestor '{candidate}'.",
                    nameof(fullPath));
            }

            string canonicalPath = NormalizeFinalWindowsPath(GetFinalPath(handle!));
            while (remainingComponents.TryPop(out string? component))
            {
                canonicalPath = Path.Combine(canonicalPath, component);
            }

            return TrimEndingDirectorySeparatorExceptRoot(canonicalPath);
        }
        finally
        {
            handle?.Dispose();
        }
    }

    private static bool TryOpenExistingPath(string path, out SafeFileHandle? handle)
    {
        handle = NativeMethods.CreateFile(
            path,
            0,
            FileShareReadWriteDelete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics,
            IntPtr.Zero);
        if (!handle.IsInvalid)
        {
            return true;
        }

        int error = Marshal.GetLastPInvokeError();
        handle.Dispose();
        handle = null;
        if (error is ErrorFileNotFound or ErrorPathNotFound)
        {
            return false;
        }

        throw new Win32Exception(error);
    }

    private static string GetFinalPath(SafeFileHandle handle)
    {
        var capacity = 512;
        while (true)
        {
            var buffer = new char[capacity];
            uint length = NativeMethods.GetFinalPathNameByHandle(
                handle,
                buffer,
                checked((uint)buffer.Length),
                0);
            if (length == 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            if (length < buffer.Length)
            {
                return new string(buffer, 0, checked((int)length));
            }

            capacity = checked((int)length + 1);
        }
    }

    private static string NormalizeFinalWindowsPath(string path)
    {
        string normalized = NormalizeDirectorySeparators(path);
        if (normalized.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            normalized = @"\\" + normalized[8..];
        }
        else if (normalized.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            normalized = normalized[4..];
        }

        return TryConvertLocalAdministrativeShare(normalized, out string? localPath)
            ? localPath
            : normalized;
    }

    private static bool TryConvertLocalAdministrativeShare(string path, out string localPath)
    {
        localPath = string.Empty;
        if (!path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return false;
        }

        int serverEnd = path.IndexOf('\\', 2);
        if (serverEnd < 0)
        {
            return false;
        }

        string server = path[2..serverEnd];
        if (!string.Equals(server, "localhost", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(server, ".", StringComparison.Ordinal)
            && !string.Equals(server, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        int shareEnd = path.IndexOf('\\', serverEnd + 1);
        ReadOnlySpan<char> share = shareEnd < 0
            ? path.AsSpan(serverEnd + 1)
            : path.AsSpan(serverEnd + 1, shareEnd - serverEnd - 1);
        if (share.Length != 2 || !char.IsAsciiLetter(share[0]) || share[1] != '$')
        {
            return false;
        }

        ReadOnlySpan<char> remainder = shareEnd < 0 ? [] : path.AsSpan(shareEnd + 1);
        localPath = remainder.IsEmpty
            ? $"{char.ToUpperInvariant(share[0])}:\\"
            : $"{char.ToUpperInvariant(share[0])}:\\{remainder}";
        return true;
    }

    private static void ValidateRemainingComponent(
        string component,
        string fullPath,
        string description)
    {
        if (string.IsNullOrEmpty(component)
            || component is "." or ".."
            || component.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || component.EndsWith(' ')
            || component.EndsWith('.'))
        {
            throw new ArgumentException(
                $"{description} '{fullPath}' contains invalid unresolvable path component '{component}'.",
                nameof(fullPath));
        }
    }

    private static string TrimEndingDirectorySeparatorExceptRoot(string path)
    {
        string? root = Path.GetPathRoot(path);
        return root is not null && string.Equals(path, root, StringComparison.OrdinalIgnoreCase)
            ? root
            : path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string NormalizeDirectorySeparators(string path) =>
        OperatingSystem.IsWindows() ? path.Replace('/', '\\') : path;

    private static void RejectWindowsDeviceNamespace(
        string normalizedPath,
        string originalPath,
        string description)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        if (normalizedPath.StartsWith(@"\\?\", StringComparison.Ordinal)
            || normalizedPath.StartsWith(@"\\.\", StringComparison.Ordinal)
            || normalizedPath.StartsWith(@"\??\", StringComparison.Ordinal)
            || normalizedPath.StartsWith(@"\\??\", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"{description} '{originalPath}' uses a Windows device path; only ordinary drive and UNC paths are supported.");
        }
    }

    private static bool IsOrdinaryWindowsPath(string path)
    {
        if (!Path.IsPathFullyQualified(path))
        {
            return false;
        }

        if (path.Length >= 3
            && char.IsAsciiLetter(path[0])
            && path[1] == ':'
            && path[2] == '\\')
        {
            return true;
        }

        if (!path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return false;
        }

        ReadOnlySpan<char> remainder = path.AsSpan(2);
        int serverEnd = remainder.IndexOf('\\');
        if (serverEnd <= 0)
        {
            return false;
        }

        ReadOnlySpan<char> shareAndPath = remainder[(serverEnd + 1)..];
        int shareEnd = shareAndPath.IndexOf('\\');
        int shareLength = shareEnd < 0 ? shareAndPath.Length : shareEnd;
        return shareLength > 0;
    }

    private static class NativeMethods
    {
        [DllImport(
            "kernel32.dll",
            EntryPoint = "CreateFileW",
            CharSet = CharSet.Unicode,
            ExactSpelling = true,
            SetLastError = true)]
        internal static extern SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport(
            "kernel32.dll",
            EntryPoint = "GetFinalPathNameByHandleW",
            CharSet = CharSet.Unicode,
            ExactSpelling = true,
            SetLastError = true)]
        internal static extern uint GetFinalPathNameByHandle(
            SafeFileHandle file,
            [Out] char[] filePath,
            uint filePathLength,
            uint flags);
    }
}
