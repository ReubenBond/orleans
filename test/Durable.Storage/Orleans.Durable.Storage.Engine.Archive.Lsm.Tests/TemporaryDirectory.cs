using System.Globalization;

namespace Orleans.Durable.Storage.Engine.Archive.Lsm.Tests;

/// <summary>
/// A directory on the operating system's file system which a test creates, as an operator creates a store's directory
/// before the store is used, and deletes once it is done.
/// </summary>
internal sealed class TemporaryDirectory : IDisposable
{
    internal TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            string.Concat("clog-archive-tests-", Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)));
        _ = Directory.CreateDirectory(Path);
    }

    internal string Path { get; }

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
