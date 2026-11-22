using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Orleans.Durable.Storage.Engine.Archive;

/// <summary>
/// The short prefix under which a cluster's archive store lies in its provider's bucket or container (archive.md §2.4,
/// orleans.md §5.13): 1 to <see cref="MaxLength"/> characters of <c>[a-z0-9_-]</c>, one object-name component, so
/// clusters configured on one bucket or container each keep their objects under their own prefix. A composition records
/// it with the store's kind and ID; that each cluster's prefix is unique within its bucket or container is the operator's
/// precondition, which <see cref="Of"/>'s hashed default makes all but certain for distinct seeds.
/// </summary>
public readonly record struct ArchiveStorePrefix
{
    /// <summary>The longest prefix: 16 characters.</summary>
    public const int MaxLength = 16;

    /// <summary>The length of a prefix <see cref="Of"/> derives: 10 characters, 50 bits.</summary>
    public const int DerivedLength = 10;

    private const string Base32 = "abcdefghijklmnopqrstuvwxyz234567";

    private static readonly SearchValues<char> Allowed = SearchValues.Create("abcdefghijklmnopqrstuvwxyz0123456789_-");

    /// <summary>Initializes a new instance of the <see cref="ArchiveStorePrefix"/> struct.</summary>
    /// <param name="value">The prefix.</param>
    /// <exception cref="ArgumentException"><paramref name="value"/> is not 1 to 16 characters of <c>[a-z0-9_-]</c>.</exception>
    public ArchiveStorePrefix(string value)
    {
        if (!IsValid(value))
        {
            throw new ArgumentException(
                $"An archive store's prefix is 1 to {MaxLength} characters of a-z, 0-9, '_', and '-'; '{value}' is not.", nameof(value));
        }

        Value = value;
    }

    /// <summary>Gets the prefix's text; <see langword="null"/> only for the default, which is no prefix.</summary>
    public string Value { get; }

    /// <summary>
    /// Returns the prefix derived from <paramref name="seed"/>, such as a deployment's service ID: the first 50 bits of the
    /// SHA-256 of its UTF-8 bytes in 10 lowercase base32 characters (RFC 4648's alphabet, lowercased). Two distinct seeds
    /// derive one prefix with probability 2⁻⁵⁰, so <c>n</c> clusters on one bucket clash with probability below
    /// <c>n²/2⁵¹</c>.
    /// </summary>
    /// <param name="seed">The seed, non-empty.</param>
    /// <returns>The derived prefix.</returns>
    public static ArchiveStorePrefix Of(string seed)
    {
        ArgumentException.ThrowIfNullOrEmpty(seed);
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        _ = SHA256.HashData(Encoding.UTF8.GetBytes(seed), hash);
        var bits = BinaryPrimitives.ReadUInt64BigEndian(hash);
        return new ArchiveStorePrefix(string.Create(DerivedLength, bits, static (text, bits) =>
        {
            for (var index = 0; index < text.Length; index++)
            {
                text[index] = Base32[(int)((bits >> (64 - 5 * (index + 1))) & 31)];
            }
        }));
    }

    /// <summary>Returns whether <paramref name="value"/> is a prefix: 1 to 16 characters of <c>[a-z0-9_-]</c>.</summary>
    /// <param name="value">The text.</param>
    /// <returns>Whether it is a prefix.</returns>
    public static bool IsValid(string? value) =>
        value is { Length: > 0 and <= MaxLength } && !value.AsSpan().ContainsAnyExcept(Allowed);

    /// <inheritdoc/>
    public override string ToString() => Value ?? string.Empty;
}
