using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;

namespace Orleans.Durable.Storage.Engine.Ledgers.Objects;

/// <summary>
/// An object-ledger store's ID (object-ledgers.md §7, orleans.md §5.14): a random non-zero 128-bit value its first user
/// creates once in <c>ledgers/store</c>, which tells one object-ledger container or bucket from another.
/// </summary>
public readonly record struct ObjectLedgerStoreId
{
    /// <summary>Initializes a new instance of the <see cref="ObjectLedgerStoreId"/> struct.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is 0.</exception>
    public ObjectLedgerStoreId(UInt128 value)
    {
        ArgumentOutOfRangeException.ThrowIfZero(value);
        Value = value;
    }

    /// <summary>Gets the ID's value.</summary>
    /// <exception cref="InvalidOperationException">The instance is the default, which no constructor made.</exception>
    public UInt128 Value =>
        field != UInt128.Zero ? field : throw new InvalidOperationException("The default ObjectLedgerStoreId is not a store ID.");

    /// <summary>Returns a new random ID from the platform's random number generator.</summary>
    public static ObjectLedgerStoreId NewRandom()
    {
        Span<byte> bytes = stackalloc byte[16];
        var value = UInt128.Zero;
        while (value == UInt128.Zero)
        {
            RandomNumberGenerator.Fill(bytes);
            value = BinaryPrimitives.ReadUInt128LittleEndian(bytes);
        }

        return new ObjectLedgerStoreId(value);
    }

    /// <summary>Returns the ID as 32 lowercase hexadecimal digits, its stored form.</summary>
    public override string ToString() => Value.ToString("x32", CultureInfo.InvariantCulture);

    /// <summary>Parses the stored form: exactly 32 lowercase hexadecimal digits, not all zero.</summary>
    /// <returns>The ID, or <see langword="null"/> when <paramref name="text"/> is not one.</returns>
    public static ObjectLedgerStoreId? TryParse(ReadOnlySpan<char> text)
    {
        if (text.Length != 32 || text.ContainsAnyExcept("0123456789abcdef") || !text.ContainsAnyExcept('0'))
        {
            return null;
        }

        return new ObjectLedgerStoreId(UInt128.Parse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture));
    }
}
