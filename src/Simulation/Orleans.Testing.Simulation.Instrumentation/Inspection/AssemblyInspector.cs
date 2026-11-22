using System.Reflection.PortableExecutable;
using Orleans.Testing.Simulation.Instrumentation.Attributes;
using Orleans.Testing.Simulation.Instrumentation.Imaging;
using Orleans.Testing.Simulation.Instrumentation.Signing;
using Mono.Cecil;

namespace Orleans.Testing.Simulation.Instrumentation.Inspection;

/// <summary>
/// Produces a read-only, deterministic description of a managed assembly's instrumentation-relevant
/// state: its image shape (managed / IL-only / mixed-mode / ReadyToRun / Authenticode), its
/// strong-name state, its debug-symbol form, and whether Clockwork has already rewritten it (the
/// idempotence marker). This is the fact base shared by the CLI <c>inspect</c> command and by tests;
/// every field is an observed fact, never an inferred capability claim.
/// </summary>
public static class AssemblyInspector
{
    private static readonly string MarkerAttributeFullName =
        typeof(ClockworkRewriteSignatureAttribute).FullName!;
    private static readonly string AssemblyMetadataAttributeFullName =
        typeof(System.Reflection.AssemblyMetadataAttribute).FullName!;

    /// <summary>Inspects the assembly (or non-managed file) at <paramref name="path"/>.</summary>
    /// <param name="path">The file to inspect.</param>
    /// <returns>The deterministic inspection result.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is null or empty.</exception>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    public static AssemblyInspection Inspect(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Assembly '{path}' was not found.", path);
        }

        string fullPath = Path.GetFullPath(path);
        AssemblyImageInfo image;
        try
        {
            image = AssemblyImageInfo.Inspect(path);
        }
        catch (BadImageFormatException)
        {
            return new AssemblyInspection(fullPath, false, default, StrongNameInfo.NotSigned, SymbolPresence.None, null);
        }

        if (!image.IsManagedAssembly)
        {
            return new AssemblyInspection(fullPath, false, image, StrongNameInfo.NotSigned, SymbolPresence.None, null);
        }

        StrongNameInfo strongName = StrongNameInspector.Inspect(path);
        SymbolPresence symbols = DetectSymbols(path);
        InstrumentationMarker? marker = TryReadMarker(path, out InstrumentationMarker read) ? read : null;

        return new AssemblyInspection(fullPath, true, image, strongName, symbols, marker);
    }

    /// <summary>
    /// Reads the idempotence metadata applied by the engine to an assembly it rewrote, including the
    /// legacy <see cref="ClockworkRewriteSignatureAttribute"/> shape, without loading the assembly.
    /// </summary>
    /// <param name="path">The assembly to read.</param>
    /// <param name="marker">The recorded marker, when present.</param>
    /// <returns><see langword="true"/> if the assembly carries the marker; otherwise <see langword="false"/>.</returns>
    public static bool TryReadMarker(string path, out InstrumentationMarker marker)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        marker = default;
        try
        {
            using AssemblyDefinition definition = AssemblyDefinition.ReadAssembly(path);
            foreach (CustomAttribute attribute in definition.CustomAttributes)
            {
                if (attribute.AttributeType.FullName == AssemblyMetadataAttributeFullName &&
                    attribute.ConstructorArguments.Count == 2 &&
                    string.Equals(
                        attribute.ConstructorArguments[0].Value as string,
                        RewriteSignatureMetadata.Key,
                        StringComparison.Ordinal) &&
                    RewriteSignatureMetadata.TryDecode(
                        attribute.ConstructorArguments[1].Value as string,
                        out string engineVersion,
                        out string ruleSetId,
                        out string ruleSetVersion,
                        out string signature,
                        out string optionsFingerprint))
                {
                    marker = new InstrumentationMarker(
                        engineVersion,
                        ruleSetId,
                        ruleSetVersion,
                        signature,
                        optionsFingerprint);
                    return true;
                }

                if (attribute.AttributeType.FullName != MarkerAttributeFullName)
                {
                    continue;
                }

                if (attribute.ConstructorArguments.Count < 4)
                {
                    return false;
                }

                marker = new InstrumentationMarker(
                    attribute.ConstructorArguments[0].Value as string ?? string.Empty,
                    attribute.ConstructorArguments[1].Value as string ?? string.Empty,
                    attribute.ConstructorArguments[2].Value as string ?? string.Empty,
                    attribute.ConstructorArguments[3].Value as string ?? string.Empty,
                    attribute.ConstructorArguments.Count >= 5
                        ? attribute.ConstructorArguments[4].Value as string ?? string.Empty
                        : string.Empty);
                return true;
            }
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException)
        {
            return false;
        }

        return false;
    }

    private static SymbolPresence DetectSymbols(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            using var reader = new PEReader(stream);
            foreach (DebugDirectoryEntry entry in reader.ReadDebugDirectory())
            {
                if (entry.Type == DebugDirectoryEntryType.EmbeddedPortablePdb)
                {
                    return SymbolPresence.Embedded;
                }
            }
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException)
        {
            return SymbolPresence.None;
        }

        string sidecar = Path.ChangeExtension(path, ".pdb");
        return File.Exists(sidecar) ? SymbolPresence.Pdb : SymbolPresence.None;
    }
}
