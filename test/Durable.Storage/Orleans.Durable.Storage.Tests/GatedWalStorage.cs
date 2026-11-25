using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Wal;
using Orleans.Durable.Storage.Engine.Wal.Portable;

namespace Orleans.Durable.Storage.Tests;

/// <summary>
/// WAL storage over <see cref="PortableWalStorage"/> whose log-file writes wait while its gate is closed, so a test holds a
/// LedgerStore operation which writes, a write or a seal, inside the store until it opens the gate.
/// </summary>
internal sealed class GatedWalStorage : WalStorage
{
    private readonly WalStorage _inner = new PortableWalStorage();
    private readonly Lock _lock = new();
    private TaskCompletionSource _gate = Opened();
    private int _held;

    /// <summary>Gets the number of writes waiting at the closed gate.</summary>
    public int Held => Volatile.Read(ref _held);

    /// <summary>Closes the gate: later writes wait until it opens.</summary>
    public void Close()
    {
        lock (_lock)
        {
            if (_gate.Task.IsCompleted)
            {
                _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
    }

    /// <summary>Opens the gate, letting every waiting write through.</summary>
    public void Open()
    {
        lock (_lock)
        {
            _ = _gate.TrySetResult();
        }
    }

    public override ValueTask CreateDirectoryAsync(string path, CancellationToken cancellationToken) => _inner.CreateDirectoryAsync(path, cancellationToken);

    public override async ValueTask<WalDirectory> OpenDirectoryAsync(string path, CancellationToken cancellationToken) =>
        new Directory(this, await _inner.OpenDirectoryAsync(path, cancellationToken));

    public override ValueTask DeleteDirectoryAsync(string path, CancellationToken cancellationToken) => _inner.DeleteDirectoryAsync(path, cancellationToken);

    public override ValueTask<IReadOnlyList<string>> ListDirectoriesAsync(string path, CancellationToken cancellationToken) => _inner.ListDirectoriesAsync(path, cancellationToken);

    public override ValueTask<WalDirectoryUsage> MeasureDirectoryAsync(string path, CancellationToken cancellationToken) => _inner.MeasureDirectoryAsync(path, cancellationToken);

    private static TaskCompletionSource Opened()
    {
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        opened.SetResult();
        return opened;
    }

    private async Task PassAsync()
    {
        Task gate;
        lock (_lock)
        {
            gate = _gate.Task;
        }

        if (gate.IsCompleted)
        {
            return;
        }

        _ = Interlocked.Increment(ref _held);
        try
        {
            await gate;
        }
        finally
        {
            _ = Interlocked.Decrement(ref _held);
        }
    }

    private sealed class Directory(GatedWalStorage storage, WalDirectory inner) : WalDirectory
    {
        public override string Path => inner.Path;

        public override int MinimumPageSize => inner.MinimumPageSize;

        public override ValueTask<IReadOnlyList<ulong>> ListFilesAsync(CancellationToken cancellationToken) => inner.ListFilesAsync(cancellationToken);

        public override async ValueTask<WalFile> CreateFileAsync(ulong file, ArcBuffer header, long preallocatedLength, WalFillPace? pace, CancellationToken cancellationToken) =>
            new File(storage, await inner.CreateFileAsync(file, header, preallocatedLength, pace, cancellationToken));

        public override async ValueTask<WalFile> CreateAllocatedFileAsync(ulong file, ArcBuffer header, long length, CancellationToken cancellationToken) =>
            new File(storage, await inner.CreateAllocatedFileAsync(file, header, length, cancellationToken));

        public override ValueTask<PreparedWalFile> PrepareFileAsync(long preallocatedLength, WalFillPace? pace, CancellationToken cancellationToken) =>
            inner.PrepareFileAsync(preallocatedLength, pace, cancellationToken);

        public override async ValueTask<WalFile> PublishPreparedFileAsync(PreparedWalFile prepared, ulong file, ArcBuffer header, CancellationToken cancellationToken) =>
            new File(storage, await inner.PublishPreparedFileAsync(prepared, file, header, cancellationToken));

        public override async ValueTask<WalFile> OpenFileAsync(ulong file, CancellationToken cancellationToken) =>
            new File(storage, await inner.OpenFileAsync(file, cancellationToken));

        public override ValueTask DeleteFileAsync(ulong file, CancellationToken cancellationToken) => inner.DeleteFileAsync(file, cancellationToken);

        public override ValueTask<WalFile> CreateNamedFileAsync(string name, ArcBuffer contents, CancellationToken cancellationToken) =>
            inner.CreateNamedFileAsync(name, contents, cancellationToken);

        public override ValueTask<WalFile> OpenNamedFileAsync(string name, CancellationToken cancellationToken) => inner.OpenNamedFileAsync(name, cancellationToken);

        public override ValueTask<IReadOnlyList<string>> ListNamedFilesAsync(string extension, CancellationToken cancellationToken) =>
            inner.ListNamedFilesAsync(extension, cancellationToken);

        public override ValueTask DeleteNamedFileAsync(string name, CancellationToken cancellationToken) => inner.DeleteNamedFileAsync(name, cancellationToken);

        public override ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class File(GatedWalStorage storage, WalFile inner) : WalFile
    {
        public override long Length => inner.Length;

        public override async ValueTask WriteAsync(long offset, ReadOnlyMemory<ArcBuffer> buffers)
        {
            await storage.PassAsync();
            await inner.WriteAsync(offset, buffers);
        }

        public override ValueTask<ArcBuffer> ReadAsync(long offset, int length, CancellationToken cancellationToken) => inner.ReadAsync(offset, length, cancellationToken);

        public override ValueTask TruncateAsync(long length, CancellationToken cancellationToken) => inner.TruncateAsync(length, cancellationToken);

        public override ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
