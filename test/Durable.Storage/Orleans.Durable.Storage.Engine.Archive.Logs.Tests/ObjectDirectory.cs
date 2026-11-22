using Orleans.Durable.Storage.FileSystem.Archive;

namespace Orleans.Durable.Storage.Engine.Archive.Logs.Tests;

/// <summary>A file-system object store in a directory of its own, which disposal removes.</summary>
internal sealed class ObjectDirectory : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "clog-chunk-logs", Guid.NewGuid().ToString("N"));

    public ObjectDirectory()
    {
        _ = Directory.CreateDirectory(_path);
        Store = new FileSystemObjectStore(_path, TimeProvider.System);
    }

    public FileSystemObjectStore Store { get; }

    public void Dispose() => Directory.Delete(_path, recursive: true);
}
