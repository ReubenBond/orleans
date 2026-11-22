using System.Diagnostics.CodeAnalysis;

namespace Orleans.Testing.Simulation.Instrumentation.Rules;

/// <summary>
/// Parses .NET runtime feature versions: <c>major.minor</c>, such as <c>11.0</c>. Rules are classified against a
/// runtime's feature version, so every patch release of a runtime shares its rules. Target runtimes and the bounds of
/// <see cref="RuntimeVersionRange"/> are feature versions.
/// </summary>
public static class RuntimeFeatureVersion
{
    /// <summary>Parses a feature version written as <see cref="Version.ToString()"/> writes it.</summary>
    /// <param name="text">The text to parse, such as <c>11.0</c>.</param>
    /// <param name="version">The parsed feature version.</param>
    /// <returns><see langword="true"/> if <paramref name="text"/> is a feature version.</returns>
    public static bool TryParse(string? text, [NotNullWhen(true)] out Version? version)
    {
        if (Version.TryParse(text, out Version? parsed) && IsFeatureVersion(parsed) && parsed.ToString() == text)
        {
            version = parsed;
            return true;
        }

        version = null;
        return false;
    }

    /// <summary>Returns <see langword="true"/> if <paramref name="version"/> has exactly a major and a minor component.</summary>
    /// <param name="version">The version to test.</param>
    /// <returns><see langword="true"/> for a feature version.</returns>
    public static bool IsFeatureVersion(Version version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return Runtime.SupportedRuntimesMarker.IsFeatureVersion(version);
    }
}
