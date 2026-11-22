using System.Reflection;
using System.Reflection.Metadata;
using System.Runtime.CompilerServices;

namespace Orleans.Testing.Simulation.Runtime;

/// <summary>
/// Records on each assembly Clockwork rewrites the .NET runtimes it supports, and checks that the process runs one of
/// them whenever a simulation starts or takes a scheduling step after an assembly has loaded. The marker is an
/// <see cref="AssemblyMetadataAttribute"/> keyed <see cref="Key"/> whose value is <c>[minimum,maximum]</c>: inclusive
/// bounds, each a feature version (major.minor) or empty when open.
/// </summary>
internal static class SupportedRuntimesMarker
{
    /// <summary>The <see cref="AssemblyMetadataAttribute"/> key of the marker.</summary>
    public const string Key = "Clockwork.SupportedRuntimes";

    private static readonly Version Runtime = new(Environment.Version.Major, Environment.Version.Minor);
    private static readonly ConditionalWeakTable<Assembly, object> Checked = new();
    private static readonly object CheckedValue = new();

    // Counts assembly loads, so a check that finds no new load since the last complete scan costs one read. The count
    // starts before any scan reads the loaded assemblies, and an assembly is in that list before its load is counted, so
    // a scan that read count n saw every assembly whose load made the count n or less.
    private static int s_loads;
    private static int s_verifiedLoads = -1;

    static SupportedRuntimesMarker() =>
        AppDomain.CurrentDomain.AssemblyLoad += static (_, _) => Interlocked.Increment(ref s_loads);

    /// <summary>Formats the marker value for an inclusive range of feature versions.</summary>
    /// <param name="minimum">The lower bound, or <see langword="null"/> when open.</param>
    /// <param name="maximum">The upper bound, or <see langword="null"/> when open.</param>
    /// <returns>The marker value.</returns>
    public static string Format(Version? minimum, Version? maximum)
    {
        RequireFeatureVersion(minimum, nameof(minimum));
        RequireFeatureVersion(maximum, nameof(maximum));
        if (minimum is not null && maximum is not null && minimum > maximum)
        {
            throw new ArgumentException($"The minimum {minimum} exceeds the maximum {maximum}.", nameof(minimum));
        }

        return $"[{minimum},{maximum}]";
    }

    /// <summary>Parses a marker value.</summary>
    /// <param name="value">The marker value.</param>
    /// <param name="minimum">The lower bound, or <see langword="null"/> when open.</param>
    /// <param name="maximum">The upper bound, or <see langword="null"/> when open.</param>
    /// <returns><see langword="true"/> if <paramref name="value"/> is a well-formed marker value.</returns>
    public static bool TryParse(string? value, out Version? minimum, out Version? maximum)
    {
        minimum = null;
        maximum = null;
        if (value is not ['[', .. string bounds, ']'])
        {
            return false;
        }

        string[] parts = bounds.Split(',');
        return parts.Length == 2
            && TryParseBound(parts[0], out minimum)
            && TryParseBound(parts[1], out maximum)
            && !(minimum is not null && maximum is not null && minimum > maximum);
    }

    /// <summary>
    /// Requires every loaded assembly Clockwork rewrote to support the runtime this process runs. Each assembly passes
    /// once per process, and nothing is read until another assembly loads. An assembly that fails is checked again at
    /// the next call, so it keeps failing.
    /// </summary>
    /// <exception cref="UnsupportedRuntimeException">A rewritten assembly does not support this runtime.</exception>
    public static void VerifyLoadedAssemblies()
    {
        int loads = Volatile.Read(ref s_loads);
        if (loads == Volatile.Read(ref s_verifiedLoads))
        {
            return;
        }

        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (Checked.TryGetValue(assembly, out _))
            {
                continue;
            }

            Verify(assembly.GetName().Name!, ReadMarker(assembly), Runtime);
            Checked.AddOrUpdate(assembly, CheckedValue);
        }

        // Racing scans may record an older count; that costs a rescan, never a missed assembly.
        Volatile.Write(ref s_verifiedLoads, loads);
    }

    /// <summary>Requires an assembly carrying <paramref name="marker"/> to support <paramref name="runtime"/>.</summary>
    /// <param name="assemblyName">The assembly's simple name.</param>
    /// <param name="marker">The assembly's marker value, or <see langword="null"/> if Clockwork did not rewrite it.</param>
    /// <param name="runtime">The feature version of the runtime the process runs.</param>
    internal static void Verify(string assemblyName, string? marker, Version runtime)
    {
        if (marker is null)
        {
            return;
        }

        if (!TryParse(marker, out Version? minimum, out Version? maximum))
        {
            throw new InvalidOperationException(
                $"Assembly '{assemblyName}' carries the Clockwork marker '{Key}' with the unreadable value '{marker}'. " +
                "Instrument it again with this version of Orleans.Testing.Simulation.");
        }

        if ((minimum is not null && runtime < minimum) || (maximum is not null && runtime > maximum))
        {
            throw new UnsupportedRuntimeException(assemblyName, minimum, maximum, runtime);
        }
    }

    // Reads the loaded image's metadata rather than its custom attributes, which would load every attribute type: an
    // assembly with an attribute whose assembly is absent must not fail every simulation.
    private static unsafe string? ReadMarker(Assembly assembly)
    {
        if (!assembly.TryGetRawMetadata(out byte* blob, out int length))
        {
            // Only an assembly without loaded metadata, such as a dynamic one, has none; Clockwork rewrites none of those.
            return null;
        }

        var reader = new MetadataReader(blob, length);
        foreach (CustomAttributeHandle handle in reader.GetAssemblyDefinition().GetCustomAttributes())
        {
            CustomAttribute attribute = reader.GetCustomAttribute(handle);
            if (!IsAssemblyMetadataAttribute(reader, attribute.Constructor))
            {
                continue;
            }

            // The value blob is the prolog (0x0001) then the key and value strings of AssemblyMetadataAttribute(string, string).
            BlobReader value = reader.GetBlobReader(attribute.Value);
            if (value.ReadUInt16() == 1 && value.ReadSerializedString() == Key)
            {
                return value.ReadSerializedString() ?? string.Empty;
            }
        }

        return null;
    }

    private static bool IsAssemblyMetadataAttribute(MetadataReader reader, EntityHandle constructor)
    {
        if (constructor.Kind != HandleKind.MemberReference)
        {
            return false;
        }

        MemberReference member = reader.GetMemberReference((MemberReferenceHandle)constructor);
        if (member.Parent.Kind != HandleKind.TypeReference)
        {
            return false;
        }

        TypeReference type = reader.GetTypeReference((TypeReferenceHandle)member.Parent);
        return reader.StringComparer.Equals(type.Name, nameof(AssemblyMetadataAttribute))
            && reader.StringComparer.Equals(type.Namespace, typeof(AssemblyMetadataAttribute).Namespace!);
    }

    /// <summary>Describes a range of feature versions for people, such as <c>11.0</c> or <c>11.0 to 12.0</c>.</summary>
    internal static string DescribeRange(Version? minimum, Version? maximum) => (minimum, maximum) switch
    {
        ({ } low, { } high) when low == high => low.ToString(),
        ({ } low, { } high) => $"{low} to {high}",
        ({ } low, null) => $"{low} or later",
        (null, { } high) => $"{high} or earlier",
        _ => "any runtime",
    };

    private static bool TryParseBound(string text, out Version? bound)
    {
        bound = null;
        if (text.Length == 0)
        {
            return true;
        }

        // Only the text Format writes parses, so a marker has one spelling.
        return Version.TryParse(text, out bound) && IsFeatureVersion(bound) && bound.ToString() == text;
    }

    private static void RequireFeatureVersion(Version? version, string parameterName)
    {
        if (version is not null && !IsFeatureVersion(version))
        {
            throw new ArgumentException($"'{version}' is not a feature version (major.minor).", parameterName);
        }
    }

    /// <summary>Returns <see langword="true"/> if <paramref name="version"/> has exactly a major and a minor component.</summary>
    internal static bool IsFeatureVersion(Version version) => version.Build == -1;
}
