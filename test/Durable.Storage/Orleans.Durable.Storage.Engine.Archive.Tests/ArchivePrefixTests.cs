using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Archive.Tests;

/// <summary>
/// A prefix's shape (archive.md §2.1): a non-empty version base, and a tombstone only for a <c>Delete</c> base which no row
/// follows, so it holds no content and no <c>Replace</c> opened it. The constructor refuses every other shape.
/// </summary>
public sealed class ArchivePrefixTests : IDisposable
{
    private static readonly RowToken VersionBase = new([0xb0]);

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    [Fact]
    public void APrefixWithATombstoneHasNoBaseAndNoContent()
    {
        using var content = ArcBuffer.Wrap(new byte[] { 1 });
        ReadOnlyMemory<byte> tombstone = new byte[8];

        using var deleted = new ArchivePrefix(default, VersionBase, hasBase: false, tombstone, default, coverage: null);
        Assert.Equal(tombstone.ToArray(), deleted.Tombstone?.ToArray());
        _ = Assert.Throws<ArgumentException>(() => new ArchivePrefix(default, VersionBase, hasBase: true, tombstone, default, coverage: null));
        _ = Assert.Throws<ArgumentException>(() => new ArchivePrefix(default, VersionBase, hasBase: false, tombstone, content, coverage: null));
        _ = Assert.Throws<ArgumentException>(() => new ArchivePrefix(default, VersionBase, hasBase: false, ReadOnlyMemory<byte>.Empty, default, coverage: null));
        _ = Assert.Throws<ArgumentException>(() => new ArchivePrefix(default, default, hasBase: false, null, default, coverage: null));
    }

    public void Dispose() => _leaks.Dispose();
}
