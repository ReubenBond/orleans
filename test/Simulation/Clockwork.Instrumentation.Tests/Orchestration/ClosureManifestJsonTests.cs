using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Orleans.Testing.Simulation.Instrumentation.Configuration;
using Orleans.Testing.Simulation.Instrumentation.Orchestration;

namespace Orleans.Testing.Simulation.Instrumentation.Tests.Orchestration;

public sealed class ClosureManifestJsonTests
{
    [Fact]
    public void DeterministicJsonRoundTripsToTypedManifest()
    {
        ClosureManifest expected = CreateManifest();

        ClosureManifest actual = ClosureManifestJson.Deserialize(Encoding.UTF8.GetBytes(expected.ToJson()));

        Assert.Equal(expected.EngineVersion, actual.EngineVersion);
        Assert.Equal(expected.RuleSetSignature, actual.RuleSetSignature);
        Assert.Equal(expected.Mode, actual.Mode);
        Assert.Equal(expected.Assemblies, actual.Assemblies);
        Assert.Equal(expected.CopiedAssets, actual.CopiedAssets);
    }

    [Fact]
    public void DeterministicJsonMatchesTypedCopiedAssetGoldenData()
    {
        ClosureManifest manifest = CreateManifest() with
        {
            CopiedAssets =
            [
                new ClosureManifestCopiedAsset("z.runtimeconfig.json", new string('f', 64)),
                new ClosureManifestCopiedAsset("a.deps.json", new string('1', 64)),
            ],
        };
        string expected = $$"""
            {
              "schemaVersion": 3,
              "engineName": "Clockwork.Instrumentation",
              "engineVersion": "1.2.3",
              "ruleSetId": "clockwork.test",
              "ruleSetVersion": "1",
              "ruleSetSignature": "{{new string('a', 64)}}",
              "configurationSignature": "{{new string('b', 64)}}",
              "mode": "RaceExploration",
              "incrementalKey": "{{new string('c', 64)}}",
              "entryRelativePath": "app.dll",
              "assemblies": [
                {
                  "relativePath": "app.dll",
                  "wasRewritten": true,
                  "wasNoOp": false,
                  "wasReSigned": false,
                  "readyToRunStripped": false,
                  "inputSha256": "{{new string('d', 64)}}",
                  "outputSha256": "{{new string('e', 64)}}",
                  "errorCount": 0
                }
              ],
              "copiedAssets": [
                {
                  "relativePath": "a.deps.json",
                  "sha256": "{{new string('1', 64)}}"
                },
                {
                  "relativePath": "z.runtimeconfig.json",
                  "sha256": "{{new string('f', 64)}}"
                }
              ]
            }
            """.ReplaceLineEndings();

        string json = manifest.ToJson();

        Assert.Equal(expected, json);
        Assert.Equal(json, ClosureManifestJson.Deserialize(Encoding.UTF8.GetBytes(json)).ToJson());
    }

    [Theory]
    [InlineData(InstrumentationMode.Controlled)]
    [InlineData(InstrumentationMode.RaceExploration)]
    public void InstrumentationModeSerializeThenDeserializeRoundTrips(InstrumentationMode mode)
    {
        ClosureManifest expected = CreateManifest() with { Mode = mode };
        string json = expected.ToJson();

        ClosureManifest actual = ClosureManifestJson.Deserialize(Encoding.UTF8.GetBytes(json));

        Assert.Equal(mode, actual.Mode);
        Assert.Equal(json, actual.ToJson());
    }

    [Theory]
    [InlineData("\"Controlled, RaceExploration\"")]
    [InlineData("\"controlled\"")]
    [InlineData("\"1\"")]
    [InlineData("1")]
    [InlineData("\"Undefined\"")]
    [InlineData("\" Controlled\"")]
    [InlineData("true")]
    [InlineData("null")]
    public void InvalidInstrumentationModeJsonTokenIsRejected(string jsonToken)
    {
        byte[] json = CreateJsonWithModeToken(jsonToken);

        ClosureManifestFormatException exception = Assert.Throws<ClosureManifestFormatException>(
            () => ClosureManifestJson.Deserialize(json));

        Assert.Equal("Closure manifest JSON is malformed.", exception.Message);
        Assert.IsType<JsonException>(exception.InnerException);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("null")]
    public void MalformedOrIncompleteDocumentIsRejected(string json)
    {
        Assert.Throws<ClosureManifestFormatException>(
            () => ClosureManifestJson.Deserialize(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void MissingRequiredEntryFieldIsRejected()
    {
        string json = CreateManifest().ToJson()
            .Replace("\"wasNoOp\": false,\r\n", string.Empty, StringComparison.Ordinal)
            .Replace("\"wasNoOp\": false,\n", string.Empty, StringComparison.Ordinal);

        Assert.Throws<ClosureManifestFormatException>(
            () => ClosureManifestJson.Deserialize(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void UnsupportedSchemaVersionIsRejected()
    {
        string json = CreateManifest().ToJson()
            .Replace("\"schemaVersion\": 3", "\"schemaVersion\": 99", StringComparison.Ordinal);

        ClosureManifestFormatException exception = Assert.Throws<ClosureManifestFormatException>(
            () => ClosureManifestJson.Deserialize(Encoding.UTF8.GetBytes(json)));

        Assert.Contains("schema version 99", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SchemaVersionTwoIsRejectedWithoutCompatibilityFallback()
    {
        string json = CreateManifest().ToJson()
            .Replace("\"schemaVersion\": 3", "\"schemaVersion\": 2", StringComparison.Ordinal);

        ClosureManifestFormatException exception = Assert.Throws<ClosureManifestFormatException>(
            () => ClosureManifestJson.Deserialize(Encoding.UTF8.GetBytes(json)));

        Assert.Contains("schema version 2", exception.Message, StringComparison.Ordinal);
        Assert.Contains("version 3", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownSchemaMemberIsRejected()
    {
        string json = CreateManifest().ToJson();
        string extended = json.Insert(json.Length - 1, ",\"unexpected\":true");

        Assert.Throws<ClosureManifestFormatException>(
            () => ClosureManifestJson.Deserialize(Encoding.UTF8.GetBytes(extended)));
    }

    [Fact]
    public void BareStringCopiedAssetIsRejected()
    {
        JsonObject root = JsonNode.Parse(CreateManifest().ToJson())!.AsObject();
        root["copiedAssets"] = new JsonArray("app.deps.json");

        ClosureManifestFormatException exception = Assert.Throws<ClosureManifestFormatException>(
            () => ClosureManifestJson.Deserialize(Encoding.UTF8.GetBytes(root.ToJsonString())));

        Assert.IsType<JsonException>(exception.InnerException);
    }

    [Fact]
    public void DuplicateCopiedAssetEntryIsRejected()
    {
        JsonObject root = JsonNode.Parse(CreateManifest().ToJson())!.AsObject();
        JsonNode asset = root["copiedAssets"]!.AsArray()[0]!;
        root["copiedAssets"] = new JsonArray(asset.DeepClone(), asset.DeepClone());

        ClosureManifestFormatException exception = Assert.Throws<ClosureManifestFormatException>(
            () => ClosureManifestJson.Deserialize(Encoding.UTF8.GetBytes(root.ToJsonString())));

        Assert.Contains("strictly ordered", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/rooted")]
    [InlineData("../outside")]
    [InlineData("directory\\asset")]
    [InlineData("directory/./asset")]
    [InlineData("C:/rooted")]
    public void InvalidCopiedAssetPathIsRejected(string relativePath)
    {
        JsonObject root = JsonNode.Parse(CreateManifest().ToJson())!.AsObject();
        root["copiedAssets"]!.AsArray()[0]!["relativePath"] = relativePath;

        ClosureManifestFormatException exception = Assert.Throws<ClosureManifestFormatException>(
            () => ClosureManifestJson.Deserialize(Encoding.UTF8.GetBytes(root.ToJsonString())));

        Assert.Contains("closure-relative path", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    public void InvalidCopiedAssetHashIsRejected(string sha256)
    {
        JsonObject root = JsonNode.Parse(CreateManifest().ToJson())!.AsObject();
        root["copiedAssets"]!.AsArray()[0]!["sha256"] = sha256;

        ClosureManifestFormatException exception = Assert.Throws<ClosureManifestFormatException>(
            () => ClosureManifestJson.Deserialize(Encoding.UTF8.GetBytes(root.ToJsonString())));

        Assert.Contains("lower-case 64-character SHA-256", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OversizedDocumentIsRejectedBeforeParsing()
    {
        byte[] bytes = new byte[ClosureManifestLimits.MaxDocumentBytes + 1];

        ClosureManifestFormatException exception = Assert.Throws<ClosureManifestFormatException>(
            () => ClosureManifestJson.Deserialize(bytes));

        Assert.Contains("limit", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MaximumLengthStringWriterReaderRoundTripSucceeds()
    {
        ClosureManifest expected = CreateManifest() with
        {
            EngineVersion = new string('v', ClosureManifestLimits.MaxStringLength),
        };

        string json = expected.ToJson();
        ClosureManifest actual = ClosureManifestJson.Deserialize(Encoding.UTF8.GetBytes(json));

        Assert.Equal(expected.EngineVersion, actual.EngineVersion);
        Assert.Equal(json, actual.ToJson());
    }

    [Fact]
    public void OverLimitStringIsRejectedByWriterAndReader()
    {
        string overLimit = new('v', ClosureManifestLimits.MaxStringLength + 1);
        ClosureManifest manifest = CreateManifest() with { EngineVersion = overLimit };
        JsonObject root = JsonNode.Parse(CreateManifest().ToJson())!.AsObject();
        root["engineVersion"] = overLimit;

        ClosureManifestFormatException writerException =
            Assert.Throws<ClosureManifestFormatException>(manifest.ToJson);
        ClosureManifestFormatException readerException =
            Assert.Throws<ClosureManifestFormatException>(
                () => ClosureManifestJson.Deserialize(
                    Encoding.UTF8.GetBytes(root.ToJsonString())));

        Assert.Contains("engineVersion", writerException.Message, StringComparison.Ordinal);
        Assert.Contains("engineVersion", readerException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MaximumEntryCountsWriterReaderRoundTripSucceeds()
    {
        ClosureManifest manifest = CreateManifest() with
        {
            Assemblies =
            [
                .. Enumerable.Range(0, ClosureManifestLimits.MaxAssemblies)
                    .Select(index => new ClosureManifestEntry(
                        $"assemblies/{index:D4}.dll",
                        WasRewritten: true,
                        WasNoOp: false,
                        WasReSigned: false,
                        ReadyToRunStripped: false,
                        InputSha256: new string('a', 64),
                        OutputSha256: new string('b', 64),
                        ErrorCount: 0)),
            ],
            CopiedAssets =
            [
                .. Enumerable.Range(0, ClosureManifestLimits.MaxCopiedAssets)
                    .Select(index => new ClosureManifestCopiedAsset(
                        $"assets/{index:D5}.bin",
                        new string('c', 64))),
            ],
        };

        string json = manifest.ToJson();
        ClosureManifest roundTripped =
            ClosureManifestJson.Deserialize(Encoding.UTF8.GetBytes(json));

        Assert.Equal(ClosureManifestLimits.MaxAssemblies, roundTripped.Assemblies.Length);
        Assert.Equal(ClosureManifestLimits.MaxCopiedAssets, roundTripped.CopiedAssets.Length);
    }

    [Fact]
    public void OverLimitEntryCountsAreRejectedBeforeSerialization()
    {
        ClosureManifestEntry assembly = CreateManifest().Assemblies[0];
        ClosureManifestCopiedAsset asset = CreateManifest().CopiedAssets[0];
        ClosureManifest tooManyAssemblies = CreateManifest() with
        {
            Assemblies = [.. Enumerable.Repeat(assembly, ClosureManifestLimits.MaxAssemblies + 1)],
        };
        ClosureManifest tooManyAssets = CreateManifest() with
        {
            CopiedAssets = [.. Enumerable.Repeat(asset, ClosureManifestLimits.MaxCopiedAssets + 1)],
        };

        ClosureManifestFormatException assemblyException =
            Assert.Throws<ClosureManifestFormatException>(tooManyAssemblies.ToJson);
        ClosureManifestFormatException assetException =
            Assert.Throws<ClosureManifestFormatException>(tooManyAssets.ToJson);

        Assert.Contains("assembly count", assemblyException.Message, StringComparison.Ordinal);
        Assert.Contains("copied-asset count", assetException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WriterRejectsDocumentWhichWouldExceedReaderByteLimit()
    {
        string prefix = new('x', 256);
        ClosureManifest manifest = CreateManifest() with
        {
            CopiedAssets =
            [
                .. Enumerable.Range(0, ClosureManifestLimits.MaxCopiedAssets)
                    .Select(index => new ClosureManifestCopiedAsset(
                        $"{prefix}/{index:D5}.bin",
                        new string('a', 64))),
            ],
        };

        ClosureManifestFormatException exception =
            Assert.Throws<ClosureManifestFormatException>(manifest.ToJson);

        Assert.Contains("bytes", exception.Message, StringComparison.Ordinal);
        Assert.Contains("limit", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("invalid\\path")]
    [InlineData("../outside")]
    public void WriterRejectsPathWhichReaderWouldReject(string path)
    {
        ClosureManifest manifest = CreateManifest() with
        {
            CopiedAssets = [new ClosureManifestCopiedAsset(path, new string('a', 64))],
        };

        ClosureManifestFormatException exception =
            Assert.Throws<ClosureManifestFormatException>(manifest.ToJson);

        Assert.Contains("closure-relative path", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WriterRejectsHashWhichReaderWouldReject()
    {
        ClosureManifest manifest = CreateManifest() with
        {
            CopiedAssets =
            [
                new ClosureManifestCopiedAsset(
                    "asset.bin",
                    new string('A', ClosureManifestLimits.Sha256Length)),
            ],
        };

        ClosureManifestFormatException exception =
            Assert.Throws<ClosureManifestFormatException>(manifest.ToJson);

        Assert.Contains("lower-case", exception.Message, StringComparison.Ordinal);
        Assert.Contains("SHA-256", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WriterAndReaderRejectFileDirectoryHierarchyCollision()
    {
        ClosureManifest manifest = CreateManifest() with
        {
            Assemblies = [CreateManifest().Assemblies[0] with { RelativePath = "assets" }],
            CopiedAssets =
            [
                new ClosureManifestCopiedAsset("assets/config.json", new string('a', 64)),
            ],
        };
        JsonObject root = JsonNode.Parse(CreateManifest().ToJson())!.AsObject();
        root["assemblies"]!.AsArray()[0]!["relativePath"] = "assets";
        root["copiedAssets"]!.AsArray()[0]!["relativePath"] = "assets/config.json";

        ClosureManifestFormatException writerException =
            Assert.Throws<ClosureManifestFormatException>(manifest.ToJson);
        ClosureManifestFormatException readerException =
            Assert.Throws<ClosureManifestFormatException>(
                () => ClosureManifestJson.Deserialize(
                    Encoding.UTF8.GetBytes(root.ToJsonString())));

        Assert.Contains("nested beneath file path", writerException.Message, StringComparison.Ordinal);
        Assert.Contains("nested beneath file path", readerException.Message, StringComparison.Ordinal);
    }

    private static byte[] CreateJsonWithModeToken(string jsonToken)
    {
        JsonObject root = JsonNode.Parse(CreateManifest().ToJson())!.AsObject();
        root["mode"] = JsonNode.Parse(jsonToken);
        return Encoding.UTF8.GetBytes(root.ToJsonString());
    }

    private static ClosureManifest CreateManifest() => new()
    {
        EngineVersion = "1.2.3",
        RuleSetId = "clockwork.test",
        RuleSetVersion = "1",
        RuleSetSignature = new string('a', 64),
        ConfigurationSignature = new string('b', 64),
        Mode = InstrumentationMode.RaceExploration,
        IncrementalKey = new string('c', 64),
        EntryRelativePath = "app.dll",
        Assemblies =
        [
            new ClosureManifestEntry(
                "app.dll",
                WasRewritten: true,
                WasNoOp: false,
                WasReSigned: false,
                ReadyToRunStripped: false,
                InputSha256: new string('d', 64),
                OutputSha256: new string('e', 64),
                ErrorCount: 0),
        ],
        CopiedAssets =
        [
            new ClosureManifestCopiedAsset("app.deps.json", new string('f', 64)),
        ],
    };
}
