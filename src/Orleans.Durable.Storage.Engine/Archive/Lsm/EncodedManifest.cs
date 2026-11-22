using Orleans.Durable.Storage.Engine.Buffers;

namespace Orleans.Durable.Storage.Engine.Archive.Lsm;

/// <summary>
/// A manifest value's encoding: its bytes, which the holder owns and disposes, and the CRC-32C of those bytes, which
/// their upload carries (archive §3), as <see cref="EncodedRunObject"/> carries a run object's.
/// </summary>
/// <param name="content">The encoding's bytes, which the instance takes.</param>
/// <param name="crc32c">The CRC-32C of <paramref name="content"/>, header included, computed over the finished bytes.</param>
internal sealed class EncodedManifest(ArcBuffer content, uint crc32c) : IDisposable
{
    /// <summary>Gets the encoding's bytes.</summary>
    public ArcBuffer Content { get; } = content;

    /// <summary>Gets the CRC-32C of <see cref="Content"/>, which the manifest's upload carries.</summary>
    public uint Crc32C { get; } = crc32c;

    /// <summary>Releases the encoding's bytes.</summary>
    public void Dispose() => Content.Dispose();
}
