using Orleans.Testing.Simulation.Instrumentation.Rewriting;

namespace Orleans.Testing.Simulation.Instrumentation.Signing;

/// <summary>
/// (Re-)signs a rewritten assembly with a supplied strong-name key. Mono.Cecil drops an assembly's
/// strong-name signature when it rewrites and writes the assembly; a signed assembly must therefore
/// be re-signed afterwards to keep its strong name and remain loadable by references that carry its
/// public-key token. This signer re-opens the staged output and re-writes it with the key blob,
/// preserving the detected portable/embedded debug-symbol form.
/// </summary>
public static class StrongNameSigner
{
    /// <summary>
    /// Re-signs the assembly at <paramref name="assemblyPath"/> in place with <paramref name="key"/>.
    /// </summary>
    /// <param name="assemblyPath">The path of the assembly to re-sign.</param>
    /// <param name="key">The strong-name key to sign with; it must contain private-key material.</param>
    /// <exception cref="SigningException">
    /// The key cannot sign (it is public-only), or the assembly could not be re-signed.
    /// </exception>
    public static void ReSign(string assemblyPath, StrongNameKey key)
    {
        ArgumentException.ThrowIfNullOrEmpty(assemblyPath);
        ArgumentNullException.ThrowIfNull(key);
        if (!key.CanSign)
        {
            throw new SigningException(
                "The supplied strong-name key is public-only and cannot sign an assembly; a full key " +
                "pair (for example one produced by 'sn -k') is required to re-sign.");
        }

        try
        {
            // Load with InMemory=true (the default in AssemblyRewriteContext.Load) so the file is not
            // locked and can be re-written in place with the strong-name key blob applied.
            using AssemblyRewriteContext context = AssemblyRewriteContext.Load(assemblyPath, readSymbols: true);
            context.Write(assemblyPath, key.Blob);
        }
        catch (Exception ex) when (ex is not SigningException)
        {
            throw new SigningException(
                $"Failed to re-sign assembly '{assemblyPath}': {ex.Message}", ex);
        }
    }
}
