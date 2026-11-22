using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;

namespace Orleans.Durable.Storage.Engine.Archive;

/// <summary>
/// An archive store's ID (archive.md §2.4): a random non-zero 128-bit value its first user creates once, which tells one
/// container, bucket, or table from another of the same kind.
/// </summary>
public readonly record struct ArchiveStoreId
{

    /// <summary>Initializes a new instance of the <see cref="ArchiveStoreId"/> struct.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is 0.</exception>
    public ArchiveStoreId(UInt128 value)
    {
        ArgumentOutOfRangeException.ThrowIfZero(value);
        Value = value;
    }

    /// <summary>Gets the ID's value.</summary>
    /// <exception cref="InvalidOperationException">The instance is the default, which no constructor made.</exception>
    public UInt128 Value =>
        field != UInt128.Zero ? field : throw new InvalidOperationException("The default ArchiveStoreId is not a store ID.");

    /// <summary>Returns a new random ID from the platform's random number generator, as a store's first user creates one.</summary>
    public static ArchiveStoreId NewRandom()
    {
        Span<byte> bytes = stackalloc byte[16];
        var value = UInt128.Zero;
        while (value == UInt128.Zero)
        {
            RandomNumberGenerator.Fill(bytes);
            value = BinaryPrimitives.ReadUInt128LittleEndian(bytes);
        }

        return new ArchiveStoreId(value);
    }

    /// <summary>Returns the ID as 32 lowercase hexadecimal digits, its stored form.</summary>
    public override string ToString() => Value.ToString("x32", CultureInfo.InvariantCulture);

    /// <summary>Parses the stored form: exactly 32 lowercase hexadecimal digits, not all zero.</summary>
    /// <returns>The ID, or <see langword="null"/> when <paramref name="text"/> is not one.</returns>
    public static ArchiveStoreId? TryParse(ReadOnlySpan<char> text)
    {
        if (text.Length != 32 || text.ContainsAnyExcept("0123456789abcdef") || !text.ContainsAnyExcept('0'))
        {
            return null;
        }

        return new ArchiveStoreId(UInt128.Parse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture));
    }
}
