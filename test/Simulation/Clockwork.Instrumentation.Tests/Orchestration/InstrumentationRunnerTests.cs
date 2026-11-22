using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Orleans.Testing.Simulation.Instrumentation.Configuration;
using Orleans.Testing.Simulation.Instrumentation.Closure;
using Orleans.Testing.Simulation.Instrumentation.Diagnostics;
using Orleans.Testing.Simulation.Instrumentation.Imaging;
using Orleans.Testing.Simulation.Instrumentation.Orchestration;
using Orleans.Testing.Simulation.Instrumentation.Rules;
using Orleans.Testing.Simulation.Instrumentation.Rules.BuiltIn;
using Orleans.Testing.Simulation.Instrumentation.Signing;
using Orleans.Testing.Simulation.Instrumentation.Tests.Infrastructure;
using Orleans.Testing.Simulation.Runtime.Racing;
using Mono.Cecil;

namespace Orleans.Testing.Simulation.Instrumentation.Tests.Orchestration;

/// <summary>
/// Verifies the end-to-end instrumentation orchestrator: it stages a runnable closure (rewriting
/// managed assemblies and copying every other asset verbatim), never mutates the source, is
/// incrementally cached and cache-invalidated on input changes, emits a deterministic closure
/// manifest, strips ReadyToRun inputs, and strips strong-name identities consistently across the
/// rewritten closure.
/// </summary>
public sealed class InstrumentationRunnerTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "cwr-orch-tests", Guid.NewGuid().ToString("n"));
    private readonly string _source;
    private readonly string _staging;

    public InstrumentationRunnerTests()
    {
        _source = Path.Combine(_root, "source");
        _staging = Path.Combine(_root, "staging");
        Directory.CreateDirectory(_source);
    }

    [Fact]
    public void StagesClosureRewritingManagedAndCopyingAssets()
    {
        BuildStandardClosure();
        InstrumentationResult result = Run(new InstrumentationConfiguration(), EmptyRuleSet());

        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        Assert.False(result.WasIncrementalHit);
        Assert.Equal(2, result.RewrittenCount);

        // Rewritten managed assemblies are present in staging.
        Assert.True(File.Exists(Path.Combine(_staging, "app.dll")));
        Assert.True(File.Exists(Path.Combine(_staging, "thirdparty.dll")));

        // Non-rewritten assets are copied verbatim, keeping the closure runnable.
        Assert.True(File.Exists(Path.Combine(_staging, "System.Fake.dll")));
        Assert.True(File.Exists(Path.Combine(_staging, "native.dll")));
        Assert.True(File.Exists(Path.Combine(_staging, "app.deps.json")));
        Assert.True(File.Exists(Path.Combine(_staging, "app.runtimeconfig.json")));
        Assert.Contains("System.Fake.dll", result.CopiedAssets);
        Assert.Contains("native.dll", result.CopiedAssets);

        // The manifest is emitted to the predictable path outside the staged closure.
        Assert.True(File.Exists(result.ManifestPath));
        Assert.False(result.ManifestPath.StartsWith(_staging + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        ClosureManifest manifest = ClosureManifestJson.Read(result.ManifestPath, out _);
        AssertCopiedAsset(manifest, "native.dll", Path.Combine(_source, "native.dll"));
        AssertCopiedAsset(manifest, "app.deps.json", Path.Combine(_source, "app.deps.json"));
        AssertCopiedAsset(
            manifest,
            "app.runtimeconfig.json",
            Path.Combine(_source, "app.runtimeconfig.json"));
        AssertCopiedAsset(manifest, "symbols.pdb", Path.Combine(_source, "symbols.pdb"));
    }

    [Fact]
    public void DoesNotMutateSourceDirectory()
    {
        BuildStandardClosure();
        byte[] before = File.ReadAllBytes(Path.Combine(_source, "app.dll"));

        Run(new InstrumentationConfiguration(), EmptyRuleSet());

        byte[] after = File.ReadAllBytes(Path.Combine(_source, "app.dll"));
        Assert.Equal(before, after);
    }

    [Theory]
    [MemberData(nameof(WindowsDeviceRequestPathCases))]
    public void RejectsWindowsDeviceRequestPathBeforeFilesystemMutation(
        string pathKind,
        string devicePathKind)
    {
        Assert.SkipWhen(
            !OperatingSystem.IsWindows(),
            "Windows device-path aliases are platform-specific.");
        BuildMinimalApp();
        Directory.CreateDirectory(_staging);
        File.WriteAllText(Path.Combine(_staging, "existing.sentinel"), "staging unchanged");
        string manifestPath = Path.Combine(_root, "metadata", "closure.json");
        string cachePath = Path.Combine(_root, "cache", "instrumentation.cache");
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        File.WriteAllText(manifestPath, "manifest unchanged");
        File.WriteAllText(cachePath, "cache unchanged");

        InstrumentationRequest request = CreateRequest() with
        {
            ManifestPath = manifestPath,
            CachePath = cachePath,
        };
        string ordinaryPath = pathKind switch
        {
            "source" => request.SourceDirectory,
            "staging" => request.StagingDirectory,
            "manifest" => request.ManifestPath,
            "cache" => request.CachePath,
            "rules" => Path.Combine(_root, "rules", "instrumentation.rules.json"),
            _ => throw new InvalidOperationException($"Unknown request path kind '{pathKind}'."),
        };
        string devicePath = ToWindowsDevicePath(ordinaryPath, devicePathKind);
        request = pathKind switch
        {
            "source" => request with { SourceDirectory = devicePath },
            "staging" => request with { StagingDirectory = devicePath },
            "manifest" => request with { ManifestPath = devicePath },
            "cache" => request with { CachePath = devicePath },
            "rules" => request with
            {
                Configuration = request.Configuration with { RuleSetPaths = [devicePath] },
            },
            _ => throw new InvalidOperationException($"Unknown request path kind '{pathKind}'."),
        };
        Dictionary<string, byte[]> before = SnapshotRootFiles();

        Exception? exception = Record.Exception(() => InstrumentationRunner.Run(request));

        AssertFileSnapshot(before, SnapshotRootFiles());
        ClosureException closureException = Assert.IsType<ClosureException>(exception);
        Assert.Contains(
            pathKind switch
            {
                "source" => nameof(InstrumentationRequest.SourceDirectory),
                "staging" => nameof(InstrumentationRequest.StagingDirectory),
                "manifest" => nameof(InstrumentationRequest.ManifestPath),
                "cache" => nameof(InstrumentationRequest.CachePath),
                "rules" => nameof(InstrumentationConfiguration.RuleSetPaths),
                _ => throw new InvalidOperationException($"Unknown request path kind '{pathKind}'."),
            },
            closureException.Message,
            StringComparison.Ordinal);
        Assert.Contains("device path", closureException.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AcceptsOrdinaryWindowsRequestPaths()
    {
        Assert.SkipWhen(
            !OperatingSystem.IsWindows(),
            "Windows path-validation behavior is platform-specific.");
        BuildMinimalApp();
        string stagingPath = Path.Combine(_root, "ordinary-staging");
        string manifestPath = Path.Combine(_root, "ordinary-metadata", "closure.json");
        string cachePath = Path.Combine(_root, "ordinary-cache", "instrumentation.cache");
        byte[] sourceAssembly = File.ReadAllBytes(Path.Combine(_source, "app.dll"));
        var request = new InstrumentationRequest
        {
            SourceDirectory = _source,
            StagingDirectory = stagingPath,
            ManifestPath = manifestPath,
            CachePath = cachePath,
            Configuration = new InstrumentationConfiguration(),
            RuleSet = EmptyRuleSet(),
        };

        InstrumentationResult result = InstrumentationRunner.Run(request);

        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        Assert.Equal(Path.GetFullPath(stagingPath), result.StagingDirectory);
        Assert.Equal(Path.GetFullPath(manifestPath), result.ManifestPath);
        Assert.Equal(sourceAssembly, File.ReadAllBytes(Path.Combine(_source, "app.dll")));
        Assert.True(File.Exists(Path.Combine(stagingPath, "app.dll")));
        Assert.True(File.Exists(manifestPath));
        JsonObject cacheRecord = JsonNode.Parse(File.ReadAllText(cachePath))!.AsObject();
        Assert.Matches("^[0-9a-f]{64}$", (string)cacheRecord["incrementalKey"]!);
        Assert.Matches("^[0-9a-f]{64}$", (string)cacheRecord["manifestSha256"]!);
        Assert.False(File.Exists(manifestPath + ".tmp"));
        Assert.False(File.Exists(cachePath + ".tmp"));
    }

    [Fact]
    public void RejectsLocalAdministrativeShareAliasOfSourceBeforeMutation()
    {
        Assert.SkipWhen(
            !OperatingSystem.IsWindows(),
            "Windows administrative-share aliases are platform-specific.");
        BuildMinimalApp();
        string sourceAssembly = Path.Combine(_source, "app.dll");
        string aliasedAssembly = ToLocalAdministrativeShare(sourceAssembly);
        Assert.SkipWhen(
            !File.Exists(aliasedAssembly),
            "The localhost administrative share is unavailable on this host.");
        byte[] contents = File.ReadAllBytes(sourceAssembly);

        ClosureException exception = Assert.Throws<ClosureException>(() =>
            Run(new InstrumentationConfiguration(), EmptyRuleSet(), aliasedAssembly));

        Assert.Contains("SourceDirectory", exception.Message, StringComparison.Ordinal);
        Assert.Equal(contents, File.ReadAllBytes(sourceAssembly));
        Assert.False(Directory.Exists(_staging));
    }

    [Fact]
    public void CanonicalizesSubstDriveAliasWhenAvailable()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "SUBST aliases are Windows-specific.");
        string? drive = TryCreateSubstDrive(_root);
        Assert.SkipWhen(drive is null, "A SUBST drive could not be created on this host.");

        try
        {
            string direct = InstrumentationPath.GetCanonicalPath(_source, "direct test path");
            string alias = InstrumentationPath.GetCanonicalPath(
                Path.Combine(drive!, "source"),
                "SUBST test path");

            Assert.Equal(direct, alias, ignoreCase: true);
        }
        finally
        {
            RemoveSubstDrive(drive!);
        }
    }

    [Fact]
    public void CanonicalizesMappedDriveAliasWhenAvailable()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Mapped-drive aliases are Windows-specific.");
        string? drive = TryCreateMappedDrive(@"\\localhost\c$");
        Assert.SkipWhen(drive is null, "A localhost administrative-share drive could not be mapped.");

        try
        {
            string relative = Path.GetRelativePath(
                Path.GetPathRoot(_source)!,
                _source);
            string direct = InstrumentationPath.GetCanonicalPath(_source, "direct test path");
            string alias = InstrumentationPath.GetCanonicalPath(
                Path.Combine(drive!, relative),
                "mapped-drive test path");

            Assert.Equal(direct, alias, ignoreCase: true);
        }
        finally
        {
            RemoveMappedDrive(drive!);
        }
    }

    public static TheoryData<string, string> WindowsDeviceRequestPathCases
    {
        get
        {
            string[] pathKinds = ["source", "staging", "manifest", "cache", "rules"];
            string[] devicePathKinds =
            [
                "extended",
                "extended-forward",
                "extended-forward-normalized",
                "device",
                "device-forward",
                "device-forward-normalized",
                "nt",
                "nt-normalized",
                "extended-unc",
                "extended-unc-forward",
                "extended-unc-forward-normalized",
                "device-unc",
                "device-unc-forward",
                "device-unc-forward-normalized",
                "nt-unc",
                "nt-unc-normalized",
            ];
            var result = new TheoryData<string, string>();
            foreach (string pathKind in pathKinds)
            {
                foreach (string devicePathKind in devicePathKinds)
                {
                    result.Add(pathKind, devicePathKind);
                }
            }

            return result;
        }
    }

    [Theory]
    [InlineData("manifest")]
    [InlineData("cache")]
    public void RejectsMetadataPathWhichWouldOverwriteSourceAssembly(string metadataKind)
    {
        BuildMinimalApp();
        string sourceAssembly = Path.Combine(_source, "app.dll");
        string escapingSourceAssembly = Path.Combine(_source, "metadata", "..", "app.dll");
        byte[] sourceContents = File.ReadAllBytes(sourceAssembly);

        var request = CreateRequest() with
        {
            SourceDirectory = _source + Path.DirectorySeparatorChar,
        };
        request = metadataKind == "manifest"
            ? request with { ManifestPath = escapingSourceAssembly }
            : request with { CachePath = escapingSourceAssembly };

        ClosureException exception =
            Assert.Throws<ClosureException>(() => InstrumentationRunner.Run(request));

        Assert.Contains(
            metadataKind == "manifest" ? "ManifestPath" : "CachePath",
            exception.Message,
            StringComparison.Ordinal);
        Assert.Contains("SourceDirectory", exception.Message, StringComparison.Ordinal);
        Assert.Equal(sourceContents, File.ReadAllBytes(sourceAssembly));
        Assert.False(Directory.Exists(Path.Combine(_source, "metadata")));
        Assert.False(Directory.Exists(_staging));
    }

    [Theory]
    [InlineData("manifest")]
    [InlineData("cache")]
    public void RejectsMetadataPathWithinSourceSubdirectory(string metadataKind)
    {
        BuildMinimalApp();
        string metadataDirectory = Path.Combine(_source, "metadata");
        string metadataPath = Path.Combine(
            metadataDirectory,
            metadataKind == "manifest" ? "closure.json" : "instrumentation.cache");

        var request = CreateRequest();
        request = metadataKind == "manifest"
            ? request with { ManifestPath = metadataPath }
            : request with { CachePath = metadataPath };

        ClosureException exception =
            Assert.Throws<ClosureException>(() => InstrumentationRunner.Run(request));

        Assert.Contains("must be outside SourceDirectory", exception.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(metadataDirectory));
        Assert.False(Directory.Exists(_staging));
    }

    [Theory]
    [InlineData("app.dll")]
    [InlineData("app.runtimeconfig.json")]
    public void RejectsManifestWhichCollidesWithPlannedStagedFile(string relativePath)
    {
        BuildMinimalApp();
        string manifestPath = Path.Combine(_staging, relativePath);
        Directory.CreateDirectory(_staging);
        string sentinelPath = Path.Combine(_staging, "existing.sentinel");
        File.WriteAllText(sentinelPath, "unchanged");

        ClosureException exception = Assert.Throws<ClosureException>(() =>
            Run(new InstrumentationConfiguration(), EmptyRuleSet(), manifestPath));

        Assert.Contains("ManifestPath", exception.Message, StringComparison.Ordinal);
        Assert.Contains("planned staged assembly or copied asset", exception.Message, StringComparison.Ordinal);
        Assert.Equal("unchanged", File.ReadAllText(sentinelPath));
        Assert.False(File.Exists(manifestPath));
    }

    [Fact]
    public void RejectsCacheWithinStagingDirectory()
    {
        BuildMinimalApp();
        string cachePath = Path.Combine(_staging, "metadata", "instrumentation.cache");

        ClosureException exception = Assert.Throws<ClosureException>(() =>
            Run(new InstrumentationConfiguration(), EmptyRuleSet(), cachePath: cachePath));

        Assert.Contains("CachePath", exception.Message, StringComparison.Ordinal);
        Assert.Contains("outside StagingDirectory", exception.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(_staging));
    }

    [Fact]
    public void RejectsCollidingManifestAndCachePathsAfterNormalization()
    {
        BuildMinimalApp();
        string manifestPath = Path.Combine(_root, "metadata", "..", "shared.metadata");
        string cachePath = Path.Combine(_root, "shared.metadata");

        ClosureException exception = Assert.Throws<ClosureException>(() =>
            Run(
                new InstrumentationConfiguration(),
                EmptyRuleSet(),
                manifestPath,
                cachePath));

        Assert.Contains("ManifestPath", exception.Message, StringComparison.Ordinal);
        Assert.Contains("CachePath", exception.Message, StringComparison.Ordinal);
        Assert.Contains("collide", exception.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(_staging));
        Assert.False(File.Exists(cachePath));
    }

    [Theory]
    [InlineData("planned-file")]
    [InlineData("metadata")]
    public void RejectsCaseVariantPathCollisionsOnWindows(string collisionKind)
    {
        Assert.SkipWhen(
            !OperatingSystem.IsWindows(),
            "Case-distinct paths are valid on case-sensitive platforms.");
        BuildMinimalApp();
        string manifestPath = collisionKind == "planned-file"
            ? Path.Combine(_staging, "APP.DLL")
            : Path.Combine(_root, "METADATA");
        string? cachePath = collisionKind == "metadata"
            ? Path.Combine(_root, "metadata")
            : null;

        ClosureException exception = Assert.Throws<ClosureException>(() =>
            Run(
                new InstrumentationConfiguration(),
                EmptyRuleSet(),
                manifestPath,
                cachePath));

        Assert.Contains("collid", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(_staging));
    }

    [Fact]
    public void NormalizesAndUsesValidExternalMetadataPaths()
    {
        BuildMinimalApp();
        string manifestPath = Path.Combine(_root, "metadata", "..", "metadata", "closure.json");
        string cachePath = Path.Combine(_root, "cache", "..", "cache", "instrumentation.key");

        InstrumentationResult result = Run(
            new InstrumentationConfiguration(),
            EmptyRuleSet(),
            manifestPath,
            cachePath);

        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        Assert.Equal(Path.GetFullPath(manifestPath), result.ManifestPath);
        Assert.True(File.Exists(Path.GetFullPath(manifestPath)));
        Assert.True(File.Exists(Path.GetFullPath(cachePath)));
        Assert.True(File.Exists(Path.Combine(_staging, "app.dll")));
    }

    [Theory]
    [InlineData("source")]
    [InlineData("staging")]
    [InlineData("manifest")]
    [InlineData("cache")]
    public void RejectsRequestPathWithExistingLinkComponent(string pathKind)
    {
        BuildMinimalApp();
        string target = pathKind switch
        {
            "source" => _source,
            "staging" => Path.Combine(_root, "staging-target"),
            _ => Path.Combine(_root, $"{pathKind}-target"),
        };
        Directory.CreateDirectory(target);
        string link = Path.Combine(_root, $"{pathKind}-link");
        bool created = TryCreateDirectoryLink(link, target);
        Assert.SkipWhen(!created, "Creating directory symbolic links is not permitted on this host.");

        InstrumentationRequest request = CreateRequest();
        request = pathKind switch
        {
            "source" => request with { SourceDirectory = link },
            "staging" => request with { StagingDirectory = link },
            "manifest" => request with { ManifestPath = Path.Combine(link, "closure.json") },
            "cache" => request with { CachePath = Path.Combine(link, "instrumentation.cache") },
            _ => throw new InvalidOperationException($"Unknown path kind '{pathKind}'."),
        };

        ClosureException exception =
            Assert.Throws<ClosureException>(() => InstrumentationRunner.Run(request));

        Assert.Contains(pathKind, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reparse-point", exception.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(_staging) && pathKind != "staging");
    }

    [Fact]
    public void LeavesPredictableAtomicTemporaryPathLinkUntouched()
    {
        BuildMinimalApp();
        string manifestPath = Path.Combine(_root, "closure.json");
        string temporaryPath = manifestPath + ".tmp";
        bool created = TryCreateDirectoryLink(temporaryPath, _source);
        Assert.SkipWhen(!created, "Creating directory symbolic links is not permitted on this host.");

        InstrumentationResult result =
            Run(new InstrumentationConfiguration(), EmptyRuleSet(), manifestPath);

        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        Assert.True(File.Exists(manifestPath));
        Assert.True(Directory.Exists(temporaryPath));
        Assert.True(File.Exists(Path.Combine(_source, "app.dll")));
    }

    [Theory]
    [InlineData("manifest-ancestor")]
    [InlineData("manifest-descendant")]
    public void RejectsManifestHierarchyCollisionWithPlannedStagedPath(string collisionKind)
    {
        BuildMinimalApp();
        string manifestPath;
        switch (collisionKind)
        {
            case "manifest-ancestor":
                Directory.CreateDirectory(Path.Combine(_source, "assets"));
                File.WriteAllText(Path.Combine(_source, "assets", "value.txt"), "asset");
                manifestPath = Path.Combine(_staging, "assets");
                break;
            case "manifest-descendant":
                File.WriteAllText(Path.Combine(_source, "metadata"), "asset");
                manifestPath = Path.Combine(_staging, "metadata", "closure.json");
                break;
            default:
                throw new InvalidOperationException($"Unknown collision kind '{collisionKind}'.");
        }

        ClosureException exception = Assert.Throws<ClosureException>(() =>
            Run(new InstrumentationConfiguration(), EmptyRuleSet(), manifestPath));

        Assert.Contains("ManifestPath", exception.Message, StringComparison.Ordinal);
        Assert.Contains("hierarchy", exception.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(_staging));
    }

    [Fact]
    public void RejectsMetadataHierarchyCollision()
    {
        BuildMinimalApp();
        string manifestPath = Path.Combine(_root, "metadata");
        string cachePath = Path.Combine(manifestPath, "instrumentation.cache");

        ClosureException exception = Assert.Throws<ClosureException>(() =>
            Run(
                new InstrumentationConfiguration(),
                EmptyRuleSet(),
                manifestPath,
                cachePath));

        Assert.Contains("metadata paths", exception.Message, StringComparison.Ordinal);
        Assert.Contains("hierarchy", exception.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(_staging));
    }

    [Fact]
    public void PreexistingPredictableTemporaryFilesRemainUntouched()
    {
        BuildMinimalApp();
        string manifestPath = Path.Combine(_root, "metadata", "closure.json");
        string cachePath = Path.Combine(_root, "cache", "instrumentation.cache");
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        File.WriteAllText(manifestPath + ".tmp", "manifest sentinel");
        File.WriteAllText(cachePath + ".tmp", "cache sentinel");

        InstrumentationResult result = Run(
            new InstrumentationConfiguration(),
            EmptyRuleSet(),
            manifestPath,
            cachePath);

        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        Assert.Equal("manifest sentinel", File.ReadAllText(manifestPath + ".tmp"));
        Assert.Equal("cache sentinel", File.ReadAllText(cachePath + ".tmp"));
        Assert.True(File.Exists(manifestPath));
        JsonObject cacheRecord = JsonNode.Parse(File.ReadAllText(cachePath))!.AsObject();
        Assert.Matches("^[0-9a-f]{64}$", (string)cacheRecord["incrementalKey"]!);
        Assert.Matches("^[0-9a-f]{64}$", (string)cacheRecord["manifestSha256"]!);
        Assert.Empty(Directory.EnumerateFiles(
            Path.GetDirectoryName(manifestPath)!,
            Path.GetFileName(manifestPath) + ".*.tmp"));
        Assert.Empty(Directory.EnumerateFiles(
            Path.GetDirectoryName(cachePath)!,
            Path.GetFileName(cachePath) + ".*.tmp"));
    }

    [Fact]
    public void HardLinkedPredictableTemporaryFilesRemainUntouchedOnWindows()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Windows hard-link behavior is platform-specific.");
        BuildMinimalApp();
        string manifestPath = Path.Combine(_root, "metadata", "closure.json");
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        string sentinelPath = Path.Combine(_root, "hard-link-sentinel");
        File.WriteAllText(sentinelPath, "unchanged");
        string predictableTemporaryPath = manifestPath + ".tmp";
        Assert.SkipWhen(
            !NativeMethods.CreateHardLink(predictableTemporaryPath, sentinelPath, IntPtr.Zero),
            "A hard link could not be created on this host.");

        InstrumentationResult result =
            Run(new InstrumentationConfiguration(), EmptyRuleSet(), manifestPath);

        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        Assert.Equal("unchanged", File.ReadAllText(sentinelPath));
        Assert.Equal("unchanged", File.ReadAllText(predictableTemporaryPath));
        Assert.True(File.Exists(manifestPath));
    }

    [Theory]
    [InlineData("configuration")]
    [InlineData("rules")]
    public void RejectsMetadataWhichWouldOverwriteProtectedConfigurationInput(string inputKind)
    {
        BuildMinimalApp();
        string inputPath = Path.Combine(_root, $"{inputKind}.input");
        File.WriteAllText(inputPath, "unchanged");
        var configuration = new InstrumentationConfiguration();
        configuration = inputKind switch
        {
            "configuration" => configuration with { SourcePath = inputPath },
            "rules" => configuration with { RuleSetPaths = [inputPath] },
            _ => throw new InvalidOperationException($"Unknown input kind '{inputKind}'."),
        };

        ClosureException exception =
            Assert.Throws<ClosureException>(() => Run(configuration, EmptyRuleSet(), inputPath));

        Assert.Contains(inputKind, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ManifestPath", exception.Message, StringComparison.Ordinal);
        Assert.Equal("unchanged", File.ReadAllText(inputPath));
        Assert.False(Directory.Exists(_staging));
    }

    [Fact]
    public void RejectsExistingFileUsedAsMetadataDirectory()
    {
        BuildMinimalApp();
        string blocker = Path.Combine(_root, "metadata-blocker");
        File.WriteAllText(blocker, "unchanged");
        string manifestPath = Path.Combine(blocker, "closure.json");

        ClosureException exception = Assert.Throws<ClosureException>(() =>
            Run(new InstrumentationConfiguration(), EmptyRuleSet(), manifestPath));

        Assert.Contains("ManifestPath", exception.Message, StringComparison.Ordinal);
        Assert.Contains("existing file", exception.Message, StringComparison.Ordinal);
        Assert.Equal("unchanged", File.ReadAllText(blocker));
        Assert.False(Directory.Exists(_staging));
    }

    [Fact]
    public void RejectsExistingFileUsedAsStagingDirectory()
    {
        BuildMinimalApp();
        File.WriteAllText(_staging, "unchanged");

        ClosureException exception = Assert.Throws<ClosureException>(() =>
            Run(new InstrumentationConfiguration(), EmptyRuleSet()));

        Assert.Contains("StagingDirectory", exception.Message, StringComparison.Ordinal);
        Assert.Contains("existing file", exception.Message, StringComparison.Ordinal);
        Assert.Equal("unchanged", File.ReadAllText(_staging));
    }

    [Fact]
    public void ValidCopiedAssetsRemainIncrementalCacheHit()
    {
        BuildStandardClosure();
        InstrumentationResult first = Run(new InstrumentationConfiguration(), EmptyRuleSet());
        byte[] stagedAssembly = File.ReadAllBytes(Path.Combine(_staging, "app.dll"));
        byte[] stagedAsset = File.ReadAllBytes(Path.Combine(_staging, "native.dll"));
        string manifest = File.ReadAllText(first.ManifestPath);

        InstrumentationResult second = Run(new InstrumentationConfiguration(), EmptyRuleSet());

        Assert.True(first.Succeeded, string.Join("\n", first.Errors));
        Assert.True(second.Succeeded);
        Assert.True(second.WasIncrementalHit);
        Assert.Empty(second.Assemblies);
        Assert.Equal(stagedAssembly, File.ReadAllBytes(Path.Combine(_staging, "app.dll")));
        Assert.Equal(stagedAsset, File.ReadAllBytes(Path.Combine(_staging, "native.dll")));
        Assert.Equal(manifest, File.ReadAllText(second.ManifestPath));

        JsonObject cacheRecord = JsonNode.Parse(File.ReadAllText(CachePath()))!.AsObject();
        Assert.Equal(1, (int)cacheRecord["schemaVersion"]!);
        Assert.Equal(
            ClosureManifestJson.Read(first.ManifestPath, out _).IncrementalKey,
            (string?)cacheRecord["incrementalKey"]);
        Assert.Equal(
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(first.ManifestPath))),
            (string?)cacheRecord["manifestSha256"]);
    }

    [Fact]
    public void TamperingUnvalidatedManifestFieldInvalidatesIncrementalCache()
    {
        BuildStandardClosure();
        InstrumentationResult first = Run(new InstrumentationConfiguration(), EmptyRuleSet());
        JsonObject tampered = JsonNode.Parse(File.ReadAllText(first.ManifestPath))!.AsObject();
        JsonObject assembly = tampered["assemblies"]!.AsArray()[0]!.AsObject();
        bool expectedWasRewritten = (bool)assembly["wasRewritten"]!;
        assembly["wasRewritten"] = !expectedWasRewritten;
        File.WriteAllText(
            first.ManifestPath,
            tampered.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        InstrumentationResult second = Run(new InstrumentationConfiguration(), EmptyRuleSet());

        Assert.True(first.Succeeded, string.Join("\n", first.Errors));
        Assert.True(second.Succeeded, string.Join("\n", second.Errors));
        Assert.False(second.WasIncrementalHit);
        Assert.Equal(
            expectedWasRewritten,
            ClosureManifestJson.Read(second.ManifestPath, out _).Assemblies[0].WasRewritten);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("legacy-plain-key")]
    [InlineData("malformed")]
    [InlineData("noncanonical")]
    [InlineData("oversized")]
    public void MissingOrCorruptCacheMarkerForcesFullRebuild(string corruption)
    {
        BuildStandardClosure();
        InstrumentationResult first = Run(new InstrumentationConfiguration(), EmptyRuleSet());
        string cachePath = CachePath();
        string validMarker = File.ReadAllText(cachePath);
        switch (corruption)
        {
            case "missing":
                File.Delete(cachePath);
                break;
            case "legacy-plain-key":
                File.WriteAllText(
                    cachePath,
                    ClosureManifestJson.Read(first.ManifestPath, out _).IncrementalKey);
                break;
            case "malformed":
                File.WriteAllText(cachePath, "{ invalid");
                break;
            case "noncanonical":
                File.WriteAllText(cachePath, validMarker + Environment.NewLine);
                break;
            case "oversized":
                File.WriteAllText(cachePath, new string('x', 513));
                break;
            default:
                throw new InvalidOperationException($"Unknown corruption '{corruption}'.");
        }

        InstrumentationResult second = Run(new InstrumentationConfiguration(), EmptyRuleSet());

        Assert.True(first.Succeeded, string.Join("\n", first.Errors));
        Assert.True(second.Succeeded, string.Join("\n", second.Errors));
        Assert.False(second.WasIncrementalHit);
        JsonObject rebuiltMarker = JsonNode.Parse(File.ReadAllText(cachePath))!.AsObject();
        Assert.Equal(
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(second.ManifestPath))),
            (string?)rebuiltMarker["manifestSha256"]);
    }

    [Fact]
    public void ValidExactStagingFileSetIncludingContainedManifestRemainsIncrementalCacheHit()
    {
        BuildStandardClosure();
        const string manifestRelativePath = "metadata/closure.manifest.json";
        string manifestPath = Path.Combine(
            _staging,
            manifestRelativePath.Replace('/', Path.DirectorySeparatorChar));
        InstrumentationResult first = Run(
            new InstrumentationConfiguration(),
            EmptyRuleSet(),
            manifestPath);
        ClosureManifest manifest = ClosureManifestJson.Read(first.ManifestPath, out _);
        string[] expectedPaths =
        [
            .. manifest.Assemblies.Select(static assembly => assembly.RelativePath),
            .. manifest.CopiedAssets.Select(static asset => asset.RelativePath),
            manifestRelativePath,
        ];

        InstrumentationResult second = Run(
            new InstrumentationConfiguration(),
            EmptyRuleSet(),
            manifestPath);

        Assert.True(first.Succeeded, string.Join("\n", first.Errors));
        Assert.Equal(
            expectedPaths.OrderBy(static path => path, StringComparer.Ordinal),
            EnumerateStagedPaths());
        Assert.True(second.Succeeded, string.Join("\n", second.Errors));
        Assert.True(second.WasIncrementalHit);
    }

    [Theory]
    [InlineData("stale.bin")]
    [InlineData("stale/nested.bin")]
    public void UnexpectedStagedFileInvalidatesIncrementalCacheAndIsCleaned(string relativePath)
    {
        BuildStandardClosure();
        InstrumentationResult first = Run(new InstrumentationConfiguration(), EmptyRuleSet());
        string stalePath = Path.Combine(
            _staging,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(stalePath)!);
        File.WriteAllText(stalePath, "stale");

        InstrumentationResult second = Run(new InstrumentationConfiguration(), EmptyRuleSet());

        Assert.True(first.Succeeded, string.Join("\n", first.Errors));
        Assert.True(second.Succeeded, string.Join("\n", second.Errors));
        Assert.False(second.WasIncrementalHit);
        Assert.False(File.Exists(stalePath));
    }

    [Fact]
    public void CaseCollidingManifestPathsInvalidateIncrementalCacheOnWindows()
    {
        Assert.SkipWhen(
            !OperatingSystem.IsWindows(),
            "Case-distinct paths are valid on case-sensitive platforms.");
        BuildStandardClosure();
        InstrumentationResult first = Run(new InstrumentationConfiguration(), EmptyRuleSet());
        ClosureManifest manifest = ClosureManifestJson.Read(first.ManifestPath, out _);
        ClosureManifestCopiedAsset native =
            manifest.CopiedAssets.Single(static asset => asset.RelativePath == "native.dll");
        JsonObject tampered = JsonNode.Parse(File.ReadAllText(first.ManifestPath))!.AsObject();
        tampered["copiedAssets"]!.AsArray().Add(new JsonObject
        {
            ["relativePath"] = "NATIVE.DLL",
            ["sha256"] = native.Sha256,
        });
        File.WriteAllText(
            first.ManifestPath,
            tampered.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        InstrumentationResult second = Run(new InstrumentationConfiguration(), EmptyRuleSet());

        Assert.True(first.Succeeded, string.Join("\n", first.Errors));
        Assert.True(second.Succeeded, string.Join("\n", second.Errors));
        Assert.False(second.WasIncrementalHit);
        Assert.Equal(
            manifest.CopiedAssets,
            ClosureManifestJson.Read(second.ManifestPath, out _).CopiedAssets);
    }

    [Fact]
    public void DeletedStagedAssemblyInvalidatesIncrementalCache()
    {
        BuildStandardClosure();
        InstrumentationResult first = Run(new InstrumentationConfiguration(), EmptyRuleSet());
        string stagedAssembly = Path.Combine(_staging, "app.dll");
        byte[] expectedAssembly = File.ReadAllBytes(stagedAssembly);
        File.Delete(stagedAssembly);

        InstrumentationResult second = Run(new InstrumentationConfiguration(), EmptyRuleSet());

        Assert.True(first.Succeeded, string.Join("\n", first.Errors));
        Assert.True(second.Succeeded, string.Join("\n", second.Errors));
        Assert.False(second.WasIncrementalHit);
        Assert.Equal(expectedAssembly, File.ReadAllBytes(stagedAssembly));
        Assert.Contains(second.Assemblies, assembly => assembly.RelativePath == "app.dll");
    }

    [Fact]
    public void CorruptedStagedAssemblyInvalidatesIncrementalCache()
    {
        BuildStandardClosure();
        InstrumentationResult first = Run(new InstrumentationConfiguration(), EmptyRuleSet());
        string stagedAssembly = Path.Combine(_staging, "app.dll");
        byte[] expectedAssembly = File.ReadAllBytes(stagedAssembly);
        File.WriteAllBytes(stagedAssembly, [0xde, 0xad, 0xbe, 0xef]);

        InstrumentationResult second = Run(new InstrumentationConfiguration(), EmptyRuleSet());

        Assert.True(first.Succeeded, string.Join("\n", first.Errors));
        Assert.True(second.Succeeded, string.Join("\n", second.Errors));
        Assert.False(second.WasIncrementalHit);
        Assert.Equal(expectedAssembly, File.ReadAllBytes(stagedAssembly));
        Assert.Contains(second.Assemblies, assembly => assembly.RelativePath == "app.dll");
    }

    [Fact]
    public void MissingCopiedAssetInvalidatesIncrementalCache()
    {
        BuildStandardClosure();
        InstrumentationResult first = Run(new InstrumentationConfiguration(), EmptyRuleSet());
        string stagedAsset = Path.Combine(_staging, "native.dll");
        File.Delete(stagedAsset);

        InstrumentationResult second = Run(new InstrumentationConfiguration(), EmptyRuleSet());

        Assert.True(first.Succeeded, string.Join("\n", first.Errors));
        Assert.True(second.Succeeded, string.Join("\n", second.Errors));
        Assert.False(second.WasIncrementalHit);
        Assert.Equal(
            File.ReadAllBytes(Path.Combine(_source, "native.dll")),
            File.ReadAllBytes(stagedAsset));
        Assert.Contains("native.dll", second.CopiedAssets);
    }

    [Fact]
    public void CorruptedCopiedAssetInvalidatesIncrementalCache()
    {
        BuildStandardClosure();
        InstrumentationResult first = Run(new InstrumentationConfiguration(), EmptyRuleSet());
        string stagedAsset = Path.Combine(_staging, "app.deps.json");
        byte[] expectedAsset = File.ReadAllBytes(Path.Combine(_source, "app.deps.json"));
        File.WriteAllText(stagedAsset, """{"corrupted":true}""");

        InstrumentationResult second = Run(new InstrumentationConfiguration(), EmptyRuleSet());

        Assert.True(first.Succeeded, string.Join("\n", first.Errors));
        Assert.True(second.Succeeded, string.Join("\n", second.Errors));
        Assert.False(second.WasIncrementalHit);
        Assert.Equal(expectedAsset, File.ReadAllBytes(stagedAsset));
        Assert.Contains("app.deps.json", second.CopiedAssets);
        AssertCopiedAsset(
            ClosureManifestJson.Read(second.ManifestPath, out _),
            "app.deps.json",
            stagedAsset);
    }

    [Fact]
    public void CorruptClosureManifestInvalidatesIncrementalCache()
    {
        BuildStandardClosure();
        InstrumentationResult first = Run(new InstrumentationConfiguration(), EmptyRuleSet());
        File.WriteAllText(first.ManifestPath, "{ not valid closure manifest json");

        InstrumentationResult second = Run(new InstrumentationConfiguration(), EmptyRuleSet());

        Assert.True(first.Succeeded, string.Join("\n", first.Errors));
        Assert.True(second.Succeeded, string.Join("\n", second.Errors));
        Assert.False(second.WasIncrementalHit);
        ClosureManifest manifest = ClosureManifestJson.Read(second.ManifestPath, out _);
        Assert.Equal("clockwork.test", manifest.RuleSetId);
        Assert.Equal(2, manifest.Assemblies.Length);
    }

    [Fact]
    public void ChangedInputInvalidatesIncrementalCache()
    {
        BuildStandardClosure();
        Run(new InstrumentationConfiguration(), EmptyRuleSet());

        // Recompile the dependency with different content; the cache key must change.
        Compile("thirdparty", "namespace Third { public static class T { public static int V() => 42; } }");
        InstrumentationResult second = Run(new InstrumentationConfiguration(), EmptyRuleSet());

        Assert.True(second.Succeeded);
        Assert.False(second.WasIncrementalHit);
    }

    [Fact]
    public void TargetRuntimeIsDerivedFromTheEntryRuntimeConfigAndRecordedOnRewrittenAssemblies()
    {
        BuildMinimalApp();
        File.WriteAllText(
            Path.Combine(_source, "app.runtimeconfig.json"),
            """{ "runtimeOptions": { "framework": { "name": "Microsoft.NETCore.App", "version": "10.0.4" } } }""");
        var net10 = new Version(10, 0);

        InstrumentationResult result = Run(new InstrumentationConfiguration(), RuleSetFor(net10));

        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        Assert.Equal(net10, result.TargetRuntime);
        Assert.Equal("from 'app.runtimeconfig.json'", result.TargetRuntimeOrigin);
        using AssemblyDefinition staged = AssemblyDefinition.ReadAssembly(Path.Combine(_staging, "app.dll"));
        Assert.Contains(
            staged.CustomAttributes,
            attribute => attribute.AttributeType.Name == nameof(System.Reflection.AssemblyMetadataAttribute)
                && (string)attribute.ConstructorArguments[0].Value == "Clockwork.SupportedRuntimes"
                && (string)attribute.ConstructorArguments[1].Value == "[10.0,10.0]");
    }

    [Fact]
    public void RuleSetThatDoesNotSupportTheTargetRuntimeFailsOnceBeforeStaging()
    {
        BuildStandardClosure();
        RewriteRuleSet ruleSet = RuleSetFor(new Version(Environment.Version.Major, Environment.Version.Minor));
        Run(new InstrumentationConfiguration(), ruleSet).EnsureSuccess();
        string[] staged = [.. EnumerateStagedPaths()];

        InstrumentationResult unsupported = Run(new InstrumentationConfiguration { TargetRuntime = new Version(12, 0) }, ruleSet);
        bool cacheAfterFailure = File.Exists(CachePath());
        string[] stagedAfterFailure = [.. EnumerateStagedPaths()];
        InstrumentationResult rerun = Run(new InstrumentationConfiguration(), ruleSet);

        Assert.False(unsupported.Succeeded);
        Assert.Equal(RewriteDiagnosticIds.RuntimeOutOfRange, Assert.Single(unsupported.Errors).Id);
        Assert.Empty(unsupported.Assemblies);
        Assert.False(cacheAfterFailure);
        Assert.Equal(staged, stagedAfterFailure);
        Assert.True(rerun.Succeeded, string.Join("\n", rerun.Errors));
        Assert.False(rerun.WasIncrementalHit);
    }

    [Fact]
    public void ChangedCopiedAssetInputInvalidatesIncrementalCache()
    {
        BuildStandardClosure();
        Run(new InstrumentationConfiguration(), EmptyRuleSet());
        string sourceAsset = Path.Combine(_source, "app.runtimeconfig.json");
        string updatedContents = File.ReadAllText(sourceAsset).Replace("latestMinor", "Major", StringComparison.Ordinal);
        File.WriteAllText(sourceAsset, updatedContents);

        InstrumentationResult second = Run(new InstrumentationConfiguration(), EmptyRuleSet());

        Assert.True(second.Succeeded, string.Join("\n", second.Errors));
        Assert.False(second.WasIncrementalHit);
        Assert.Equal(updatedContents, File.ReadAllText(Path.Combine(_staging, "app.runtimeconfig.json")));
        AssertCopiedAsset(
            ClosureManifestJson.Read(second.ManifestPath, out _),
            "app.runtimeconfig.json",
            sourceAsset);
    }

    [Fact]
    public void DistinctAssetContentsProduceDistinctIncrementalKeys()
    {
        BuildMinimalApp();
        InstrumentationResult first = Run(new InstrumentationConfiguration(), EmptyRuleSet());
        string firstKey = ReadIncrementalKey();
        string runtimeConfig = Path.Combine(_source, "app.runtimeconfig.json");
        File.WriteAllText(runtimeConfig, File.ReadAllText(runtimeConfig).Replace("latestMinor", "Major", StringComparison.Ordinal));

        InstrumentationResult second = Run(new InstrumentationConfiguration(), EmptyRuleSet());
        string secondKey = ReadIncrementalKey();

        Assert.True(first.Succeeded, string.Join("\n", first.Errors));
        Assert.True(second.Succeeded, string.Join("\n", second.Errors));
        Assert.False(second.WasIncrementalHit);
        Assert.NotEqual(firstKey, secondKey);
    }

    [Theory]
    [InlineData("configuration")]
    [InlineData("rules")]
    public void ExactSourceDocumentBytesBindIncrementalKey(string inputKind)
    {
        BuildMinimalApp();
        string sourcePath = Path.Combine(_root, $"{inputKind}.json");
        File.WriteAllText(sourcePath, "{}");
        var configuration = inputKind == "configuration"
            ? new InstrumentationConfiguration { SourcePath = sourcePath }
            : new InstrumentationConfiguration { RuleSetPaths = [sourcePath] };
        InstrumentationResult first = Run(configuration, EmptyRuleSet());
        string firstKey = ReadIncrementalKey();
        File.WriteAllText(sourcePath, "{ }");

        InstrumentationResult second = Run(configuration, EmptyRuleSet());
        string secondKey = ReadIncrementalKey();

        Assert.True(first.Succeeded, string.Join("\n", first.Errors));
        Assert.True(second.Succeeded, string.Join("\n", second.Errors));
        Assert.False(second.WasIncrementalHit);
        Assert.NotEqual(firstKey, secondKey);
    }

    [Theory]
    [InlineData("configuration")]
    [InlineData("rules")]
    public void DeletedSourceDocumentFailsInsteadOfUsingSharedCacheSentinel(string inputKind)
    {
        BuildMinimalApp();
        string deletedPath = Path.Combine(_root, $"{inputKind}.deleted");
        var configuration = inputKind == "configuration"
            ? new InstrumentationConfiguration { SourcePath = deletedPath }
            : new InstrumentationConfiguration { RuleSetPaths = [deletedPath] };

        ClosureException exception = Assert.Throws<ClosureException>(
            () => Run(configuration, EmptyRuleSet()));

        Assert.Contains(inputKind, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("missing", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(_staging));
        Assert.False(File.Exists(CachePath()));
    }

    [Fact]
    public void InaccessiblePlannedAssetFailsInsteadOfUsingSharedCacheSentinel()
    {
        Assert.SkipWhen(
            !OperatingSystem.IsWindows(),
            "Exclusive file sharing is used to model an access-denied planned input.");
        BuildMinimalApp();
        string assetPath = Path.Combine(_source, "app.runtimeconfig.json");
        using var exclusive = new FileStream(
            assetPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);

        // A configured target runtime leaves the runtimeconfig.json to the closure's hashing, which this test exercises.
        ClosureException exception = Assert.Throws<ClosureException>(
            () => Run(new InstrumentationConfiguration { TargetRuntime = RewriteTestContext.TargetRuntime }, EmptyRuleSet()));

        Assert.Contains("app.runtimeconfig.json", exception.Message, StringComparison.Ordinal);
        Assert.Contains("unreadable", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(_staging));
        Assert.False(File.Exists(CachePath()));
    }

    [Fact]
    public void ChangedInstrumentationModeInvalidatesIncrementalCache()
    {
        BuildStandardClosure();
        Run(new InstrumentationConfiguration(), EmptyRuleSet());

        InstrumentationResult second = Run(
            new InstrumentationConfiguration { Mode = InstrumentationMode.RaceExploration },
            EmptyRuleSet());

        Assert.True(second.Succeeded);
        Assert.False(second.WasIncrementalHit);
        Assert.Contains("\"mode\": \"RaceExploration\"", File.ReadAllText(second.ManifestPath));
    }

    [Fact]
    public void ControlledTaskRulesHardenBroadExceptionHandlers()
    {
        Compile(
            "app",
            """
            namespace App;

            public static class Handler
            {
                public static int Run()
                {
                    try
                    {
                        return 1;
                    }
                    catch (System.Exception)
                    {
                        return -1;
                    }
                }
            }
            """);
        File.Copy(
            typeof(Orleans.Testing.Simulation.Runtime.SimulationExceptionGuard).Assembly.Location,
            Path.Combine(_source, "Orleans.Testing.Simulation.dll"));
        ProcessAppRunner.WriteRuntimeConfig(Path.Combine(_source, "app.dll"));

        InstrumentationResult result = Run(
            new InstrumentationConfiguration(),
            BuiltInRuleSets.BuildControlledTasks(BuiltInRuleSets.AllFamilies));

        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        Assert.Contains(
            result.Assemblies.Single(assembly => assembly.RelativePath == "app.dll").Manifest!.Transformations,
            transformation => transformation.RuleId == "clockwork.exceptions.harden"
                && transformation.Method.EndsWith(".Run", StringComparison.Ordinal));
    }

    [Fact]
    public void CopiesReplacementAssemblyDependenciesWithoutRewritingThem()
    {
        string keyPath = WriteKey();
        string dependency = Compile(
            "shim-dependency",
            "namespace ShimDependency; public static class ValueProvider { public static int Value => 1; }",
            keyPath);
        Compile(
            "shim",
            "namespace Shim; public static class Replacement { public static int Value => ShimDependency.ValueProvider.Value; }",
            references: [dependency]);
        BuildMinimalApp();

        var ruleSet = new RewriteRuleSet(
            "clockwork.test.replacement-closure",
            "1.0",
            [
                RewriteRule.SubstituteType(
                    "clockwork.test.replacement",
                    "Missing.Target",
                    RewriteReplacement.Type("shim", "Shim.Replacement")),
            ]);

        InstrumentationResult result = Run(new InstrumentationConfiguration(), ruleSet);

        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        Assert.Contains("shim.dll", result.CopiedAssets);
        Assert.Contains("shim-dependency.dll", result.CopiedAssets);
        Assert.DoesNotContain(result.Assemblies, assembly => assembly.RelativePath == "shim-dependency.dll");
    }

    [Fact]
    public void RaceModeRejectsSourceAssetWhichCollidesWithInjectedRuntime()
    {
        BuildMinimalApp();
        FixtureCompiler.Compile(
            "Orleans.Testing.Simulation",
            "namespace Orleans.Testing.Simulation.Runtime { public static class LegacyRuntime { } }",
            _source,
            FixtureSymbols.None,
            optimize: true);

        ClosureException exception = Assert.Throws<ClosureException>(() =>
            Run(
                new InstrumentationConfiguration { Mode = InstrumentationMode.RaceExploration },
                EmptyRuleSet()));

        Assert.Contains("race runtime", exception.Message, StringComparison.Ordinal);
        Assert.Contains("collides", exception.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(_staging));
    }

    [Fact]
    public void RaceModeRejectsInjectedRuntimeFileDirectoryCollision()
    {
        BuildMinimalApp();
        string collisionDirectory = Path.Combine(_source, "Orleans.Testing.Simulation.dll");
        Directory.CreateDirectory(collisionDirectory);
        File.WriteAllText(Path.Combine(collisionDirectory, "content.bin"), "asset");

        ClosureException exception = Assert.Throws<ClosureException>(() =>
            Run(
                new InstrumentationConfiguration { Mode = InstrumentationMode.RaceExploration },
                EmptyRuleSet()));

        Assert.Contains("file and descendant", exception.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(_staging));
    }

    [Fact]
    public void CorruptedRaceRuntimeAssetInvalidatesIncrementalCache()
    {
        BuildMinimalApp();
        var configuration = new InstrumentationConfiguration
        {
            Mode = InstrumentationMode.RaceExploration,
        };
        InstrumentationResult first = Run(configuration, EmptyRuleSet());
        string stagedRuntime = Path.Combine(_staging, "Orleans.Testing.Simulation.dll");
        byte[] expectedRuntime = File.ReadAllBytes(typeof(RaceInstrumentation).Assembly.Location);
        File.WriteAllBytes(stagedRuntime, [0xde, 0xad, 0xbe, 0xef]);

        InstrumentationResult second = Run(configuration, EmptyRuleSet());

        Assert.True(first.Succeeded, string.Join("\n", first.Errors));
        Assert.True(second.Succeeded, string.Join("\n", second.Errors));
        Assert.False(second.WasIncrementalHit);
        Assert.Equal(expectedRuntime, File.ReadAllBytes(stagedRuntime));
        Assert.Contains("Orleans.Testing.Simulation.dll", second.CopiedAssets);
        AssertCopiedAsset(
            ClosureManifestJson.Read(second.ManifestPath, out _),
            "Orleans.Testing.Simulation.dll",
            stagedRuntime);
    }

    [Theory]
    [MemberData(nameof(UnsafeStagingDirectories))]
    public void RejectsStagingDirectoryWhichOverlapsSource(string stagingSelector)
    {
        BuildMinimalApp();
        string staging = stagingSelector switch
        {
            "same" => Path.Combine(_source, "..", "source"),
            "parent" => _root,
            "child" => Path.Combine(_source, "instrumented"),
            _ => throw new InvalidOperationException($"Unknown staging selector '{stagingSelector}'."),
        };

        ClosureException exception = Assert.Throws<ClosureException>(() =>
            InstrumentationRunner.Run(new InstrumentationRequest
            {
                SourceDirectory = _source,
                StagingDirectory = staging,
                Configuration = new InstrumentationConfiguration(),
                RuleSet = EmptyRuleSet(),
            }));

        Assert.Contains("dedicated directory", exception.Message);
        Assert.True(File.Exists(Path.Combine(_source, "app.dll")));
    }

    public static TheoryData<string> UnsafeStagingDirectories => new()
    {
        "same",
        "parent",
        "child",
    };

    [Fact]
    public void ManifestIsDeterministic()
    {
        BuildStandardClosure();
        InstrumentationResult first = Run(new InstrumentationConfiguration(), EmptyRuleSet());
        string firstManifest = File.ReadAllText(first.ManifestPath);

        // Force a full re-run by clearing the cache, then compare manifests byte-for-byte.
        File.Delete(CachePath());
        InstrumentationResult second = Run(new InstrumentationConfiguration(), EmptyRuleSet());
        string secondManifest = File.ReadAllText(second.ManifestPath);

        Assert.Equal(firstManifest, secondManifest);
    }

    [Fact]
    public void ReadyToRunInputHasNativeAndStrongNameIdentityStripped()
    {
        string? r2r = FindReadyToRunAssembly();
        Assert.SkipWhen(r2r is null, "No ReadyToRun image found in the shared framework.");

        BuildMinimalApp();
        File.Copy(r2r!, Path.Combine(_source, "r2rdep.dll"));

        InstrumentationResult result = Run(new InstrumentationConfiguration(), EmptyRuleSet());

        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        AssemblyInstrumentationResult stripped = result.Assemblies.Single(a => a.RelativePath == "r2rdep.dll");
        Assert.True(stripped.ReadyToRunStripped);
        Assert.Contains(stripped.Diagnostics, d => d.Id == RewriteDiagnosticIds.ReadyToRunStripped);
        Assert.Contains(stripped.Diagnostics, d => d.Id == RewriteDiagnosticIds.StrongNameStripped);
        Assert.Equal(StrongNameStatus.None, StrongNameInspector.Inspect(Path.Combine(_staging, "r2rdep.dll")).Status);
        Assert.True(File.Exists(CachePath()));
    }

    [Fact]
    public void ReadyToRunRewritePreservesOldTempNamedAssetAndCleansWorkspace()
    {
        string? r2r = FindReadyToRunAssembly();
        Assert.SkipWhen(r2r is null, "No ReadyToRun image found in the shared framework.");

        BuildMinimalApp();
        File.Copy(r2r!, Path.Combine(_source, "r2rdep.dll"));
        const string oldTemporaryName = "r2rdep.dll.r2rstrip.tmp";
        byte[] legitimateAsset = [0xde, 0xad, 0xbe, 0xef];
        File.WriteAllBytes(Path.Combine(_source, oldTemporaryName), legitimateAsset);
        string cacheIdentity = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(CachePath())))[..16];
        string temporaryPattern = $"clockwork-r2r-{cacheIdentity}-*";
        string[] before = Directory.EnumerateDirectories(Path.GetTempPath(), temporaryPattern)
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();

        InstrumentationResult result = Run(new InstrumentationConfiguration(), EmptyRuleSet());

        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        Assert.Contains(oldTemporaryName, result.CopiedAssets);
        Assert.Equal(
            legitimateAsset,
            File.ReadAllBytes(Path.Combine(_staging, oldTemporaryName)));
        Assert.Equal(
            before,
            Directory.EnumerateDirectories(Path.GetTempPath(), temporaryPattern)
                .OrderBy(static path => path, StringComparer.Ordinal));
    }

    [Fact]
    public void ReadyToRunStripperProducesManagedILOnlyOutput()
    {
        string? r2r = FindReadyToRunAssembly();
        Assert.SkipWhen(r2r is null, "No ReadyToRun image found in the shared framework.");

        string output = Path.Combine(_root, "stripped.dll");
        ReadyToRunStripper.StripToIL(r2r!, output);

        AssemblyImageInfo image = AssemblyImageInfo.Inspect(output);
        Assert.True(image.IsManagedAssembly);
        Assert.True(image.IsILOnly);
        Assert.False(image.IsReadyToRun);
    }

    [Fact]
    public void StripsStrongNamedClosureConsistently()
    {
        string keyPath = WriteKey();
        string third = Compile(
            "thirdparty", "namespace Third { public static class T { public static int V() => 1; } }", keyPath);
        Compile(
            "app",
            "namespace App { public static class A { public static int Go() => Third.T.V(); } }",
            keyPath,
            references: [third]);
        ProcessAppRunner.WriteRuntimeConfig(Path.Combine(_source, "app.dll"));

        InstrumentationResult result = Run(new InstrumentationConfiguration(), EmptyRuleSet());

        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        Assert.All(result.Assemblies, assembly => Assert.False(assembly.WasReSigned));
        Assert.Equal(StrongNameStatus.None, StrongNameInspector.Inspect(Path.Combine(_staging, "app.dll")).Status);
        Assert.Equal(StrongNameStatus.None, StrongNameInspector.Inspect(Path.Combine(_staging, "thirdparty.dll")).Status);
        Assert.Null(ReferenceTokenOf(Path.Combine(_staging, "app.dll"), "thirdparty"));
    }

    [Fact]
    public void PreservesStrongIdentityReferencedByCopiedAssembly()
    {
        string keyPath = WriteKey();
        string dependency = Compile(
            "dependency",
            "namespace Dependency { public static class Value { public static int Get() => 1; } }",
            keyPath);
        Compile(
            "app",
            "namespace App { public static class Entry { public static int Get() => Dependency.Value.Get(); } }",
            keyPath,
            references: [dependency]);
        ProcessAppRunner.WriteRuntimeConfig(Path.Combine(_source, "app.dll"));

        InstrumentationResult result = Run(
            new InstrumentationConfiguration { IncludePatterns = ["dependency.dll"] },
            EmptyRuleSet());

        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        string stagedDependency = Path.Combine(_staging, "dependency.dll");
        Assert.Equal(StrongNameStatus.StrongNameSigned, StrongNameInspector.Inspect(stagedDependency).Status);
        Assert.Equal(
            StrongNameInspector.Inspect(stagedDependency).PublicKeyToken,
            ReferenceTokenOf(Path.Combine(_staging, "app.dll"), "dependency"));
    }

    private InstrumentationResult Run(
        InstrumentationConfiguration configuration,
        RewriteRuleSet ruleSet,
        string? manifestPath = null,
        string? cachePath = null)
    {
        InstrumentationRequest request = CreateRequest(configuration, ruleSet);
        if (manifestPath is not null)
        {
            request = request with { ManifestPath = manifestPath };
        }

        if (cachePath is not null)
        {
            request = request with { CachePath = cachePath };
        }

        return InstrumentationRunner.Run(request);
    }

    private InstrumentationRequest CreateRequest(
        InstrumentationConfiguration? configuration = null,
        RewriteRuleSet? ruleSet = null) =>
        new()
        {
            SourceDirectory = _source,
            StagingDirectory = _staging,
            Configuration = configuration ?? new InstrumentationConfiguration(),
            RuleSet = ruleSet ?? EmptyRuleSet(),
        };

    private string CachePath() =>
        Path.GetFullPath(_staging).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + ".cache";

    private string ReadIncrementalKey() =>
        (string)JsonNode.Parse(File.ReadAllText(CachePath()))!["incrementalKey"]!;

    private static string ToWindowsDevicePath(string ordinaryPath, string devicePathKind)
    {
        string fullPath = Path.GetFullPath(ordinaryPath);
        string root = Path.GetPathRoot(fullPath)
            ?? throw new InvalidOperationException($"Path '{ordinaryPath}' has no root.");
        if (root.Length < 2 || root[1] != ':')
        {
            throw new InvalidOperationException($"Path '{ordinaryPath}' is not rooted on a Windows drive.");
        }

        string forwardPath = fullPath.Replace('\\', '/');
        string uncPath = $@"localhost\{root[0]}$\{fullPath[root.Length..]}";
        string forwardUncPath = uncPath.Replace('\\', '/');
        return devicePathKind switch
        {
            "extended" => @"\\?\" + fullPath,
            "extended-forward" => "//?/" + forwardPath,
            "extended-forward-normalized" => Path.GetFullPath("//?/" + forwardPath),
            "device" => @"\\.\" + fullPath,
            "device-forward" => "//./" + forwardPath,
            "device-forward-normalized" => Path.GetFullPath("//./" + forwardPath),
            "nt" => @"\??\" + fullPath,
            "nt-normalized" => Path.GetFullPath(@"\??\" + fullPath),
            "extended-unc" => @"\\?\UNC\" + uncPath,
            "extended-unc-forward" => "//?/UNC/" + forwardUncPath,
            "extended-unc-forward-normalized" => Path.GetFullPath("//?/UNC/" + forwardUncPath),
            "device-unc" => @"\\.\UNC\" + uncPath,
            "device-unc-forward" => "//./UNC/" + forwardUncPath,
            "device-unc-forward-normalized" => Path.GetFullPath("//./UNC/" + forwardUncPath),
            "nt-unc" => @"\??\UNC\" + uncPath,
            "nt-unc-normalized" => Path.GetFullPath(@"\??\UNC\" + uncPath),
            _ => throw new InvalidOperationException($"Unknown Windows device path kind '{devicePathKind}'."),
        };
    }

    private Dictionary<string, byte[]> SnapshotRootFiles() =>
        Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetRelativePath(_root, path),
                File.ReadAllBytes,
                StringComparer.OrdinalIgnoreCase);

    private static void AssertFileSnapshot(
        IReadOnlyDictionary<string, byte[]> expected,
        IReadOnlyDictionary<string, byte[]> actual)
    {
        Assert.Equal(
            expected.Keys.OrderBy(static path => path, StringComparer.OrdinalIgnoreCase),
            actual.Keys.OrderBy(static path => path, StringComparer.OrdinalIgnoreCase));
        foreach ((string path, byte[] contents) in expected)
        {
            Assert.Equal(contents, actual[path]);
        }
    }

    private void BuildStandardClosure()
    {
        string third = Compile("thirdparty", "namespace Third { public static class T { public static int V() => 1; } }");
        Compile(
            "app",
            "namespace App { public static class A { public static int Go() => Third.T.V(); } }",
            references: [third]);
        Compile("System.Fake", "namespace SysFake { public static class C { public static int V() => 1; } }");

        File.WriteAllBytes(Path.Combine(_source, "native.dll"), [0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07]);
        File.WriteAllBytes(Path.Combine(_source, "symbols.pdb"), [0x08, 0x09, 0x0a, 0x0b]);
        File.WriteAllText(Path.Combine(_source, "app.deps.json"), "{}");
        ProcessAppRunner.WriteRuntimeConfig(Path.Combine(_source, "app.dll"));
    }

    private void BuildMinimalApp()
    {
        Compile("app", "namespace App { public static class A { public static int Go() => 1; } }");
        ProcessAppRunner.WriteRuntimeConfig(Path.Combine(_source, "app.dll"));
    }

    private static RewriteRuleSet EmptyRuleSet() => new("clockwork.test", "1.0", []);

    // A rule for an API the closure never calls, classified for one runtime.
    private static RewriteRuleSet RuleSetFor(Version runtime) => new(
        "clockwork.test",
        "1.0",
        [
            RewriteRule.RedirectCall(
                "unused",
                MemberSignature.Method("System.DateTime", "get_Now"),
                RewriteReplacement.Method("Shims", "Shims.Clock", "Now")) with
            {
                SupportedRuntimes = new RuntimeVersionRange(runtime, runtime),
            },
        ]);

    private IEnumerable<string> EnumerateStagedPaths() =>
        Directory.EnumerateFiles(_staging, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(_staging, path)
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace(Path.AltDirectorySeparatorChar, '/'))
            .OrderBy(static path => path, StringComparer.Ordinal);

    private static void AssertCopiedAsset(
        ClosureManifest manifest,
        string relativePath,
        string expectedContentsPath)
    {
        ClosureManifestCopiedAsset asset =
            Assert.Single(manifest.CopiedAssets, candidate => candidate.RelativePath == relativePath);
        Assert.Equal(
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(expectedContentsPath))),
            asset.Sha256);
    }

    private string Compile(string name, string source, string? keyPath = null, IEnumerable<string>? references = null) =>
        FixtureCompiler.Compile(
            name, source, _source, FixtureSymbols.PortableFile, optimize: false,
            additionalReferencePaths: references, strongNameKeyFile: keyPath);

    private string WriteKey(string fileName = "test.snk")
    {
        string keyPath = Path.Combine(_root, fileName);
        File.WriteAllBytes(keyPath, StrongNameKeys.CreatePrivateKeyBlob());
        return keyPath;
    }

    private static bool TryCreateDirectoryLink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or PlatformNotSupportedException
                or NotSupportedException)
        {
            return false;
        }
    }

    private static string ToLocalAdministrativeShare(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string root = Path.GetPathRoot(fullPath)
            ?? throw new InvalidOperationException($"Path '{path}' has no filesystem root.");
        if (root.Length < 2 || root[1] != ':')
        {
            throw new InvalidOperationException($"Path '{path}' is not on a Windows drive.");
        }

        return $@"\\localhost\{char.ToLowerInvariant(root[0])}$\{fullPath[root.Length..]}";
    }

    private static string? TryCreateSubstDrive(string targetPath)
    {
        string? drive = FindAvailableDriveRoot();
        if (drive is null)
        {
            return null;
        }

        bool created = NativeMethods.DefineDosDevice(0, drive[..2], Path.GetFullPath(targetPath));
        if (created && Directory.Exists(drive))
        {
            return drive;
        }

        if (created)
        {
            RemoveSubstDrive(drive);
        }

        return null;
    }

    private static void RemoveSubstDrive(string drive)
    {
        _ = NativeMethods.DefineDosDevice(0x00000002, drive[..2], null);
    }

    private static string? TryCreateMappedDrive(string remotePath)
    {
        string? drive = FindAvailableDriveRoot();
        if (drive is null)
        {
            return null;
        }

        var resource = new NativeMethods.NetResource
        {
            ResourceType = 1,
            LocalName = drive[..2],
            RemoteName = remotePath,
        };
        int result = NativeMethods.WNetAddConnection2(ref resource, null, null, 0);
        if (result == 0 && Directory.Exists(drive))
        {
            return drive;
        }

        if (result == 0)
        {
            RemoveMappedDrive(drive);
        }

        return null;
    }

    private static void RemoveMappedDrive(string drive)
    {
        _ = NativeMethods.WNetCancelConnection2(drive[..2], 0, force: true);
    }

    private static string? FindAvailableDriveRoot()
    {
        for (char letter = 'Z'; letter >= 'D'; letter--)
        {
            string candidate = $"{letter}:\\";
            if (!Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? ReferenceTokenOf(string assemblyPath, string referenceName)
    {
        using AssemblyDefinition definition = AssemblyDefinition.ReadAssembly(
            assemblyPath, new ReaderParameters { ReadSymbols = false, InMemory = true });
        AssemblyNameReference reference = definition.MainModule.AssemblyReferences
            .Single(r => string.Equals(r.Name, referenceName, StringComparison.Ordinal));
        return StrongNameInspector.FormatToken(reference.PublicKeyToken)!;
    }

    private static string? FindReadyToRunAssembly()
    {
        string trusted = (string)(AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty);
        foreach (string path in trusted.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            {
                continue;
            }

            try
            {
                if (AssemblyImageInfo.Inspect(path).IsReadyToRun)
                {
                    return path;
                }
            }
            catch (BadImageFormatException)
            {
            }
        }

        return null;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct NetResource
        {
            internal uint Scope;
            internal uint ResourceType;
            internal uint DisplayType;
            internal uint Usage;
            internal string? LocalName;
            internal string? RemoteName;
            internal string? Comment;
            internal string? Provider;
        }

        [DllImport(
            "kernel32.dll",
            EntryPoint = "CreateHardLinkW",
            CharSet = CharSet.Unicode,
            ExactSpelling = true,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreateHardLink(
            string fileName,
            string existingFileName,
            IntPtr securityAttributes);

        [DllImport(
            "kernel32.dll",
            EntryPoint = "DefineDosDeviceW",
            CharSet = CharSet.Unicode,
            ExactSpelling = true,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DefineDosDevice(
            uint flags,
            string deviceName,
            string? targetPath);

        [DllImport(
            "mpr.dll",
            EntryPoint = "WNetAddConnection2W",
            CharSet = CharSet.Unicode,
            ExactSpelling = true,
            SetLastError = true)]
        internal static extern int WNetAddConnection2(
            ref NetResource netResource,
            string? password,
            string? userName,
            uint flags);

        [DllImport(
            "mpr.dll",
            EntryPoint = "WNetCancelConnection2W",
            CharSet = CharSet.Unicode,
            ExactSpelling = true,
            SetLastError = true)]
        internal static extern int WNetCancelConnection2(
            string name,
            uint flags,
            [MarshalAs(UnmanagedType.Bool)] bool force);
    }
}
