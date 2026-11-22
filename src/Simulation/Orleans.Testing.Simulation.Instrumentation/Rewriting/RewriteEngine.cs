using System.Collections.Immutable;
using System.Security.Cryptography;
using Orleans.Testing.Simulation.Instrumentation.Diagnostics;
using Orleans.Testing.Simulation.Instrumentation.Manifest;
using Orleans.Testing.Simulation.Instrumentation.Rules;
using Mono.Cecil;

namespace Orleans.Testing.Simulation.Instrumentation.Rewriting;

/// <summary>
/// <para>
/// The rewrite engine: a generic, deterministic Mono.Cecil transformation pipeline that
/// applies a versioned <see cref="RewriteRuleSet"/> to an assembly. It loads the input, enforces
/// idempotence via an assembly-level signature marker, runs an ordered set of passes
/// (<see cref="CallSiteRewritingPass"/> then <see cref="TypeReferenceRewritingPass"/>), validates the
/// result by reading it back, writes the output preserving portable/embedded PDBs, and emits a
/// deterministic <see cref="InstrumentationManifest"/>.
/// </para>
/// <para>
/// This engine is <b>internal and experimental</b>. It performs the IL transformation mechanics only.
/// It does not activate MSBuild targets or CLI commands, re-sign or Authenticode-sign assemblies,
/// rewrite publish output recursively, hook assembly loading, or supply any concrete BCL shim - those
/// are out of scope for the rewrite engine (see the compatibility notes). Rules and replacement assemblies are
/// supplied by the caller.
/// </para>
/// </summary>
public static class RewriteEngine
{
    /// <summary>The name recorded as the producing engine in manifests and signature markers.</summary>
    public const string EngineName = "Clockwork.Instrumentation";

    /// <summary>
    /// The simple name of the shim assembly that declares the exception-hardening guard
    /// (<c>Orleans.Testing.Simulation.Runtime.SimulationExceptionGuard</c>). The caller must include the Clockwork assembly in
    /// <see cref="RewriteOptions.ReplacementAssemblyPaths"/> when
    /// <see cref="RewriteOptions.HardenExceptionHandlers"/> is enabled.
    /// </summary>
    private const string ExceptionGuardShimAssembly = "Orleans.Testing.Simulation";

    /// <summary>Gets the engine version recorded in manifests and idempotence markers.</summary>
    public static string EngineVersion =>
        typeof(RewriteEngine).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    /// <summary>Applies <paramref name="request"/>'s rule set to its input assembly.</summary>
    /// <param name="request">The rewrite request.</param>
    /// <returns>The deterministic outcome, including the manifest and diagnostics.</returns>
    public static RewriteResult Rewrite(RewriteRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Options);
        RewriteOptions options = request.Options;

        string signature = request.RuleSet.ComputeSignature();
        string optionsFingerprint = options.ComputeSemanticFingerprint();
        string engineVersion = EngineVersion;
        string inputHash = ComputeFileHash(request.InputPath);

        var diagnostics = new List<RewriteDiagnostic>();
        var exclusions = new List<ManifestExclusion>();
        var optionalUnresolved = new SortedSet<string>(StringComparer.Ordinal);

        using AssemblyRewriteContext context = AssemblyRewriteContext.Load(
            request.InputPath,
            options.ReferenceSearchDirectories,
            readSymbols: true,
            onResolveFailure: reference =>
            {
                if (options.WarnOnUnresolvedReferences)
                {
                    optionalUnresolved.Add(reference.FullName);
                }
            });

        diagnostics.AddRange(context.LoadDiagnostics);

        bool hasSymbols = context.Symbols is SymbolKind.Portable or SymbolKind.Embedded;
        var inputIdentity = new ManifestAssemblyIdentity(
            context.Name,
            Path.GetFileName(request.InputPath),
            inputHash,
            hasSymbols,
            context.Symbols.ToString());

        // A rule set that does not support the target runtime fails once, before any call site is visited.
        RuleSetRuntimeSupport runtimeSupport = RuleSetRuntimeSupport.Evaluate(request.RuleSet, options.TargetRuntime);
        if (!runtimeSupport.IsSupported)
        {
            diagnostics.Add(runtimeSupport.ToDiagnostic("requested"));
            return Failed(request, engineVersion, signature, inputIdentity, diagnostics, exclusions, [], []);
        }

        // Mono.Cecil cannot round-trip mixed-mode assemblies; fail clearly before touching them.
        if (!context.IsPureIL())
        {
            diagnostics.Add(RewriteDiagnostic.Error(
                RewriteDiagnosticIds.MixedModeAssembly,
                $"Assembly '{context.Name}' is a mixed-mode assembly and cannot be rewritten."));
            return Failed(request, engineVersion, signature, inputIdentity, diagnostics, exclusions, [], []);
        }

        // Idempotence: an assembly already carrying our signature is either a verified no-op (same
        // engine + rule set + content signature) or an incompatible rewrite that must fail.
        if (context.TryGetRewriteSignature(out ClockworkRewriteSignatureValues existing))
        {
            return HandleAlreadyRewritten(
                request,
                options,
                engineVersion,
                signature,
                optionsFingerprint,
                existing,
                context.ReadSupportedRuntimes(),
                AssemblyRewriteContext.FormatSupportedRuntimes(runtimeSupport.SupportedRuntimes),
                inputIdentity,
                diagnostics,
                exclusions);
        }

        CollectExclusions(context, options, exclusions, diagnostics, out HashSet<string> skip);

        var matcher = new RewriteRuleMatcher(runtimeSupport);
        using var resolver = new ReplacementResolver(options.ReplacementAssemblyPaths, options.ReferenceSearchDirectories);
        var session = new RewriteSession(context.MainModule, matcher, resolver, skip, options.RewrittenAssemblyNames);

        RewritePass[] passes =
        [
            .. RuntimeAsyncDetectionPass.Applies(matcher)
                ? new RewritePass[] { new RuntimeAsyncDetectionPass(session) }
                : [],
            new CallSiteRewritingPass(session),
            new TypeReferenceRewritingPass(session),
            new MemberSubstitutionRewritingPass(session),
            .. options.InstrumentRaceExploration
                ? new RewritePass[]
                {
                    new CollectionAccessRewritingPass(session),
                    new RaceExplorationRewritingPass(session),
                }
                : [],
            .. options.HardenExceptionHandlers
                ? new RewritePass[] { new ExceptionHardeningRewritingPass(session, ExceptionGuardShimAssembly) }
                : [],
            .. options.DetectUncontrolledTasks
                ? new RewritePass[] { new CrossAssemblyTaskDetectionPass(session) }
                : [],
        ];

        foreach (RewritePass pass in passes)
        {
            RunPass(pass, context, skip);
        }

        diagnostics.AddRange(session.GetDiagnostics());

        // A resolution that met a cycle of type forwarders failed, so whatever depended on it was not checked.
        foreach (string cycle in context.ForwarderCycles)
        {
            diagnostics.Add(RewriteDiagnostic.Error(
                RewriteDiagnosticIds.UnresolvedReference,
                $"{cycle}, so '{context.Name}' cannot be checked against the type."));
        }

        ImmutableArray<ManifestTransformation> transformations = session.GetTransformations();

        var unresolved = new SortedSet<string>(optionalUnresolved, StringComparer.Ordinal);
        foreach (string reference in session.GetUnresolvedReferences())
        {
            unresolved.Add(reference);
        }

        foreach (string reference in optionalUnresolved)
        {
            diagnostics.Add(RewriteDiagnostic.Warning(
                RewriteDiagnosticIds.UnresolvedReference,
                $"Optional assembly reference '{reference}' could not be resolved."));
        }

        if (diagnostics.Any(d => d.IsError))
        {
            return Failed(request, engineVersion, signature, inputIdentity, diagnostics, exclusions, transformations, [.. unresolved]);
        }

        context.StripStrongNames(options.StrongNameAssemblyNames.ToHashSet(StringComparer.Ordinal));
        context.ApplyRewriteSignature(new ClockworkRewriteSignatureValues(
            engineVersion, request.RuleSet.Id, request.RuleSet.Version, signature, optionsFingerprint));
        context.ApplySupportedRuntimes(runtimeSupport.SupportedRuntimes);

        EnsureOutputDirectory(request.OutputPath);
        context.Write(request.OutputPath);

        diagnostics.AddRange(RewriteValidator.Validate(
            request.OutputPath,
            [
                .. options.ReferenceSearchDirectories,
                .. options.ReplacementAssemblyPaths.Select(path => Path.GetDirectoryName(Path.GetFullPath(path)) ?? string.Empty),
            ],
            new RewriteValidator.BindingScope(matcher.GetSubstitutionTypeNames(), session.RewrittenAssemblyNames)));

        string? outputHash = options.ComputeOutputHash ? ComputeFileHash(request.OutputPath) : null;
        var outputIdentity = new ManifestAssemblyIdentity(
            context.Name,
            Path.GetFileName(request.OutputPath),
            outputHash,
            hasSymbols,
            context.Symbols.ToString());

        bool succeeded = !diagnostics.Any(d => d.IsError);
        InstrumentationManifest manifest = BuildManifest(
            request, engineVersion, signature, inputIdentity, outputIdentity, wasNoOp: false,
            transformations, exclusions, [.. unresolved], diagnostics);

        return new RewriteResult
        {
            Succeeded = succeeded,
            WasNoOp = false,
            WasWritten = true,
            Manifest = manifest,
        };
    }

    private static RewriteResult HandleAlreadyRewritten(
        RewriteRequest request,
        RewriteOptions options,
        string engineVersion,
        string signature,
        string optionsFingerprint,
        ClockworkRewriteSignatureValues existing,
        string? existingSupportedRuntimes,
        string supportedRuntimes,
        ManifestAssemblyIdentity inputIdentity,
        List<RewriteDiagnostic> diagnostics,
        List<ManifestExclusion> exclusions)
    {
        // An input rewritten without the supported-runtimes record would run unchecked, so it is not a verified no-op.
        bool compatible = existing.Signature == signature
            && existing.EngineVersion == engineVersion
            && existing.RuleSetId == request.RuleSet.Id
            && existing.RuleSetVersion == request.RuleSet.Version
            && existing.OptionsFingerprint == optionsFingerprint
            && existingSupportedRuntimes == supportedRuntimes;

        if (!compatible)
        {
            diagnostics.Add(RewriteDiagnostic.Error(
                RewriteDiagnosticIds.IncompatibleRewriteVersion,
                $"Assembly '{inputIdentity.Name}' was already rewritten by engine {existing.EngineVersion} with rule set " +
                $"'{existing.RuleSetId}' v{existing.RuleSetVersion} (signature {existing.Signature}). Re-run against the " +
                $"original, un-rewritten assembly to apply a different rule set or rewrite options " +
                $"(existing options fingerprint '{DisplayFingerprint(existing.OptionsFingerprint)}', requested '{optionsFingerprint}'; " +
                $"existing supported runtimes '{existingSupportedRuntimes ?? "none"}', requested '{supportedRuntimes}'); " +
                "double-rewriting is refused."));
            return Failed(request, engineVersion, signature, inputIdentity, diagnostics, exclusions, [], []);
        }

        diagnostics.Add(RewriteDiagnostic.Info(
            RewriteDiagnosticIds.AlreadyRewritten,
            $"Assembly '{inputIdentity.Name}' is already rewritten with a matching signature; rewriting is a verified no-op."));

        bool written = false;
        if (!PathsEqual(request.InputPath, request.OutputPath))
        {
            EnsureOutputDirectory(request.OutputPath);
            File.Copy(request.InputPath, request.OutputPath, overwrite: true);
            CopySymbolsIfPresent(request.InputPath, request.OutputPath);
            written = true;
        }

        ManifestAssemblyIdentity? outputIdentity = written
            ? inputIdentity with { FileName = Path.GetFileName(request.OutputPath) }
            : null;

        InstrumentationManifest manifest = BuildManifest(
            request, engineVersion, signature, inputIdentity, outputIdentity, wasNoOp: true,
            [], exclusions, [], diagnostics);

        return new RewriteResult
        {
            Succeeded = true,
            WasNoOp = true,
            WasWritten = written,
            Manifest = manifest,
        };
    }

    private static string DisplayFingerprint(string fingerprint) =>
        string.IsNullOrEmpty(fingerprint) ? "legacy-unfingerprinted" : fingerprint;

    private static void RunPass(RewritePass pass, AssemblyRewriteContext context, HashSet<string> skip)
    {
        foreach (ModuleDefinition module in context.Definition.Modules)
        {
            pass.VisitModule(module);
            foreach (TypeDefinition type in module.GetTypes())
            {
                if (skip.Contains(type.FullName))
                {
                    continue;
                }

                pass.VisitType(type);
                foreach (MethodDefinition method in type.Methods)
                {
                    pass.VisitMethod(method);
                    if (pass.IsMethodBodyModified)
                    {
                        RewritePass.FixInstructionOffsets(method);
                        pass.IsMethodBodyModified = false;
                    }
                }
            }

            pass.CompleteVisit();
        }
    }

    private static void CollectExclusions(
        AssemblyRewriteContext context,
        RewriteOptions options,
        List<ManifestExclusion> exclusions,
        List<RewriteDiagnostic> diagnostics,
        out HashSet<string> skip)
    {
        skip = new HashSet<string>(StringComparer.Ordinal);
        var excludedByOption = new HashSet<string>(options.ExcludedTypeFullNames, StringComparer.Ordinal);

        foreach (ModuleDefinition module in context.Definition.Modules)
        {
            foreach (TypeDefinition type in module.GetTypes())
            {
                string fullName = type.FullName;
                if (!skip.Contains(fullName) && IsGeneratedTestHostBootstrap(fullName))
                {
                    skip.Add(fullName);
                    const string reason = "Generated test-host bootstrap executes before a simulation is active.";
                    exclusions.Add(new ManifestExclusion(fullName, reason));
                    diagnostics.Add(RewriteDiagnostic.Info(
                        RewriteDiagnosticIds.TypeExcluded,
                        $"Type '{fullName}' was excluded from rewriting: {reason}"));
                }
                else if (!skip.Contains(fullName) && excludedByOption.Contains(fullName))
                {
                    skip.Add(fullName);
                    exclusions.Add(new ManifestExclusion(fullName, "Excluded by options."));
                    diagnostics.Add(RewriteDiagnostic.Info(
                        RewriteDiagnosticIds.TypeExcluded, $"Type '{fullName}' was excluded from rewriting by options."));
                }
                else if (!skip.Contains(fullName) && AssemblyRewriteContext.IsExcludedByAttribute(type, out string reason))
                {
                    skip.Add(fullName);
                    string effectiveReason = string.IsNullOrEmpty(reason) ? "Excluded by [DoNotRewrite]." : reason;
                    exclusions.Add(new ManifestExclusion(fullName, effectiveReason));
                    diagnostics.Add(RewriteDiagnostic.Info(
                        RewriteDiagnosticIds.TypeExcluded, $"Type '{fullName}' was excluded from rewriting: {effectiveReason}"));
                }
                else if (!skip.Contains(fullName)
                    && type.DeclaringType is { } declaring
                    && skip.Contains(declaring.FullName)
                    && IsCompilerGenerated(type))
                {
                    // An excluded type's async and iterator state machines and its lambda and local-function closures
                    // carry its code, so they are excluded with it: rewriting them alone would leave the type and its
                    // helpers inconsistent. GetTypes yields a declaring type before its nested types, so this is
                    // transitive. An explicitly declared nested type keeps its own annotation.
                    skip.Add(fullName);
                    string nestedReason = $"Compiler-generated type nested in excluded type '{declaring.FullName}'.";
                    exclusions.Add(new ManifestExclusion(fullName, nestedReason));
                    diagnostics.Add(RewriteDiagnostic.Info(
                        RewriteDiagnosticIds.TypeExcluded, $"Type '{fullName}' was excluded from rewriting: {nestedReason}"));
                }
            }
        }
    }

    // The compiler marks the types it generates in a declared type [CompilerGenerated], but not the types it nests in
    // those, such as an async lambda's state machine in its closure, which are generated all the same.
    private static bool IsCompilerGenerated(TypeDefinition type) =>
        type.CustomAttributes.Any(attribute =>
            attribute.AttributeType.FullName == "System.Runtime.CompilerServices.CompilerGeneratedAttribute")
        || (type.DeclaringType is { } declaring && IsCompilerGenerated(declaring));

    private static bool IsGeneratedTestHostBootstrap(string fullName) =>
        fullName.Contains("XunitAutoGeneratedEntryPoint", StringComparison.Ordinal) ||
        fullName.EndsWith(".SelfRegisteredExtensions", StringComparison.Ordinal) ||
        fullName.Contains("TestingPlatformEntryPoint", StringComparison.Ordinal);

    private static RewriteResult Failed(
        RewriteRequest request,
        string engineVersion,
        string signature,
        ManifestAssemblyIdentity inputIdentity,
        List<RewriteDiagnostic> diagnostics,
        List<ManifestExclusion> exclusions,
        ImmutableArray<ManifestTransformation> transformations,
        ImmutableArray<string> unresolved)
    {
        InstrumentationManifest manifest = BuildManifest(
            request, engineVersion, signature, inputIdentity, output: null, wasNoOp: false,
            transformations, exclusions, unresolved, diagnostics);

        return new RewriteResult
        {
            Succeeded = false,
            WasNoOp = false,
            WasWritten = false,
            Manifest = manifest,
        };
    }

    private static InstrumentationManifest BuildManifest(
        RewriteRequest request,
        string engineVersion,
        string signature,
        ManifestAssemblyIdentity input,
        ManifestAssemblyIdentity? output,
        bool wasNoOp,
        ImmutableArray<ManifestTransformation> transformations,
        List<ManifestExclusion> exclusions,
        ImmutableArray<string> unresolved,
        List<RewriteDiagnostic> diagnostics) => new()
        {
            EngineName = EngineName,
            EngineVersion = engineVersion,
            RuleSetId = request.RuleSet.Id,
            RuleSetVersion = request.RuleSet.Version,
            RuleSetSignature = signature,
            Mode = request.Options.InstrumentRaceExploration
                ? Configuration.InstrumentationMode.RaceExploration
                : Configuration.InstrumentationMode.Controlled,
            Input = input,
            Output = output,
            WasNoOp = wasNoOp,
            Transformations = transformations,
            Exclusions = [.. exclusions],
            UnresolvedReferences = unresolved,
            Diagnostics = [.. diagnostics],
        };

    private static string ComputeFileHash(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static void EnsureOutputDirectory(string outputPath)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static void CopySymbolsIfPresent(string inputPath, string outputPath)
    {
        string inputPdb = Path.ChangeExtension(inputPath, "pdb");
        if (File.Exists(inputPdb))
        {
            File.Copy(inputPdb, Path.ChangeExtension(outputPath, "pdb"), overwrite: true);
        }
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
