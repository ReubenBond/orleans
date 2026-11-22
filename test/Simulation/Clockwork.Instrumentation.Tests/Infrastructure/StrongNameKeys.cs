using System.Security.Cryptography;

namespace Orleans.Testing.Simulation.Instrumentation.Tests.Infrastructure;

/// <summary>
/// Produces strong-name key material for tests without relying on the Windows-only
/// <c>RSACryptoServiceProvider.ExportCspBlob</c>. It encodes an <see cref="RSA"/> key as a Microsoft
/// CryptoAPI <c>PRIVATEKEYBLOB</c> (the exact byte shape an <c>.snk</c> file holds and that
/// Mono.Cecil consumes via <c>WriterParameters.StrongNameKeyBlob</c>), so signing fixtures run
/// identically on every platform.
/// </summary>
internal static class StrongNameKeys
{
    private const byte PublicKeyBlob = 0x06;
    private const byte PrivateKeyBlob = 0x07;
    private const byte CurrentBlobVersion = 0x02;
    private const uint CalgRsaSign = 0x00002400;
    private const uint MagicRsa1 = 0x31415352; // "RSA1"
    private const uint MagicRsa2 = 0x32415352; // "RSA2"

    /// <summary>Creates a new 2048-bit RSA key and returns it as a CryptoAPI private-key blob.</summary>
    /// <returns>The private-key blob bytes (a full strong-name key pair).</returns>
    public static byte[] CreatePrivateKeyBlob()
    {
        using var rsa = RSA.Create(2048);
        return ExportPrivateKeyBlob(rsa);
    }

    /// <summary>Encodes an RSA key as a CryptoAPI private-key blob (RSA2).</summary>
    /// <param name="rsa">The RSA key, which must contain private parameters.</param>
    /// <returns>The private-key blob bytes.</returns>
    public static byte[] ExportPrivateKeyBlob(RSA rsa)
    {
        RSAParameters p = rsa.ExportParameters(includePrivateParameters: true);
        int modLen = p.Modulus!.Length;
        int halfLen = modLen / 2;

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(PrivateKeyBlob);
        writer.Write(CurrentBlobVersion);
        writer.Write((ushort)0);
        writer.Write(CalgRsaSign);
        writer.Write(MagicRsa2);
        writer.Write((uint)(modLen * 8));
        WriteExponent(writer, p.Exponent!);
        WriteLittleEndian(writer, p.Modulus!, modLen);
        WriteLittleEndian(writer, p.P!, halfLen);
        WriteLittleEndian(writer, p.Q!, halfLen);
        WriteLittleEndian(writer, p.DP!, halfLen);
        WriteLittleEndian(writer, p.DQ!, halfLen);
        WriteLittleEndian(writer, p.InverseQ!, halfLen);
        WriteLittleEndian(writer, p.D!, modLen);
        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>Encodes the public half of an RSA key as a CryptoAPI public-key blob (RSA1).</summary>
    /// <param name="rsa">The RSA key.</param>
    /// <returns>The public-key blob bytes (cannot sign).</returns>
    public static byte[] ExportPublicKeyBlob(RSA rsa)
    {
        RSAParameters p = rsa.ExportParameters(includePrivateParameters: false);
        int modLen = p.Modulus!.Length;

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(PublicKeyBlob);
        writer.Write(CurrentBlobVersion);
        writer.Write((ushort)0);
        writer.Write(CalgRsaSign);
        writer.Write(MagicRsa1);
        writer.Write((uint)(modLen * 8));
        WriteExponent(writer, p.Exponent!);
        WriteLittleEndian(writer, p.Modulus!, modLen);
        writer.Flush();
        return stream.ToArray();
    }

    private static void WriteExponent(BinaryWriter writer, byte[] exponent)
    {
        // The CryptoAPI public exponent is a 32-bit little-endian value.
        uint value = 0;
        foreach (byte b in exponent)
        {
            value = (value << 8) | b;
        }

        writer.Write(value);
    }

    private static void WriteLittleEndian(BinaryWriter writer, byte[] bigEndian, int length)
    {
        byte[] buffer = new byte[length];
        // RSAParameters are big-endian and minimally sized; CryptoAPI wants fixed-length little-endian.
        for (int i = 0; i < bigEndian.Length && i < length; i++)
        {
            buffer[i] = bigEndian[bigEndian.Length - 1 - i];
        }

        writer.Write(buffer);
    }
}
