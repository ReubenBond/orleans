using System.Reflection.PortableExecutable;

namespace Orleans.Testing.Simulation.Instrumentation.Imaging;

/// <summary>
/// A read-only description of a PE file's image shape, computed directly from the PE and CLI headers
/// via <see cref="PEReader"/> (part of the runtime; no Mono.Cecil dependency). It answers the
/// questions the build pipeline must ask <em>before</em> attempting a Cecil round-trip: is this a
/// managed assembly at all, is it IL-only or mixed-mode, is it ReadyToRun (carries an ahead-of-time
/// native image), and is it Authenticode-signed. Mono.Cecil silently drops native code and
/// Authenticode signatures on write, so these must be detected and handled deliberately rather than
/// discovered after the fact.
/// </summary>
/// <param name="IsManagedAssembly">Whether the file has a CLI (COR) header, i.e. is a managed assembly.</param>
/// <param name="IsILOnly">Whether the <c>ILOnly</c> COR flag is set (not mixed-mode).</param>
/// <param name="IsReadyToRun">Whether the image carries a ReadyToRun/native header (AOT native code).</param>
/// <param name="HasNativeEntryPoint">Whether the <c>NativeEntryPoint</c> COR flag is set.</param>
/// <param name="Requires32Bit">Whether the <c>Requires32Bit</c> COR flag is set.</param>
/// <param name="HasAuthenticodeSignature">Whether the PE carries an Authenticode certificate table.</param>
public readonly record struct AssemblyImageInfo(
    bool IsManagedAssembly,
    bool IsILOnly,
    bool IsReadyToRun,
    bool HasNativeEntryPoint,
    bool Requires32Bit,
    bool HasAuthenticodeSignature)
{
    /// <summary>
    /// Gets a value indicating whether this is a mixed-mode (managed + embedded native) assembly,
    /// which Mono.Cecil cannot round-trip.
    /// </summary>
    public bool IsMixedMode => IsManagedAssembly && !IsILOnly;

    /// <summary>
    /// Reads the image headers of the file at <paramref name="path"/> and classifies its shape.
    /// </summary>
    /// <param name="path">The path of the PE file to inspect.</param>
    /// <returns>The classified image info.</returns>
    /// <exception cref="BadImageFormatException">The file is not a valid PE image.</exception>
    public static AssemblyImageInfo Inspect(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        using FileStream stream = File.OpenRead(path);
        return Inspect(stream);
    }

    /// <summary>Reads the image headers from a stream and classifies its shape.</summary>
    /// <param name="stream">A seekable stream positioned at the start of the PE file.</param>
    /// <returns>The classified image info.</returns>
    /// <exception cref="BadImageFormatException">The stream does not contain a valid PE image.</exception>
    public static AssemblyImageInfo Inspect(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var reader = new PEReader(stream, PEStreamOptions.LeaveOpen);
        PEHeaders headers = reader.PEHeaders;

        bool hasAuthenticode = headers.PEHeader is { CertificateTableDirectory.Size: > 0 };

        CorHeader? cor = headers.CorHeader;
        if (cor is null)
        {
            return new AssemblyImageInfo(
                IsManagedAssembly: false,
                IsILOnly: false,
                IsReadyToRun: false,
                HasNativeEntryPoint: false,
                Requires32Bit: false,
                HasAuthenticodeSignature: hasAuthenticode);
        }

        CorFlags flags = cor.Flags;
        return new AssemblyImageInfo(
            IsManagedAssembly: true,
            IsILOnly: (flags & CorFlags.ILOnly) != 0,
            IsReadyToRun: cor.ManagedNativeHeaderDirectory.Size > 0,
            HasNativeEntryPoint: (flags & CorFlags.NativeEntryPoint) != 0,
            Requires32Bit: (flags & CorFlags.Requires32Bit) != 0,
            HasAuthenticodeSignature: hasAuthenticode);
    }
}
