using System.Globalization;

namespace Orleans.Durable.Storage.Testing;

/// <summary>
/// Establishes that a component closed every file under a directory when it was disposed: each file then opens with
/// no sharing, which an open handle refuses (docs/spec/verification.md §11.1).
/// </summary>
/// <remarks>
/// Windows refuses an exclusive open while any handle to the file is open. On Unix, .NET takes an advisory
/// <c>flock</c> for each file it opens (exclusive for <see cref="FileShare.None"/>, shared otherwise), and the WAL takes a
/// shared one for each file it opens for direct I/O, so the check sees every handle the WAL's storages open; a handle
/// native code opens without <c>flock</c>, as RocksDB opens its files, goes unseen there.
/// </remarks>
internal static class FileHandleLeakCheck
{
    /// <summary>
    /// Fails unless every file under <paramref name="directory"/> opens exclusively.
    /// </summary>
    internal static void AssertClosed(string directory)
    {
        var open = new List<string>();
        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                open.Add(Path.GetRelativePath(directory, path));
            }
        }

        if (open.Count > 0)
        {
            Assert.Fail(string.Create(
                CultureInfo.InvariantCulture,
                $"{open.Count} files under {directory} are still open: {string.Join(", ", open)}."));
        }
    }
}
