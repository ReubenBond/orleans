using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;

namespace Orleans.Testing.Simulation.Instrumentation.Signing;

/// <summary>
/// A strong-name key loaded from an <c>.snk</c> file. The file is expected to hold a Microsoft
/// CryptoAPI key blob: a full private-key blob (as produced by <c>sn -k</c>), which can sign, or a
/// public-key-only blob (as produced by <c>sn -p</c>), which cannot. The raw blob bytes are handed
/// to Mono.Cecil's <c>WriterParameters.StrongNameKeyBlob</c> to (re-)sign a rewritten assembly.
/// </summary>
public sealed class StrongNameKey
{
    private const byte PrivateKeyBlobType = 0x07;
    private const byte PublicKeyBlobType = 0x06;
    private const byte CurrentBlobVersion = 0x02;
    private const uint RsaSignAlgorithmId = 0x00002400;
    private const uint PublicKeyMagic = 0x31415352;
    private const uint PrivateKeyMagic = 0x32415352;
    private const uint Sha1AlgorithmId = 0x00008004;
    private readonly byte[] _blob;

    private StrongNameKey(byte[] blob, bool canSign, string publicKeyToken)
    {
        _blob = [.. blob];
        CanSign = canSign;
        PublicKeyToken = publicKeyToken;
    }

    /// <summary>Gets the raw CryptoAPI key blob bytes.</summary>
    public byte[] Blob => [.. _blob];

    /// <summary>
    /// Gets a value indicating whether this key contains private-key material and can therefore sign
    /// an assembly. A public-only key cannot.
    /// </summary>
    public bool CanSign { get; }

    /// <summary>Gets the lower-case public-key token produced by this key.</summary>
    public string PublicKeyToken { get; }

    /// <summary>Loads a strong-name key from the file at <paramref name="path"/>.</summary>
    /// <param name="path">The path of the <c>.snk</c> file.</param>
    /// <returns>The loaded key.</returns>
    /// <exception cref="SigningException">The file is missing, empty, or not a recognized key blob.</exception>
    public static StrongNameKey Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!File.Exists(path))
        {
            throw new SigningException($"Strong-name key file was not found: '{path}'.");
        }

        byte[] blob;
        try
        {
            blob = File.ReadAllBytes(path);
        }
        catch (IOException ex)
        {
            throw new SigningException($"Failed to read strong-name key file '{path}': {ex.Message}", ex);
        }

        return FromBlob(blob, path);
    }

    /// <summary>Creates a strong-name key from raw CryptoAPI key blob bytes.</summary>
    /// <param name="blob">The key blob bytes.</param>
    /// <param name="sourceName">A descriptive name of the blob's origin, used in error messages.</param>
    /// <returns>The key.</returns>
    /// <exception cref="SigningException">The blob is empty or not a recognized key blob.</exception>
    public static StrongNameKey FromBlob(byte[] blob, string sourceName = "<blob>")
    {
        ArgumentNullException.ThrowIfNull(blob);
        if (blob.Length < 12)
        {
            throw new SigningException(
                $"Strong-name key '{sourceName}' is not a valid CryptoAPI key blob (too short).");
        }

        // PUBLICKEYSTRUC.bType is the first byte: 0x07 = PRIVATEKEYBLOB, 0x06 = PUBLICKEYBLOB.
        byte blobType = blob[0];
        return blobType switch
        {
            PrivateKeyBlobType => CreateValidated(blob, isPrivate: true, sourceName),
            PublicKeyBlobType => CreateValidated(blob, isPrivate: false, sourceName),
            _ => throw new SigningException(
                $"Strong-name key '{sourceName}' is not a recognized CryptoAPI key blob " +
                $"(unexpected blob type 0x{blobType:x2})."),
        };
    }

    private static StrongNameKey CreateValidated(byte[] blob, bool isPrivate, string sourceName)
    {
        int publicBlobLength = ValidateCryptoApiBlob(blob, isPrivate, sourceName);
        return new StrongNameKey(
            blob,
            canSign: isPrivate,
            ComputePublicKeyToken(blob, publicBlobLength));
    }

    private static int ValidateCryptoApiBlob(byte[] blob, bool isPrivate, string sourceName)
    {
        if (blob.Length < 20)
        {
            throw new SigningException(
                $"Strong-name key '{sourceName}' is not a valid CryptoAPI RSA key blob (too short).");
        }

        if (blob[1] != CurrentBlobVersion
            || BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(2, 2)) != 0
            || BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(4, 4)) != RsaSignAlgorithmId)
        {
            throw new SigningException(
                $"Strong-name key '{sourceName}' has an invalid CryptoAPI blob header or algorithm.");
        }

        uint expectedMagic = isPrivate ? PrivateKeyMagic : PublicKeyMagic;
        if (BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(8, 4)) != expectedMagic)
        {
            throw new SigningException(
                $"Strong-name key '{sourceName}' has invalid RSA key magic.");
        }

        uint bitLength = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(12, 4));
        if (bitLength == 0
            || bitLength % (isPrivate ? 16u : 8u) != 0
            || bitLength / 8 > int.MaxValue)
        {
            throw new SigningException(
                $"Strong-name key '{sourceName}' has an invalid RSA key length.");
        }

        int modulusLength = checked((int)(bitLength / 8));
        int halfLength = modulusLength / 2;
        long expectedLength = isPrivate
            ? 20L + (2L * modulusLength) + (5L * halfLength)
            : 20L + modulusLength;
        if (expectedLength > int.MaxValue)
        {
            throw new SigningException(
                $"Strong-name key '{sourceName}' has an invalid RSA key length.");
        }

        if (blob.Length != expectedLength)
        {
            string problem = blob.Length < expectedLength ? "truncated" : "contains trailing data";
            throw new SigningException(
                $"Strong-name key '{sourceName}' is {problem}; expected {expectedLength} bytes but found {blob.Length}.");
        }

        int offset = 20;
        byte[] modulus = ReadComponent(blob, ref offset, modulusLength);
        if ((modulus[0] & 0x80) == 0)
        {
            throw new SigningException(
                $"Strong-name key '{sourceName}' has an RSA modulus inconsistent with its declared bit length.");
        }

        byte[]? p = null;
        byte[]? q = null;
        byte[]? dp = null;
        byte[]? dq = null;
        byte[]? inverseQ = null;
        byte[]? d = null;
        if (isPrivate)
        {
            p = ReadComponent(blob, ref offset, halfLength);
            q = ReadComponent(blob, ref offset, halfLength);
            dp = ReadComponent(blob, ref offset, halfLength);
            dq = ReadComponent(blob, ref offset, halfLength);
            inverseQ = ReadComponent(blob, ref offset, halfLength);
            d = ReadComponent(blob, ref offset, modulusLength);
        }

        if (offset != blob.Length)
        {
            throw new SigningException(
                $"Strong-name key '{sourceName}' has malformed RSA component offsets.");
        }

        uint publicExponent = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(16, 4));
        if (publicExponent < 3 || (publicExponent & 1) == 0)
        {
            throw new SigningException(
                $"Strong-name key '{sourceName}' has an invalid RSA public exponent.");
        }

        var parameters = new RSAParameters
        {
            Modulus = modulus,
            Exponent = EncodePublicExponent(publicExponent),
            P = p,
            Q = q,
            DP = dp,
            DQ = dq,
            InverseQ = inverseQ,
            D = d,
        };
        if (isPrivate && !HasConsistentPrivateComponents(parameters, publicExponent))
        {
            throw new SigningException(
                $"Strong-name key '{sourceName}' contains invalid RSA key components.");
        }

        try
        {
            using RSA rsa = RSA.Create();
            rsa.ImportParameters(parameters);
        }
        catch (CryptographicException exception)
        {
            throw new SigningException(
                $"Strong-name key '{sourceName}' contains invalid RSA key components.",
                exception);
        }

        return 20 + modulusLength;
    }

    private static bool HasConsistentPrivateComponents(RSAParameters parameters, uint publicExponent)
    {
        BigInteger modulus = ToBigInteger(parameters.Modulus!);
        BigInteger p = ToBigInteger(parameters.P!);
        BigInteger q = ToBigInteger(parameters.Q!);
        BigInteger dp = ToBigInteger(parameters.DP!);
        BigInteger dq = ToBigInteger(parameters.DQ!);
        BigInteger inverseQ = ToBigInteger(parameters.InverseQ!);
        BigInteger d = ToBigInteger(parameters.D!);
        if (p <= 1 || q <= 1 || d <= 1 || modulus != p * q)
        {
            return false;
        }

        BigInteger pMinusOne = p - 1;
        BigInteger qMinusOne = q - 1;
        if (dp != d % pMinusOne
            || dq != d % qMinusOne
            || (inverseQ * q) % p != BigInteger.One)
        {
            return false;
        }

        BigInteger lambda =
            (pMinusOne / BigInteger.GreatestCommonDivisor(pMinusOne, qMinusOne)) * qMinusOne;
        return (d * publicExponent) % lambda == BigInteger.One;
    }

    private static BigInteger ToBigInteger(byte[] bigEndian) =>
        new(bigEndian, isUnsigned: true, isBigEndian: true);

    private static byte[] ReadComponent(byte[] blob, ref int offset, int length)
    {
        byte[] component = blob.AsSpan(offset, length).ToArray();
        Array.Reverse(component);
        offset += length;
        return component;
    }

    private static byte[] EncodePublicExponent(uint exponent)
    {
        Span<byte> encoded = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(encoded, exponent);
        int firstNonZero = 0;
        while (firstNonZero < encoded.Length - 1 && encoded[firstNonZero] == 0)
        {
            firstNonZero++;
        }

        return encoded[firstNonZero..].ToArray();
    }

    private static string ComputePublicKeyToken(byte[] blob, int publicBlobLength)
    {
        byte[] publicBlob = blob[..publicBlobLength];
        publicBlob[0] = PublicKeyBlobType;
        BinaryPrimitives.WriteUInt32LittleEndian(publicBlob.AsSpan(8, 4), PublicKeyMagic);

        byte[] strongNamePublicKey = new byte[12 + publicBlobLength];
        BinaryPrimitives.WriteUInt32LittleEndian(
            strongNamePublicKey.AsSpan(0, 4),
            BinaryPrimitives.ReadUInt32LittleEndian(publicBlob.AsSpan(4, 4)));
        BinaryPrimitives.WriteUInt32LittleEndian(strongNamePublicKey.AsSpan(4, 4), Sha1AlgorithmId);
        BinaryPrimitives.WriteUInt32LittleEndian(strongNamePublicKey.AsSpan(8, 4), (uint)publicBlobLength);
        publicBlob.CopyTo(strongNamePublicKey, 12);

        // ECMA-335 defines strong-name public-key tokens as the reversed low 8 bytes of SHA-1.
#pragma warning disable CA5350
        byte[] hash = SHA1.HashData(strongNamePublicKey);
#pragma warning restore CA5350
        Span<byte> token = stackalloc byte[8];
        for (var index = 0; index < token.Length; index++)
        {
            token[index] = hash[hash.Length - 1 - index];
        }

        return Convert.ToHexStringLower(token);
    }
}
