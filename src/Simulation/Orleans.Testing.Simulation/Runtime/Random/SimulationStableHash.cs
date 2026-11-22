using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Orleans.Testing.Simulation.Runtime.Random;

/// <summary>
/// <para>
/// The stable, cross-process-safe string-hashing primitive shared by every deterministic seed
/// derivation in Clockwork: the <c>SimulationSeed</c> type (in the Orleans.Testing.Simulation.Simulation
/// package) delegates to this exact algorithm, and <see cref="SimulationSeedAuthority"/> builds
/// its per-domain/per-site derivation on top of it, so "derive a stable seed from a string" has
/// exactly one implementation across the codebase.
/// </para>
/// <para>
/// This intentionally never uses <see cref="string.GetHashCode()"/>/<see cref="object.GetHashCode()"/>:
/// the runtime documents that hash code as unstable across processes, .NET versions, and even
/// repeated runs of the same process (string hashing is randomized per-process by default) - using
/// it here would silently break reproducibility. Instead, this SHA-256-hashes the UTF-8 bytes of
/// the input and interprets the first four (or eight, for <see cref="ToInt64(string)"/>) bytes of
/// the digest as a big-endian signed integer. SHA-256 and UTF-8 are both fixed, versioned,
/// platform-independent algorithms, so the same input always produces the same result on any
/// machine, any .NET version, and any process.
/// </para>
/// </summary>
internal static class SimulationStableHash
{
    /// <summary>
    /// The separator inserted between components by the <c>*Combine</c>/<see cref="ToInt32(IEnumerable{string})"/>
    /// overloads before hashing, so that e.g. combining ("ab", "c") never collides with ("a", "bc").
    /// This is the ASCII "unit separator" control character (U+001F), chosen because it is not
    /// valid in most human-authored identifiers (test names, node addresses, domain names) and is
    /// stable across .NET versions and platforms.
    /// </summary>
    public const char ComponentSeparator = '\u001f';

    /// <summary>
    /// Derives a deterministic <see cref="int"/> from a single string.
    /// </summary>
    /// <param name="value">The string to derive a value from.</param>
    /// <returns>
    /// The first four bytes of the SHA-256 hash of the UTF-8 encoding of <paramref name="value"/>,
    /// interpreted as a big-endian signed integer.
    /// </returns>
    public static int ToInt32(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        HashUtf8(value, hash);
        return BinaryPrimitives.ReadInt32BigEndian(hash);
    }

    /// <summary>
    /// Derives a single deterministic <see cref="int"/> from multiple string components (for
    /// example, a domain name and a stable site identifier), combined in order so the result
    /// depends on both the content and the position of each component.
    /// </summary>
    /// <param name="values">The components to combine, in order.</param>
    /// <returns>A deterministic value derived from all of <paramref name="values"/>.</returns>
    public static int ToInt32(IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return ToInt32(string.Join(ComponentSeparator, values));
    }

    /// <summary>
    /// Derives a single deterministic <see cref="int"/> from multiple string components. See
    /// <see cref="ToInt32(IEnumerable{string})"/>.
    /// </summary>
    /// <param name="values">The components to combine, in order.</param>
    public static int ToInt32(params string[] values) => ToInt32((IEnumerable<string>)values);

    /// <summary>
    /// Derives a deterministic <see cref="long"/> from a single string, using the first eight
    /// bytes of the SHA-256 digest instead of <see cref="ToInt32(string)"/>'s four. Useful where a
    /// wider identity space is wanted (e.g. a stable decision-log correlation id) without changing
    /// the four-byte seed contract that <c>SimulationSeed</c>/<see cref="ToInt32(string)"/> already
    /// guarantee.
    /// </summary>
    /// <param name="value">The string to derive a value from.</param>
    public static long ToInt64(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        HashUtf8(value, hash);
        return BinaryPrimitives.ReadInt64BigEndian(hash);
    }

    private static void HashUtf8(string value, Span<byte> destination)
    {
        var byteCount = Encoding.UTF8.GetByteCount(value);
        Span<byte> buffer = byteCount <= 256 ? stackalloc byte[byteCount] : new byte[byteCount];
        Encoding.UTF8.GetBytes(value, buffer);
        SHA256.HashData(buffer, destination);
    }
}
