using System.Buffers;
using Orleans.Durable.Storage.Engine.Buffers;

namespace Orleans.Durable.Storage.Engine.Archive;

/// <summary>
/// A key's content in a store's prefix (archive.md §4): the base's body, then each later held <c>Append</c>'s body,
/// concatenated, as <see cref="ArcBuffer"/> pages the content owns; page boundaries are not command boundaries.
/// </summary>
/// <remarks>Construction takes its own lease on the bytes it is given, which the caller keeps and disposes.</remarks>
/// <param name="bytes">The content's bytes, of which the content retains its own lease; the default is empty.</param>
public sealed class ArchiveContent(ArcBuffer bytes) : IDisposable
{
    private readonly ArcBuffer _bytes = bytes.Retain();
    private int _disposed;

    /// <summary>Gets the content's length in bytes.</summary>
    public int Length => _bytes.Length;

    /// <summary>Returns the content's bytes, valid while the content is not disposed.</summary>
    public ReadOnlySequence<byte> AsReadOnlySequence() => _bytes.AsReadOnlySequence();

    /// <summary>Returns an owned buffer of the content, which retains its pages; the caller disposes it.</summary>
    public ArcBuffer Retain() => _bytes.Retain();

    /// <summary>Returns a copy of the content's bytes.</summary>
    public byte[] ToArray() => _bytes.ToArray();

    /// <summary>Releases the content's lease on its pages.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _bytes.Dispose();
        }
    }
}
