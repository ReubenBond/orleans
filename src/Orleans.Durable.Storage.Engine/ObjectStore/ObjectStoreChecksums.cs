using System.Buffers;
using System.Buffers.Binary;
using System.IO.Hashing;
using Orleans.Durable.Storage.Engine.Buffers;

namespace Orleans.Durable.Storage.Engine.ObjectStore;

/// <summary>
/// The CRC-32C which crosses the object store's boundary: the writers compute it over the bytes they verify, and the
/// providers check it or send it to their service (object-store.md §2.1).
/// </summary>
public static class ObjectStoreChecksums
{
    [ThreadStatic]
    private static Crc32? _perThread;

    /// <summary>
    /// Returns the CRC-32C of <paramref name="content"/>.
    /// </summary>
    public static uint Crc32C(ArcBuffer content)
    {
        var crc = _perThread ??= new(Crc32ParameterSet.Crc32C);
        crc.Reset();
        foreach (var segment in content)
        {
            crc.Append(segment);
        }

        return crc.GetCurrentHashAsUInt32();
    }

    /// <summary>
    /// Returns the CRC-32C of <paramref name="bytes"/>, appended segment by segment.
    /// </summary>
    public static uint Crc32C(ReadOnlySequence<byte> bytes)
    {
        if (bytes.IsSingleSegment)
        {
            return Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, bytes.FirstSpan);
        }

        var crc = _perThread ??= new(Crc32ParameterSet.Crc32C);
        crc.Reset();
        foreach (var segment in bytes)
        {
            crc.Append(segment.Span);
        }

        return crc.GetCurrentHashAsUInt32();
    }

    /// <summary>
    /// Returns the CRC-32C an upload of <paramref name="content"/> carries: its writer's <paramref name="crc32c"/>, so the
    /// service checks the bytes the writer verified, or, when no writer gave one, the content's own.
    /// </summary>
    public static uint Crc32CToSend(ArcBuffer content, uint? crc32c) => crc32c ?? Crc32C(content);

    /// <summary>
    /// Returns the base64 of the big-endian <paramref name="crc32c"/>, as S3's <c>x-amz-checksum-crc32c</c> and a Cloud
    /// Storage object's <c>crc32c</c> carry it.
    /// </summary>
    public static string Crc32CBase64(uint crc32c)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, crc32c);
        return Convert.ToBase64String(bytes);
    }

    /// <summary>
    /// Parses a service's base64 of a big-endian CRC-32C, as <see cref="Crc32CBase64"/> writes it.
    /// </summary>
    /// <returns>The CRC-32C, or <see langword="null"/> when <paramref name="base64"/> is no base64 of 4 bytes.</returns>
    public static uint? Crc32CFromBase64(string? base64)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        return base64 is not null && Convert.TryFromBase64String(base64, bytes, out var written) && written == sizeof(uint)
            ? BinaryPrimitives.ReadUInt32BigEndian(bytes)
            : null;
    }

    /// <summary>
    /// Throws when a writer gave <paramref name="crc32c"/> and <paramref name="bytes"/>, the bytes the store is about to
    /// keep, have another CRC-32C.
    /// </summary>
    /// <param name="name">The object's name.</param>
    /// <param name="bytes">The bytes the store keeps: its copy, or what it wrote.</param>
    /// <param name="crc32c">The writer's CRC-32C, or <see langword="null"/> when no writer gave one.</param>
    /// <exception cref="ObjectContentChangedException">The bytes do not match the writer's CRC-32C.</exception>
    public static void ThrowIfChanged(ObjectName name, ReadOnlySequence<byte> bytes, uint? crc32c)
    {
        if (crc32c is { } expected && Crc32C(bytes) != expected)
        {
            throw ObjectContentChangedException.For(name);
        }
    }
}
