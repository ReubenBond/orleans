using Mono.Cecil;

namespace Orleans.Testing.Simulation.Instrumentation.Signing;

/// <summary>
/// Detects the strong-name state of an assembly from its Mono.Cecil metadata and CLI header:
/// whether it carries a public key, whether the <c>StrongNameSigned</c> flag is set (fully or
/// public-signed) or absent (delay-signed), and the public-key token used by references to it.
/// </summary>
public static class StrongNameInspector
{
    /// <summary>Inspects the strong-name state of the assembly at <paramref name="path"/>.</summary>
    /// <param name="path">The path of the assembly to inspect.</param>
    /// <returns>The observed strong-name identity.</returns>
    public static StrongNameInfo Inspect(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        using AssemblyDefinition definition = AssemblyDefinition.ReadAssembly(path, new ReaderParameters
        {
            ReadSymbols = false,
            InMemory = true,
        });
        return Inspect(definition);
    }

    /// <summary>Inspects the strong-name state of an already-loaded assembly definition.</summary>
    /// <param name="definition">The assembly definition to inspect.</param>
    /// <returns>The observed strong-name identity.</returns>
    public static StrongNameInfo Inspect(AssemblyDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        AssemblyNameDefinition name = definition.Name;
        if (!name.HasPublicKey)
        {
            return StrongNameInfo.NotSigned;
        }

        bool signedFlag = definition.Modules.Any(m => (m.Attributes & ModuleAttributes.StrongNameSigned) != 0);
        StrongNameStatus status = signedFlag ? StrongNameStatus.StrongNameSigned : StrongNameStatus.DelaySigned;
        return new StrongNameInfo(status, FormatToken(name.PublicKeyToken));
    }

    /// <summary>
    /// Formats a raw public-key-token byte array as the lower-case hex string used in assembly
    /// references, or <see langword="null"/> when the token is empty.
    /// </summary>
    /// <param name="token">The raw token bytes.</param>
    /// <returns>The lower-case hex token, or <see langword="null"/>.</returns>
    public static string? FormatToken(byte[]? token)
    {
        if (token is null || token.Length == 0)
        {
            return null;
        }

        return Convert.ToHexStringLower(token);
    }
}
