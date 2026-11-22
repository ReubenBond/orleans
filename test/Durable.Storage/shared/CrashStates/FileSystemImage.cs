using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Orleans.Durable.Storage.Testing;

/// <summary>
/// The kinds of file-system operation a <see cref="RecordingFileSystem"/> records.
/// </summary>
internal enum FileSystemOperationKind
{
    /// <summary>An entry operation: <c>Directory</c> gains <c>Name</c>, naming the new, empty file <c>Node</c>.</summary>
    CreateFile,

    /// <summary>An entry operation: <c>Directory</c> gains <c>Name</c>, naming the new, empty directory <c>Node</c>.</summary>
    MakeDirectory,

    /// <summary>An entry operation: <c>Directory</c> gains <c>Name</c>, naming the existing file <c>Node</c>, which keeps its other names.</summary>
    Link,

    /// <summary>An entry operation: <c>Target</c> in <c>Directory</c> atomically names <c>Node</c>, replacing its file, and <c>Name</c> is removed.</summary>
    Rename,

    /// <summary>An entry operation: <c>Directory</c> loses <c>Name</c>, which named the file <c>Node</c>.</summary>
    Unlink,

    /// <summary>An entry operation: <c>Directory</c> loses <c>Name</c>, which named the directory <c>Node</c>.</summary>
    RemoveDirectory,

    /// <summary>A content operation: <c>Bytes</c> are written to the file <c>Node</c> at <c>Offset</c>.</summary>
    Write,

    /// <summary>A content operation: the file <c>Node</c> is cut or extended to <c>Offset</c> bytes.</summary>
    Truncate,

    /// <summary>A flush (<c>fsync</c>) of the file <c>Node</c>.</summary>
    Flush,

    /// <summary>A flush of the directory <c>Node</c>: <c>fsync</c>, or <c>FlushFileBuffers</c> of its handle on Windows.</summary>
    FlushDirectory,

    /// <summary>A write-through write completed: the write recorded at index <c>CompletedWrite</c> is durable.</summary>
    SyncWrite,

    /// <summary>
    /// A write-through write ended without completing: the device failed it, or its process crashed. The write recorded
    /// at index <c>CompletedWrite</c>, which reads return, is durable once a later flush of its file completes.
    /// </summary>
    FailWrite,
}

/// <summary>
/// One recorded file-system operation. Every operation names the inodes it changes, so applying any subset of a trace
/// to an image is well defined.
/// </summary>
/// <param name="Kind">The operation.</param>
/// <param name="Node">The inode created, removed, renamed, written, or flushed.</param>
/// <param name="Path">The operation's path relative to the root when it was recorded, for diagnostics.</param>
/// <param name="Directory">The directory inode an entry operation changes, or -1.</param>
/// <param name="Name">The entry an entry operation adds or removes, or a rename's source.</param>
/// <param name="Target">A rename's destination.</param>
/// <param name="Offset">A write's offset or a truncation's length.</param>
/// <param name="Bytes">A write's bytes.</param>
/// <param name="CompletedWrite">The index of the write a <see cref="FileSystemOperationKind.SyncWrite"/> completes or a
/// <see cref="FileSystemOperationKind.FailWrite"/> ends, or -1.</param>
/// <param name="WriteThrough">Whether a write is write-through: its bytes are durable once its
/// <see cref="FileSystemOperationKind.SyncWrite"/> completes it, and the length it extends once a later flush of its file
/// completes.</param>
/// <param name="Time">When a file's creation, write, or truncation ran, by the recording's clock: the file's last write
/// once the operation applies. Default for every other operation.</param>
internal sealed record FileSystemOperation(
    FileSystemOperationKind Kind,
    int Node,
    string Path,
    int Directory = -1,
    string? Name = null,
    string? Target = null,
    long Offset = 0,
    byte[]? Bytes = null,
    int CompletedWrite = -1,
    bool WriteThrough = false,
    DateTimeOffset Time = default)
{
    /// <summary>
    /// Gets a value indicating whether the operation changes a directory's entries, which a flush of that directory
    /// makes durable.
    /// </summary>
    public bool IsEntry => Kind is FileSystemOperationKind.CreateFile or FileSystemOperationKind.MakeDirectory or FileSystemOperationKind.Link
        or FileSystemOperationKind.Rename or FileSystemOperationKind.Unlink or FileSystemOperationKind.RemoveDirectory;

    /// <summary>
    /// Gets a value indicating whether the operation changes a file's contents, which a flush of that file makes durable.
    /// </summary>
    public bool IsContent => Kind is FileSystemOperationKind.Write or FileSystemOperationKind.Truncate;

    /// <summary>
    /// Gets the inode whose flush makes the operation durable: the directory of an entry operation, the file of a
    /// content operation, or -1 for a flush.
    /// </summary>
    public int FlushedBy
    {
        get
        {
            if (IsEntry)
            {
                return Directory;
            }

            return IsContent ? Node : -1;
        }
    }

    /// <summary>
    /// Gets the sectors a write covers, which a crash may persist independently.
    /// </summary>
    public long Sectors => Kind == FileSystemOperationKind.Write && Bytes!.Length != 0
        ? (Offset + Bytes.Length - 1) / CrashStateEnumerator.SectorSize - Offset / CrashStateEnumerator.SectorSize + 1
        : 0;

    public override string ToString() => Kind switch
    {
        FileSystemOperationKind.Rename => string.Create(CultureInfo.InvariantCulture, $"rename {Path} -> {Target} (#{Node})"),
        FileSystemOperationKind.Write => string.Create(CultureInfo.InvariantCulture, $"{(WriteThrough ? "write-through" : "write")} {Path} (#{Node}) [{Offset}, {Offset + Bytes!.Length})"),
        FileSystemOperationKind.Truncate => string.Create(CultureInfo.InvariantCulture, $"truncate {Path} (#{Node}) to {Offset}"),
        FileSystemOperationKind.SyncWrite => string.Create(CultureInfo.InvariantCulture, $"sync write #{CompletedWrite} of {Path} (#{Node})"),
        FileSystemOperationKind.FailWrite => string.Create(CultureInfo.InvariantCulture, $"fail write #{CompletedWrite} of {Path} (#{Node})"),
        FileSystemOperationKind.Link => string.Create(CultureInfo.InvariantCulture, $"link {Path} as {Name} (#{Node})"),
        FileSystemOperationKind.CreateFile or FileSystemOperationKind.MakeDirectory or FileSystemOperationKind.Unlink
            or FileSystemOperationKind.RemoveDirectory or FileSystemOperationKind.Flush or FileSystemOperationKind.FlushDirectory
            or _ => string.Create(CultureInfo.InvariantCulture, $"{Kind} {Path} (#{Node})"),
    };
}

/// <summary>
/// A file system's state as inodes: directories map names to inodes and files hold bytes. The root is inode 0.
/// </summary>
internal sealed class FileSystemImage
{
    /// <summary>
    /// The root directory's inode.
    /// </summary>
    public const int Root = 0;

    private readonly Dictionary<int, Inode> _nodes;

    /// <summary>
    /// Initializes a new instance of the <see cref="FileSystemImage"/> class holding only an empty root.
    /// </summary>
    public FileSystemImage()
    {
        _nodes = new() { [Root] = Inode.NewDirectory() };
    }

    private FileSystemImage(Dictionary<int, Inode> nodes) => _nodes = nodes;

    /// <summary>
    /// Gets one more than the highest inode the image holds.
    /// </summary>
    public int NextNode => _nodes.Keys.Max() + 1;

    /// <summary>
    /// Returns a copy which shares nothing mutable with this image.
    /// </summary>
    public FileSystemImage Clone() => new(_nodes.ToDictionary(static pair => pair.Key, static pair => pair.Value.Clone()));

    /// <summary>
    /// Returns whether <paramref name="node"/> is a directory.
    /// </summary>
    public bool IsDirectory(int node) => _nodes.TryGetValue(node, out var inode) && inode.Entries is not null;

    /// <summary>
    /// Returns the inode <paramref name="name"/> names in <paramref name="directory"/>, or -1.
    /// </summary>
    public int Lookup(int directory, string name) =>
        _nodes.TryGetValue(directory, out var inode) && inode.Entries is { } entries && entries.TryGetValue(name, out var node) ? node : -1;

    /// <summary>
    /// Returns a directory's entries in ordinal order.
    /// </summary>
    public IReadOnlyList<(string Name, int Node)> Entries(int directory) =>
        [.. _nodes[directory].Entries!.Select(static entry => (entry.Key, entry.Value))];

    /// <summary>
    /// Returns a file's length.
    /// </summary>
    public long Length(int node) => _nodes[node].Length;

    /// <summary>
    /// Returns when a file was last created, written, or truncated, by the recording's clock.
    /// </summary>
    public DateTimeOffset LastWrite(int node) => _nodes[node].LastWrite;

    /// <summary>
    /// Copies up to <paramref name="length"/> bytes of a file from <paramref name="offset"/>.
    /// </summary>
    public byte[] Read(int node, long offset, int length)
    {
        var inode = _nodes[node];
        var available = (int)Math.Clamp(inode.Length - offset, 0, length);
        return inode.Content.AsSpan((int)Math.Min(offset, inode.Length), available).ToArray();
    }

    /// <summary>
    /// Returns a file's bytes.
    /// </summary>
    public byte[] ReadAll(int node) => Read(node, 0, (int)_nodes[node].Length);

    /// <summary>
    /// Returns the bytes of the file at <paramref name="path"/>, whose components '/' separates, or
    /// <see langword="null"/> when no file is reachable there.
    /// </summary>
    public byte[]? ReadFile(string path)
    {
        var node = Root;
        foreach (var name in path.Split('/'))
        {
            node = Lookup(node, name);
            if (node < 0)
            {
                return null;
            }
        }

        return IsDirectory(node) ? null : ReadAll(node);
    }

    /// <summary>
    /// Applies an operation. A write persists only the sectors <paramref name="sectors"/> selects, when it is given.
    /// </summary>
    /// <remarks>
    /// An operation whose inodes another, lost operation created finds them as orphans: a write lands in its file
    /// whether or not the file's name survived, and an entry operation in a directory whose own name was lost changes
    /// that unreachable directory.
    /// </remarks>
    public void Apply(FileSystemOperation operation, Func<long, bool>? sectors = null)
    {
        switch (operation.Kind)
        {
            case FileSystemOperationKind.CreateFile:
                File(operation.Node).Touch(operation.Time);
                Directory(operation.Directory)[operation.Name!] = operation.Node;
                break;
            case FileSystemOperationKind.MakeDirectory:
                _ = Directory(operation.Node);
                Directory(operation.Directory)[operation.Name!] = operation.Node;
                break;
            case FileSystemOperationKind.Link:
                _ = File(operation.Node);
                Directory(operation.Directory)[operation.Name!] = operation.Node;
                break;
            case FileSystemOperationKind.Rename:
                // The seam renames only files. A kept rename whose file lost its creation and every write names an
                // empty file, as a kept link does.
                _ = File(operation.Node);
                var entries = Directory(operation.Directory);
                entries[operation.Target!] = operation.Node;
                if (entries.TryGetValue(operation.Name!, out var renamed) && renamed == operation.Node)
                {
                    _ = entries.Remove(operation.Name!);
                }

                break;
            case FileSystemOperationKind.Unlink:
            case FileSystemOperationKind.RemoveDirectory:
                var parent = Directory(operation.Directory);
                if (parent.TryGetValue(operation.Name!, out var removed) && removed == operation.Node)
                {
                    _ = parent.Remove(operation.Name!);
                }

                break;
            case FileSystemOperationKind.Write:
                File(operation.Node).Write(operation.Offset, operation.Bytes!, sectors);
                File(operation.Node).Touch(operation.Time);
                break;
            case FileSystemOperationKind.Truncate:
                File(operation.Node).Truncate(operation.Offset);
                File(operation.Node).Touch(operation.Time);
                break;
            case FileSystemOperationKind.Flush:
            case FileSystemOperationKind.FlushDirectory:
            case FileSystemOperationKind.SyncWrite:
            case FileSystemOperationKind.FailWrite:
            default:
                // A flush, or a write's completion or failure, changes what is durable, not what the image holds.
                break;
        }
    }

    /// <summary>
    /// Applies a write whose bytes persisted but whose extension of its file's length did not: bytes beyond the length
    /// are held unseen, and appear if a later operation extends the file over them.
    /// </summary>
    public void ApplyData(FileSystemOperation operation)
    {
        File(operation.Node).WriteData(operation.Offset, operation.Bytes!);
        File(operation.Node).Touch(operation.Time);
    }

    /// <summary>
    /// Applies a write whose new file length persisted but whose bytes did not: the file grows to the write's end with
    /// zeros, and bytes it already held are unchanged, as ext4's <c>data=writeback</c> allows.
    /// </summary>
    public void ApplyZeroExtension(FileSystemOperation operation)
    {
        var file = File(operation.Node);
        var end = operation.Offset + operation.Bytes!.Length;
        if (end > file.Length)
        {
            file.Truncate(end);
        }

        file.Touch(operation.Time);
    }

    /// <summary>
    /// Returns a hash of the tree reachable from the root: every path, kind, and file content. Images with equal
    /// fingerprints look the same to every reader.
    /// </summary>
    public string Fingerprint()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[sizeof(long)];
        foreach (var (path, node) in Reachable())
        {
            hash.AppendData(Encoding.UTF8.GetBytes(path));
            var inode = _nodes[node];
            if (inode.Entries is null)
            {
                _ = BitConverter.TryWriteBytes(length, inode.Length);
                hash.AppendData(length);
                hash.AppendData(inode.Content.AsSpan(0, (int)inode.Length));
            }
            else
            {
                hash.AppendData("/"u8);
            }

            hash.AppendData([0]);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    /// <summary>
    /// Lists the reachable tree, one path per line with each file's length.
    /// </summary>
    public string Describe()
    {
        var text = new StringBuilder();
        foreach (var (path, node) in Reachable())
        {
            var inode = _nodes[node];
            _ = inode.Entries is null
                ? text.Append(CultureInfo.InvariantCulture, $"  {path} ({inode.Length} bytes, #{node})").AppendLine()
                : text.Append(CultureInfo.InvariantCulture, $"  {path}/ (#{node})").AppendLine();
        }

        return text.ToString();
    }

    private IEnumerable<(string Path, int Node)> Reachable()
    {
        var pending = new Stack<(string Path, int Node)>();
        pending.Push((string.Empty, Root));
        while (pending.TryPop(out var current))
        {
            yield return current;
            if (_nodes[current.Node].Entries is { } entries)
            {
                foreach (var (name, child) in entries.Reverse())
                {
                    pending.Push((current.Path.Length == 0 ? name : current.Path + "/" + name, child));
                }
            }
        }
    }

    private SortedDictionary<string, int> Directory(int node)
    {
        if (!_nodes.TryGetValue(node, out var inode))
        {
            inode = Inode.NewDirectory();
            _nodes.Add(node, inode);
        }

        return inode.Entries ?? throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"Inode {node} is a file, not a directory."));
    }

    private Inode File(int node)
    {
        if (!_nodes.TryGetValue(node, out var inode))
        {
            inode = new Inode();
            _nodes.Add(node, inode);
        }

        return inode.Entries is null ? inode : throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"Inode {node} is a directory, not a file."));
    }

    private sealed class Inode
    {
        public SortedDictionary<string, int>? Entries { get; private init; }

        public byte[] Content { get; private set; } = [];

        public long Length { get; private set; }

        // Not part of the fingerprint: crash states differ by what files hold, not by when they were written.
        public DateTimeOffset LastWrite { get; private set; }

        public static Inode NewDirectory() => new() { Entries = [with(StringComparer.Ordinal)] };

        // A file's bytes past its length are not carried over: a file system reads past a file's end as zeros.
        public Inode Clone() => new()
        {
            Entries = Entries is null ? null : new SortedDictionary<string, int>(Entries, StringComparer.Ordinal),
            Content = Content.AsSpan(0, (int)Length).ToArray(),
            Length = Length,
            LastWrite = LastWrite,
        };

        public void Touch(DateTimeOffset time) => LastWrite = time;

        public void Write(long offset, byte[] bytes, Func<long, bool>? sectors)
        {
            for (var start = 0; start < bytes.Length;)
            {
                var position = offset + start;
                var sector = position / CrashStateEnumerator.SectorSize;
                var end = (int)Math.Min(bytes.Length, (sector + 1) * CrashStateEnumerator.SectorSize - offset);
                if (sectors is null || sectors(sector))
                {
                    Store(position, bytes.AsSpan(start, end - start));
                }

                start = end;
            }
        }

        /// <summary>
        /// Stores bytes without changing the length. Past the length, the file holds only zeros and such bytes, so
        /// extending it shows them.
        /// </summary>
        public void WriteData(long offset, byte[] bytes)
        {
            Reserve(offset + bytes.Length);
            bytes.CopyTo(Content.AsSpan((int)offset));
        }

        public void Truncate(long length)
        {
            Reserve(length);
            if (length < Length)
            {
                Content.AsSpan((int)length).Clear();
            }

            Length = length;
        }

        private void Store(long position, ReadOnlySpan<byte> bytes)
        {
            var end = position + bytes.Length;
            Reserve(end);
            bytes.CopyTo(Content.AsSpan((int)position));
            Length = Math.Max(Length, end);
        }

        private void Reserve(long length)
        {
            if (Content.Length < length)
            {
                var resized = new byte[Math.Max(length, Content.Length * 2L)];
                Content.CopyTo(resized, 0);
                Content = resized;
            }
        }
    }
}
