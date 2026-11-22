using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Orleans.Testing.Simulation.Instrumentation.Closure;
using Orleans.Testing.Simulation.Instrumentation.Configuration;
using Orleans.Testing.Simulation.Instrumentation.Diagnostics;
using Orleans.Testing.Simulation.Instrumentation.Imaging;
using Orleans.Testing.Simulation.Instrumentation.Rewriting;
using Orleans.Testing.Simulation.Instrumentation.Rules;
using Orleans.Testing.Simulation.Instrumentation.Rules.BuiltIn;
using Orleans.Testing.Simulation.Instrumentation.Signing;
using Mono.Cecil;

namespace Orleans.Testing.Simulation.Instrumentation.Orchestration;

/// <summary>
/// The deterministic orchestrator that turns an application output/publish directory into an
/// instrumented closure staged in a separate directory. It discovers the closure, strips ReadyToRun
/// inputs to IL, strips rewritten strong-name identities consistently, rewrites managed IL with the
/// <see cref="RewriteEngine"/>, copies every non-rewritten asset verbatim, emits a deterministic
/// closure manifest, and maintains an incremental cache keyed by every input's content hash plus the
/// engine, rule-set, and configuration signatures. The source directory is never modified.
/// </summary>
public static class InstrumentationRunner
{
    /// <summary>Runs the instrumentation over the closure described by <paramref name="request"/>.</summary>
    /// <param name="request">The instrumentation request.</param>
    /// <returns>The deterministic outcome, including per-assembly results and diagnostics.</returns>
    /// <exception cref="ClosureException">
    /// The closure is invalid, or source, staging, manifest, and cache paths violate the runner's
    /// isolation requirements.
    /// </exception>
    public static InstrumentationResult Run(InstrumentationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        string sourceDirectory = NormalizeDirectoryPath(
            request.SourceDirectory,
            nameof(InstrumentationRequest.SourceDirectory));
        string stagingDirectory = NormalizeDirectoryPath(
            request.StagingDirectory,
            nameof(InstrumentationRequest.StagingDirectory));
        string manifestPath = NormalizeMetadataPath(
            request.ManifestPathOverride ?? stagingDirectory + ".manifest.json",
            nameof(InstrumentationRequest.ManifestPath));
        string cachePath = NormalizeMetadataPath(
            request.CachePathOverride ?? stagingDirectory + ".cache",
            nameof(InstrumentationRequest.CachePath));
        InstrumentationConfiguration configuration = NormalizeConfigurationPaths(request.Configuration);
        ValidateRequestPathIsolation(
            sourceDirectory,
            stagingDirectory,
            manifestPath,
            cachePath,
            configuration);
        ValidateStagingDirectory(sourceDirectory, stagingDirectory);
        ValidateMetadataLocations(
            sourceDirectory,
            stagingDirectory,
            manifestPath,
            cachePath);
        ValidateProtectedInputLocations(
            stagingDirectory,
            manifestPath,
            cachePath,
            configuration);
        request = request with
        {
            SourceDirectory = sourceDirectory,
            StagingDirectory = stagingDirectory,
            ManifestPath = manifestPath,
            CachePath = cachePath,
            Configuration = configuration,
        };

        // The one classifier that keeps framework assemblies out of the closure's rewrite also excludes them from the
        // staged binding check, since nothing they reference is rewritten.
        FrameworkAssemblyClassifier frameworkClassifier = FrameworkAssemblyClassifier.Default;
        ClosurePlan plan = ClosureDiscovery.Discover(
            sourceDirectory,
            configuration,
            request.EntryAssemblyName,
            frameworkClassifier);
        ValidateClosurePlanPaths(plan, configuration, stagingDirectory, manifestPath);
        ValidateRequestPathIsolation(
            sourceDirectory,
            stagingDirectory,
            manifestPath,
            cachePath,
            configuration);
        ValidateStagingDirectory(sourceDirectory, stagingDirectory);
        ValidateMetadataLocations(
            sourceDirectory,
            stagingDirectory,
            manifestPath,
            cachePath);
        ValidateProtectedInputLocations(
            stagingDirectory,
            manifestPath,
            cachePath,
            configuration);

        Version targetRuntime;
        string targetRuntimeOrigin;
        try
        {
            (targetRuntime, targetRuntimeOrigin) = TargetRuntimeDiscovery.Resolve(configuration, plan);
        }
        catch (ClosureException)
        {
            DeleteIfExists(request.CachePath);
            throw;
        }

        configuration = configuration with { TargetRuntime = targetRuntime };
        request = request with { Configuration = configuration };

        // A rule set that does not support the target fails once, before anything is staged or rewritten.
        RuleSetRuntimeSupport runtimeSupport = RuleSetRuntimeSupport.Evaluate(request.RuleSet, targetRuntime);
        if (!runtimeSupport.IsSupported)
        {
            DeleteIfExists(request.CachePath);
            return new InstrumentationResult
            {
                Succeeded = false,
                WasIncrementalHit = false,
                TargetRuntime = targetRuntime,
                TargetRuntimeOrigin = targetRuntimeOrigin,
                StagingDirectory = stagingDirectory,
                ManifestPath = request.ManifestPath,
                Diagnostics = [runtimeSupport.ToDiagnostic(targetRuntimeOrigin)],
            };
        }

        var topLevel = new List<RewriteDiagnostic>();

        string incrementalKey;
        try
        {
            incrementalKey = ComputeIncrementalKey(plan, configuration, request.RuleSet);
        }
        catch (ClosureException)
        {
            DeleteIfExists(request.CachePath);
            throw;
        }

        // Incremental short-circuit: identical inputs and a verified staged closure already exist.
        if (IsValidIncrementalHit(
                request,
                stagingDirectory,
                plan,
                configuration,
                incrementalKey))
        {
            return new InstrumentationResult
            {
                Succeeded = true,
                WasIncrementalHit = true,
                TargetRuntime = targetRuntime,
                TargetRuntimeOrigin = targetRuntimeOrigin,
                StagingDirectory = stagingDirectory,
                ManifestPath = request.ManifestPath,
                Diagnostics = [.. topLevel],
            };
        }

        DeleteIfExists(request.CachePath);

        ImmutableArray<string> replacementPaths =
            ResolveReplacementAssemblies(sourceDirectory, request.RuleSet, configuration);
        HashSet<string> replacementNames = ResolveReplacementClosureNames(sourceDirectory, replacementPaths);
        (ImmutableArray<string> rewrittenAssemblyNames, ImmutableArray<string> rewrittenStrongNames) =
            DiscoverRewrittenAssemblyNames(plan, replacementNames);
        HashSet<string> protectedStrongNames = DiscoverCopiedStrongNameReferences(
            plan,
            replacementNames,
            rewrittenStrongNames.ToHashSet(StringComparer.Ordinal));
        ImmutableArray<string> strongNameAssemblyNames =
            [.. rewrittenStrongNames.Where(name => !protectedStrongNames.Contains(name))];

        PrepareStagingDirectory(stagingDirectory);

        var copied = new List<string>();
        foreach (ClosureAsset asset in plan.AssetsToCopy)
        {
            string destination = ToStagingPath(stagingDirectory, asset.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(asset.SourcePath, destination, overwrite: true);
            copied.Add(asset.RelativePath);
        }

        bool containsControlledTaskRules = BuiltInRuleSets.ContainsControlledTaskRules(request.RuleSet);
        var options = new RewriteOptions
        {
            ReplacementAssemblyPaths = replacementPaths,
            ReferenceSearchDirectories = [sourceDirectory],
            TargetRuntime = targetRuntime,
            HardenExceptionHandlers = containsControlledTaskRules,
            InstrumentRaceExploration = configuration.Mode == InstrumentationMode.RaceExploration,
            StrongNameAssemblyNames = strongNameAssemblyNames,
            RewrittenAssemblyNames = rewrittenAssemblyNames,
        };

        var assemblyResults = new List<AssemblyInstrumentationResult>();
        foreach (ClosureAsset asset in plan.AssembliesToRewrite)
        {
            // A rule set's replacement ("shim") assemblies are inputs to the rewrite, not targets of
            // it: rewriting a shim would corrupt it (e.g. redirect its own calls into itself). Copy
            // them verbatim so the staged closure can resolve the redirects at runtime.
            if (replacementNames.Contains(Path.GetFileNameWithoutExtension(asset.RelativePath)))
            {
                string replacementDestination = ToStagingPath(stagingDirectory, asset.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(replacementDestination)!);
                File.Copy(asset.SourcePath, replacementDestination, overwrite: true);
                copied.Add(asset.RelativePath);
                continue;
            }

            assemblyResults.Add(ProcessAssembly(
                asset,
                sourceDirectory,
                stagingDirectory,
                request.CachePath,
                configuration,
                request.RuleSet,
                options));
        }

        if (configuration.Mode == InstrumentationMode.RaceExploration)
        {
            const string runtimeAsset = "Orleans.Testing.Simulation.dll";
            File.Copy(
                typeof(Runtime.Racing.RaceInstrumentation).Assembly.Location,
                Path.Combine(stagingDirectory, runtimeAsset),
                overwrite: true);
            if (!copied.Contains(runtimeAsset, StringComparer.Ordinal))
            {
                copied.Add(runtimeAsset);
            }
        }

        if (!assemblyResults.SelectMany(a => a.Errors).Any())
        {
            var excludedTypesByAssembly = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (Manifest.InstrumentationManifest rewritten in assemblyResults.Select(a => a.Manifest).OfType<Manifest.InstrumentationManifest>())
            {
                excludedTypesByAssembly[rewritten.Input.Name] = rewritten.Exclusions
                    .Select(exclusion => exclusion.TypeFullName)
                    .ToHashSet(StringComparer.Ordinal);
            }

            topLevel.AddRange(ClosureBindingValidator.Validate(
                plan,
                stagingDirectory,
                frameworkClassifier,
                runtimeSupport,
                rewrittenAssemblyNames,
                excludedTypesByAssembly));
        }

        bool succeeded = topLevel.All(d => !d.IsError) && !assemblyResults.SelectMany(a => a.Errors).Any();

        ClosureManifest manifest = BuildManifest(
            plan,
            configuration,
            request.RuleSet,
            stagingDirectory,
            incrementalKey,
            assemblyResults,
            copied);
        string manifestJson = manifest.ToJson();
        byte[] manifestUtf8 = Encoding.UTF8.GetBytes(manifestJson);
        WriteAllTextAtomically(request.ManifestPath, manifestJson);

        if (succeeded)
        {
            var cacheRecord = new IncrementalCacheRecord(
                incrementalKey,
                HashBytes(manifestUtf8));
            WriteAllTextAtomically(request.CachePath, cacheRecord.ToJson());
        }

        return new InstrumentationResult
        {
            Succeeded = succeeded,
            WasIncrementalHit = false,
            TargetRuntime = targetRuntime,
            TargetRuntimeOrigin = targetRuntimeOrigin,
            StagingDirectory = stagingDirectory,
            ManifestPath = request.ManifestPath,
            Assemblies = [.. assemblyResults],
            CopiedAssets = [.. copied],
            Diagnostics = [.. topLevel],
        };
    }

    private static AssemblyInstrumentationResult ProcessAssembly(
        ClosureAsset asset,
        string sourceDirectory,
        string stagingDirectory,
        string cachePath,
        InstrumentationConfiguration configuration,
        Rules.RewriteRuleSet ruleSet,
        RewriteOptions options)
    {
        string inputPath = asset.SourcePath;
        string outputPath = ToStagingPath(stagingDirectory, asset.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var diagnostics = new List<RewriteDiagnostic>();

        AssemblyImageInfo image;
        try
        {
            image = AssemblyImageInfo.Inspect(inputPath);
        }
        catch (BadImageFormatException ex)
        {
            diagnostics.Add(RewriteDiagnostic.Error(
                RewriteDiagnosticIds.ValidationFailed,
                $"'{asset.RelativePath}' is not a valid PE image: {ex.Message}"));
            return new AssemblyInstrumentationResult(asset.RelativePath, false, false, false, false, null, [.. diagnostics]);
        }

        if (!image.IsManagedAssembly)
        {
            diagnostics.Add(RewriteDiagnostic.Error(
                RewriteDiagnosticIds.ValidationFailed,
                $"'{asset.RelativePath}' is native-only and does not contain usable managed IL."));
            return new AssemblyInstrumentationResult(asset.RelativePath, false, false, false, false, null, [.. diagnostics]);
        }

        // ReadyToRun images clear the ILOnly CLI flag, so they must be classified before the
        // mixed-mode check (which keys off ILOnly) or a genuine R2R image would be misreported.
        bool readyToRunStripped = image.IsReadyToRun;
        string engineInput = inputPath;
        string engineOutput = outputPath;
        string? temporaryDirectory = null;
        try
        {
            if (readyToRunStripped)
            {
                diagnostics.Add(RewriteDiagnostic.Info(
                    RewriteDiagnosticIds.ReadyToRunStripped,
                    $"'{asset.RelativePath}' is ReadyToRun; the native image is stripped and IL-only output is produced."));
                temporaryDirectory = CreateReadyToRunTemporaryDirectory(
                    sourceDirectory,
                    stagingDirectory,
                    cachePath);
                engineInput = Path.Combine(temporaryDirectory, "input", Path.GetFileName(outputPath));
                engineOutput = Path.Combine(temporaryDirectory, "output", Path.GetFileName(outputPath));
                Directory.CreateDirectory(Path.GetDirectoryName(engineInput)!);
                Directory.CreateDirectory(Path.GetDirectoryName(engineOutput)!);
                try
                {
                    ReadyToRunStripper.StripToIL(inputPath, engineInput);
                }
                catch (Exception ex) when (ex is BadImageFormatException or IOException)
                {
                    diagnostics.Add(RewriteDiagnostic.Error(
                        RewriteDiagnosticIds.ValidationFailed,
                        $"'{asset.RelativePath}' is ReadyToRun but does not contain usable managed IL: {ex.Message}"));
                    return new AssemblyInstrumentationResult(
                        asset.RelativePath, false, false, false, readyToRunStripped, null, [.. diagnostics]);
                }
            }
            else if (image.IsMixedMode)
            {
                diagnostics.Add(RewriteDiagnostic.Error(
                    RewriteDiagnosticIds.MixedModeAssembly,
                    $"'{asset.RelativePath}' is a mixed-mode assembly, which cannot be rewritten. Exclude it or build a pure-IL variant."));
                return new AssemblyInstrumentationResult(asset.RelativePath, false, false, false, false, null, [.. diagnostics]);
            }

            if (image.HasAuthenticodeSignature)
            {
                diagnostics.Add(RewriteDiagnostic.Warning(
                    RewriteDiagnosticIds.AuthenticodeDropped,
                    $"'{asset.RelativePath}' carries an Authenticode signature, which cannot be preserved across a rewrite and is dropped. Re-sign the staged output out of band if required."));
            }

            StrongNameInfo strongName = StrongNameInspector.Inspect(inputPath);
            if (strongName.HasPublicKey &&
                options.StrongNameAssemblyNames.Contains(
                    System.Reflection.AssemblyName.GetAssemblyName(inputPath).Name!,
                    StringComparer.Ordinal))
            {
                diagnostics.Add(RewriteDiagnostic.Info(
                    RewriteDiagnosticIds.StrongNameStripped,
                    $"'{asset.RelativePath}' is strong-named ({strongName.Status}, token {strongName.PublicKeyToken}); its rewritten test identity and closure references are stripped automatically."));
            }

            RewriteResult rewrite = RewriteEngine.Rewrite(
                new RewriteRequest(engineInput, engineOutput, ruleSet, options));
            diagnostics.AddRange(rewrite.Diagnostics);

            if (!rewrite.Succeeded)
            {
                CopyReadyToRunOutputIntoStaging(engineOutput, outputPath, temporaryDirectory);
                return new AssemblyInstrumentationResult(
                    asset.RelativePath, rewrite.WasWritten, rewrite.WasNoOp, false, readyToRunStripped, rewrite.Manifest, [.. diagnostics]);
            }

            CopyReadyToRunOutputIntoStaging(engineOutput, outputPath, temporaryDirectory);
            return new AssemblyInstrumentationResult(
                asset.RelativePath, rewrite.WasWritten, rewrite.WasNoOp, false, readyToRunStripped, rewrite.Manifest, [.. diagnostics]);
        }
        finally
        {
            DeleteReadyToRunTemporaryDirectory(temporaryDirectory);
        }
    }

    private static ImmutableArray<string> ResolveReplacementAssemblies(
        string sourceDirectory,
        Rules.RewriteRuleSet ruleSet,
        InstrumentationConfiguration configuration)
    {
        var paths = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string name in ruleSet.Rules
            .SelectMany(r => r.BaseCallReplacement is { } baseCall
                ? [r.Replacement.AssemblyName, baseCall.AssemblyName]
                : new[] { r.Replacement.AssemblyName })
            .Where(n => !string.IsNullOrEmpty(n))
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string candidate = Path.Combine(sourceDirectory, name + ".dll");
            if (File.Exists(candidate))
            {
                paths.Add(candidate);
            }

        }

        if (configuration.Mode == InstrumentationMode.RaceExploration)
        {
            paths.Add(typeof(Runtime.Racing.RaceInstrumentation).Assembly.Location);
        }

        return [.. paths];
    }

    // The assemblies the closure rewrites - every rewrite candidate except the replacement assemblies and their
    // dependencies, which are copied - and those of them that are strong-named.
    private static (ImmutableArray<string> Names, ImmutableArray<string> StrongNamed) DiscoverRewrittenAssemblyNames(
        ClosurePlan plan,
        HashSet<string> replacementNames)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        var strongNamed = new SortedSet<string>(StringComparer.Ordinal);
        foreach (ClosureAsset asset in plan.AssembliesToRewrite)
        {
            string fileName = Path.GetFileNameWithoutExtension(asset.RelativePath);
            if (replacementNames.Contains(fileName))
            {
                continue;
            }

            using AssemblyDefinition definition = AssemblyDefinition.ReadAssembly(
                asset.SourcePath,
                new ReaderParameters { ReadSymbols = false, InMemory = true });
            names.Add(definition.Name.Name);
            if (definition.Name.HasPublicKey)
            {
                strongNamed.Add(definition.Name.Name);
            }
        }

        return ([.. names], [.. strongNamed]);
    }

    private static HashSet<string> DiscoverCopiedStrongNameReferences(
        ClosurePlan plan,
        HashSet<string> replacementNames,
        HashSet<string> strippedAssemblyNames)
    {
        var protectedNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (ClosureAsset asset in plan.Assets)
        {
            bool copiedManagedAssembly =
                asset.Kind == AssetKind.ManagedAssembly &&
                (!asset.Rewrite || replacementNames.Contains(Path.GetFileNameWithoutExtension(asset.RelativePath)));
            if (!copiedManagedAssembly)
            {
                continue;
            }

            using AssemblyDefinition definition = AssemblyDefinition.ReadAssembly(
                asset.SourcePath,
                new ReaderParameters { ReadSymbols = false, InMemory = true });
            foreach (AssemblyNameReference reference in definition.MainModule.AssemblyReferences)
            {
                if (!strippedAssemblyNames.Contains(reference.Name) ||
                    reference.PublicKeyToken is not { Length: > 0 })
                {
                    continue;
                }

                protectedNames.Add(reference.Name);
            }
        }

        return protectedNames;
    }

    private static HashSet<string> ResolveReplacementClosureNames(
        string sourceDirectory,
        ImmutableArray<string> replacementPaths)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inspectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>(replacementPaths);
        while (pending.TryPop(out string? path))
        {
            path = InstrumentationPath.GetFullPath(path, "replacement assembly path");
            if (!inspectedPaths.Add(path))
            {
                continue;
            }

            using AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(
                path,
                new ReaderParameters { ReadSymbols = false, InMemory = true });
            names.Add(assembly.Name.Name);
            foreach (AssemblyNameReference reference in assembly.MainModule.AssemblyReferences)
            {
                names.Add(reference.Name);
                string candidate = Path.Combine(sourceDirectory, reference.Name + ".dll");
                if (!File.Exists(candidate))
                {
                    candidate = Path.Combine(Path.GetDirectoryName(path)!, reference.Name + ".dll");
                }

                if (File.Exists(candidate))
                {
                    pending.Push(candidate);
                }
            }
        }

        return names;
    }

    private static ClosureManifest BuildManifest(
        ClosurePlan plan,
        InstrumentationConfiguration configuration,
        Rules.RewriteRuleSet ruleSet,
        string stagingDirectory,
        string incrementalKey,
        IReadOnlyList<AssemblyInstrumentationResult> assemblyResults,
        IReadOnlyList<string> copied)
    {
        var entries = assemblyResults.Select(a => new ClosureManifestEntry(
            a.RelativePath,
            a.WasRewritten,
            a.WasNoOp,
            a.WasReSigned,
            a.ReadyToRunStripped,
            a.Manifest?.Input.Sha256,
            TryHashCacheOutputFile(ToStagingPath(stagingDirectory, a.RelativePath)),
            a.Errors.Count())).ToImmutableArray();

        return new ClosureManifest
        {
            EngineVersion = RewriteEngine.EngineVersion,
            RuleSetId = ruleSet.Id,
            RuleSetVersion = ruleSet.Version,
            RuleSetSignature = ruleSet.ComputeSignature(),
            ConfigurationSignature = configuration.ComputeSignature(),
            Mode = configuration.Mode,
            IncrementalKey = incrementalKey,
            EntryRelativePath = plan.EntryAssemblyRelativePath,
            Assemblies = entries,
            CopiedAssets =
            [
                .. copied
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(static path => path, StringComparer.Ordinal)
                    .Select(path => new ClosureManifestCopiedAsset(
                        path,
                        TryHashCacheOutputFile(ToStagingPath(stagingDirectory, path))
                            ?? throw new ClosureException(
                                $"Copied asset '{path}' is missing or unreadable in the staged closure."))),
            ],
        };
    }

    private static bool IsValidIncrementalHit(
        InstrumentationRequest request,
        string stagingDirectory,
        ClosurePlan plan,
        InstrumentationConfiguration configuration,
        string incrementalKey)
    {
        try
        {
            if (!Directory.Exists(stagingDirectory)
                || !IncrementalCacheRecord.TryRead(
                    request.CachePath,
                    out IncrementalCacheRecord cacheRecord)
                || !string.Equals(
                    cacheRecord.IncrementalKey,
                    incrementalKey,
                    StringComparison.Ordinal)
                || !TryReadManifestBytes(request.ManifestPath, out byte[] manifestUtf8)
                || !string.Equals(
                    cacheRecord.ManifestSha256,
                    HashBytes(manifestUtf8),
                    StringComparison.Ordinal))
            {
                return false;
            }

            ClosureManifest manifest = ClosureManifestJson.Deserialize(manifestUtf8);
            if (!string.Equals(manifest.EngineVersion, RewriteEngine.EngineVersion, StringComparison.Ordinal)
                || !string.Equals(manifest.RuleSetId, request.RuleSet.Id, StringComparison.Ordinal)
                || !string.Equals(manifest.RuleSetVersion, request.RuleSet.Version, StringComparison.Ordinal)
                || !string.Equals(
                    manifest.RuleSetSignature,
                    request.RuleSet.ComputeSignature(),
                    StringComparison.Ordinal)
                || !string.Equals(
                    manifest.ConfigurationSignature,
                    configuration.ComputeSignature(),
                    StringComparison.Ordinal)
                || manifest.Mode != configuration.Mode
                || !string.Equals(manifest.IncrementalKey, incrementalKey, StringComparison.Ordinal)
                || !string.Equals(
                    manifest.EntryRelativePath,
                    plan.EntryAssemblyRelativePath,
                    StringComparison.Ordinal))
            {
                return false;
            }

            var representedPaths = new HashSet<string>(ClosurePathComparer);
            foreach (ClosureManifestEntry assembly in manifest.Assemblies)
            {
                if (assembly.ErrorCount != 0
                    || !TryResolveStagedPath(
                        stagingDirectory,
                        assembly.RelativePath,
                        out string? stagedPath,
                        out string? normalizedPath)
                    || !representedPaths.Add(normalizedPath))
                {
                    return false;
                }

                string? expectedHash = assembly.OutputSha256 ?? assembly.InputSha256;
                if (expectedHash is null
                    || !string.Equals(TryHashCacheOutputFile(stagedPath), expectedHash, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            foreach (ClosureManifestCopiedAsset asset in manifest.CopiedAssets)
            {
                if (!TryResolveStagedPath(
                        stagingDirectory,
                        asset.RelativePath,
                        out string? stagedPath,
                        out string? normalizedPath)
                    || !representedPaths.Add(normalizedPath)
                    || !string.Equals(
                        TryHashCacheOutputFile(stagedPath),
                        asset.Sha256,
                        StringComparison.Ordinal))
                {
                    return false;
                }
            }

            var expectedPaths = new HashSet<string>(ClosurePathComparer);
            foreach (ClosureAsset asset in plan.Assets)
            {
                if (!TryResolveStagedPath(
                        stagingDirectory,
                        asset.RelativePath,
                        out _,
                        out string? normalizedPath)
                    || !expectedPaths.Add(normalizedPath))
                {
                    return false;
                }
            }

            if (configuration.Mode == InstrumentationMode.RaceExploration)
            {
                expectedPaths.Add("Orleans.Testing.Simulation.dll");
            }

            if (!expectedPaths.SetEquals(representedPaths))
            {
                return false;
            }

            var expectedStagedPaths = new HashSet<string>(representedPaths, ClosurePathComparer);
            string manifestPath = InstrumentationPath.GetFullPath(
                request.ManifestPath,
                nameof(InstrumentationRequest.ManifestPath));
            string stagingRoot = stagingDirectory;
            if (IsSameOrDescendant(manifestPath, stagingRoot)
                && (!TryNormalizeStagedPath(
                        stagingRoot,
                        manifestPath,
                        out _,
                        out string? manifestRelativePath)
                    || !expectedStagedPaths.Add(manifestRelativePath)))
            {
                return false;
            }

            var actualStagedPaths = new HashSet<string>(ClosurePathComparer);
            foreach (string file in Directory.EnumerateFiles(
                stagingRoot,
                "*",
                SearchOption.AllDirectories))
            {
                if (!TryNormalizeStagedPath(
                        stagingRoot,
                        file,
                        out _,
                        out string? actualRelativePath)
                    || !actualStagedPaths.Add(actualRelativePath))
                {
                    return false;
                }
            }

            return actualStagedPaths.SetEquals(expectedStagedPaths);
        }
        catch (Exception exception) when (
            exception is ClosureManifestFormatException
                or IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException)
        {
            return false;
        }
    }

    private static bool TryReadManifestBytes(string path, out byte[] bytes)
    {
        bytes = [];
        var info = new FileInfo(path);
        if (!info.Exists
            || info.Length is <= 0 or > ClosureManifestLimits.MaxDocumentBytes)
        {
            return false;
        }

        bytes = File.ReadAllBytes(path);
        return bytes.Length is > 0 and <= ClosureManifestLimits.MaxDocumentBytes;
    }

    private static bool TryResolveStagedPath(
        string stagingDirectory,
        string relativePath,
        out string stagedPath,
        out string normalizedPath)
    {
        stagedPath = string.Empty;
        normalizedPath = string.Empty;
        if (string.IsNullOrEmpty(relativePath) || Path.IsPathRooted(relativePath))
        {
            return false;
        }

        return TryNormalizeStagedPath(
            stagingDirectory,
            ToStagingPath(stagingDirectory, relativePath),
            out stagedPath,
            out normalizedPath);
    }

    private static bool TryNormalizeStagedPath(
        string stagingDirectory,
        string path,
        out string stagedPath,
        out string normalizedPath)
    {
        stagedPath = string.Empty;
        normalizedPath = string.Empty;
        string stagingRoot;
        string candidate;
        try
        {
            stagingRoot = InstrumentationPath.GetFullPath(
                stagingDirectory,
                nameof(InstrumentationRequest.StagingDirectory));
            candidate = InstrumentationPath.GetFullPath(path, "staged closure path");
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or NotSupportedException
                or IOException
                or UnauthorizedAccessException)
        {
            return false;
        }

        string canonicalStagingRoot = GetCanonicalIdentity(
            stagingRoot,
            nameof(InstrumentationRequest.StagingDirectory));
        string canonicalCandidate = GetCanonicalIdentity(candidate, "staged closure path");
        if (!IsSameOrDescendantCanonical(canonicalCandidate, canonicalStagingRoot)
            || string.Equals(canonicalCandidate, canonicalStagingRoot, PathComparison))
        {
            return false;
        }

        stagedPath = candidate;
        normalizedPath = NormalizeClosureRelativePath(
            Path.GetRelativePath(canonicalStagingRoot, canonicalCandidate));
        return true;
    }

    private static string ComputeIncrementalKey(
        ClosurePlan plan,
        InstrumentationConfiguration configuration,
        Rules.RewriteRuleSet ruleSet)
    {
        var canonical = new CanonicalEncoding("InstrumentationIncrementalKey");
        canonical.AddString("EngineVersion", RewriteEngine.EngineVersion);
        canonical.AddInt32("ManifestSchemaVersion", ClosureManifest.SchemaVersion);
        canonical.AddString("ConfigurationSignature", configuration.ComputeSignature());
        canonical.AddString("RuleSetId", ruleSet.Id);
        canonical.AddString("RuleSetVersion", ruleSet.Version);
        canonical.AddString("RuleSetSignature", ruleSet.ComputeSignature());
        canonical.AddString(
            "ConfigurationSourceSha256",
            configuration.SourcePath is null
                ? null
                : HashRequiredSourceFile(configuration.SourcePath, "Configuration source"));
        canonical.AddStringSequence(
            "RuleSetSources",
            configuration.RuleSetPaths.Select(static path =>
            {
                var source = new CanonicalEncoding("RuleSetSource");
                source.AddString("Path", path);
                source.AddString("Sha256", HashRequiredSourceFile(path, "Rule-set source"));
                return source.ToString();
            }));
        if (configuration.Mode == InstrumentationMode.RaceExploration)
        {
            canonical.AddString(
                "RaceRuntimeSha256",
                HashRequiredSourceFile(
                    typeof(Runtime.Racing.RaceInstrumentation).Assembly.Location,
                    "Race-exploration runtime"));
        }
        else
        {
            canonical.AddString("RaceRuntimeSha256", null);
        }

        canonical.AddStringSequence(
            "Assets",
            plan.Assets.Select(static asset =>
            {
                var encodedAsset = new CanonicalEncoding(nameof(ClosureAsset));
                encodedAsset.AddString(nameof(ClosureAsset.RelativePath), asset.RelativePath);
                encodedAsset.AddBoolean(nameof(ClosureAsset.Rewrite), asset.Rewrite);
                encodedAsset.AddString(
                    "SourceSha256",
                    HashRequiredSourceFile(
                        asset.SourcePath,
                        $"Planned input asset '{asset.RelativePath}'"));
                return encodedAsset.ToString();
            }));

        return HashString(canonical.ToString());
    }

    private static void PrepareStagingDirectory(string stagingDirectory)
    {
        ValidateExistingPathComponents(
            stagingDirectory,
            nameof(InstrumentationRequest.StagingDirectory),
            terminalMustBeDirectory: true);
        ValidateDirectoryTreeHasNoReparsePoints(
            stagingDirectory,
            nameof(InstrumentationRequest.StagingDirectory));
        if (Directory.Exists(stagingDirectory))
        {
            Directory.Delete(stagingDirectory, recursive: true);
        }

        Directory.CreateDirectory(stagingDirectory);
    }

    private static void ValidateStagingDirectory(string sourceDirectory, string stagingDirectory)
    {
        string source = NormalizeDirectoryPath(
            sourceDirectory,
            nameof(InstrumentationRequest.SourceDirectory));
        string staging = NormalizeDirectoryPath(
            stagingDirectory,
            nameof(InstrumentationRequest.StagingDirectory));
        string root = Path.GetPathRoot(staging)
            ?? throw new ClosureException($"Staging directory '{stagingDirectory}' has no filesystem root.");
        string canonicalStaging = GetCanonicalIdentity(
            staging,
            nameof(InstrumentationRequest.StagingDirectory));
        string canonicalRoot = GetCanonicalIdentity(root, "StagingDirectory filesystem root");

        if (string.Equals(canonicalStaging, canonicalRoot, PathComparison)
            || IsSameOrDescendant(source, staging)
            || IsSameOrDescendant(staging, source))
        {
            throw new ClosureException(
                $"Staging directory '{stagingDirectory}' must be a dedicated directory which does not equal, contain, or reside within source directory '{sourceDirectory}'.");
        }
    }

    private static string NormalizeMetadataPath(string path, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ClosureException(
                $"Instrumentation request {propertyName} must be a non-empty file path.");
        }

        try
        {
            if (Path.EndsInDirectorySeparator(path))
            {
                throw new ClosureException(
                    $"Instrumentation request {propertyName} '{path}' must identify a file, not a directory.");
            }

            string fullPath = InstrumentationPath.GetFullPath(
                path,
                $"Instrumentation request {propertyName}");
            if (string.IsNullOrEmpty(Path.GetFileName(fullPath)) || Directory.Exists(fullPath))
            {
                throw new ClosureException(
                    $"Instrumentation request {propertyName} '{path}' must identify a file, not a directory.");
            }

            ValidateMetadataPathSegments(fullPath, propertyName);
            return fullPath;
        }
        catch (ClosureException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or NotSupportedException
                or IOException
                or UnauthorizedAccessException)
        {
            throw new ClosureException(
                $"Instrumentation request {propertyName} '{path}' is not a valid file path: {exception.Message}",
                exception);
        }
    }

    private static InstrumentationConfiguration NormalizeConfigurationPaths(
        InstrumentationConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        try
        {
            var ruleSetPaths = ImmutableArray.CreateBuilder<string>(configuration.RuleSetPaths.Length);
            for (var index = 0; index < configuration.RuleSetPaths.Length; index++)
            {
                ruleSetPaths.Add(InstrumentationPath.GetFullPath(
                    configuration.RuleSetPaths[index],
                    $"Instrumentation request Configuration.RuleSetPaths[{index}]"));
            }

            string? configurationSourcePath = configuration.SourcePath is null
                ? null
                : InstrumentationPath.GetFullPath(
                    configuration.SourcePath,
                    "Instrumentation request configuration source file");
            return configuration with
            {
                RuleSetPaths = ruleSetPaths.ToImmutable(),
                SourcePath = configurationSourcePath,
            };
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or NotSupportedException
                or IOException
                or UnauthorizedAccessException)
        {
            throw new ClosureException(
                $"Instrumentation request configuration contains an invalid path: {exception.Message}",
                exception);
        }
    }

    private static void ValidateMetadataPathSegments(string fullPath, string propertyName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string root = Path.GetPathRoot(fullPath)
            ?? throw new ClosureException(
                $"Instrumentation request {propertyName} '{fullPath}' has no filesystem root.");
        string relativePath = Path.GetRelativePath(root, fullPath);
        char[] invalidFileNameCharacters = Path.GetInvalidFileNameChars();
        foreach (string segment in relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment.IndexOfAny(invalidFileNameCharacters) >= 0
                || segment.EndsWith(' ')
                || segment.EndsWith('.'))
            {
                throw new ClosureException(
                    $"Instrumentation request {propertyName} '{fullPath}' contains an ambiguous or invalid path segment '{segment}'.");
            }
        }
    }

    private static void ValidateMetadataLocations(
        string sourceDirectory,
        string stagingDirectory,
        string manifestPath,
        string cachePath)
    {
        ValidateMetadataOutsideSource(
            nameof(InstrumentationRequest.ManifestPath),
            manifestPath,
            sourceDirectory);
        ValidateMetadataOutsideSource(
            nameof(InstrumentationRequest.CachePath),
            cachePath,
            sourceDirectory);

        if (IsSameOrDescendant(stagingDirectory, manifestPath))
        {
            throw new ClosureException(
                $"Instrumentation request ManifestPath '{manifestPath}' must not equal or be an ancestor of StagingDirectory '{stagingDirectory}'; a metadata file cannot also be a directory.");
        }

        if (PathsHaveHierarchyCollision(cachePath, stagingDirectory))
        {
            throw new ClosureException(
                $"Instrumentation request CachePath '{cachePath}' must be outside StagingDirectory '{stagingDirectory}' and must not be an ancestor of it; cache metadata cannot collide with the staged closure hierarchy.");
        }

        (string Name, string Path)[] metadataPaths =
        [
            (nameof(InstrumentationRequest.ManifestPath), manifestPath),
            (nameof(InstrumentationRequest.CachePath), cachePath),
        ];
        for (var leftIndex = 0; leftIndex < metadataPaths.Length; leftIndex++)
        {
            for (var rightIndex = leftIndex + 1; rightIndex < metadataPaths.Length; rightIndex++)
            {
                (string leftName, string leftPath) = metadataPaths[leftIndex];
                (string rightName, string rightPath) = metadataPaths[rightIndex];
                if (PathsHaveHierarchyCollision(leftPath, rightPath))
                {
                    throw new ClosureException(
                        $"Instrumentation metadata paths {leftName} '{leftPath}' and {rightName} '{rightPath}' collide by identity or hierarchy; each metadata path must identify an independent file.");
                }
            }
        }
    }

    private static void ValidateProtectedInputLocations(
        string stagingDirectory,
        string manifestPath,
        string cachePath,
        InstrumentationConfiguration configuration)
    {
        var inputs = new List<(string Name, string Path)>();
        if (configuration.SourcePath is { } configurationSourcePath)
        {
            inputs.Add(("configuration source file", configurationSourcePath));
        }

        for (var index = 0; index < configuration.RuleSetPaths.Length; index++)
        {
            inputs.Add(($"Configuration.RuleSetPaths[{index}]", configuration.RuleSetPaths[index]));
        }

        foreach ((string inputName, string inputPath) in inputs)
        {
            if (PathsHaveHierarchyCollision(inputPath, stagingDirectory))
            {
                throw new ClosureException(
                    $"Instrumentation request {inputName} '{inputPath}' collides by canonical filesystem identity or hierarchy with StagingDirectory '{stagingDirectory}'; staging cleanup must not modify an instrumentation input.");
            }

            if (PathsHaveHierarchyCollision(inputPath, manifestPath))
            {
                throw new ClosureException(
                    $"Instrumentation request {inputName} '{inputPath}' collides by canonical filesystem identity or hierarchy with ManifestPath '{manifestPath}'; metadata output must not overwrite an instrumentation input.");
            }

            if (PathsHaveHierarchyCollision(inputPath, cachePath))
            {
                throw new ClosureException(
                    $"Instrumentation request {inputName} '{inputPath}' collides by canonical filesystem identity or hierarchy with CachePath '{cachePath}'; metadata output must not overwrite an instrumentation input.");
            }
        }
    }

    private static void ValidateMetadataOutsideSource(
        string propertyName,
        string metadataPath,
        string sourceDirectory)
    {
        if (PathsHaveHierarchyCollision(metadataPath, sourceDirectory))
        {
            throw new ClosureException(
                $"Instrumentation request {propertyName} '{metadataPath}' must be outside SourceDirectory '{sourceDirectory}' and must not be an ancestor of it so metadata cannot alias a source file or directory.");
        }
    }

    private static void ValidateClosurePlanPaths(
        ClosurePlan plan,
        InstrumentationConfiguration configuration,
        string stagingDirectory,
        string manifestPath)
    {
        var plannedPaths = new Dictionary<string, string>(ClosurePathComparer);
        var plannedEntries = new List<(string StagedPath, string RelativePath)>();
        foreach (ClosureAsset asset in plan.Assets)
        {
            if (!TryResolveStagedPath(
                    stagingDirectory,
                    asset.RelativePath,
                    out string? stagedPath,
                    out string? normalizedRelativePath)
                || !string.Equals(
                    NormalizeClosureRelativePath(asset.RelativePath),
                    normalizedRelativePath,
                    PathComparison))
            {
                throw new ClosureException(
                    $"Closure asset path '{asset.RelativePath}' escapes or ambiguously identifies a path outside StagingDirectory '{stagingDirectory}'.");
            }

            string canonicalStagedPath = GetCanonicalIdentity(
                stagedPath,
                $"Planned staged path for closure asset '{asset.RelativePath}'");
            if (!plannedPaths.TryAdd(canonicalStagedPath, asset.RelativePath))
            {
                throw new ClosureException(
                    $"Closure asset paths '{plannedPaths[canonicalStagedPath]}' and '{asset.RelativePath}' collide by canonical filesystem identity on this platform.");
            }

            plannedEntries.Add((stagedPath, asset.RelativePath));
        }

        if (configuration.Mode == InstrumentationMode.RaceExploration)
        {
            string runtimePath = Path.Combine(stagingDirectory, "Orleans.Testing.Simulation.dll");
            string canonicalRuntimePath = GetCanonicalIdentity(runtimePath, "Planned race runtime path");
            if (!plannedPaths.TryAdd(canonicalRuntimePath, "Orleans.Testing.Simulation.dll"))
            {
                throw new ClosureException(
                    $"Planned race runtime 'Orleans.Testing.Simulation.dll' collides with closure asset '{plannedPaths[canonicalRuntimePath]}' on this platform.");
            }

            plannedEntries.Add((runtimePath, "Orleans.Testing.Simulation.dll"));
        }

        foreach ((string plannedPath, string relativePath) in plannedEntries)
        {
            string? ancestor = Path.GetDirectoryName(plannedPath);
            while (ancestor is not null && !string.Equals(ancestor, stagingDirectory, PathComparison))
            {
                string canonicalAncestor = GetCanonicalIdentity(
                    ancestor,
                    $"Ancestor of planned staged path '{relativePath}'");
                if (plannedPaths.TryGetValue(canonicalAncestor, out string? ancestorRelativePath))
                {
                    throw new ClosureException(
                        $"Planned staged paths '{ancestorRelativePath}' and '{relativePath}' collide as a file and descendant path.");
                }

                ancestor = Path.GetDirectoryName(ancestor);
            }
        }

        foreach ((string plannedPath, string relativePath) in plannedEntries)
        {
            if (PathsHaveHierarchyCollision(manifestPath, plannedPath))
            {
                throw new ClosureException(
                    $"Instrumentation request ManifestPath '{manifestPath}' collides by canonical filesystem identity or hierarchy with planned staged assembly or copied asset '{relativePath}'.");
            }
        }
    }

    private static void ValidateRequestPathIsolation(
        string sourceDirectory,
        string stagingDirectory,
        string manifestPath,
        string cachePath,
        InstrumentationConfiguration configuration)
    {
        ValidateExistingPathComponents(
            sourceDirectory,
            nameof(InstrumentationRequest.SourceDirectory),
            terminalMustBeDirectory: true);
        ValidateExistingPathComponents(
            stagingDirectory,
            nameof(InstrumentationRequest.StagingDirectory),
            terminalMustBeDirectory: true);
        ValidateExistingPathComponents(
            manifestPath,
            nameof(InstrumentationRequest.ManifestPath),
            terminalMustBeDirectory: false);
        ValidateExistingPathComponents(
            cachePath,
            nameof(InstrumentationRequest.CachePath),
            terminalMustBeDirectory: false);
        if (configuration.SourcePath is { } configurationSourcePath)
        {
            ValidateExistingPathComponents(
                configurationSourcePath,
                "configuration source file",
                terminalMustBeDirectory: false);
        }

        for (var index = 0; index < configuration.RuleSetPaths.Length; index++)
        {
            ValidateExistingPathComponents(
                configuration.RuleSetPaths[index],
                $"Configuration.RuleSetPaths[{index}]",
                terminalMustBeDirectory: false);
        }

        ValidateDirectoryTreeHasNoReparsePoints(
            sourceDirectory,
            nameof(InstrumentationRequest.SourceDirectory));
        ValidateDirectoryTreeHasNoReparsePoints(
            stagingDirectory,
            nameof(InstrumentationRequest.StagingDirectory));
    }

    private static void ValidateExistingPathComponents(
        string path,
        string propertyName,
        bool terminalMustBeDirectory)
    {
        string fullPath = InstrumentationPath.GetFullPath(
            path,
            $"Instrumentation request {propertyName}");
        string root = Path.GetPathRoot(fullPath)
            ?? throw new ClosureException(
                $"Instrumentation request {propertyName} '{path}' has no filesystem root.");
        string relativePath = Path.GetRelativePath(root, fullPath);
        string[] segments = relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        string current = root;
        for (var index = 0; index < segments.Length; index++)
        {
            current = Path.Combine(current, segments[index]);
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(current);
            }
            catch (Exception exception) when (
                exception is FileNotFoundException or DirectoryNotFoundException)
            {
                if (HasLinkTarget(current))
                {
                    throw new ClosureException(
                        $"Instrumentation request {propertyName} '{path}' has existing symbolic-link, junction, or reparse-point component '{current}', which is not permitted.");
                }

                break;
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or NotSupportedException
                    or ArgumentException)
            {
                throw new ClosureException(
                    $"Instrumentation request {propertyName} '{path}' cannot safely inspect existing component '{current}': {exception.Message}",
                    exception);
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new ClosureException(
                    $"Instrumentation request {propertyName} '{path}' has existing symbolic-link, junction, or reparse-point component '{current}', which is not permitted.");
            }

            bool isDirectory = (attributes & FileAttributes.Directory) != 0;
            bool terminal = index == segments.Length - 1;
            if ((!terminal || terminalMustBeDirectory) && !isDirectory)
            {
                throw new ClosureException(
                    $"Instrumentation request {propertyName} '{path}' requires directory component '{current}', but an existing file occupies that path.");
            }

            if (terminal && !terminalMustBeDirectory && isDirectory)
            {
                throw new ClosureException(
                    $"Instrumentation request {propertyName} '{path}' must identify a file, but an existing directory occupies that path.");
            }
        }
    }

    private static bool HasLinkTarget(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget is not null
                || new DirectoryInfo(path).LinkTarget is not null;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException
                or ArgumentException)
        {
            return false;
        }
    }

    private static void ValidateDirectoryTreeHasNoReparsePoints(string path, string propertyName)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        var pending = new Stack<string>();
        pending.Push(path);
        try
        {
            while (pending.TryPop(out string? directory))
            {
                foreach (string entry in Directory.EnumerateFileSystemEntries(
                    directory,
                    "*",
                    SearchOption.TopDirectoryOnly))
                {
                    FileAttributes attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new ClosureException(
                            $"Instrumentation request {propertyName} '{path}' contains symbolic link, junction, or reparse point '{entry}', which is not permitted.");
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        pending.Push(entry);
                    }
                }
            }
        }
        catch (ClosureException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException
                or ArgumentException)
        {
            throw new ClosureException(
                $"Instrumentation request {propertyName} '{path}' cannot safely inspect its existing directory tree: {exception.Message}",
                exception);
        }
    }

    private static bool IsSameOrDescendant(string candidate, string parent)
    {
        string canonicalCandidate = GetCanonicalIdentity(candidate, "Filesystem path");
        string canonicalParent = GetCanonicalIdentity(parent, "Filesystem path");
        return IsSameOrDescendantCanonical(canonicalCandidate, canonicalParent);
    }

    private static bool IsSameOrDescendantCanonical(string candidate, string parent)
    {
        if (string.Equals(candidate, parent, PathComparison))
        {
            return true;
        }

        string parentPrefix = Path.EndsInDirectorySeparator(parent)
            ? parent
            : parent + Path.DirectorySeparatorChar;
        return candidate.StartsWith(parentPrefix, PathComparison);
    }

    private static bool PathsHaveHierarchyCollision(string left, string right)
    {
        string canonicalLeft = GetCanonicalIdentity(left, "Filesystem path");
        string canonicalRight = GetCanonicalIdentity(right, "Filesystem path");
        return IsSameOrDescendantCanonical(canonicalLeft, canonicalRight)
            || IsSameOrDescendantCanonical(canonicalRight, canonicalLeft);
    }

    private static string GetCanonicalIdentity(string path, string description)
    {
        try
        {
            return InstrumentationPath.GetCanonicalPath(path, description);
        }
        catch (ClosureException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or NotSupportedException
                or IOException
                or UnauthorizedAccessException)
        {
            throw new ClosureException(
                $"{description} '{path}' cannot be safely resolved to a canonical filesystem path: {exception.Message}",
                exception);
        }
    }

    private static string NormalizeDirectoryPath(string path, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ClosureException(
                $"Instrumentation request {propertyName} must be a non-empty directory path.");
        }

        try
        {
            string fullPath = InstrumentationPath.GetFullPath(
                path,
                $"Instrumentation request {propertyName}");
            string? root = Path.GetPathRoot(fullPath);
            return root is not null && string.Equals(fullPath, root, PathComparison)
                ? root
                : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (ClosureException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or NotSupportedException
                or IOException
                or UnauthorizedAccessException)
        {
            throw new ClosureException(
                $"Instrumentation request {propertyName} '{path}' is not a valid directory path: {exception.Message}",
                exception);
        }
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static StringComparer ClosurePathComparer =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private static string NormalizeClosureRelativePath(string path) =>
        path.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');

    private static void WriteAllTextAtomically(string path, string contents)
    {
        string fullPath = InstrumentationPath.GetFullPath(path, "metadata output");
        ValidateExistingPathComponents(fullPath, "metadata output", terminalMustBeDirectory: false);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string? temporaryPath = null;
        try
        {
            FileStream? stream = null;
            for (var attempt = 0; attempt < 16; attempt++)
            {
                string candidate = fullPath + "."
                    + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16))
                    + ".tmp";
                try
                {
                    stream = new FileStream(
                        candidate,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        bufferSize: 4096,
                        FileOptions.WriteThrough);
                    temporaryPath = candidate;
                    break;
                }
                catch (IOException) when (File.Exists(candidate))
                {
                }
            }

            if (stream is null || temporaryPath is null)
            {
                throw new IOException(
                    $"Could not create a unique temporary file for metadata output '{fullPath}' after 16 attempts.");
            }

            using (stream)
            {
                using var writer = new StreamWriter(
                    stream,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    bufferSize: 4096,
                    leaveOpen: true);
                writer.Write(contents);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, fullPath, overwrite: true);
            temporaryPath = null;
        }
        finally
        {
            if (temporaryPath is not null)
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static string ToStagingPath(string stagingDirectory, string relativePath) =>
        Path.Combine(stagingDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static string? TryHashCacheOutputFile(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            return Convert.ToHexStringLower(SHA256.HashData(stream));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string HashRequiredSourceFile(string path, string description)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            return Convert.ToHexStringLower(SHA256.HashData(stream));
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException)
        {
            throw new ClosureException(
                $"{description} '{path}' is missing, unreadable, or could not be hashed: {exception.Message}",
                exception);
        }
    }

    private static string CreateReadyToRunTemporaryDirectory(
        string sourceDirectory,
        string stagingDirectory,
        string cachePath)
    {
        string normalizedCachePath = InstrumentationPath.GetFullPath(
            cachePath,
            nameof(InstrumentationRequest.CachePath));
        string cacheIdentity = HashString(normalizedCachePath)[..16];
        DirectoryInfo directory = Directory.CreateTempSubdirectory(
            $"orleans-simulation-r2r-{cacheIdentity}-");
        if (PathsHaveHierarchyCollision(directory.FullName, sourceDirectory)
            || PathsHaveHierarchyCollision(directory.FullName, stagingDirectory))
        {
            directory.Delete(recursive: true);
            throw new ClosureException(
                $"Could not create a ReadyToRun temporary workspace outside source and staging directories.");
        }

        return directory.FullName;
    }

    private static void CopyReadyToRunOutputIntoStaging(
        string engineOutput,
        string outputPath,
        string? temporaryDirectory)
    {
        if (temporaryDirectory is null || !File.Exists(engineOutput))
        {
            return;
        }

        File.Copy(engineOutput, outputPath, overwrite: true);
        string temporarySymbols = Path.ChangeExtension(engineOutput, "pdb");
        if (File.Exists(temporarySymbols))
        {
            File.Copy(
                temporarySymbols,
                Path.ChangeExtension(outputPath, "pdb"),
                overwrite: true);
        }
    }

    private static void DeleteReadyToRunTemporaryDirectory(string? temporaryDirectory)
    {
        if (temporaryDirectory is null)
        {
            return;
        }

        try
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string HashBytes(byte[] bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static string HashString(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
