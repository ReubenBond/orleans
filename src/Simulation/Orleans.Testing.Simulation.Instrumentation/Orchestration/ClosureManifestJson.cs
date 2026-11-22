using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Orleans.Testing.Simulation.Instrumentation.Configuration;

namespace Orleans.Testing.Simulation.Instrumentation.Orchestration;

/// <summary>Reads and validates bounded, versioned closure-manifest JSON.</summary>
public static class ClosureManifestJson
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        AllowTrailingCommas = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = ClosureManifestLimits.MaxJsonDepth,
        Converters = { new StrictJsonStringEnumConverter<InstrumentationMode>() },
    };

    /// <summary>Reads a bounded manifest and returns both its typed model and original canonical bytes.</summary>
    public static ClosureManifest Read(string path, out byte[] canonicalUtf8)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException("Closure manifest was not found.", path);
        }

        if (info.Length > ClosureManifestLimits.MaxDocumentBytes)
        {
            throw new ClosureManifestFormatException(
                $"Closure manifest is {info.Length} bytes; the limit is {ClosureManifestLimits.MaxDocumentBytes} bytes.");
        }

        canonicalUtf8 = File.ReadAllBytes(path);
        return Deserialize(canonicalUtf8);
    }

    /// <summary>Deserializes and validates a bounded closure manifest from UTF-8 JSON.</summary>
    public static ClosureManifest Deserialize(ReadOnlySpan<byte> utf8Json)
    {
        ValidateDocumentSize(utf8Json.Length);

        try
        {
            ClosureManifestDocument? document =
                JsonSerializer.Deserialize<ClosureManifestDocument>(utf8Json, SerializerOptions);
            if (document is null)
            {
                throw new ClosureManifestFormatException("Closure manifest JSON contained no document.");
            }

            return ValidateAndConvert(document);
        }
        catch (JsonException exception)
        {
            throw new ClosureManifestFormatException("Closure manifest JSON is malformed.", exception);
        }
    }

    private static ClosureManifest ValidateAndConvert(ClosureManifestDocument document)
    {
        if (document.SchemaVersion != ClosureManifest.SchemaVersion)
        {
            throw new ClosureManifestFormatException(
                $"Unsupported closure-manifest schema version {document.SchemaVersion}. " +
                $"This reader supports version {ClosureManifest.SchemaVersion}.");
        }

        if (document.Assemblies is null)
        {
            throw new ClosureManifestFormatException("assemblies is required.");
        }

        var assemblies = ImmutableArray.CreateBuilder<ClosureManifestEntry>(document.Assemblies.Length);
        for (var index = 0; index < document.Assemblies.Length; index++)
        {
            ClosureManifestEntryDocument entry = document.Assemblies[index]
                ?? throw new ClosureManifestFormatException($"assemblies[{index}] is required.");
            assemblies.Add(new ClosureManifestEntry(
                entry.RelativePath,
                entry.WasRewritten,
                entry.WasNoOp,
                entry.WasReSigned,
                entry.ReadyToRunStripped,
                entry.InputSha256,
                entry.OutputSha256,
                entry.ErrorCount));
        }

        if (document.CopiedAssets is null)
        {
            throw new ClosureManifestFormatException("copiedAssets is required.");
        }

        var copiedAssets =
            ImmutableArray.CreateBuilder<ClosureManifestCopiedAsset>(document.CopiedAssets.Length);
        for (var index = 0; index < document.CopiedAssets.Length; index++)
        {
            ClosureManifestCopiedAssetDocument asset = document.CopiedAssets[index]
                ?? throw new ClosureManifestFormatException($"copiedAssets[{index}] is required.");
            copiedAssets.Add(new ClosureManifestCopiedAsset(asset.RelativePath, asset.Sha256));
        }

        var manifest = new ClosureManifest
        {
            EngineName = document.EngineName,
            EngineVersion = document.EngineVersion,
            RuleSetId = document.RuleSetId,
            RuleSetVersion = document.RuleSetVersion,
            RuleSetSignature = document.RuleSetSignature,
            ConfigurationSignature = document.ConfigurationSignature,
            Mode = document.Mode,
            IncrementalKey = document.IncrementalKey,
            EntryRelativePath = document.EntryRelativePath,
            Assemblies = assemblies.MoveToImmutable(),
            CopiedAssets = copiedAssets.MoveToImmutable(),
        };
        Validate(manifest, requireOrdered: true);
        return manifest;
    }

    internal static void ValidateForSerialization(ClosureManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        Validate(manifest, requireOrdered: false);
    }

    internal static void ValidateDocumentSize(int utf8Length)
    {
        if (utf8Length > ClosureManifestLimits.MaxDocumentBytes)
        {
            throw new ClosureManifestFormatException(
                $"Closure manifest is {utf8Length} bytes; the limit is {ClosureManifestLimits.MaxDocumentBytes} bytes.");
        }
    }

    private static void Validate(ClosureManifest manifest, bool requireOrdered)
    {
        RequireString(manifest.EngineName, "engineName");
        if (!string.Equals(manifest.EngineName, "Clockwork.Instrumentation", StringComparison.Ordinal))
        {
            throw new ClosureManifestFormatException(
                $"Unsupported closure-manifest engine '{manifest.EngineName}'.");
        }

        RequireString(manifest.EngineVersion, "engineVersion");
        RequireString(manifest.RuleSetId, "ruleSetId");
        RequireString(manifest.RuleSetVersion, "ruleSetVersion");
        RequireSha256(manifest.RuleSetSignature, "ruleSetSignature");
        RequireSha256(manifest.ConfigurationSignature, "configurationSignature");
        RequireSha256(manifest.IncrementalKey, "incrementalKey");
        RequireOptionalClosureRelativePath(manifest.EntryRelativePath, "entryRelativePath");
        if (!Enum.IsDefined(manifest.Mode))
        {
            throw new ClosureManifestFormatException($"mode value '{manifest.Mode}' is unsupported.");
        }

        if (manifest.Assemblies.IsDefault)
        {
            throw new ClosureManifestFormatException("assemblies is required.");
        }

        if (manifest.Assemblies.Length > ClosureManifestLimits.MaxAssemblies)
        {
            throw new ClosureManifestFormatException(
                $"Closure-manifest assembly count {manifest.Assemblies.Length} exceeds {ClosureManifestLimits.MaxAssemblies}.");
        }

        IEnumerable<ClosureManifestEntry> assemblies = requireOrdered
            ? manifest.Assemblies
            : manifest.Assemblies.OrderBy(static entry => entry.RelativePath, StringComparer.Ordinal);
        string? previousPath = null;
        var pathFields = new List<(string Path, string Field)>(
            manifest.Assemblies.Length + manifest.CopiedAssets.Length);
        var assemblyIndex = 0;
        foreach (ClosureManifestEntry entry in assemblies)
        {
            string field = $"assemblies[{assemblyIndex}]";
            RequireClosureRelativePath(entry.RelativePath, $"{field}.relativePath");
            RequireOptionalSha256(entry.InputSha256, $"{field}.inputSha256");
            RequireOptionalSha256(entry.OutputSha256, $"{field}.outputSha256");
            if (entry.ErrorCount < 0)
            {
                throw new ClosureManifestFormatException($"{field}.errorCount cannot be negative.");
            }

            if (previousPath is not null &&
                StringComparer.Ordinal.Compare(previousPath, entry.RelativePath) >= 0)
            {
                throw new ClosureManifestFormatException(
                    "Closure-manifest assemblies must be strictly ordered by unique relativePath.");
            }

            previousPath = entry.RelativePath;
            pathFields.Add((entry.RelativePath, $"{field}.relativePath"));
            assemblyIndex++;
        }

        if (manifest.CopiedAssets.IsDefault)
        {
            throw new ClosureManifestFormatException("copiedAssets is required.");
        }

        if (manifest.CopiedAssets.Length > ClosureManifestLimits.MaxCopiedAssets)
        {
            throw new ClosureManifestFormatException(
                $"Closure-manifest copied-asset count {manifest.CopiedAssets.Length} exceeds {ClosureManifestLimits.MaxCopiedAssets}.");
        }

        IEnumerable<ClosureManifestCopiedAsset> copiedAssets = requireOrdered
            ? manifest.CopiedAssets
            : manifest.CopiedAssets.OrderBy(static asset => asset.RelativePath, StringComparer.Ordinal);
        string? previousAsset = null;
        var copiedAssetIndex = 0;
        foreach (ClosureManifestCopiedAsset asset in copiedAssets)
        {
            string field = $"copiedAssets[{copiedAssetIndex}]";
            RequireClosureRelativePath(asset.RelativePath, $"{field}.relativePath");
            RequireSha256(asset.Sha256, $"{field}.sha256");
            if (previousAsset is not null &&
                StringComparer.Ordinal.Compare(previousAsset, asset.RelativePath) >= 0)
            {
                throw new ClosureManifestFormatException(
                    "Closure-manifest copied assets must be strictly ordered by unique relativePath.");
            }

            previousAsset = asset.RelativePath;
            pathFields.Add((asset.RelativePath, $"{field}.relativePath"));
            copiedAssetIndex++;
        }

        ValidatePathHierarchy(pathFields);
    }

    private static void ValidatePathHierarchy(IReadOnlyList<(string Path, string Field)> pathFields)
    {
        var paths = new Dictionary<string, string>(PathComparer);
        foreach ((string path, string field) in pathFields)
        {
            if (!paths.TryAdd(path, field))
            {
                throw new ClosureManifestFormatException(
                    $"{field} collides with {paths[path]} on this platform.");
            }
        }

        foreach ((string path, string field) in pathFields)
        {
            for (var separator = path.IndexOf('/'); separator >= 0; separator = path.IndexOf('/', separator + 1))
            {
                string ancestor = path[..separator];
                if (paths.TryGetValue(ancestor, out string? ancestorField))
                {
                    throw new ClosureManifestFormatException(
                        $"{field} is nested beneath file path {ancestorField}.");
                }
            }
        }
    }

    private static void RequireOptionalSha256(string? value, string field)
    {
        if (value is null)
        {
            return;
        }

        RequireSha256(value, field);
    }

    private static void RequireSha256(string? value, string field)
    {
        RequireString(value, field);
        string hash = value!;
        if (hash.Length != ClosureManifestLimits.Sha256Length ||
            hash.Any(static character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new ClosureManifestFormatException(
                $"{field} must be a lower-case 64-character SHA-256 value.");
        }
    }

    private static void RequireClosureRelativePath(string? value, string field)
    {
        RequireString(value, field);
        string path = value!;
        if (path[0] == '/'
            || path.Contains('\\')
            || (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':')
            || path.Split('/').Any(static segment => segment is "" or "." or ".."))
        {
            throw new ClosureManifestFormatException(
                $"{field} must be a normalized closure-relative path.");
        }
    }

    private static void RequireOptionalClosureRelativePath(string? value, string field)
    {
        if (value is not null)
        {
            RequireClosureRelativePath(value, field);
        }
    }

    private static void RequireString(string? value, string field)
    {
        if (string.IsNullOrEmpty(value))
        {
            throw new ClosureManifestFormatException($"{field} is required.");
        }

        if (value.Length > ClosureManifestLimits.MaxStringLength)
        {
            throw new ClosureManifestFormatException(
                $"{field} length {value.Length} exceeds {ClosureManifestLimits.MaxStringLength}.");
        }
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private sealed record ClosureManifestDocument
    {
        public required int SchemaVersion { get; init; }

        public required string EngineName { get; init; }

        public required string EngineVersion { get; init; }

        public required string RuleSetId { get; init; }

        public required string RuleSetVersion { get; init; }

        public required string RuleSetSignature { get; init; }

        public required string ConfigurationSignature { get; init; }

        public required InstrumentationMode Mode { get; init; }

        public required string IncrementalKey { get; init; }

        public required string? EntryRelativePath { get; init; }

        public required ClosureManifestEntryDocument[]? Assemblies { get; init; }

        public required ClosureManifestCopiedAssetDocument[]? CopiedAssets { get; init; }
    }

    private sealed record ClosureManifestEntryDocument
    {
        public required string RelativePath { get; init; }

        public required bool WasRewritten { get; init; }

        public required bool WasNoOp { get; init; }

        public required bool WasReSigned { get; init; }

        public required bool ReadyToRunStripped { get; init; }

        public required string? InputSha256 { get; init; }

        public required string? OutputSha256 { get; init; }

        public required int ErrorCount { get; init; }
    }

    private sealed record ClosureManifestCopiedAssetDocument
    {
        public required string RelativePath { get; init; }

        public required string Sha256 { get; init; }
    }
}

/// <summary>Thrown when closure-manifest JSON is malformed, incompatible, or exceeds a hard limit.</summary>
public sealed class ClosureManifestFormatException : Exception
{
    /// <summary>Initializes a closure-manifest format exception.</summary>
    public ClosureManifestFormatException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a closure-manifest format exception with a parser exception.</summary>
    public ClosureManifestFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
