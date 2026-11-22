using Orleans.Testing.Simulation.Instrumentation.Diagnostics;
using Orleans.Testing.Simulation.Instrumentation.Configuration;

namespace Orleans.Testing.Simulation.Instrumentation.Signing;

/// <summary>Loads an optional configured strong-name key and produces a blocking diagnostic on failure.</summary>
public static class StrongNameKeyLoader
{
    /// <summary>
    /// Loads the key at <paramref name="path"/>, treating an absent path as no key and a configured
    /// unusable key as an error.
    /// </summary>
    public static StrongNameKeyLoadResult LoadConfigured(string? path)
    {
        if (path is null)
        {
            return new StrongNameKeyLoadResult(null, null);
        }

        try
        {
            string fullPath = InstrumentationPath.GetFullPath(path, "Strong-name key");
            StrongNameKey key = StrongNameKey.Load(fullPath);
            return key.CanSign
                ? new StrongNameKeyLoadResult(key, null)
                : new StrongNameKeyLoadResult(
                    null,
                    RewriteDiagnostic.Error(
                        RewriteDiagnosticIds.StrongNameReSignRequired,
                        $"Strong-name key '{path}' is a public-only key and cannot sign."));
        }
        catch (Exception exception) when (
            exception is SigningException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new StrongNameKeyLoadResult(
                null,
                RewriteDiagnostic.Error(
                    RewriteDiagnosticIds.StrongNameReSignRequired,
                    $"Failed to load strong-name key '{path}': {exception.Message}"));
        }
    }
}

/// <summary>The key and optional blocking diagnostic produced while loading configured key material.</summary>
/// <param name="Key">The usable private key, or <see langword="null"/>.</param>
/// <param name="Diagnostic">The blocking diagnostic for a configured unusable key, or <see langword="null"/>.</param>
public readonly record struct StrongNameKeyLoadResult(
    StrongNameKey? Key,
    RewriteDiagnostic? Diagnostic);
